from __future__ import annotations

import argparse
import base64
import io
import json
import math
import os
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont


_SOURCE_RELOADING = bool(globals().get("_SOURCE_RELOADING", False))
_SOURCE_PATH = Path(__file__).resolve()
_SOURCE_MTIME_NS = int(globals().get("_SOURCE_MTIME_NS", _SOURCE_PATH.stat().st_mtime_ns))
# Survives source reloads: one lock for reloading, dispatching and writing to stdout.
_IO_LOCK = globals().get("_IO_LOCK") or threading.Lock()


if not _SOURCE_RELOADING and hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")


PROTOCOL_VERSION = "2025-06-18"
MAX_CONFIG_BYTES = 64 * 1024
MAX_RESPONSE_BYTES = 4 * 1024 * 1024
GAME_TOOL_NAME = "game_actions"
SCENE_TOOL_NAME = "scene_screenshot"
SPRITE_TOOL_NAME = "sprite_editor"
BRIDGE_TOOL_NAME = "bridge_server"
PROFILER_TOOL_NAME = "profiler"
SHADER_PREVIEW_TOOL_NAME = "shader_preview"
SERVER_INSTRUCTIONS = (
    "Этот MCP содержит кадры сцены, Game View, Profiler, редактор разметки Sprite и превью материалов и узлов Shader Graph. Остальные возможности Unity Agent Bridge "
    "доступны через skill unity-agent-bridge и scripts/uab.ps1; список MCP tools не является списком возможностей плагина."
)
STATE_FILE_PREFIX = "mcp-state-"
SERVER_RESTART_WAIT_SECONDS = 30.0
LANDSCAPE_SCREENSHOT_LIMIT = (1280, 720)
PORTRAIT_SCREENSHOT_LIMIT = (720, 1280)

ACTION_PROPERTIES: dict[str, Any] = {
    "action": {
        "type": "string",
        "enum": [
            "click",
            "double_click",
            "hover",
            "drag",
            "scroll",
            "press_key",
            "key_down",
            "key_up",
            "type_text",
            "wait",
            "screenshot",
            "zoom",
        ],
    },
    "x": {"type": "number"},
    "y": {"type": "number"},
    "targetX": {"type": "number"},
    "targetY": {"type": "number"},
    "button": {"type": "string", "enum": ["left", "right", "middle"]},
    "duration": {"type": "number", "minimum": 0, "maximum": 10},
    "scrollX": {
        "type": "number",
        "description": "Горизонтальная прокрутка: отрицательное значение — влево, положительное — вправо.",
    },
    "scrollY": {
        "type": "number",
        "description": "Вертикальная прокрутка: отрицательное значение — вверх, положительное — вниз.",
    },
    "key": {"type": "string"},
    "text": {"type": "string"},
    "seconds": {"type": "number", "minimum": 0, "maximum": 30, "description": "Игровое время ожидания."},
    "timeScale": {
        "type": "number",
        "exclusiveMinimum": 0,
        "maximum": 100,
        "description": "Множитель Time.timeScale на время wait; по умолчанию 1.",
    },
    "object": {"type": "string", "description": "Для wait: путь объекта; seconds становится пределом ожидания его state."},
    "state": {"type": "string", "enum": ["visible", "hidden", "attached", "detached"], "description": "По умолчанию visible."},
    "region": {
        "type": "array",
        "items": {"type": "number"},
        "minItems": 4,
        "maxItems": 4,
        "description": "[x0, y0, x1, y1] в пикселях последнего кадра.",
    },
    "scale": {
        "type": "number",
        "minimum": 0.1,
        "maximum": 1,
        "description": "Для screenshot и zoom: доля размера возвращаемой картинки; x/y остаются в пикселях полного кадра.",
    },
}

GAME_ACTIONS_TOOL = {
    "name": GAME_TOOL_NAME,
    "description": (
        "Выполняет пакет действий в Game View Unity. Первый вызов запускает игру и ждёт готового кадра. "
        "После пакета игра ставится на паузу. Результат: кадры screenshot/zoom по порядку и итоговый снимок; "
        "если пакет заканчивается screenshot или zoom, отдельного итогового снимка нет; time — Time.time кадров; "
        "sounds — звуки, начавшиеся за пакет. "
        "profile: true записывает Profiler на время пакета и добавляет сводку и график. "
        "frames: N добавляет сетку из N кадров, снятых равномерно по реальному времени пакета, с подписями игрового времени; "
        "wait с малым timeScale растягивает момент на большую часть кадров, как замедленная съёмка."
    ),
    "inputSchema": {
        "type": "object",
        "properties": {
            "actions": {
                "type": "array",
                "maxItems": 100,
                "items": {
                    "type": "object",
                    "properties": ACTION_PROPERTIES,
                    "required": ["action"],
                    "additionalProperties": False,
                },
                "description": (
                    "Форматы: click/double_click(x,y,button?); hover(x,y); "
                    "drag(x,y,targetX,targetY,button?,duration?); scroll(x,y,scrollX? или scrollY?); "
                    "press_key(key,duration?); key_down/key_up(key); type_text(text); wait(seconds,timeScale?,object?,state?); "
                    "screenshot(scale?); zoom(region,scale?). "
                    "x/y — пиксели последнего полученного кадра. Пустой массив делает начальный снимок."
                ),
            },
            "profile": {"type": "boolean"},
            "frames": {"type": "integer", "minimum": 4, "maximum": 16},
        },
        "required": ["actions"],
        "additionalProperties": False,
    },
}

PROFILER_TOOL = {
    "name": PROFILER_TOOL_NAME,
    "description": (
        "Profiler: frames запускает игру при необходимости, записывает кадры, ставит паузу и возвращает график и сводку; "
        "без frames показывает последнюю запись. График: CPU Usage по группам окна Profiler с номерами худших кадров, "
        "Rendering и Memory. marker добавляет жёлтую линию маркера, range увеличивает участок. "
        "target — цель записи окна Profiler: Edit Mode записывает сам редактор (EditorLoop) вместо игры."
    ),
    "inputSchema": {
        "type": "object",
        "properties": {
            "frames": {"type": "integer", "minimum": 10, "maximum": 4000},
            "target": {"type": "string", "enum": ["Play Mode", "Edit Mode"]},
            "marker": {"type": "string", "minLength": 1},
            "range": {"type": "array", "items": {"type": "integer", "minimum": 0}, "minItems": 2, "maxItems": 2},
        },
        "additionalProperties": False,
    },
}

SPRITE_EDITOR_TOOL = {
    "name": SPRITE_TOOL_NAME,
    "description": (
        "Показывает или изменяет разметку Sprite и сразу возвращает изображение. "
        "multiple: auto либо manual с точными прямоугольниками; single: border с зелёными линиями границ."
    ),
    "inputSchema": {
        "type": "object",
        "properties": {
            "path": {"type": "string", "description": "Путь текстуры внутри Assets."},
            "action": {"type": "string", "enum": ["preview", "auto", "manual", "border"]},
            "slices": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "name": {"type": "string"},
                        "x": {"type": "number", "minimum": 0},
                        "y": {"type": "number", "minimum": 0},
                        "width": {"type": "number", "exclusiveMinimum": 0},
                        "height": {"type": "number", "exclusiveMinimum": 0},
                    },
                    "required": ["x", "y", "width", "height"],
                    "additionalProperties": False,
                },
            },
            "border": {
                "type": "object",
                "properties": {
                    "left": {"type": "number", "minimum": 0},
                    "right": {"type": "number", "minimum": 0},
                    "top": {"type": "number", "minimum": 0},
                    "bottom": {"type": "number", "minimum": 0},
                },
                "required": ["left", "right", "top", "bottom"],
                "additionalProperties": False,
            },
        },
        "required": ["path", "action"],
        "additionalProperties": False,
    },
}

SCENE_SCREENSHOT_TOOL = {
    "name": SCENE_TOOL_NAME,
    "description": "Снимает объект текущей сцены: путь /Сцена/Объект, имя (одноимённые вместе) или описание (лучшее совпадение). "
    "grid возвращает коллаж 2×2, flat — один ортографический кадр; targets — снятые объекты.",
    "inputSchema": {
        "type": "object",
        "properties": {
            "query": {"type": "string", "minLength": 1},
            "mode": {"type": "string", "enum": ["grid", "flat"], "default": "grid"},
            "from": {"type": "string", "description": "Откуда смотреть: x,y,z или путь объекта (его центр, например комнаты); один кадр в перспективе вместо mode."},
        },
        "required": ["query"],
        "additionalProperties": False,
    },
}

SHADER_PREVIEW_TOOL = {
    "name": SHADER_PREVIEW_TOOL_NAME,
    "description": "Превью материала, шейдера, Shader Graph или объекта сцены (/Сцена/Объект — материал с текущими значениями) сеткой с подписями. "
    "shapes — формы превью Inspector (по умолчанию Sphere) и Plane — плоскость XZ сверху; "
    "nodes — узлы Shader Graph по id или имени (\"Output\" — итоговое превью), как в окне графа (плоско — Preview=Preview 2D); "
    "chain=true с двумя узлами показывает каждый шаг между ними.",
    "inputSchema": {
        "type": "object",
        "properties": {
            "path": {"type": "string", "minLength": 1},
            "shapes": {"type": "array", "items": {"type": "string", "enum": ["Sphere", "Cube", "Cylinder", "Torus", "Quad", "Plane"]}, "maxItems": 6},
            "nodes": {"type": "array", "items": {"type": "string", "minLength": 1}, "maxItems": 16},
            "chain": {"type": "boolean", "default": False},
        },
        "required": ["path"],
        "additionalProperties": False,
    },
}

BRIDGE_SERVER_TOOL = {
    "name": BRIDGE_TOOL_NAME,
    "description": "Запускает или перезапускает Unity Agent Bridge и ждёт его готовности.",
    "inputSchema": {
        "type": "object",
        "properties": {"action": {"type": "string", "enum": ["start", "restart"]}},
        "required": ["action"],
        "additionalProperties": False,
    },
}


class ToolFailure(RuntimeError):
    def __init__(self, message: str, screenshot: Path | None = None) -> None:
        super().__init__(message)
        self.screenshot = screenshot


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path)
    parser.add_argument("--client", choices=("codex", "claude", "zai"), required=True)
    parser.add_argument("--client-version", required=True)
    arguments = parser.parse_args()
    project = active_project(arguments.project)
    client = arguments.client
    client_version = arguments.client_version
    state_file = write_state(project, client, client_version, "started")
    threading.Thread(target=watch_source, daemon=True).start()
    try:
        for raw_line in sys.stdin:
            if not raw_line.strip():
                continue
            with _IO_LOCK:
                try:
                    reload_source_and_notify()
                    project = active_project(arguments.project)
                    write_state(project, client, client_version, "ready")
                    response = dispatch_line(raw_line, project, client, client_version)
                except Exception as error:
                    response = rpc_error(request_id_from_line(raw_line), -32603, concise_error(error))
                if response is not None:
                    write_message(response)
        return 0
    finally:
        state_file.unlink(missing_ok=True)


def active_project(configured: Path | None = None) -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is unavailable.")
    marker = Path(local_app_data) / "UnityAgentBridge" / "active-project.json"
    try:
        project = Path(json.loads(marker.read_text(encoding="utf-8"))["projectRoot"]).resolve()
    except (FileNotFoundError, KeyError, TypeError, ValueError, json.JSONDecodeError, OSError) as error:
        if configured is None:
            raise RuntimeError("Unity Agent Bridge has no selected project.") from error
        project = configured.resolve()
    if not (project / "Assets" / "UnityAgentBridge" / "Python~" / "mcp_server.py").is_file():
        raise RuntimeError("Selected Unity project has no Unity Agent Bridge MCP server.")
    return project


def write_message(message: Any) -> None:
    sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def tool_list() -> list[dict[str, Any]]:
    return [GAME_ACTIONS_TOOL, SCENE_SCREENSHOT_TOOL, PROFILER_TOOL, SPRITE_EDITOR_TOOL, SHADER_PREVIEW_TOOL, BRIDGE_SERVER_TOOL]


# An edited mcp_server.py is picked up without a restart, and the client is told to re-list the tools.
def watch_source() -> None:
    while True:
        time.sleep(1.0)
        with _IO_LOCK:
            try:
                reload_source_and_notify()
            except Exception:
                pass


def reload_source_and_notify() -> None:
    before = json.dumps(tool_list(), sort_keys=True)
    if reload_source_if_changed() and json.dumps(tool_list(), sort_keys=True) != before:
        write_message({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})


def reload_source_if_changed() -> bool:
    global _SOURCE_MTIME_NS, _SOURCE_RELOADING
    modified = _SOURCE_PATH.stat().st_mtime_ns
    if modified == _SOURCE_MTIME_NS:
        return False
    time.sleep(0.05)
    modified = _SOURCE_PATH.stat().st_mtime_ns
    source = _SOURCE_PATH.read_text(encoding="utf-8")
    compiled = compile(source, str(_SOURCE_PATH), "exec")
    _SOURCE_RELOADING = True
    try:
        exec(compiled, globals(), globals())
    finally:
        _SOURCE_RELOADING = False
    _SOURCE_MTIME_NS = modified
    return True


def dispatch_line(raw_line: str, project: Path, client: str, client_version: str) -> dict[str, Any] | list[dict[str, Any]] | None:
    try:
        message = json.loads(raw_line)
    except json.JSONDecodeError as error:
        return rpc_error(None, -32700, f"Parse error: {error.msg}")

    if isinstance(message, list):
        if not message:
            return rpc_error(None, -32600, "Invalid Request")
        responses = [response for item in message if (response := dispatch(item, project, client, client_version)) is not None]
        return responses or None
    return dispatch(message, project, client, client_version)


def request_id_from_line(raw_line: str) -> Any:
    try:
        message = json.loads(raw_line)
        return message.get("id") if isinstance(message, dict) else None
    except json.JSONDecodeError:
        return None


def dispatch(message: Any, project: Path, client: str, client_version: str) -> dict[str, Any] | None:
    if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
        return rpc_error(message.get("id") if isinstance(message, dict) else None, -32600, "Invalid Request")

    request_id = message.get("id")
    method = message.get("method")
    if request_id is None:
        return None
    if not isinstance(method, str):
        return rpc_error(request_id, -32600, "Invalid Request")

    try:
        if method == "initialize":
            result = {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {"listChanged": True}},
                "serverInfo": {"name": "unity-game", "version": client_version},
                "instructions": SERVER_INSTRUCTIONS,
            }
        elif method == "ping":
            result = {}
        elif method == "tools/list":
            write_state(project, client, client_version, "ready")
            result = {"tools": tool_list()}
        elif method == "tools/call":
            params = message.get("params")
            if not isinstance(params, dict):
                raise ValueError("params must be an object.")
            result = call_tool(params, project)
        else:
            return rpc_error(request_id, -32601, "Method not found")
        return {"jsonrpc": "2.0", "id": request_id, "result": result}
    except ValueError as error:
        return rpc_error(request_id, -32602, str(error))
    except Exception as error:
        return rpc_error(request_id, -32603, concise_error(error))


def call_tool(params: dict[str, Any], project: Path) -> dict[str, Any]:
    name = params.get("name")
    arguments = params.get("arguments")
    if not isinstance(arguments, dict):
        raise ValueError("arguments must be an object.")
    if name == SCENE_TOOL_NAME:
        return call_scene_screenshot(arguments, project)
    if name == SPRITE_TOOL_NAME:
        return call_sprite_editor(arguments, project)
    if name == BRIDGE_TOOL_NAME:
        return call_bridge_server(arguments, project)
    if name == PROFILER_TOOL_NAME:
        return call_profiler(arguments, project)
    if name == SHADER_PREVIEW_TOOL_NAME:
        return call_shader_preview(arguments, project)
    if name != GAME_TOOL_NAME:
        raise ValueError(f"Unknown tool: {name}")
    if "actions" not in arguments or not set(arguments) <= {"actions", "profile", "frames"}:
        raise ValueError("game_actions accepts actions, profile and frames.")
    actions = arguments.get("actions")
    if not isinstance(actions, list):
        raise ValueError("actions must be an array.")
    profile = arguments.get("profile", False)
    if not isinstance(profile, bool):
        raise ValueError("profile must be a boolean.")
    request = {"operation": "game-actions", "actions": actions, "profile": profile}
    if "frames" in arguments:
        request["frames"] = arguments["frames"]

    try:
        result = invoke_bridge(project, request)
        frames = result.get("screenshots") or [result.get("screenshot")]
        content = [image_content(checked_screenshot_path(frame)) for frame in frames]
        if result.get("times"):
            content.append({"type": "text", "text": "time: " + ", ".join(f"{value:g}" for value in result["times"])})
        if result.get("sounds"):
            content.append({"type": "text", "text": "sounds: " + "; ".join(result["sounds"])})
        clip = result.get("clip") or {}
        if clip.get("screenshots"):
            labels = [f"t={value:.2f}" for value in clip.get("times", [])]
            content.append(collage_content([checked_screenshot_path(path) for path in clip["screenshots"]], labels, keep_end=False))
        if result.get("profile"):
            content.append({"type": "text", "text": json.dumps(result["profile"], ensure_ascii=False, separators=(",", ":"))})
        if result.get("ok") is False:
            content.append({"type": "text", "text": concise_text(result.get("error"), "Game action failed.")})
            return {"content": content, "isError": True}
        return {"content": content, "isError": False}
    except ToolFailure as error:
        content: list[dict[str, Any]] = []
        if error.screenshot is not None and error.screenshot.is_file():
            content.append(image_content(error.screenshot))
        content.append({"type": "text", "text": concise_error(error)})
        return {"content": content, "isError": True}


def call_bridge_server(arguments: dict[str, Any], project: Path) -> dict[str, Any]:
    if set(arguments) != {"action"}:
        raise ValueError("bridge_server accepts only the action field.")
    action = arguments.get("action")
    if action not in {"start", "restart"}:
        raise ValueError("action must be start or restart.")

    runtime = project / "Library" / "UnityAgentBridge"
    runtime.mkdir(parents=True, exist_ok=True)
    initial_pid = server_pid(runtime / "server.json")
    requested_at = time.time_ns()
    control = runtime / f"server-control-{uuid.uuid4().hex}.json"
    temporary = control.with_suffix(".tmp")
    temporary.write_text(json.dumps({"action": action}, separators=(",", ":")), encoding="utf-8")
    temporary.replace(control)

    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        current_pid = server_pid(runtime / "server.json")
        restarted = action == "start" or initial_pid is None or current_pid != initial_pid
        if current_pid is not None and restarted:
            try:
                result = invoke_bridge(project, {"operation": "health"})
                if result.get("ok") is not False:
                    return {"content": [{"type": "text", "text": "running"}], "isError": False}
            except ToolFailure:
                pass
        failure = recent_server_failure(runtime / "server-error.json", requested_at)
        if failure:
            return {"content": [{"type": "text", "text": failure}], "isError": True}
        time.sleep(0.5)

    return {"content": [{"type": "text", "text": "Unity Agent Bridge did not become ready."}], "isError": True}


def server_pid(path: Path) -> int | None:
    try:
        value = json.loads(path.read_text(encoding="utf-8")).get("pid")
        return value if isinstance(value, int) and not isinstance(value, bool) and value > 0 else None
    except (FileNotFoundError, OSError, AttributeError, json.JSONDecodeError):
        return None


def recent_server_failure(path: Path, requested_at: int) -> str | None:
    try:
        if path.stat().st_mtime_ns < requested_at:
            return None
        value = json.loads(path.read_text(encoding="utf-8")).get("error")
        return concise_text(value, "Unity Agent Bridge failed to start.")
    except (FileNotFoundError, OSError, AttributeError, json.JSONDecodeError):
        return None


def call_profiler(arguments: dict[str, Any], project: Path) -> dict[str, Any]:
    unknown = set(arguments).difference({"frames", "marker", "range", "target"})
    if unknown:
        raise ValueError("profiler has unknown fields: " + ", ".join(sorted(unknown)))
    try:
        result = invoke_bridge(project, {"operation": "profiler", **arguments})
        content = [image_content(screenshot_path(result))]
        if result.get("profile"):
            content.append({"type": "text", "text": json.dumps(result["profile"], ensure_ascii=False, separators=(",", ":"))})
        return {"content": content, "isError": False}
    except ToolFailure as error:
        return {"content": [{"type": "text", "text": concise_error(error)}], "isError": True}


def call_sprite_editor(arguments: dict[str, Any], project: Path) -> dict[str, Any]:
    allowed = {"path", "action", "slices", "border"}
    unknown = set(arguments).difference(allowed)
    missing = {"path", "action"}.difference(arguments)
    if missing:
        raise ValueError("sprite_editor is missing: " + ", ".join(sorted(missing)))
    if unknown:
        raise ValueError("sprite_editor has unknown fields: " + ", ".join(sorted(unknown)))
    path = arguments.get("path")
    action = arguments.get("action")
    if not isinstance(path, str) or not path.strip():
        raise ValueError("path must be a non-empty string.")
    if action not in {"preview", "auto", "manual", "border"}:
        raise ValueError("action must be preview, auto, manual or border.")
    if action == "manual" and (not isinstance(arguments.get("slices"), list) or not arguments["slices"]):
        raise ValueError("manual action requires a non-empty slices array.")
    if action == "border" and not isinstance(arguments.get("border"), dict):
        raise ValueError("border action requires border.")
    if action != "manual" and "slices" in arguments:
        raise ValueError("slices is valid only for manual action.")
    if action != "border" and "border" in arguments:
        raise ValueError("border is valid only for border action.")
    try:
        payload = {"operation": "sprite-editor", **arguments}
        result = invoke_bridge(project, payload)
        return {"content": [image_content(screenshot_path(result))], "isError": False}
    except ToolFailure as error:
        return {"content": [{"type": "text", "text": concise_error(error)}], "isError": True}


def call_shader_preview(arguments: dict[str, Any], project: Path) -> dict[str, Any]:
    unknown = set(arguments).difference({"path", "shapes", "nodes", "chain"})
    if "path" not in arguments:
        raise ValueError("shader_preview is missing: path")
    if unknown:
        raise ValueError("shader_preview has unknown fields: " + ", ".join(sorted(unknown)))
    if not isinstance(arguments.get("chain", False), bool):
        raise ValueError("chain must be a boolean.")
    try:
        result = invoke_bridge(project, {"operation": "shader-preview", **arguments})
        screenshots = result.get("screenshots")
        if not isinstance(screenshots, list) or not screenshots:
            raise ToolFailure("Unity Agent Bridge returned no preview.")
        content = [collage_content([checked_screenshot_path(path) for path in screenshots], result.get("labels", []), keep_end=False)]
        if result.get("errors"):
            content.append({"type": "text", "text": "errors: " + " | ".join(result["errors"])})
        return {"content": content, "isError": False}
    except ToolFailure as error:
        return {"content": [{"type": "text", "text": concise_error(error)}], "isError": True}


def call_scene_screenshot(arguments: dict[str, Any], project: Path) -> dict[str, Any]:
    missing = {"query"}.difference(arguments)
    unknown = set(arguments).difference({"query", "mode", "from"})
    if missing:
        raise ValueError("scene_screenshot is missing: " + ", ".join(sorted(missing)))
    if unknown:
        raise ValueError("scene_screenshot has unknown fields: " + ", ".join(sorted(unknown)))
    query = arguments.get("query")
    mode = arguments.get("mode", "grid")
    if not isinstance(query, str) or not query.strip():
        raise ValueError("query must be a non-empty string.")
    if mode not in {"grid", "flat"}:
        raise ValueError("mode must be grid or flat.")
    try:
        viewpoint = arguments.get("from")
        if viewpoint is not None and (not isinstance(viewpoint, str) or not viewpoint.strip()):
            raise ValueError("from must be x,y,z or an object path.")
        result = invoke_bridge(project, {"operation": "scene-screenshot", "query": query, "mode": mode, "from": viewpoint})
        screenshots = result.get("screenshots")
        if not isinstance(screenshots, list) or not screenshots:
            raise ToolFailure("Unity Agent Bridge returned no scene screenshot.")
        paths = [checked_screenshot_path(path) for path in screenshots]
        labels = result.get("labels", [])
        if not isinstance(labels, list) or any(not isinstance(label, str) for label in labels):
            raise ToolFailure("Unity scene screenshot labels are invalid.")
        content = [collage_content(paths, labels)] if mode == "grid" and not viewpoint else [image_content(paths[0])]
        targets = result.get("targets") or []
        if targets:
            content.append({"type": "text", "text": "targets: " + ", ".join(str(target) for target in targets)})
        return {"content": content, "isError": False}
    except ToolFailure as error:
        return {"content": [{"type": "text", "text": concise_error(error)}], "isError": True}


def invoke_bridge(project: Path, payload: dict[str, Any]) -> dict[str, Any]:
    deadline = time.monotonic() + SERVER_RESTART_WAIT_SECONDS
    while True:
        try:
            response_body = post_bridge(project, payload)
            break
        except (FileNotFoundError, ConnectionRefusedError):
            if time.monotonic() >= deadline:
                raise ToolFailure("Unity Agent Bridge server is not running.") from None
            time.sleep(0.2)

    if len(response_body) > MAX_RESPONSE_BYTES:
        raise ToolFailure("Unity Agent Bridge response is too large.")
    try:
        result = json.loads(response_body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ToolFailure("Unity Agent Bridge returned an invalid response.") from error
    if not isinstance(result, dict):
        raise ToolFailure("Unity Agent Bridge returned an invalid response.")
    if result.get("ok") is False and not result.get("screenshot"):
        raise ToolFailure(concise_text(result.get("error"), "Unity game action failed."))
    return result


def post_bridge(project: Path, payload: dict[str, Any]) -> bytes:
    config_path = project / "Library" / "UnityAgentBridge" / "server.json"
    try:
        if config_path.stat().st_size > MAX_CONFIG_BYTES:
            raise ToolFailure("Unity Agent Bridge server config is invalid.")
        config = json.loads(config_path.read_text(encoding="utf-8"))
        port = config["port"]
        token = config["token"]
        configured_project = Path(config["projectRoot"]).resolve()
    except FileNotFoundError:
        raise
    except (KeyError, TypeError, ValueError, json.JSONDecodeError, OSError) as error:
        raise ToolFailure("Unity Agent Bridge server config is invalid.") from error

    if configured_project != project or isinstance(port, bool) or not isinstance(port, int) or not isinstance(token, str):
        raise ToolFailure("Unity Agent Bridge server config does not match this project.")

    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/invoke",
        data=body,
        method="POST",
        headers={"Content-Type": "application/json", "X-Unity-Agent-Token": token},
    )
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(request, timeout=360) as response:
            return response.read(MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as error:
        return error.read(MAX_RESPONSE_BYTES + 1)
    except urllib.error.URLError as error:
        if isinstance(error.reason, ConnectionRefusedError):
            raise error.reason from None
        raise ToolFailure("Unity Agent Bridge server is unavailable.") from error
    except (TimeoutError, OSError) as error:
        raise ToolFailure("Unity Agent Bridge server is unavailable.") from error


def screenshot_path(result: dict[str, Any]) -> Path:
    value = result.get("screenshot")
    if not isinstance(value, str) or not value:
        raise ToolFailure("Unity Agent Bridge returned no screenshot.")
    return checked_screenshot_path(value)


def checked_screenshot_path(value: Any) -> Path:
    if not isinstance(value, str) or not value:
        raise ToolFailure("Unity Agent Bridge returned no screenshot.")
    path = Path(value).resolve()
    if not path.is_file():
        raise ToolFailure("Unity screenshot was not created.")
    return path


def image_content(path: Path) -> dict[str, str]:
    try:
        with Image.open(path) as source:
            source.load()
            return encoded_image_content(source)
    except (OSError, ValueError) as error:
        raise ToolFailure("Unity screenshot cannot be read.") from error


def collage_content(paths: list[Path], labels: list[str], keep_end: bool = True) -> dict[str, str]:
    if not 1 <= len(paths) <= 16:
        raise ToolFailure("Unity grid screenshot must contain one to sixteen views.")
    if labels and len(labels) != len(paths):
        raise ToolFailure("Unity grid screenshot labels do not match its views.")
    images: list[Image.Image] = []
    collage: Image.Image | None = None
    try:
        for path in paths:
            with Image.open(path) as source:
                source.load()
                images.append(source.convert("RGB"))
        width = max(image.width for image in images)
        height = max(image.height for image in images)
        columns = 1 if len(images) == 1 else 2 if len(images) <= 4 else math.ceil(math.sqrt(len(images)))
        rows = (len(images) + columns - 1) // columns
        collage = Image.new("RGB", (width * columns, height * rows))
        for index, image in enumerate(images):
            if image.size != (width, height):
                image.thumbnail((width, height), Image.Resampling.LANCZOS)
            x = (index % columns) * width + (width - image.width) // 2
            y = (index // columns) * height + (height - image.height) // 2
            collage.paste(image, (x, y))
            if labels:
                draw_scene_label(collage, labels[index], index, width, height, columns, keep_end)
        return encoded_image_content(collage)
    except (OSError, ValueError) as error:
        raise ToolFailure("Unity grid screenshot cannot be composed.") from error
    finally:
        if collage is not None:
            collage.close()
        for image in images:
            image.close()


def draw_scene_label(
    collage: Image.Image,
    label: str,
    index: int,
    cell_width: int,
    cell_height: int,
    columns: int,
    keep_end: bool = True,
) -> None:
    font_size = max(18, min(30, cell_height // 24))
    font = ImageFont.truetype(scene_label_font(), font_size)
    draw = ImageDraw.Draw(collage)
    padding = max(6, font_size // 3)
    available = cell_width - padding * 2
    # Scene paths keep their end (the object), preview labels their start (number and node id).
    visible = truncate_label_start(draw, label, font, available) if keep_end else truncate_label_end(draw, label, font, available)
    left = (index % columns) * cell_width
    top = (index // columns) * cell_height
    bar_height = font_size + padding * 2
    draw.rectangle((left, top, left + cell_width - 1, top + bar_height), fill=(0, 0, 0))
    draw.text((left + padding, top + padding), visible, font=font, fill=(255, 255, 255))


def truncate_label_start(draw: ImageDraw.ImageDraw, label: str, font: ImageFont.FreeTypeFont, width: int) -> str:
    if draw.textlength(label, font=font) <= width:
        return label
    prefix = "…"
    low = 0
    high = len(label)
    while low < high:
        length = (low + high + 1) // 2
        if draw.textlength(prefix + label[-length:], font=font) <= width:
            low = length
        else:
            high = length - 1
    return prefix + label[-low:] if low else prefix


def truncate_label_end(draw: ImageDraw.ImageDraw, label: str, font: ImageFont.FreeTypeFont, width: int) -> str:
    if draw.textlength(label, font=font) <= width:
        return label
    while label and draw.textlength(label + "…", font=font) > width:
        label = label[:-1]
    return label + "…"


def scene_label_font() -> str:
    candidates = [
        Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / "segoeui.ttf",
        Path("/System/Library/Fonts/Supplemental/Arial Unicode.ttf"),
        Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    ]
    for path in candidates:
        if path.is_file():
            return str(path)
    raise ToolFailure("A Unicode font for scene screenshot labels was not found.")


def encoded_image_content(source: Image.Image) -> dict[str, str]:
    limit = LANDSCAPE_SCREENSHOT_LIMIT if source.width >= source.height else PORTRAIT_SCREENSHOT_LIMIT
    if source.width > limit[0] or source.height > limit[1]:
        source.thumbnail(limit, Image.Resampling.LANCZOS)
    output = io.BytesIO()
    source.save(output, format="PNG", compress_level=6)
    data = base64.b64encode(output.getvalue()).decode("ascii")
    return {"type": "image", "data": data, "mimeType": "image/png"}


def concise_error(error: BaseException) -> str:
    return concise_text(str(error), type(error).__name__)


def concise_text(value: Any, default: str) -> str:
    text = value.strip() if isinstance(value, str) else ""
    return (text or default).splitlines()[0][:500]


def rpc_error(request_id: Any, code: int, message: str) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}


def state_path(client: str) -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is unavailable.")
    return Path(local_app_data) / "UnityAgentBridge" / f"{STATE_FILE_PREFIX}{client}-{os.getpid()}.json"


def write_state(project: Path, client: str, client_version: str, state: str) -> Path:
    path = state_path(client)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    temporary.write_text(
        json.dumps(
            {
                "state": state,
                "client": client,
                "clientVersion": client_version,
                "projectRoot": str(project),
                "pid": os.getpid(),
                "updatedAt": int(time.time()),
            },
            ensure_ascii=False,
            separators=(",", ":"),
        ),
        encoding="utf-8",
    )
    temporary.replace(path)
    return path


if __name__ == "__main__" and not _SOURCE_RELOADING:
    raise SystemExit(main())
