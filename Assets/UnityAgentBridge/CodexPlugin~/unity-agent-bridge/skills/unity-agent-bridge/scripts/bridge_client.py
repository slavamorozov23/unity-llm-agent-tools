from __future__ import annotations

import argparse
import base64
import difflib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")


def forwarded_arguments() -> None:
    encoded = os.environ.pop("UNITY_AGENT_BRIDGE_ARGUMENTS", None)
    if not encoded:
        return
    try:
        arguments = json.loads(base64.b64decode(encoded).decode("utf-8"))
    except (ValueError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RuntimeError("Unity Agent Bridge arguments are invalid.") from error
    if not isinstance(arguments, list) or not all(isinstance(item, str) for item in arguments):
        raise RuntimeError("Unity Agent Bridge arguments are invalid.")
    sys.argv[1:] = arguments


forwarded_arguments()


SERVER_RESTART_WAIT_SECONDS = 20.0
READ_ONLY_OPERATIONS = {
    "health", "tree", "find", "object-info", "component-suggest", "object-picker", "asset-find",
    "creation-templates", "asset-info", "asset-object-picker", "logs", "status",
    "shader-info", "object-templates", "build-scenes",
    "game-resolutions", "packages", "packages-search", "input-axes", "animation-table",
    "animation-properties", "animator-find", "animator-motions", "animator-graph",
    "animator-runtime-state", "timeline-info", "profiler-hierarchy",
}


# Commands agents have typed that do not exist, with what does that job; spelling alone does not lead there.
CONFUSED_COMMANDS = {
    "component-info": "object-info --path <object> --component <type>",
    "object-modify": "component-modify --path <object> --component Transform --set ...; object-rename|active|tag|layer|static",
    "info": "object-info --path <object> or asset-info --path <Assets/...>",
    "console": "logs; status counts errors",
    "clear-logs": "logs --clear",
    "profiler": "the MCP profiler tool records; profiler-hierarchy reads the record",
    "screenshot": "the MCP scene_screenshot or game_actions tool",
    "scene": "tree, scene-open or scene-save",
    "project-search": "asset-find --name <description> or object-find --name <description>",
    "help": "<command> --help; the skill's references list the commands",
    "start": "play start",
    "stop": "play stop",
    "pause": "play pause",
    "resume": "play resume",
}


class CompactArgumentParser(argparse.ArgumentParser):
    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        # Values that start with a minus and a digit are values, not options: --near -11.7,1,0, --set x=-2.
        self._negative_number_matcher = re.compile(r"^-\.?\d")

    def error(self, message: str) -> None:
        choice = re.match(r"argument (\S+): invalid choice: '([^']*)' \(choose from (.*)\)", message)
        if choice:
            if choice.group(1) == "command" and choice.group(2) in CONFUSED_COMMANDS:
                raise ValueError(f"Unknown command '{choice.group(2)}'. Use: {CONFUSED_COMMANDS[choice.group(2)]}.")
            options = re.findall(r"'([^']+)'", choice.group(3))
            close = difflib.get_close_matches(choice.group(2), options, n=3, cutoff=0.4)
            hint = f" Did you mean: {', '.join(close)}?" if close else ""
            what = "command" if choice.group(1) == "command" else choice.group(1)
            raise ValueError(f"Unknown {what} '{choice.group(2)}'.{hint}")
        usage = self.format_usage()
        commands = getattr(self, "commands", None)
        command = next((item for item in sys.argv[1:] if not item.startswith("-")), None)
        if commands is not None and command in commands.choices:
            usage = commands.choices[command].format_usage()
        raise ValueError(f"{message}\n{usage.strip()}")


def read_server(project: Path, wait_seconds: float = 0.0) -> dict[str, Any]:
    config_path = project / "Library" / "UnityAgentBridge" / "server.json"
    deadline = time.monotonic() + wait_seconds
    while True:
        if config_path.is_file():
            try:
                config = json.loads(config_path.read_text(encoding="utf-8"))
                if Path(config["projectRoot"]).resolve() != project.resolve():
                    raise RuntimeError("Bridge server configuration belongs to a different Unity project.")
                if process_exists(int(config["pid"])):
                    return config
            except (KeyError, TypeError, ValueError, json.JSONDecodeError, OSError):
                pass
        if time.monotonic() >= deadline:
            raise RuntimeError("Unity Agent Bridge server is not running. Open Tools > Unity Agent Bridge and press Start Server.")
        time.sleep(0.1)


def find_project() -> Path:
    current = Path.cwd().resolve()
    for candidate in (current, *current.parents):
        if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings").is_dir():
            return candidate
    raise RuntimeError("Текущая папка не находится внутри проекта Unity.")


def detected_client() -> str | None:
    explicit = os.environ.get("UNITY_AGENT_BRIDGE_CLIENT", "").casefold()
    if explicit in {"codex", "claude"}:
        return explicit
    plugin_root = Path(__file__).resolve().parents[3]
    if (plugin_root / ".claude-plugin" / "plugin.json").is_file():
        return "claude"
    if (plugin_root / ".codex-plugin" / "plugin.json").is_file():
        return "codex"
    return None


def mcp_status(project: Path) -> dict[str, Any]:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        return {"ready": False, "error": "LOCALAPPDATA is unavailable."}
    discovery = Path(local_app_data) / "UnityAgentBridge"
    client = detected_client()
    locator = discovery / "active-project.json"
    try:
        active = Path(json.loads(locator.read_text(encoding="utf-8"))["projectRoot"]).resolve()
    except FileNotFoundError:
        return {"ready": False, "error": "Unity Agent Bridge has no active project. Restart its server in Unity."}
    except (KeyError, TypeError, ValueError, json.JSONDecodeError, OSError):
        return {"ready": False, "error": "Unity Agent Bridge active project is invalid."}
    if active != project.resolve():
        return {"ready": False, "error": "Unity game MCP points to another Unity project."}
    clients = [client] if client else ["claude", "codex"]
    states: dict[str, dict[str, Any]] = {}
    for candidate in clients:
        error_path = discovery / f"mcp-error-{candidate}.txt"
        if error_path.is_file():
            states[candidate] = {"ready": False, "state": "error", "error": error_path.read_text(encoding="utf-8").strip()[:500]}
            continue
        state_files = list(discovery.glob(f"mcp-state-{candidate}-*.json"))
        candidate_states = []
        for state_path in state_files:
            try:
                state = json.loads(state_path.read_text(encoding="utf-8"))
                pid = int(state.get("pid", 0))
                if pid <= 0 or not process_exists(pid):
                    state_path.unlink(missing_ok=True)
                    continue
                candidate_states.append(state)
            except (TypeError, ValueError, json.JSONDecodeError, OSError):
                continue
        if not candidate_states:
            states[candidate] = {"ready": False, "state": "not-started"}
            continue
        if any(Path(state.get("projectRoot", "")).resolve() == project.resolve() and state.get("state") == "ready" for state in candidate_states):
            states[candidate] = {"ready": True, "state": "ready"}
        elif any(Path(state.get("projectRoot", "")).resolve() == project.resolve() and state.get("state") == "started" for state in candidate_states):
            states[candidate] = {"ready": False, "state": "started"}
        elif all(Path(state.get("projectRoot", "")).resolve() != project.resolve() for state in candidate_states):
            states[candidate] = {"ready": False, "state": "other-project"}
        else:
            states[candidate] = {"ready": False, "state": "not-started"}
    ready_client = next((name for name, state in states.items() if state.get("ready")), None)
    if ready_client:
        return {"ready": True, "state": "ready", "client": ready_client}
    if client:
        state = states[client]
        state_name = state.get("state")
        if state.get("error"):
            return state
        if state_name == "not-started":
            return {"ready": False, "state": state_name, "error": f"{client.title()} has not started the unity-game MCP server."}
        if state_name == "started":
            return {"ready": False, "state": state_name, "error": f"Unity game MCP started, but {client.title()} did not complete its handshake."}
        if state_name == "other-project":
            return {"ready": False, "state": state_name, "error": "Unity game MCP was started for another Unity project."}
        return {"ready": False, "state": str(state_name), "error": f"Unity game MCP is not connected to {client.title()}."}
    return {"ready": False, "state": "not-connected", "clients": states, "error": "No connected unity-game MCP client was found."}


def version_status(project: Path) -> dict[str, Any]:
    client_version, client_revision = local_plugin_identity()
    project_version, project_revision = project_plugin_identity(project)
    server = None
    try:
        config = read_server(project)
        server = config.get("bridgeVersion")
    except RuntimeError:
        pass
    mcp = mcp_status(project)
    result = {
        "client": client_version,
        "project": project_version,
        "server": server or "stopped",
        "mcp": mcp.get("state", "not-connected"),
    }
    if client_revision != project_revision or (server not in {None, project_version}):
        result["match"] = False
    else:
        result["match"] = True
    return result


def process_exists(pid: int) -> bool:
    if os.name == "nt":
        import ctypes

        process_query_limited_information = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(process_query_limited_information, False, pid)
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


def invoke(project: Path, operation: str, arguments: dict[str, Any]) -> dict[str, Any]:
    local_version, local_revision = local_plugin_identity()
    project_version, project_revision = project_plugin_identity(project)
    if project_revision != local_revision:
        project_manifest = project / "Assets" / "UnityAgentBridge" / "CodexPlugin~" / "unity-agent-bridge" / "codex-plugin" / "plugin.json"
        installed_manifest = installed_plugin_manifest()
        raise RuntimeError(
            "Unity Agent Bridge plugin mismatch: "
            f"Codex has {local_version}, Unity project has {project_version}. "
            f"Installed manifest: {installed_manifest}. Project manifest: {project_manifest}. "
            "Install the project copy and open a new chat."
        )
    payload = json.dumps({"operation": operation, **arguments}, ensure_ascii=False).encode("utf-8")
    deadline = time.monotonic() + SERVER_RESTART_WAIT_SECONDS
    while True:
        config = read_server(project, max(0.0, deadline - time.monotonic()))
        running_revision = config.get("bridgeRevision")
        if running_revision is not None and running_revision != project_revision:
            raise RuntimeError(
                "Unity Agent Bridge server is outdated: "
                f"server has {config.get('bridgeVersion', 'unknown')}, project has {project_version}. "
                "Restart the server in Unity."
            )
        request = urllib.request.Request(
            f"http://127.0.0.1:{config['port']}/invoke",
            data=payload,
            headers={"Content-Type": "application/json", "X-Unity-Agent-Token": config["token"]},
            method="POST",
        )
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open(request, timeout=360) as response:
                result = json.loads(response.read().decode("utf-8"))
            break
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Bridge HTTP {error.code}: {detail}") from error
        except (urllib.error.URLError, OSError) as error:
            refused = isinstance(getattr(error, "reason", error), ConnectionRefusedError)
            if (refused or operation in READ_ONLY_OPERATIONS) and time.monotonic() < deadline:
                time.sleep(0.15)
                continue
            if operation not in READ_ONLY_OPERATIONS:
                raise RuntimeError(
                    "Bridge restarted while applying the command; its result is unknown. Check the target before repeating it."
                ) from None
            reason = error.reason if isinstance(error, urllib.error.URLError) else error
            raise RuntimeError(f"Bridge server is unreachable: {reason}") from None
    result.pop("pluginVersion", None)
    result.pop("pluginRevision", None)
    if not result.get("ok", False):
        raise RuntimeError(result.get("error", "Bridge operation failed without an error message."))
    if operation == "health":
        result["clientVersion"] = local_version
        result["bridgeVersion"] = project_version
    if operation in {"refresh", "compile"}:
        wait_for_refresh(project, result)
        if operation == "compile":
            play_stopped = result.get("playStopped") is True
            errors = current_compilation_errors(project)
            # Shaders compile on import too: the ones with errors (Shader Graph, .hlsl includes) come along.
            shader_errors = invoke(project, "shader-errors", {}).get("shaders") or []
            result.clear()
            result.update({"ok": True, "compiled": not errors, "errors": errors})
            if shader_errors:
                result["shaderErrors"] = shader_errors[:10]
            if play_stopped:
                result["playStopped"] = True
    return result


def wait_for_refresh(project: Path, result: dict[str, Any]) -> None:
    marker_value = result.pop("refreshMarker", None)
    if not isinstance(marker_value, str) or not marker_value:
        raise RuntimeError("Bridge did not return the Asset refresh marker.")
    marker = Path(marker_value)
    marker.write_text("acknowledged", encoding="utf-8")
    deadline = time.monotonic() + 360.0
    try:
        while time.monotonic() < deadline:
            if marker.is_file():
                state = marker.read_text(encoding="utf-8")
                if state == "complete":
                    result["message"] = "Assets refreshed."
                    return
                if state.startswith("error:"):
                    raise RuntimeError(state[6:])
            time.sleep(0.1)
        raise RuntimeError("Unity did not finish refreshing Assets within 360 seconds.")
    finally:
        marker.unlink(missing_ok=True)


def current_compilation_errors(project: Path) -> list[dict[str, str]]:
    path = project / "Library" / "UnityAgentBridge" / "CurrentCompilationErrors.json"
    try:
        entries = json.loads(path.read_text(encoding="utf-8")).get("entries", [])
    except (FileNotFoundError, OSError, AttributeError, json.JSONDecodeError):
        return []
    if not isinstance(entries, list):
        return []
    result = []
    for entry in entries[-20:]:
        if not isinstance(entry, dict):
            continue
        result.append({
            "message": str(entry.get("message", "")),
            "location": str(entry.get("stackTrace", "")),
        })
    return result


def local_plugin_identity() -> tuple[str, str]:
    manifest = installed_plugin_manifest()
    try:
        version = json.loads(manifest.read_text(encoding="utf-8"))["version"]
    except (KeyError, TypeError, json.JSONDecodeError, OSError) as error:
        raise RuntimeError(f"Installed Unity Agent Bridge plugin manifest is invalid: {manifest}") from error
    if not isinstance(version, str) or not version:
        raise RuntimeError(f"Installed Unity Agent Bridge plugin version is invalid: {manifest}")
    return version, plugin_revision(version)


def installed_plugin_manifest() -> Path:
    plugin_root = Path(__file__).resolve().parents[3]
    manifests = (
        plugin_root / ".codex-plugin" / "plugin.json",
        plugin_root / ".claude-plugin" / "plugin.json",
        plugin_root / "codex-plugin" / "plugin.json",
    )
    for manifest in manifests:
        if not manifest.is_file():
            continue
        return manifest
    raise RuntimeError(f"Installed Unity Agent Bridge plugin manifest is missing below: {plugin_root}")


def project_plugin_identity(project: Path) -> tuple[str, str]:
    manifest = project / "Assets" / "UnityAgentBridge" / "CodexPlugin~" / "unity-agent-bridge" / "codex-plugin" / "plugin.json"
    try:
        version = json.loads(manifest.read_text(encoding="utf-8"))["version"]
    except (FileNotFoundError, KeyError, TypeError, json.JSONDecodeError, OSError) as error:
        raise RuntimeError("Unity project has no valid Unity Agent Bridge plugin manifest.") from error
    if not isinstance(version, str) or not version:
        raise RuntimeError("Unity project has an invalid Unity Agent Bridge plugin version.")
    return version, plugin_revision(version)


def plugin_revision(version: str) -> str:
    _base, separator, suffix = version.partition("+")
    product, dot, revision = suffix.partition(".")
    if not separator or product not in {"codex", "claude"} or not dot or not revision:
        raise RuntimeError("Installed Unity Agent Bridge plugin version has no revision.")
    return revision


def set_value_json(path: str, value: str) -> str:
    try:
        json.loads(value)
        return value
    except json.JSONDecodeError:
        pass
    stripped = value.strip()
    # A list of assets or objects typed without quotes: [Assets/A.mat, Assets/B.mat].
    if stripped.startswith("[") and stripped.endswith("]") and "{" not in stripped:
        items = [item.strip() for item in stripped[1:-1].split(",") if item.strip()]
        return json.dumps([json_or_text(item) for item in items], ensure_ascii=False)
    if stripped.startswith(("{", "[", '"')) or stripped in {"true", "false", "null"}:
        raise ValueError(f"Invalid JSON value for --set {path}: {value}")
    return json.dumps(value, ensure_ascii=False)


def json_or_text(value: str) -> Any:
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def property_values(entries: list[list[str]] | None) -> list[dict[str, str]]:
    values: list[dict[str, str]] = []
    for entry in entries or []:
        entry = [part for item in entry for part in split_compound_set(item)]
        merged: list[str] = []
        for item in entry:
            if merged and "=" in merged[-1] and re.fullmatch(r"-?\d+(\.\d+)?", item):
                merged[-1] += "," + item
                continue
            # A curve or gradient typed unquoted ("size=0:0 1:1", "color=0:#FF0000 1:#0000FF / 0:1 1:0") comes in pieces.
            if merged and ":" in merged[-1].partition("=")[2] and "=" not in item:
                merged[-1] += " " + item
                continue
            key, separator, value = item.partition("=")
            if separator and re.fullmatch(r"-?\d+(\.\d+)?(\s+-?\d+(\.\d+)?)+", value.strip()):
                item = key + "=" + ",".join(value.split())
            merged.append(item)
        entry = merged
        if entry and all("=" in item for item in entry):
            pairs = [item.split("=", 1) for item in entry]
            for path, value in pairs:
                if not path:
                    raise ValueError("--set property path cannot be empty.")
                values.append({"path": path, "value": set_value_json(path, value)})
            continue
        index = 0
        while index < len(entry):
            path = entry[index]
            index += 1
            if not path or index >= len(entry):
                raise ValueError("--set requires PROPERTY VALUE, PROPERTY=VALUE, or PROPERTY x=VALUE y=VALUE.")
            if "=" in entry[index]:
                fields: dict[str, Any] = {}
                while index < len(entry) and "=" in entry[index]:
                    key, field_value = entry[index].split("=", 1)
                    if not key:
                        raise ValueError("--set object field cannot be empty.")
                    try:
                        fields[key] = json.loads(field_value)
                    except json.JSONDecodeError:
                        fields[key] = field_value
                    index += 1
                value = json.dumps(fields, ensure_ascii=False, separators=(",", ":"))
            else:
                value = set_value_json(path, entry[index])
                index += 1
            values.append({"path": path, "value": value})
    return values


def appended_property_values(entries: list[list[str]] | None) -> list[dict[str, Any]]:
    values = property_values(entries)
    for value in values:
        value["append"] = True
    return values


def removed_property_values(entries: list[list[str]] | None) -> list[dict[str, Any]]:
    values: list[dict[str, Any]] = []
    for entry in entries or []:
        if len(entry) != 2:
            raise ValueError("--remove requires UNITY_EVENT INDEX.")
        try:
            index = int(entry[1])
        except ValueError as error:
            raise ValueError("UnityEvent listener index must be an integer.") from error
        if index < 0:
            raise ValueError("UnityEvent listener index cannot be negative.")
        values.append({"path": entry[0], "value": str(index), "remove": True})
    return values


def component_target(path: str, component: str | None, index: int, required: bool = True) -> tuple[str, str | None, int]:
    match = re.fullmatch(r"(.+)#([^#\[\]]+)\[(\d+)\]", path) or re.fullmatch(r"(.+)#([A-Za-z_][\w.]*)()", path)
    if match:
        reference_component = urllib.parse.unquote(match.group(2))
        reference_index = int(match.group(3)) if match.group(3) else index
        if component and component.rsplit(".", 1)[-1].casefold() != reference_component.rsplit(".", 1)[-1].casefold():
            raise ValueError("--component conflicts with the component reference in --path.")
        if index >= 0 and index != reference_index:
            raise ValueError("--component-index conflicts with the component reference in --path.")
        return match.group(1), reference_component, reference_index
    if required and not component:
        raise ValueError("Provide --component or use --path OBJECT#TYPE[INDEX].")
    return path, component, index


# Several --path are a multi-selection: the same component on every object.
def component_targets(paths: list[str], component: str | None, index: int, required: bool = True) -> tuple[list[str], str | None, int]:
    targets = [component_target(path, component, index, required) for path in paths]
    if len({(item[1] or "").rsplit(".", 1)[-1].casefold() for item in targets}) > 1 or len({item[2] for item in targets}) > 1:
        raise ValueError("Every --path must address the same component.")
    return [item[0] for item in targets], targets[0][1], targets[0][2]


SET_KEY = re.compile(r"(?:[^\W\d_a-zа-яё][\w.\[\]:#-]*\s+)*[\w.\[\]:#-]+=(?!=)")


def split_compound_set(value: str) -> list[str]:
    parts: list[str] = []
    start = 0
    depth = 0
    quote = ""
    escaped = False
    for index, character in enumerate(value):
        if escaped:
            escaped = False
            continue
        if character == "\\" and quote:
            escaped = True
            continue
        if quote:
            if character == quote:
                quote = ""
            continue
        if character in {'"', "'"}:
            quote = character
        elif character in "[{(":
            depth += 1
        elif character in "]})":
            depth = max(0, depth - 1)
        elif character.isspace() and depth == 0:
            remainder = value[index:].lstrip()
            part = value[start:index].strip()
            # Inspector labels start with a capital and may go on in lower case ("Surface Type=", "Receive fog="); fields of a compound
            # value follow a camelCase property ("position x=", "onClick target="). Code operators ("h ^= h >> 16") are not keys.
            if "=" in part and SET_KEY.match(remainder) or not re.search(r"\s", part) and part[0].islower() and re.match(r"[\w.\[\]:#-]+=(?!=)", remainder):
                if part:
                    parts.append(part)
                start = len(value) - len(remainder)
    tail = value[start:].strip()
    if tail:
        parts.append(tail)
    return parts or [value]


def add_set_argument(command: argparse.ArgumentParser, required: bool = False) -> None:
    command.add_argument("--set", action="append", nargs="+", required=required)


def json_value(value: str | None, expected: type, label: str, empty: Any) -> Any:
    if value is None:
        return empty
    parsed = json.loads(value)
    if not isinstance(parsed, expected):
        raise ValueError(f"{label} must contain JSON {expected.__name__}.")
    return parsed


def parser() -> argparse.ArgumentParser:
    result = CompactArgumentParser(prog="uab.ps1")
    commands = result.add_subparsers(dest="command", required=True)
    result.commands = commands
    commands.add_parser("health")
    commands.add_parser("mcp-status")
    commands.add_parser("scene-save")
    commands.add_parser("refresh")
    commands.add_parser("compile")
    tree = commands.add_parser("tree")
    tree.add_argument("--path", "--root", dest="path")
    tree.add_argument("--depth", type=int)
    children = commands.add_parser("object-children")
    children.add_argument("--path", required=True)

    find = commands.add_parser("object-find", aliases=("find",))
    find.add_argument("--name", "--query", dest="query")
    find.add_argument("--path")
    find.add_argument("--component")
    find.add_argument("--ref")
    find.add_argument("--near")
    find.add_argument("--radius", type=float)
    find.add_argument("--limit", type=int, default=10)
    find.add_argument("--offset", type=int, default=0)
    asset_find = commands.add_parser("asset-find")
    asset_find.add_argument("--name", "--query", dest="query")
    asset_find.add_argument("--type")
    asset_find.add_argument("--path")
    asset_find.add_argument("--ref")
    asset_find.add_argument("--limit", type=int, default=10)
    asset_find.add_argument("--offset", type=int, default=0)

    info = commands.add_parser("object-info")
    info.add_argument("--path", action="append", required=True)
    info.add_argument("--component")
    info.add_argument("--component-index", type=int, default=-1)
    info.add_argument("--property")
    info.add_argument("--runtime", action="store_true")

    modify = commands.add_parser("component-modify")
    modify.add_argument("--path", action="append", required=True)
    modify.add_argument("--component")
    modify.add_argument("--component-index", type=int, default=-1)
    add_set_argument(modify)
    modify.add_argument("--append", action="append", nargs="+")
    modify.add_argument("--remove", action="append", nargs=2)

    add = commands.add_parser("component-add")
    add.add_argument("--path", required=True)
    add.add_argument("--component", required=True)
    add_set_argument(add)

    remove = commands.add_parser("component-remove")
    remove.add_argument("--path", required=True)
    remove.add_argument("--component")
    remove.add_argument("--component-index", type=int, default=-1)

    component_action = commands.add_parser("component-action")
    component_action.add_argument("--path", required=True)
    component_action.add_argument("--component")
    component_action.add_argument("--component-index", type=int, default=-1)
    component_action.add_argument("--action", required=True)
    add_set_argument(component_action)

    asset_action = commands.add_parser("asset-action")
    asset_action.add_argument("--path", required=True)
    asset_action.add_argument("--action", required=True)
    add_set_argument(asset_action)

    suggest = commands.add_parser("component-suggest")
    suggest.add_argument("--path", required=True)
    suggest.add_argument("--component", required=True)
    suggest.add_argument("--query", required=True)

    picker = commands.add_parser("object-picker")
    picker.add_argument("--path", required=True)
    picker.add_argument("--component")
    picker.add_argument("--component-index", type=int, default=-1)
    picker.add_argument("--property", required=True)

    delete = commands.add_parser("object-delete")
    delete.add_argument("--path", required=True)
    duplicate = commands.add_parser("object-duplicate")
    duplicate.add_argument("--path", required=True)
    duplicate.add_argument("--name")
    create_object = commands.add_parser("object-create")
    create_object.add_argument("--parent", required=True)
    create_object.add_argument("--name")
    create_object.add_argument("--template")
    object_templates = commands.add_parser("object-templates")
    object_templates.add_argument("--query")
    save_prefab = commands.add_parser("prefab-save")
    save_prefab.add_argument("--path")
    save_prefab.add_argument("--prefab")
    apply_prefab = commands.add_parser("prefab-apply")
    apply_prefab.add_argument("--path", required=True)
    apply_prefab.add_argument("--component")
    apply_prefab.add_argument("--component-index", type=int, default=-1)
    apply_prefab.add_argument("--property")
    instantiate_prefab = commands.add_parser("prefab-instantiate")
    instantiate_prefab.add_argument("--prefab", required=True)
    instantiate_prefab.add_argument("--parent", required=True)
    instantiate_prefab.add_argument("--name")
    add_set_argument(instantiate_prefab)
    revert_prefab = commands.add_parser("prefab-revert")
    revert_prefab.add_argument("--path", required=True)
    revert_prefab.add_argument("--component")
    revert_prefab.add_argument("--component-index", type=int, default=-1)
    revert_prefab.add_argument("--property")
    open_prefab = commands.add_parser("prefab-open")
    open_prefab.add_argument("--prefab", required=True)
    commands.add_parser("prefab-close")
    move = commands.add_parser("object-move")
    move.add_argument("--path", required=True)
    move.add_argument("--parent", "--destination", dest="destination", required=True)
    move.add_argument("--index", type=int)
    rename = commands.add_parser("object-rename")
    rename.add_argument("--path", required=True)
    rename.add_argument("--name", "--destination", dest="destination", required=True)
    active = commands.add_parser("object-active")
    active.add_argument("--path", required=True)
    active.add_argument("--active", choices=("true", "false"), required=True)
    tag = commands.add_parser("object-tag")
    tag.add_argument("--path", required=True)
    tag.add_argument("--tag", required=True)
    layer = commands.add_parser("object-layer")
    layer.add_argument("--path", required=True)
    layer.add_argument("--layer", required=True)
    layer.add_argument("--children", action="store_true")
    static = commands.add_parser("object-static")
    static.add_argument("--path", required=True)
    static.add_argument("--static", required=True)
    static.add_argument("--children", action="store_true")

    logs = commands.add_parser("logs")
    logs.add_argument("--query")
    logs.add_argument("--level", type=str.casefold, choices=("error", "assert", "warning", "log", "exception"))
    logs.add_argument("--since", choices=("play", "compile"))
    logs.add_argument("--since-minutes", type=float)
    logs.add_argument("--limit", "--count", dest="limit", type=int, default=20)
    logs.add_argument("--stacktrace", action="store_true")
    logs.add_argument("--clear", action="store_true")
    commands.add_parser("status")
    commands.add_parser("state")
    commands.add_parser("version")
    play = commands.add_parser("play")
    play.add_argument("action", choices=("start", "stop", "status", "pause", "resume", "step"))
    play.add_argument("--frames", type=int, default=1)
    menu = commands.add_parser("menu")
    menu_source = menu.add_mutually_exclusive_group()
    menu_source.add_argument("--query")
    menu_source.add_argument("--path")
    lighting = commands.add_parser("lighting")
    lighting.add_argument("action", choices=("bake", "cancel", "clear"))
    profiler_hierarchy = commands.add_parser("profiler-hierarchy")
    profiler_hierarchy.add_argument("--frame", default="worst")
    profiler_hierarchy.add_argument("--thread")
    profiler_hierarchy.add_argument("--query")
    profiler_hierarchy.add_argument("--path")
    profiler_hierarchy.add_argument("--sort", choices=("total", "self", "gc", "calls"))
    profiler_hierarchy.add_argument("--limit", type=int, default=10)
    commands.add_parser("game-resolutions")
    game_resolution = commands.add_parser("game-resolution")
    game_resolution.add_argument("--width", type=int, required=True)
    game_resolution.add_argument("--height", type=int, required=True)
    game_actions = commands.add_parser("game-actions")
    action_source = game_actions.add_mutually_exclusive_group(required=True)
    action_source.add_argument("--actions")
    action_source.add_argument("--actions-file", type=Path)
    game_actions.add_argument("--profile", action="store_true")

    commands.add_parser("packages")
    commands.add_parser("packages-refresh")
    package_search = commands.add_parser("packages-search")
    package_search.add_argument("--query", required=True)
    for package_command in ("package-install", "package-update"):
        package = commands.add_parser(package_command)
        package.add_argument("--name", nargs="+", required=True)
        package.add_argument("--version")
    package_remove = commands.add_parser("package-remove")
    package_remove.add_argument("--name", nargs="+", required=True)

    commands.add_parser("input-axes")
    input_axis_create = commands.add_parser("input-axis-create")
    input_axis_create.add_argument("--name", required=True)
    add_set_argument(input_axis_create)
    input_axis_delete = commands.add_parser("input-axis-delete")
    input_axis_delete.add_argument("--name", required=True)
    commands.add_parser("build-scenes")
    build_scene = commands.add_parser("build-scene")
    build_scene.add_argument("action", choices=("add", "remove", "enable", "disable", "move"))
    build_scene.add_argument("--path", required=True)
    build_scene.add_argument("--index", type=int, default=-1)

    scene_open = commands.add_parser("scene-open")
    scene_open.add_argument("--scene", "--path", dest="scene", required=True)

    creation_templates = commands.add_parser("creation-templates")
    creation_templates.add_argument("--query")
    creation_templates.add_argument("--path")
    asset_create = commands.add_parser("asset-create")
    asset_create.add_argument("--template", required=True)
    asset_create.add_argument("--path", required=True)
    asset_create.add_argument("--source")
    asset_info = commands.add_parser("asset-info")
    asset_info.add_argument("--path", required=True)
    asset_info.add_argument("--property")
    asset_info.add_argument("--section", choices=("model",))
    asset_modify = commands.add_parser("asset-modify")
    asset_modify.add_argument("--path", required=True)
    add_set_argument(asset_modify)
    asset_modify.add_argument("--code")
    asset_modify.add_argument("--file")
    asset_modify.add_argument("--confirm", action="store_true")
    eval_command = commands.add_parser("eval")
    eval_command.add_argument("--missing", required=True)
    eval_source = eval_command.add_mutually_exclusive_group(required=True)
    eval_source.add_argument("--code")
    eval_source.add_argument("--file")
    shader_info = commands.add_parser("shader-info")
    shader_info.add_argument("--path")
    shader_info.add_argument("--property")
    material_modify = commands.add_parser("material-modify")
    material_modify.add_argument("--path", required=True)
    add_set_argument(material_modify, required=True)
    asset_reimport = commands.add_parser("asset-reimport")
    asset_reimport.add_argument("--path", required=True)
    asset_reimport.add_argument("--as-sprite", action="store_true")
    asset_import_package = commands.add_parser("asset-import-package")
    asset_import_package.add_argument("--path", required=True)
    asset_move = commands.add_parser("asset-move")
    asset_move.add_argument("--path", required=True)
    asset_move.add_argument("--destination", required=True)
    asset_duplicate = commands.add_parser("asset-duplicate")
    asset_duplicate.add_argument("--path", required=True)
    asset_duplicate.add_argument("--destination")
    asset_delete = commands.add_parser("asset-delete")
    asset_delete.add_argument("--path", required=True)
    asset_picker = commands.add_parser("asset-object-picker")
    asset_picker.add_argument("--path", required=True)
    asset_picker.add_argument("--property", required=True)

    animation_table = commands.add_parser("animation-table")
    animation_table.add_argument("--query")
    animation_table.add_argument("--path")
    clip_info = commands.add_parser("clip-info", aliases=("animation-clip-info",))
    clip_info.add_argument("--query")
    clip_info.add_argument("--path")
    animation_properties = commands.add_parser("animation-properties")
    animation_properties.add_argument("--path", required=True)
    animation_properties.add_argument("--query")
    animation_clip_create = commands.add_parser("animation-clip-create")
    animation_clip_create.add_argument("--name")
    animation_clip_create.add_argument("--path", required=True)
    add_set_argument(animation_clip_create)
    animation_clip_delete = commands.add_parser("animation-clip-delete")
    animation_clip_delete.add_argument("--path", "--anim", dest="path", required=True)
    animation_property = commands.add_parser("animation-property")
    animation_property.add_argument("action", choices=("get", "create", "modify", "delete"))
    animation_property.add_argument("--path", "--anim", dest="path", required=True)
    animation_property.add_argument("--object-path", default="")
    animation_property.add_argument("--property")
    animation_property.add_argument("--keys")
    animation_property.add_argument("--key", action="append", nargs="+")
    animation_setting = commands.add_parser("animation-clip-setting")
    animation_setting.add_argument("action", choices=("get", "set"), nargs="?")
    animation_setting.add_argument("--path", "--anim", dest="path", required=True)
    animation_setting.add_argument("--parameter")
    animation_setting.add_argument("--value")
    add_set_argument(animation_setting)

    animator_find = commands.add_parser("animator-info", aliases=("animator-find",))
    animator_find_source = animator_find.add_mutually_exclusive_group(required=True)
    animator_find_source.add_argument("--path")
    animator_find_source.add_argument("--controller")
    animator_find_source.add_argument("--query")
    animator_component = commands.add_parser("animator-component")
    animator_component.add_argument("action", choices=("create", "delete"))
    animator_component.add_argument("--path", required=True)
    animator_component.add_argument("--controller")
    animator_assign = commands.add_parser("animator-controller-assign")
    animator_assign.add_argument("action", choices=("assign", "detach"))
    animator_assign.add_argument("--path", required=True)
    animator_assign.add_argument("--controller")
    animator_motions = commands.add_parser("animator-motions")
    animator_motions_source = animator_motions.add_mutually_exclusive_group(required=True)
    animator_motions_source.add_argument("--path")
    animator_motions_source.add_argument("--controller")
    animator_motions_source.add_argument("--query")
    animator_graph = commands.add_parser("animator-graph")
    animator_graph_source = animator_graph.add_mutually_exclusive_group(required=True)
    animator_graph_source.add_argument("--path")
    animator_graph_source.add_argument("--controller")

    animator_state = commands.add_parser("animator-state")
    animator_state.add_argument("action", choices=("create", "modify", "delete"))
    animator_state.add_argument("--controller", required=True)
    animator_state.add_argument("--layer", required=True)
    animator_state.add_argument("--state", required=True)
    animator_state.add_argument("--state-machine")
    animator_state.add_argument("--motion")
    add_set_argument(animator_state)
    animator_state_motion = commands.add_parser("animator-state-motion")
    animator_state_motion.add_argument("action", choices=("assign", "detach"))
    animator_state_motion.add_argument("--controller", required=True)
    animator_state_motion.add_argument("--layer", required=True)
    animator_state_motion.add_argument("--state", required=True)
    animator_state_motion.add_argument("--state-machine")
    animator_state_motion.add_argument("--motion")
    animator_transition = commands.add_parser("animator-transition")
    animator_transition.add_argument("action", choices=("create", "modify", "delete"))
    animator_transition.add_argument("--controller", required=True)
    animator_transition.add_argument("--layer", required=True)
    animator_transition.add_argument("--from", dest="from_state", required=True)
    animator_transition.add_argument("--to", dest="to_state", required=True)
    animator_transition.add_argument("--state-machine")
    animator_transition.add_argument("--transition-index", type=int, default=-1)
    add_set_argument(animator_transition)
    animator_transition.add_argument("--conditions")
    animator_parameter = commands.add_parser("animator-parameter")
    animator_parameter.add_argument("action", choices=("create", "modify", "delete"))
    animator_parameter.add_argument("--controller", required=True)
    animator_parameter.add_argument("--name", required=True)
    animator_parameter.add_argument("--type", choices=("Float", "Int", "Bool", "Trigger"))
    animator_parameter.add_argument("--value")
    animator_parameter.add_argument("--new-name")
    animator_layer = commands.add_parser("animator-layer")
    animator_layer.add_argument("action", choices=("create", "modify", "delete"))
    animator_layer.add_argument("--controller", required=True)
    animator_layer.add_argument("--layer", required=True)
    add_set_argument(animator_layer)
    animator_state_machine = commands.add_parser("animator-state-machine")
    animator_state_machine.add_argument("action", choices=("create", "modify", "delete"))
    animator_state_machine.add_argument("--controller", required=True)
    animator_state_machine.add_argument("--layer", required=True)
    animator_state_machine.add_argument("--name", required=True)
    animator_state_machine.add_argument("--parent")
    animator_state_machine.add_argument("--new-name")
    animator_blend_tree = commands.add_parser("animator-blend-tree")
    animator_blend_tree.add_argument("action", choices=("create", "modify", "delete"))
    animator_blend_tree.add_argument("--controller", required=True)
    animator_blend_tree.add_argument("--layer", required=True)
    animator_blend_tree.add_argument("--state", required=True)
    animator_blend_tree.add_argument("--name", required=True)
    animator_blend_tree.add_argument("--state-machine")
    animator_blend_tree.add_argument("--settings")
    animator_control = commands.add_parser("animator-control")
    animator_control.add_argument("--path", required=True)
    animator_control.add_argument("--state")
    animator_control.add_argument("--layer")
    add_set_argument(animator_control)
    animator_runtime = commands.add_parser("animator-runtime-state")
    animator_runtime.add_argument("--path", required=True)

    timeline_info = commands.add_parser("timeline-info")
    timeline_info_source = timeline_info.add_mutually_exclusive_group(required=True)
    timeline_info_source.add_argument("--path")
    timeline_info_source.add_argument("--timeline")
    timeline_info_source.add_argument("--query")
    timeline_track = commands.add_parser("timeline-track")
    timeline_track.add_argument("action", choices=("create", "modify", "delete"))
    timeline_track.add_argument("--path")
    timeline_track.add_argument("--timeline")
    timeline_track.add_argument("--track", required=True)
    timeline_track.add_argument("--type")
    timeline_track.add_argument("--parent")
    add_set_argument(timeline_track)
    timeline_clip = commands.add_parser("timeline-clip")
    timeline_clip.add_argument("action", choices=("create", "modify", "delete"))
    timeline_clip.add_argument("--path")
    timeline_clip.add_argument("--timeline")
    timeline_clip.add_argument("--track", required=True)
    timeline_clip.add_argument("--clip")
    timeline_clip.add_argument("--asset")
    add_set_argument(timeline_clip)
    timeline_marker = commands.add_parser("timeline-marker")
    timeline_marker.add_argument("action", choices=("create", "modify", "delete"))
    timeline_marker.add_argument("--path")
    timeline_marker.add_argument("--timeline")
    timeline_marker.add_argument("--track")
    timeline_marker.add_argument("--marker", type=int)
    timeline_marker.add_argument("--type")
    add_set_argument(timeline_marker)
    return result


def build_operation(args: argparse.Namespace) -> tuple[str, dict[str, Any]]:
    command = args.command
    if command in {"health", "status", "scene-save", "refresh", "compile", "game-resolutions", "packages", "packages-refresh", "input-axes", "build-scenes"}:
        return command, {}
    if command == "state":
        return "status", {}
    if command == "tree":
        return command, {"path": args.path, "depth": args.depth}
    if command == "object-children":
        return command, {"path": args.path}
    if command in {"find", "object-find"}:
        if not args.query and not args.component and not args.ref and not args.near:
            raise ValueError("object-find requires --name, --component, --ref or --near.")
        if args.radius is not None and not args.near:
            raise ValueError("--radius requires --near.")
        return "find", {"query": args.query, "path": args.path, "componentType": args.component, "ref": args.ref, "near": args.near, "radius": args.radius, "limit": args.limit, "offset": args.offset}
    if command == "asset-find":
        if not (args.query or args.type or args.path or args.ref):
            raise ValueError("asset-find requires --name, --type, --path or --ref.")
        return command, {"query": args.query, "type": args.type, "path": args.path, "ref": args.ref, "limit": args.limit, "offset": args.offset}
    if command == "object-info":
        paths, component, component_index = component_targets(args.path, args.component, args.component_index, False)
        return command, {
            "path": paths[0],
            "paths": paths,
            "componentType": component,
            "componentIndex": component_index,
            "propertyPath": args.property,
            "runtime": args.runtime,
        }
    if command == "component-modify":
        paths, component, component_index = component_targets(args.path, args.component, args.component_index)
        values = property_values(args.set) + appended_property_values(args.append) + removed_property_values(args.remove)
        if not values:
            raise ValueError("component-modify requires --set, --append or --remove.")
        return command, {"path": paths[0], "paths": paths, "componentType": component, "componentIndex": component_index, "values": values}
    if command == "component-add":
        return command, {"path": args.path, "componentType": args.component, "values": property_values(args.set)}
    if command == "component-remove":
        path, component, component_index = component_target(args.path, args.component, args.component_index)
        return command, {"path": path, "componentType": component, "componentIndex": component_index}
    if command == "component-action":
        path, component, component_index = component_target(args.path, args.component, args.component_index)
        return command, {
            "path": path,
            "componentType": component,
            "componentIndex": component_index,
            "action": args.action,
            "values": property_values(args.set),
        }
    if command == "asset-action":
        return command, {"path": args.path, "action": args.action, "values": property_values(args.set)}
    if command == "component-suggest":
        return command, {"path": args.path, "componentName": args.component, "query": args.query}
    if command == "object-picker":
        path, component, component_index = component_target(args.path, args.component, args.component_index)
        return command, {"path": path, "componentType": component, "componentIndex": component_index, "propertyPath": args.property}
    if command == "object-delete":
        return command, {"path": args.path}
    if command == "object-duplicate":
        return command, {"path": args.path, "name": args.name}
    if command == "object-create":
        if not args.name and not args.template:
            raise ValueError("object-create requires --name or --template.")
        return command, {"parentPath": args.parent, "name": args.name, "template": args.template}
    if command == "object-templates":
        return command, {"query": args.query}
    if command == "prefab-save":
        return command, {"path": args.path, "prefab": args.prefab}
    if command == "prefab-apply":
        if (args.property or args.component_index >= 0) and not args.component:
            raise ValueError("--property and --component-index require --component.")
        return command, {
            "path": args.path,
            "componentType": args.component,
            "componentIndex": args.component_index,
            "propertyPath": args.property,
        }
    if command == "prefab-instantiate":
        return command, {"prefab": args.prefab, "parentPath": args.parent, "name": args.name, "values": property_values(args.set)}
    if command == "prefab-revert":
        if (args.property or args.component_index >= 0) and not args.component:
            raise ValueError("--property and --component-index require --component.")
        return command, {
            "path": args.path,
            "componentType": args.component,
            "componentIndex": args.component_index,
            "propertyPath": args.property,
        }
    if command == "prefab-open":
        return command, {"prefab": args.prefab}
    if command == "prefab-close":
        return command, {}
    if command == "object-move":
        return command, {"path": args.path, "destinationPath": args.destination, "siblingIndex": args.index}
    if command == "object-rename":
        return command, {"path": args.path, "destinationPath": args.destination}
    if command == "object-active":
        return command, {"path": args.path, "boolValue": args.active == "true"}
    if command == "object-tag":
        return command, {"path": args.path, "tag": args.tag}
    if command == "object-layer":
        return command, {"path": args.path, "layer": args.layer, "children": args.children}
    if command == "object-static":
        return command, {"path": args.path, "static": args.static, "children": args.children}
    if command == "logs":
        if args.clear:
            if args.query or args.level or args.since or args.since_minutes is not None or args.stacktrace or args.limit != 20:
                raise ValueError("logs --clear cannot be combined with filters.")
            return command, {"clear": True}
        return command, {
            "query": args.query,
            "level": args.level,
            "since": args.since,
            "sinceMinutes": args.since_minutes,
            "limit": args.limit,
            "stackTrace": args.stacktrace,
        }
    if command == "play":
        if args.action == "status":
            return "status", {}
        if args.frames != 1 and args.action != "step":
            raise ValueError("--frames works only with play step.")
        return command, {"action": args.action, "frames": args.frames}
    if command == "menu":
        return command, {"query": args.query, "path": args.path}
    if command == "lighting":
        return command, {"action": args.action}
    if command == "profiler-hierarchy":
        return command, {key: value for key, value in vars(args).items() if key != "command" and value is not None}
    if command == "game-resolution":
        return command, {"width": args.width, "height": args.height}
    if command == "game-actions":
        source = args.actions if args.actions is not None else args.actions_file.read_text(encoding="utf-8")
        actions = json.loads(source)
        if not isinstance(actions, list):
            raise ValueError("Game actions must contain a JSON list.")
        return command, {"actions": actions, "profile": args.profile}
    if command == "packages-search":
        return command, {"query": args.query}
    if command in {"package-install", "package-update", "package-remove"}:
        names = [part.strip() for value in args.name for part in value.split(",") if part.strip()]
        if command == "package-remove":
            return command, {"names": names}
        if len(names) > 1 and args.version:
            raise ValueError("--version can only be used with one package.")
        return command, {"names": names, "version": args.version}
    if command == "input-axis-create":
        return command, {"name": args.name, "values": property_values(args.set)}
    if command == "input-axis-delete":
        return command, {"name": args.name}
    if command == "build-scene":
        if args.action in {"add", "move"} and args.index < -1:
            raise ValueError("--index must be zero or greater.")
        return command, {"action": args.action, "path": args.path, "index": args.index}
    if command == "scene-open":
        return command, {"scene": args.scene}
    if command == "creation-templates":
        return command, {"query": args.query, "path": args.path}
    if command == "asset-create":
        return command, {"template": args.template, "path": args.path, "source": args.source}
    if command == "asset-info":
        if args.property and args.section:
            raise ValueError("Use either --property or --section.")
        return command, {"path": args.path, "propertyPath": args.property, "section": args.section}
    if command == "asset-modify":
        values = property_values(args.set)
        if args.code is not None or args.file:
            # Shader Graph code: the text as one value, read like eval's --code/--file.
            code = Path(args.file).read_text(encoding="utf-8-sig") if args.file else args.code
            values.append({"path": "Code", "value": json.dumps(code, ensure_ascii=False)})
        if not values:
            raise ValueError("asset-modify requires --set, --code or --file.")
        return command, {"path": args.path, "values": values, "confirm": args.confirm}
    if command == "shader-info":
        if not args.path and not args.property:
            raise ValueError("shader-info requires --path, or --property for global shader values.")
        return command, {"path": args.path, "propertyPath": args.property}
    if command == "eval":
        code = Path(args.file).read_text(encoding="utf-8-sig") if args.file else args.code
        if not args.missing.strip() or not code.strip():
            raise ValueError("eval needs --missing: what the plugin lacked, and --code or --file.")
        return command, {"missing": args.missing, "code": code}
    if command == "material-modify":
        return command, {"path": args.path, "values": property_values(args.set)}
    if command == "asset-reimport":
        return command, {"path": args.path, "boolValue": args.as_sprite}
    if command == "asset-import-package":
        return command, {"path": args.path}
    if command == "asset-move":
        return command, {"path": args.path, "destinationPath": args.destination}
    if command == "asset-duplicate":
        return command, {"path": args.path, "destinationPath": args.destination}
    if command == "asset-delete":
        return command, {"path": args.path}
    if command == "asset-object-picker":
        return command, {"path": args.path, "propertyPath": args.property}
    if command in {"animation-table", "animation-clip-info", "clip-info"}:
        if not args.query and not (args.path or "").casefold().endswith(".anim"):
            raise ValueError("--query is required unless --path points to an .anim clip.")
        operation = "animation-table" if command == "animation-table" else "animation-clip-info"
        return operation, {"query": args.query, "path": args.path}
    if command == "animation-properties":
        return command, {"path": args.path, "query": args.query}
    if command == "animation-clip-create":
        return command, {"name": args.name, "path": args.path, "values": property_values(args.set)}
    if command == "animation-clip-delete":
        return command, {"clip": args.path}
    if command == "animation-property":
        if args.action != "get" and not args.property:
            raise ValueError("--property is required for create, modify and delete.")
        if args.keys is not None and args.key:
            raise ValueError("Use either --keys or repeated --key.")
        keys = json_value(args.keys, list, "--keys", [])
        if args.key:
            keys = []
            for group in args.key:
                key = {}
                for token in group:
                    if "=" not in token:
                        raise ValueError("--key values must use FIELD=VALUE.")
                    field, raw = token.split("=", 1)
                    if not field:
                        raise ValueError("--key field is empty.")
                    try:
                        value = json.loads(raw)
                    except json.JSONDecodeError:
                        value = raw
                    key[field] = value
                keys.append(key)
        allowed = {
            "frame", "time", "value", "inTangent", "outTangent",
            "inWeight", "outWeight", "weightedMode", "reference",
            "inSlope", "outSlope",
        }
        if (args.property or "").casefold() in {"events", "event"}:
            allowed = {"frame", "time", "function", "float", "int", "string", "object"}
        for index, key in enumerate(keys):
            if not isinstance(key, dict):
                raise ValueError(f"--keys[{index}] must be an object.")
            unknown = sorted(set(key) - allowed)
            if unknown:
                raise ValueError(f"--keys[{index}] has unknown fields: {', '.join(unknown)}")
            for alias, canonical in (("inSlope", "inTangent"), ("outSlope", "outTangent")):
                if alias not in key:
                    continue
                if canonical in key and key[canonical] != key[alias]:
                    raise ValueError(f"--keys[{index}] contains conflicting {alias} and {canonical}.")
                key[canonical] = key.pop(alias)
        return command, {
            "action": args.action,
            "clip": args.path,
            "objectPath": args.object_path,
            "property": args.property,
            "keys": keys,
        }
    if command == "animation-clip-setting":
        values = property_values(args.set)
        if len(values) > 1:
            raise ValueError("animation-clip-setting accepts one setting per call.")
        if values:
            if args.action == "get" or args.parameter is not None or args.value is not None:
                raise ValueError("Use either --set or get with --parameter.")
            setting = values[0]
            result = {
                "action": "set",
                "clip": args.path,
                "parameter": setting["path"],
                "value": json.loads(setting["value"]),
            }
            return command, result
        action = args.action or "get"
        if not args.parameter:
            raise ValueError("--parameter is required when --set is not used.")
        if action == "set" and args.value is None:
            raise ValueError("--value is required for the legacy set syntax.")
        result = {"action": action, "clip": args.path, "parameter": args.parameter}
        if args.value is not None:
            result["value"] = args.value
        return command, result
    if command in {"animator-info", "animator-find"}:
        controller = args.controller
        if args.path and args.path.replace("\\", "/").startswith("Assets/"):
            controller = args.path
        if controller:
            return "animator-graph", {"path": None, "controller": controller}
        return "animator-find", {"path": args.path, "query": args.query}
    if command == "animator-component":
        return command, {"action": args.action, "path": args.path, "controller": args.controller}
    if command == "animator-controller-assign":
        return command, {"action": args.action, "path": args.path, "controller": args.controller}
    if command == "animator-motions":
        return command, {"path": args.path, "controller": args.controller, "query": args.query}
    if command == "animator-graph":
        return command, {"path": args.path, "controller": args.controller}
    if command == "animator-state":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "state": args.state, "stateMachine": args.state_machine, "motion": args.motion,
            "values": property_values(args.set),
        }
    if command == "animator-state-motion":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "state": args.state, "stateMachine": args.state_machine, "motion": args.motion,
        }
    if command == "animator-transition":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "fromState": args.from_state, "toState": args.to_state,
            "stateMachine": args.state_machine, "transitionIndex": args.transition_index,
            "values": property_values(args.set),
            "conditions": json_value(args.conditions, list, "--conditions", None),
        }
    if command == "animator-parameter":
        return command, {
            "action": args.action, "controller": args.controller, "name": args.name,
            "type": args.type, "value": args.value, "newName": args.new_name,
        }
    if command == "animator-layer":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "values": property_values(args.set),
        }
    if command == "animator-state-machine":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "name": args.name, "parent": args.parent, "newName": args.new_name,
        }
    if command == "animator-blend-tree":
        return command, {
            "action": args.action, "controller": args.controller, "layer": args.layer,
            "state": args.state, "name": args.name, "stateMachine": args.state_machine,
            "settings": json_value(args.settings, dict, "--settings", None),
        }
    if command == "animator-control":
        return command, {
            "path": args.path, "state": args.state, "layer": args.layer,
            "values": property_values(args.set),
        }
    if command == "animator-runtime-state":
        return command, {"path": args.path}
    if command == "timeline-info":
        return command, {"path": args.path, "timeline": args.timeline, "query": args.query}
    if command.startswith("timeline-"):
        arguments = {key: value for key, value in vars(args).items() if key not in {"command", "set"} and value is not None}
        if "set" in vars(args):
            arguments["values"] = property_values(args.set)
        if not (args.path or args.timeline):
            raise ValueError("Provide --path DIRECTOR or --timeline Assets/.../Name.playable.")
        if command == "timeline-clip" and args.action != "create" and not args.clip:
            raise ValueError("--clip NAME|INDEX is required for modify and delete.")
        if command == "timeline-marker" and args.action != "create" and args.marker is None:
            raise ValueError("--marker INDEX is required for modify and delete.")
        return command, arguments
    raise RuntimeError(f"Unsupported client command: {command}")


def concise_result(command: str, arguments: dict[str, Any], result: dict[str, Any]) -> dict[str, Any]:
    if command == "game-actions":
        return {
            **({"profile": result["profile"]} if result.get("profile") else {}),
            "screenshots": result.get("screenshots") or [result.get("screenshot")],
            "times": result.get("times", []),
            "executed": result.get("executedActions", 0),
            "requested": result.get("requestedActions", len(arguments.get("actions", []))),
            **({"sounds": result["sounds"]} if result.get("sounds") else {}),
        }
    if command == "game-resolutions":
        return {"resolutions": result.get("resolutions", [])}
    if command == "game-resolution":
        return {"resolution": result.get("resolution", {})}
    if command in {"package-install", "package-update"}:
        packages = result.get("packages") or []
        return {
            "packages": [
                {
                    "name": package.get("name"),
                    "version": package.get("version"),
                    "minimumUnity": package.get("minimumUnity"),
                    "compatible": package.get("compatible", True),
                }
                for package in packages
            ],
            "dependencyChanges": result.get("dependencyChanges") or [],
        }

    object_info = result.get("objectInfo")
    if command != "object-info" and isinstance(object_info, dict):
        concise: dict[str, Any] = {"ok": True, "path": object_info.get("path")}
        if command == "component-add":
            concise["componentIndex"] = result.get("componentIndex")
        if command == "object-active":
            concise["active"] = object_info.get("activeSelf")
        if result.get("message"):
            concise["message"] = result["message"]
        return concise

    asset_info = result.get("assetInfo")
    if command != "asset-info" and isinstance(asset_info, dict):
        if command == "asset-create":
            concise = {
                "ok": True,
                "asset": {
                    "name": asset_info.get("name"),
                    "path": asset_info.get("assetPath"),
                    "type": asset_info.get("type"),
                },
            }
            if result.get("componentType"):
                concise["componentType"] = result["componentType"]
            if asset_info.get("changes"):
                concise["changes"] = asset_info["changes"]
            return concise
        concise = {"ok": True, "path": asset_info.get("assetPath", arguments.get("path"))}
        if asset_info.get("changes"):
            concise["changes"] = asset_info["changes"]
        return concise

    return result


def main() -> int:
    args = parser().parse_args()
    project = find_project()
    if args.command == "mcp-status":
        print(json.dumps(mcp_status(project), ensure_ascii=False, separators=(",", ":")))
        return 0
    if args.command == "version":
        print(json.dumps(version_status(project), ensure_ascii=False, separators=(",", ":")))
        return 0
    operation, arguments = build_operation(args)
    result = invoke(project, operation, arguments)
    concise = concise_result(args.command, arguments, result)
    if result.get("note"):
        concise["note"] = result["note"]
    print(json.dumps(concise, ensure_ascii=False, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, ValueError, json.JSONDecodeError) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
