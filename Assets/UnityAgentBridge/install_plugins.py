from __future__ import annotations

import argparse
import hashlib
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


PLUGIN_NAME = "unity-agent-bridge"
CODEX_MARKETPLACE = "personal"
CLAUDE_MARKETPLACE = "unity-agent-bridge-local"
ZAI_MARKETPLACE = "unity-agent-bridge-local-zai"
CLAUDE_OUTPUT = "UnityAgentBridge.ClaudeMarketplace"
ZAI_OUTPUT = "UnityAgentBridge.ZaiMarketplace"
IGNORED_NAMES = {"__pycache__"}
IGNORED_SUFFIXES = {".meta", ".pyc", ".pyo"}
NO_WINDOW = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
CLAUDE_WHEN_TO_USE = (
    "Использовать при вопросах о возможностях Unity Agent Bridge и при работе со сценами, объектами, "
    "компонентами, префабами, Assets, пакетами, InputManager, освещением, AnimationClip, Animator, Timeline, "
    "диагностикой, профайлером, пунктами меню, Play Mode, кадрами сцены или игрой."
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Собрать и установить Unity Agent Bridge для Codex, Claude и ZCode.")
    parser.add_argument("--asset-root", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--no-install", action="store_true", help="Только собрать все версии.")
    parser.add_argument("--status", action="store_true", help="Показать точные версии всех копий.")
    target = parser.add_mutually_exclusive_group()
    target.add_argument("--codex-only", action="store_true", help="Установить только Codex.")
    target.add_argument("--claude-only", action="store_true", help="Установить только Claude.")
    target.add_argument("--zai-only", action="store_true", help="Установить только ZCode (Unity server не трогается).")
    parser.add_argument("--detached", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()

    asset_root = args.asset_root.resolve()
    project_root = find_project_root(asset_root)
    codex_source = asset_root / "CodexPlugin~" / PLUGIN_NAME
    claude_overlay = asset_root / "ClaudePlugin~" / PLUGIN_NAME
    zai_overlay = asset_root / "ZaiPlugin~" / PLUGIN_NAME
    require_directory(codex_source, "Основная версия плагина")
    require_directory(claude_overlay, "Отличия Claude")
    require_directory(zai_overlay, "Отличия ZCode")

    if args.status:
        print_status(asset_root, project_root)
        return

    install_codex_requested = not args.no_install and not args.claude_only and not args.zai_only
    install_claude_requested = not args.no_install and not args.codex_only and not args.zai_only
    install_zai_requested = not args.no_install and not args.codex_only and not args.claude_only

    # Stopping Claude Desktop kills its whole process tree, including an installer started from a Claude chat.
    if install_claude_requested and not args.detached and inside_claude_desktop():
        log = project_root / "Library" / "UnityAgentBridge" / "install.log"
        relaunch_detached(log)
        print(f"Claude Desktop перезапустится, установка продолжится отдельно. Лог: {log}")
        return

    if install_codex_requested and codex_desktop_is_running():
        print("Codex запущен: новая версия будет доступна в новом чате.")

    running_server = read_running_server(project_root)
    if not args.no_install and not args.zai_only:
        stop_running_mcp_clients(project_root)

    if args.no_install:
        manifest = read_json(codex_source / "codex-plugin" / "plugin.json")
        codex_version = require_string(manifest.get("version"), "Codex version")
    else:
        cachebuster = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
        codex_version = update_codex_version(codex_source, cachebuster)
    claude_version = codex_version.replace("+codex.", "+claude.")
    zai_version = codex_version.replace("+codex.", "+zai.")

    build_root = project_root / "Library" / "UnityAgentBridge" / "PluginBuild"
    codex_build = build_codex_plugin(
        codex_source, build_root / "Codex" / PLUGIN_NAME, codex_version, project_root
    )
    claude_marketplace = build_claude_marketplace(
        codex_source, claude_overlay, project_root / CLAUDE_OUTPUT, claude_version, project_root
    )
    zai_marketplace = build_zai_marketplace(
        codex_source, zai_overlay, project_root / ZAI_OUTPUT, zai_version, project_root
    )

    if args.no_install:
        print(f"Built Codex {codex_version}, Claude {claude_version} and ZCode {zai_version}")
        return

    if install_codex_requested:
        install_codex(codex_build, codex_version)

    claude_desktop = running_claude_desktop() if install_claude_requested else None
    try:
        if claude_desktop is not None:
            stop_claude_desktop(claude_desktop[1])
        if install_claude_requested:
            install_claude(claude_marketplace, claude_version)
        if not args.zai_only and running_server is not None:
            restart_unity_server(project_root, running_server, codex_version)
        verify_installations(
            codex_build,
            claude_marketplace,
            codex_version,
            claude_version,
            verify_codex=install_codex_requested,
            verify_claude=install_claude_requested,
        )
        time.sleep(2.0)
        verify_installations(
            codex_build,
            claude_marketplace,
            codex_version,
            claude_version,
            verify_codex=install_codex_requested,
            verify_claude=install_claude_requested,
        )
    finally:
        if claude_desktop is not None:
            start_claude_desktop(claude_desktop[0])
    if install_zai_requested:
        install_zai(zai_marketplace, zai_version)

    installed_names = []
    if install_codex_requested:
        installed_names.append(f"Codex {codex_version}")
    if install_claude_requested:
        installed_names.append(f"Claude {claude_version}")
    if install_zai_requested:
        installed_names.append(f"ZCode marketplace {zai_version}")
    print("Installed " + " and ".join(installed_names) + ". Open new chats.")


def update_codex_version(source: Path, cachebuster: str) -> str:
    manifest_path = source / "codex-plugin" / "plugin.json"
    manifest = read_json(manifest_path)
    old_version = require_string(manifest.get("version"), "Codex version")
    base_version = old_version.split("+", 1)[0]
    version = f"{base_version}+codex.{cachebuster}"
    manifest["version"] = version
    write_json(manifest_path, manifest)
    return version


def build_codex_plugin(source: Path, destination: Path, version: str, project_root: Path) -> Path:
    replace_tree(source, destination)
    rename_required(destination / "codex-plugin", destination / ".codex-plugin")
    rename_required(destination / "mcp.json", destination / ".mcp.json")
    configure_mcp(destination / ".mcp.json", "codex", version, project_root)
    validate_codex_plugin(destination)
    return destination


def update_codex_marketplace(path: Path) -> None:
    if path.exists():
        marketplace = read_json(path)
    else:
        marketplace = {"name": CODEX_MARKETPLACE, "interface": {"displayName": "Personal"}, "plugins": []}

    if marketplace.get("name") != CODEX_MARKETPLACE or not isinstance(marketplace.get("plugins"), list):
        raise RuntimeError(f"Неожиданный формат Codex marketplace: {path}")

    entry = {
        "name": PLUGIN_NAME,
        "source": {"source": "local", "path": f"./plugins/{PLUGIN_NAME}"},
        "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"},
        "category": "Developer Tools",
    }
    plugins = marketplace["plugins"]
    for index, plugin in enumerate(plugins):
        if isinstance(plugin, dict) and plugin.get("name") == PLUGIN_NAME:
            plugins[index] = entry
            break
    else:
        plugins.append(entry)
    write_json_atomic(path, marketplace)


def build_claude_marketplace(
    codex_source: Path, claude_overlay: Path, output: Path, version: str, project_root: Path
) -> Path:
    temporary = output.with_name(output.name + ".tmp")
    remove_tree(temporary)
    plugin = temporary / "plugins" / PLUGIN_NAME
    try:
        copy_tree(codex_source, plugin, excluded_roots={"codex-plugin"}, excluded_skill_roots={"agents"})
        copy_tree(claude_overlay, plugin, merge=True)
        rename_required(plugin / "mcp.json", plugin / ".mcp.json")
        rename_required(plugin / "claude-plugin", plugin / ".claude-plugin")
        configure_mcp(plugin / ".mcp.json", "claude", version, project_root)
        configure_claude_skill(plugin / "skills" / PLUGIN_NAME / "SKILL.md")

        manifest_path = plugin / ".claude-plugin" / "plugin.json"
        manifest = read_json(manifest_path)
        manifest["version"] = version
        write_json(manifest_path, manifest)

        marketplace = {
            "name": CLAUDE_MARKETPLACE,
            "owner": {"name": "Slava Morozov"},
            "metadata": {"description": "Unity Agent Bridge для Claude Code."},
            "plugins": [{
                "name": PLUGIN_NAME,
                "source": f"./plugins/{PLUGIN_NAME}",
                "description": manifest["description"],
                "version": version,
            }],
        }
        write_json(temporary / ".claude-plugin" / "marketplace.json", marketplace)
        validate_claude_plugin(plugin)
        sync_directory(temporary, output)
        remove_tree(temporary)
    except BaseException:
        remove_tree(temporary)
        raise
    return output


def build_zai_marketplace(
    codex_source: Path, zai_overlay: Path, output: Path, version: str, project_root: Path
) -> Path:
    temporary = output.with_name(output.name + ".tmp")
    remove_tree(temporary)
    plugin = temporary / "plugins" / PLUGIN_NAME
    try:
        copy_tree(codex_source, plugin, excluded_roots={"codex-plugin"}, excluded_skill_roots={"agents"})
        copy_tree(zai_overlay, plugin, merge=True)
        rename_required(plugin / "mcp.json", plugin / ".mcp.json")
        rename_required(plugin / "zai-plugin", plugin / ".zcode-plugin")
        configure_mcp(plugin / ".mcp.json", "zai", version, project_root)

        manifest_path = plugin / ".zcode-plugin" / "plugin.json"
        manifest = read_json(manifest_path)
        manifest["version"] = version
        manifest["mcpServers"] = read_json(plugin / ".mcp.json")["mcpServers"]
        write_json(manifest_path, manifest)

        marketplace = {
            "name": ZAI_MARKETPLACE,
            "owner": {"name": "Slava Morozov"},
            "metadata": {"description": "Unity Agent Bridge для ZCode."},
            "plugins": [{
                "name": PLUGIN_NAME,
                "source": f"./plugins/{PLUGIN_NAME}",
                "description": manifest["description"],
                "version": version,
                "author": {"name": "Slava Morozov"},
            }],
        }
        write_json(temporary / "marketplace.json", marketplace)
        validate_zai_plugin(plugin)
        sync_directory(temporary, output)
        remove_tree(temporary)
    except BaseException:
        remove_tree(temporary)
        raise
    return output


def configure_mcp(path: Path, client: str, version: str, project_root: Path) -> None:
    value = read_json(path)
    args = value["mcpServers"]["unity-game"]["args"]
    command = require_string(args[-1], "Claude MCP command")
    command = command.replace("mcp-error-codex.txt", f"mcp-error-{client}.txt")
    command = command.replace("--client codex", f"--client {client}")
    command = command.replace("__PLUGIN_VERSION__", version)
    args[-1] = command
    write_json(path, value)


def configure_claude_skill(path: Path) -> None:
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines or lines[0] != "---":
        raise RuntimeError("SKILL.md не содержит frontmatter.")
    try:
        end = lines.index("---", 1)
    except ValueError as error:
        raise RuntimeError("SKILL.md содержит незакрытый frontmatter.") from error
    frontmatter = [line for line in lines[1:end] if not line.startswith("when_to_use:")]
    description = next((i for i, line in enumerate(frontmatter) if line.startswith("description:")), None)
    if description is None:
        raise RuntimeError("SKILL.md не содержит description.")
    frontmatter.insert(description + 1, "when_to_use: " + json.dumps(CLAUDE_WHEN_TO_USE, ensure_ascii=False))
    path.write_text(
        "\n".join(["---", *frontmatter, "---", *lines[end + 1 :]]) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def install_codex(source: Path, version: str) -> None:
    marketplace_path = Path.home() / ".agents" / "plugins" / "marketplace.json"
    marketplace_source = Path.home() / "plugins" / PLUGIN_NAME
    replace_tree(source, marketplace_source)
    update_codex_marketplace(marketplace_path)
    plugin_id = f"{PLUGIN_NAME}@{CODEX_MARKETPLACE}"
    if codex_is_installed(plugin_id) or codex_is_configured(plugin_id):
        run([str(codex_plugin_cli()), "plugin", "remove", plugin_id], "Удаление старой версии Codex")
    run([str(codex_plugin_cli()), "plugin", "add", plugin_id], "Установка Codex")
    installed = codex_plugin_record(plugin_id)
    if not installed or installed.get("version") != version:
        actual = installed.get("version") if installed else "не зарегистрирована"
        raise RuntimeError(f"Codex зарегистрировал версию {actual}, ожидалась {version}.")
    cache = Path.home() / ".codex" / "plugins" / "cache" / CODEX_MARKETPLACE / PLUGIN_NAME / version
    require_directory(cache, "Установленная версия Codex")
    assert_same_tree(source, cache, "Codex")


def install_claude(marketplace: Path, version: str) -> None:
    known_path = Path.home() / ".claude" / "plugins" / "known_marketplaces.json"
    known = read_json(known_path) if known_path.exists() else {}
    configured = known.get(CLAUDE_MARKETPLACE)
    configured_path = None
    if isinstance(configured, dict):
        source = configured.get("source")
        if isinstance(source, dict):
            configured_path = source.get("path")
        configured_path = configured_path or configured.get("installLocation")

    if configured_path and Path(configured_path).resolve() != marketplace.resolve():
        run(
            ["claude", "plugin", "marketplace", "remove", CLAUDE_MARKETPLACE],
            "Отключение Claude marketplace прошлого проекта",
        )
        configured = None
    if not configured:
        run(["claude", "plugin", "marketplace", "add", str(marketplace)], "Подключение Claude marketplace")
    run(["claude", "plugin", "marketplace", "update", CLAUDE_MARKETPLACE], "Обновление Claude marketplace")

    plugin_id = f"{PLUGIN_NAME}@{CLAUDE_MARKETPLACE}"
    installed = read_claude_installation(plugin_id)
    if installed:
        run(["claude", "plugin", "uninstall", plugin_id, "--scope", "user"], "Удаление старой версии Claude")
    run(["claude", "plugin", "install", plugin_id, "--scope", "user"], "Установка Claude")

    installed = read_claude_installation(plugin_id)
    if not installed or installed.get("version") != version:
        actual = installed.get("version") if installed else "не установлена"
        raise RuntimeError(f"Claude установил версию {actual}, ожидалась {version}.")


def codex_is_installed(plugin_id: str) -> bool:
    return codex_plugin_record(plugin_id) is not None


def codex_plugin_record(plugin_id: str) -> dict | None:
    result = run_json(
        [str(codex_plugin_cli()), "plugin", "list", "--marketplace", CODEX_MARKETPLACE, "--available", "--json"],
        "Проверка Codex",
    )
    for group in ("installed", "available"):
        entries = result.get(group) if isinstance(result, dict) else None
        for plugin in entries or []:
            if isinstance(plugin, dict) and plugin.get("pluginId") == plugin_id and plugin.get("installed") is not False:
                return plugin
    return None


def codex_is_configured(plugin_id: str) -> bool:
    config = Path.home() / ".codex" / "config.toml"
    if not config.is_file():
        return False
    return f'[plugins."{plugin_id}"]' in config.read_text(encoding="utf-8")


def verify_installations(
    codex_source: Path,
    claude_marketplace: Path,
    codex_version: str,
    claude_version: str,
    *,
    verify_codex: bool = True,
    verify_claude: bool = True,
) -> None:
    if verify_codex:
        codex = codex_plugin_record(f"{PLUGIN_NAME}@{CODEX_MARKETPLACE}")
        if not codex or codex.get("version") != codex_version:
            raise RuntimeError("Версия Codex не совпала после установки.")
        codex_cache = Path.home() / ".codex" / "plugins" / "cache" / CODEX_MARKETPLACE / PLUGIN_NAME / codex_version
        assert_same_tree(codex_source, codex_cache, "Codex")

    if verify_claude:
        plugin_id = f"{PLUGIN_NAME}@{CLAUDE_MARKETPLACE}"
        installed = read_claude_installation(plugin_id)
        if not installed or installed.get("version") != claude_version:
            raise RuntimeError("Версия Claude не совпала после установки.")
        claude_source = claude_marketplace / "plugins" / PLUGIN_NAME
        claude_cache = Path(require_string(installed.get("installPath"), "Claude installPath"))
        assert_same_tree(claude_source, claude_cache, "Claude")


def print_status(asset_root: Path, project_root: Path) -> None:
    codex_manifest = asset_root / "CodexPlugin~" / PLUGIN_NAME / "codex-plugin" / "plugin.json"
    source_version = require_string(read_json(codex_manifest).get("version"), "Версия исходников")
    codex = codex_plugin_record(f"{PLUGIN_NAME}@{CODEX_MARKETPLACE}")
    claude = read_claude_installation(f"{PLUGIN_NAME}@{CLAUDE_MARKETPLACE}")
    server = read_running_server(project_root)
    states = read_mcp_states(project_root)
    print("Source: " + source_version)
    codex_cache = installed_codex_cache_version()
    codex_text = str(codex.get("version")) if codex else "not registered"
    if codex_cache and (not codex or codex_cache != codex.get("version")):
        codex_text += f"; cache {codex_cache}"
    print("Codex: " + codex_text)
    print("Claude: " + (str(claude.get("version")) if claude else "not installed"))
    zai_manifest = project_root / ZAI_OUTPUT / "plugins" / PLUGIN_NAME / ".zcode-plugin" / "plugin.json"
    zai_text = "not built"
    if zai_manifest.is_file():
        zai_text = require_string(read_json(zai_manifest).get("version"), "Версия ZCode-сборки")
    zai_installed = read_zcode_installation()
    if zai_installed and zai_installed != zai_text:
        zai_text += f"; zcode cache {zai_installed}"
    elif zai_installed:
        zai_text += " (installed in ZCode)"
    print("ZCode: " + zai_text)
    print("Unity: " + (str(server.get("bridgeVersion")) if server else "stopped"))
    for client in ("codex", "claude", "zai"):
        state = states.get(client)
        if state is None:
            print(f"MCP {client}: not running")
        else:
            print(f"MCP {client}: {state.get('clientVersion', 'unknown')} ({state.get('state', 'unknown')})")


def read_running_server(project_root: Path) -> dict | None:
    path = project_root / "Library" / "UnityAgentBridge" / "server.json"
    if not path.is_file():
        return None
    try:
        value = read_json(path)
        pid = int(value.get("pid", 0))
        return value if pid > 0 and process_exists(pid) else None
    except (TypeError, ValueError, RuntimeError):
        return None


def installed_codex_cache_version() -> str | None:
    root = Path.home() / ".codex" / "plugins" / "cache" / CODEX_MARKETPLACE / PLUGIN_NAME
    if not root.is_dir():
        return None
    versions = [path.name for path in root.iterdir() if path.is_dir()]
    return sorted(versions)[-1] if versions else None


def restart_unity_server(project_root: Path, current: dict, expected: str) -> None:
    if current.get("bridgeVersion") == expected:
        return
    request = urllib.request.Request(
        f"http://127.0.0.1:{int(current['port'])}/shutdown",
        data=b"",
        headers={"X-Unity-Agent-Token": require_string(current.get("token"), "Unity server token")},
        method="POST",
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=10) as response:
            if response.status != 200:
                raise RuntimeError(f"Unity server shutdown returned HTTP {response.status}.")
    except urllib.error.URLError:
        terminate_process(int(current["pid"]), "Unity server")

    deadline = time.monotonic() + 15.0
    while process_exists(int(current["pid"])) and time.monotonic() < deadline:
        time.sleep(0.1)
    if process_exists(int(current["pid"])):
        raise RuntimeError("Старый Unity server не остановился за 15 секунд.")

    existing = read_running_server(project_root)
    if existing is None:
        start_unity_server(project_root)
    elif existing.get("bridgeVersion") != expected:
        raise RuntimeError(f"Unity автоматически запустил старую версию {existing.get('bridgeVersion')}.")
    wait_for_unity_server_version(project_root, expected)


def start_unity_server(project_root: Path) -> None:
    runtime = project_root / "Library" / "UnityAgentBridge"
    python = runtime / "Runtime" / "venv" / "Scripts" / "python.exe"
    script = project_root / "Assets" / "UnityAgentBridge" / "Python~" / "server.py"
    required_files([python, script])
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        port = int(probe.getsockname()[1])
    command = [
        str(python), str(script),
        "--project", str(project_root),
        "--port", str(port),
        "--token", secrets.token_hex(16),
        "--model-cache", str(runtime / "models"),
    ]
    flags = 0
    if os.name == "nt":
        flags = subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS
    subprocess.Popen(
        command,
        cwd=script.parent,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=flags,
        close_fds=True,
    )


def wait_for_unity_server_version(project_root: Path, expected: str) -> None:
    deadline = time.monotonic() + 150.0
    while time.monotonic() < deadline:
        server = read_running_server(project_root)
        if server is not None and server.get("bridgeVersion") == expected:
            return
        time.sleep(0.25)
    actual = read_running_server(project_root)
    version = actual.get("bridgeVersion") if actual else "stopped"
    raise RuntimeError(f"Unity server has {version}, expected {expected}.")


def read_mcp_states(project_root: Path) -> dict[str, dict]:
    discovery = Path(os.environ.get("LOCALAPPDATA", "")) / "UnityAgentBridge"
    result: dict[str, dict] = {}
    if not discovery.is_dir():
        return result
    for path in discovery.glob("mcp-state-*.json"):
        try:
            state = read_json(path)
            pid = int(state.get("pid", 0))
            if Path(str(state.get("projectRoot", ""))).resolve() != project_root.resolve():
                continue
            if pid <= 0 or not process_exists(pid):
                path.unlink(missing_ok=True)
                continue
            client = state.get("client")
            if client in {"codex", "claude", "zai"}:
                result[client] = state
        except (OSError, TypeError, ValueError, RuntimeError):
            continue
    return result


def stop_running_mcp_clients(project_root: Path) -> None:
    for state in read_mcp_states(project_root).values():
        pid = int(state["pid"])
        terminate_process(pid, "MCP")


def terminate_process(pid: int, label: str) -> None:
    if not process_exists(pid):
        return
    if os.name == "nt":
        result = subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            text=True,
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            creationflags=NO_WINDOW,
        )
        if result.returncode != 0 and process_exists(pid):
            raise RuntimeError(f"Не удалось остановить {label} {pid}.")
        return
    os.kill(pid, 15)


def process_exists(pid: int) -> bool:
    if os.name == "nt":
        import ctypes

        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        exit_code = ctypes.c_ulong()
        try:
            if not ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                return False
            return exit_code.value == 259
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


def read_claude_installation(plugin_id: str) -> dict | None:
    path = Path.home() / ".claude" / "plugins" / "installed_plugins.json"
    if not path.exists():
        return None
    entries = read_json(path).get("plugins", {}).get(plugin_id, [])
    return entries[0] if entries and isinstance(entries[0], dict) else None


def install_zai(marketplace: Path, version: str) -> None:
    manifest = read_json(marketplace / "plugins" / PLUGIN_NAME / ".zcode-plugin" / "plugin.json")
    if manifest.get("version") != version:
        raise RuntimeError("Версия ZCode-сборки не совпала.")
    home_marketplace = Path.home() / ".zcode" / "UnityAgentBridge.ZaiMarketplace"
    sync_directory(marketplace, home_marketplace)
    installed = read_zcode_installation()
    if installed is None:
        print(
            "ZCode: маркетплейс собран и синхронизирован в "
            f"{home_marketplace}. В ZCode: Plugin Marketplace → «+ New» → этот путь → Install "
            f"{PLUGIN_NAME}@{ZAI_MARKETPLACE}."
        )
    elif installed != version:
        print(
            f"ZCode: установлена версия {installed}, собрана {version}. "
            "Обновите плагин в Plugin Marketplace (Personal → unity-agent-bridge-local-zai → Update)."
        )


def read_zcode_installation() -> str | None:
    root = Path.home() / ".zcode" / "cli" / "plugins" / "cache" / ZAI_MARKETPLACE / PLUGIN_NAME
    if not root.is_dir():
        return None
    versions = [path.name for path in root.iterdir() if path.is_dir()]
    return sorted(versions)[-1] if versions else None


def validate_zai_plugin(plugin: Path) -> None:
    required_files([
        plugin / ".zcode-plugin" / "plugin.json",
        plugin / ".mcp.json",
        plugin / "skills" / PLUGIN_NAME / "SKILL.md",
        plugin / "skills" / PLUGIN_NAME / "scripts" / "uab.ps1",
    ])
    servers = read_json(plugin / ".zcode-plugin" / "plugin.json").get("mcpServers")
    if not isinstance(servers, dict) or "unity-game" not in servers:
        raise RuntimeError("Манифест ZCode не содержит mcpServers unity-game.")
    if (plugin / ".codex-plugin").exists() or (plugin / "codex-plugin").exists():
        raise RuntimeError("ZCode-сборка содержит манифест Codex.")


def validate_codex_plugin(plugin: Path) -> None:
    required_files([
        plugin / ".codex-plugin" / "plugin.json",
        plugin / ".mcp.json",
        plugin / "skills" / PLUGIN_NAME / "SKILL.md",
        plugin / "skills" / PLUGIN_NAME / "scripts" / "bridge_client.py",
    ])


def validate_claude_plugin(plugin: Path) -> None:
    required_files([
        plugin / ".claude-plugin" / "plugin.json",
        plugin / ".mcp.json",
        plugin / "skills" / PLUGIN_NAME / "SKILL.md",
    ])
    if (plugin / ".codex-plugin").exists() or (plugin / "codex-plugin").exists():
        raise RuntimeError("Claude-сборка содержит манифест Codex.")


def run(command: list[str], stage: str) -> None:
    run_process(command, stage)


def run_json(command: list[str], stage: str) -> object:
    output = run_process(command, stage)
    try:
        return json.loads(output)
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{stage}: команда вернула некорректный JSON.") from error


def run_process(command: list[str], stage: str) -> str:
    candidate = Path(command[0])
    executable = str(candidate) if candidate.is_file() else shutil.which(command[0])
    if not executable:
        raise RuntimeError(f"{stage}: команда {command[0]} не найдена.")
    result = subprocess.run(
        [executable, *command[1:]],
        text=True,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        creationflags=NO_WINDOW,
    )
    if result.returncode != 0:
        message = (result.stderr or result.stdout).strip().splitlines()
        detail = message[-1] if message else f"код {result.returncode}"
        raise RuntimeError(f"{stage}: {detail}")
    return result.stdout


def codex_plugin_cli() -> Path:
    path = Path.home() / ".codex" / "plugins" / ".plugin-appserver" / "codex.exe"
    if not path.is_file():
        raise RuntimeError(f"Codex Desktop plugin CLI не найден: {path}")
    return path


def codex_desktop_is_running() -> bool:
    if os.name != "nt":
        return False
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq codex.exe", "/FO", "CSV", "/NH"],
        text=True,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        creationflags=NO_WINDOW,
    )
    return result.returncode == 0 and any(
        line.strip().lower().startswith('"codex.exe"')
        for line in result.stdout.splitlines()
    )


def running_claude_desktop() -> tuple[Path, list[int]] | None:
    if os.name != "nt":
        return None
    launcher = Path(os.environ.get("LOCALAPPDATA", "")) / "AnthropicClaude" / "Claude.exe"
    if not launcher.is_file():
        return None
    script = (
        "$root=[IO.Path]::GetFullPath($env:LOCALAPPDATA+'\\AnthropicClaude\\');"
        "@(Get-Process -Name claude -ErrorAction SilentlyContinue | "
        "Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)} | "
        "Select-Object -ExpandProperty Id) | ConvertTo-Json -Compress"
    )
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        text=True,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        creationflags=NO_WINDOW,
    )
    if result.returncode != 0:
        raise RuntimeError("Не удалось определить процессы Claude Desktop: " + result.stderr.strip())
    raw = result.stdout.strip()
    if not raw:
        return None
    value = json.loads(raw)
    process_ids = [value] if isinstance(value, int) else value
    if not isinstance(process_ids, list) or not all(isinstance(pid, int) and pid > 0 for pid in process_ids):
        raise RuntimeError("Claude Desktop вернул некорректный список процессов.")
    return (launcher, process_ids) if process_ids else None


def inside_claude_desktop() -> bool:
    if os.name != "nt":
        return False
    script = (
        "$root=[IO.Path]::GetFullPath($env:LOCALAPPDATA+'\\AnthropicClaude\\');"
        f"$id={os.getpid()};"
        "for($i=0;$i -lt 32 -and $id;$i++){"
        "$p=Get-CimInstance Win32_Process -Filter \"ProcessId=$id\";if(-not $p){break};"
        "if($p.ExecutablePath -and [IO.Path]::GetFullPath($p.ExecutablePath).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){'yes';break};"
        "$id=$p.ParentProcessId}"
    )
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        text=True,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        creationflags=NO_WINDOW,
    )
    return result.stdout.strip() == "yes"


def relaunch_detached(log: Path) -> None:
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [sys.executable, str(Path(__file__).resolve()), *sys.argv[1:], "--detached"]
    environment = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP | NO_WINDOW
    with open(log, "w", encoding="utf-8") as output:
        try:
            subprocess.Popen(command, stdout=output, stderr=output, env=environment,
                             creationflags=flags | subprocess.CREATE_BREAKAWAY_FROM_JOB)
        except OSError:
            subprocess.Popen(command, stdout=output, stderr=output, env=environment, creationflags=flags)


def stop_claude_desktop(process_ids: list[int]) -> None:
    for pid in process_ids:
        terminate_process(pid, "Claude Desktop")
    deadline = time.monotonic() + 10.0
    while any(process_exists(pid) for pid in process_ids) and time.monotonic() < deadline:
        time.sleep(0.05)
    if any(process_exists(pid) for pid in process_ids):
        raise RuntimeError("Claude Desktop не завершился полностью.")


def start_claude_desktop(launcher: Path) -> None:
    os.startfile(str(launcher))


def copy_tree(
    source: Path,
    destination: Path,
    *,
    merge: bool = False,
    excluded_roots: set[str] | None = None,
    excluded_skill_roots: set[str] | None = None,
) -> None:
    excluded_roots = excluded_roots or set()
    excluded_skill_roots = excluded_skill_roots or set()
    if not merge and destination.exists():
        remove_tree(destination)
    destination.mkdir(parents=True, exist_ok=True)
    skill_root = Path("skills") / PLUGIN_NAME
    for path in source.rglob("*"):
        relative = path.relative_to(source)
        if not relative.parts or relative.parts[0] in excluded_roots:
            continue
        if len(relative.parts) > len(skill_root.parts) and relative.parts[: len(skill_root.parts)] == skill_root.parts:
            if relative.parts[len(skill_root.parts)] in excluded_skill_roots:
                continue
        if ignored(path):
            continue
        target = destination / relative
        if path.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)


def replace_tree(source: Path, destination: Path) -> None:
    temporary = destination.with_name(destination.name + ".tmp")
    backup = destination.with_name(destination.name + ".old")
    remove_tree(temporary)
    remove_tree(backup)
    copy_tree(source, temporary)
    try:
        sync_directory(temporary, destination)
    finally:
        remove_tree(temporary)


def sync_directory(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    expected_files = {
        path.relative_to(source)
        for path in source.rglob("*")
        if path.is_file()
    }
    for relative in sorted(expected_files):
        source_file = source / relative
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_name(target.name + f".{os.getpid()}.tmp")
        shutil.copy2(source_file, temporary)
        os.replace(temporary, target)
    for path in sorted(destination.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        relative = path.relative_to(destination)
        if path.is_file() and relative not in expected_files:
            path.unlink()
        elif path.is_dir() and not any(path.iterdir()):
            path.rmdir()


def assert_same_tree(expected: Path, actual: Path, name: str) -> None:
    expected_digest = tree_digest(expected)
    actual_digest = tree_digest(actual)
    if expected_digest != actual_digest:
        raise RuntimeError(f"{name}: установленная копия отличается от собранной.")


def tree_digest(root: Path) -> str:
    digest = hashlib.sha256()
    files = sorted(path for path in root.rglob("*") if path.is_file() and not ignored(path))
    for path in files:
        relative = path.relative_to(root).as_posix().encode("utf-8")
        digest.update(len(relative).to_bytes(4, "big"))
        digest.update(relative)
        digest.update(path.read_bytes())
    return digest.hexdigest()


def ignored(path: Path) -> bool:
    return any(part in IGNORED_NAMES for part in path.parts) or path.suffix.lower() in IGNORED_SUFFIXES


def rename_required(source: Path, destination: Path) -> None:
    if not source.exists():
        raise FileNotFoundError(source)
    if destination.exists():
        if destination.is_dir():
            remove_tree(destination)
        else:
            destination.unlink()
    os.replace(source, destination)


def required_files(paths: list[Path]) -> None:
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise RuntimeError("Не хватает файлов плагина: " + ", ".join(missing))


def require_directory(path: Path, name: str) -> None:
    if not path.is_dir():
        raise RuntimeError(f"{name} не найдена: {path}")


def require_string(value: object, name: str) -> str:
    if not isinstance(value, str) or not value:
        raise RuntimeError(f"{name} не задан.")
    return value


def find_project_root(asset_root: Path) -> Path:
    assets = next((parent for parent in [asset_root, *asset_root.parents] if parent.name.lower() == "assets"), None)
    if assets is None or assets.parent == assets:
        raise RuntimeError("Папка Assets не найдена.")
    return assets.parent


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise RuntimeError(f"Не удалось прочитать JSON: {path}") from error
    if not isinstance(value, dict):
        raise RuntimeError(f"Корень JSON должен быть объектом: {path}")
    return value


def write_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def write_json_atomic(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        "w", encoding="utf-8", newline="\n", dir=path.parent, delete=False
    ) as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
        temporary = Path(handle.name)
    os.replace(temporary, path)


def remove_tree(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise
