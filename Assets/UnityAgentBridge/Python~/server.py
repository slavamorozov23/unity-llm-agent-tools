from __future__ import annotations

import argparse
import contextlib
import json
import math
import os
import re
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable

import numpy as np
import wordninja
from fastembed import TextEmbedding
from PIL import Image, ImageDraw, ImageFont


MODEL_NAME = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
MODEL_READY_FILE = ".unity-agent-bridge-model-ready"
MAX_BODY_BYTES = 32 * 1024 * 1024
RESULT_COUNT = 10
# #line maps compiler errors to the agent's own lines: eval(line,column).
EVAL_TEMPLATE = """using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class UnityAgentBridgeEval
{
    public static object Run()
    {
#line 1 "eval"
__BODY__
#line hidden
        return null;
    }
}
"""
GAME_BUTTONS = {"left", "right", "middle"}
PLAY_START_SECONDS = 240.0
WAIT_BUDGET_SECONDS = 330.0
LANDSCAPE_SCREENSHOT_LIMIT = (1280, 720)
PORTRAIT_SCREENSHOT_LIMIT = (720, 1280)


class UnityGameInput:
    def __init__(self, unity: Any, frame_width: int, frame_height: int, capture: Any) -> None:
        self.unity = unity
        self.capture = capture
        self.frame_width = positive_integer(frame_width, "frame width")
        self.frame_height = positive_integer(frame_height, "frame height")
        self.held_keys: list[str] = []
        self.held_button: str | None = None
        self.last_point = (0.0, 0.0)
        self.executed_actions = 0
        # A capture or a finished wait leaves the game paused; it goes on only when the next action needs it,
        # so screenshots take no game time and show the moment a wait ended.
        self.captured = False

    def perform(self, actions: list[dict[str, Any]]) -> None:
        for action in actions:
            kind = action["action"]
            if self.captured and kind not in {"screenshot", "zoom", "wait"}:
                self.unity.call("resume-game")
                self.captured = False
            if kind == "click":
                self.click(action, 1)
            elif kind == "double_click":
                self.click(action, 2)
            elif kind == "hover":
                self.point_action("hover", action, include_button=False)
            elif kind == "drag":
                self.drag(action)
            elif kind == "scroll":
                self.point_action(
                    "scroll",
                    action,
                    include_button=False,
                    extra={"deltaX": action.get("scrollX", 0), "deltaY": action.get("scrollY", 0)},
                )
            elif kind == "press_key":
                self.press_key(action["key"], action["duration"])
            elif kind == "key_down":
                self.dispatch("key-down", name=action["key"])
                self.held_keys.append(action["key"])
            elif kind == "key_up":
                self.dispatch("key-up", name=action["key"])
                if action["key"] in self.held_keys:
                    self.held_keys.remove(action["key"])
            elif kind == "type_text":
                self.dispatch("type-text", name=action["text"])
            elif kind == "wait":
                self.wait(action["seconds"], action.get("timeScale", 1.0), action.get("object"), action.get("state", "visible"))
            elif kind in {"screenshot", "zoom"}:
                self.capture(action)
            else:
                raise RuntimeError(f"Validated action was not implemented: {kind}")
            self.executed_actions += 1

    def wait(self, seconds: float, scale: float, target: str | None = None, state: str = "visible") -> None:
        previous_scale = required_string(
            self.unity.call("multiply-game-time-scale", action=repr(scale)),
            "message",
        )
        try:
            stopped = float(previous_scale) == 0.0
            if stopped and not target:
                if self.captured:
                    self.unity.call("resume-game")
                    self.captured = False
                time.sleep(seconds)
                return
            # Counted from the paused capture frame when there is one; Unity stops the game on the frame the wait ends,
            # and the next action goes on from there, so a screenshot after it shows exactly that moment.
            started = float(required_string(self.unity.call("get-game-time"), "message"))
            if target:
                condition = {"path": target, "state": state, "seconds": seconds, "realTime": stopped}
                self.unity.call("pause-game-when", action=json.dumps(condition))
            else:
                self.unity.call("pause-game-at", action=repr(started + seconds))
            if self.captured:
                self.unity.call("resume-game")
            self.captured = True
            deadline = time.monotonic() + seconds / (1.0 if stopped else scale) * 4.0 + 5.0
            last, moved = started, time.monotonic()
            while True:
                if target:
                    result = required_string(self.unity.call("game-wait-status"), "message")
                    if result == "met":
                        return
                    if result.startswith("timeout"):
                        raise TimeoutError(f"{target} did not become {state} in {seconds:g} s: {result.partition(': ')[2]}.")
                    if stopped:
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Unity did not update.")
                        time.sleep(0.01)
                        continue
                current = float(required_string(self.unity.call("get-game-time"), "message"))
                if not target and current - started >= seconds:
                    return
                if current != last:
                    last, moved = current, time.monotonic()
                elif time.monotonic() - moved >= 1.0:
                    self.stalled()
                    moved = time.monotonic()
                if time.monotonic() >= deadline:
                    raise TimeoutError("Unity game time did not advance.")
                time.sleep(0.01)
        finally:
            self.unity.call("restore-game-time-scale")

    # Game time stood still for a second: the bridge's own pause goes on; what stopped it otherwise is the agent's to know.
    def stalled(self) -> None:
        info = unity_message_json(self.unity.call("get-status"))
        if info.get("paused"):
            by = info.get("pausedBy")
            if by == "error":
                raise RuntimeError(f"Error Pause stopped the game: {info.get('lastError', '')}")
            if by == "editor":
                raise RuntimeError("The game was paused by Debug.Break or the Pause button.")
            self.unity.call("resume-game")
        elif info.get("timeScale", 1) == 0:
            raise RuntimeError("Time.timeScale is 0, so game time stands still.")

    def release_all(self) -> None:
        if self.held_button is not None:
            self.dispatch(
                "mouse-up",
                values={"x": self.last_point[0], "y": self.last_point[1], "button": self.held_button},
            )
            self.held_button = None
        for key in reversed(self.held_keys):
            self.dispatch("key-up", name=key)
        self.held_keys.clear()

    def point_action(
        self,
        action: str,
        source: dict[str, Any],
        include_button: bool = True,
        extra: dict[str, Any] | None = None,
    ) -> None:
        values: dict[str, Any] = {"x": source["x"], "y": source["y"]}
        if include_button:
            values["button"] = source.get("button", "left")
        if extra:
            values.update(extra)
        self.dispatch(action, values=values)

    def click(self, action: dict[str, Any], count: int) -> None:
        button = action.get("button", "left")
        point = {"x": action["x"], "y": action["y"]}
        self.dispatch("hover", values=point)
        for index in range(count):
            values = {"x": action["x"], "y": action["y"], "button": button, "clickCount": index + 1}
            self.dispatch("mouse-down", values=values)
            self.held_button = button
            self.last_point = (action["x"], action["y"])
            time.sleep(0.2)
            self.dispatch("mouse-up", values=values)
            self.held_button = None
            if index + 1 < count:
                time.sleep(0.1)

    def press_key(self, chord: str, duration: float) -> None:
        keys = [part.strip() for part in chord.split("+")]
        if any(not key for key in keys):
            raise ValueError("A key chord contains an empty key.")
        pressed: list[str] = []
        try:
            for key in keys:
                self.dispatch("key-down", name=key)
                pressed.append(key)
            time.sleep(duration)
        finally:
            for key in reversed(pressed):
                self.dispatch("key-up", name=key)

    def drag(self, action: dict[str, Any]) -> None:
        button = action.get("button", "left")
        start = (action["x"], action["y"])
        target = (action["targetX"], action["targetY"])
        duration = action["duration"]
        self.dispatch("hover", values={"x": start[0], "y": start[1]})
        self.dispatch("mouse-down", values={"x": start[0], "y": start[1], "button": button})
        self.held_button = button
        self.last_point = start
        try:
            time.sleep(0.2)
            started = time.monotonic()
            steps = max(1, round(duration * 30))
            for step in range(1, steps + 1):
                target_time = started + duration * step / steps
                delay = target_time - time.monotonic()
                if delay > 0:
                    time.sleep(delay)
                progress = step / steps
                self.last_point = (
                    start[0] + (target[0] - start[0]) * progress,
                    start[1] + (target[1] - start[1]) * progress,
                )
                self.dispatch(
                    "mouse-drag",
                    values={"x": self.last_point[0], "y": self.last_point[1], "button": button},
                )
        finally:
            self.dispatch(
                "mouse-up",
                values={"x": self.last_point[0], "y": self.last_point[1], "button": button},
            )
            self.held_button = None

    def dispatch(self, action: str, name: str | None = None, values: dict[str, Any] | None = None) -> None:
        entries = {
            "frameWidth": self.frame_width,
            "frameHeight": self.frame_height,
            **(values or {}),
        }
        self.unity.call(
            "dispatch-game-input",
            action=action,
            name=name,
            values=[{"path": key, "value": str(value)} for key, value in entries.items()],
        )


def write_atomic(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + str(os.getpid()) + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def acquire_start_lock(runtime: Path):
    path = runtime / "server-start.lock"
    handle = path.open("a+b")
    handle.seek(0)
    if handle.read(1) != b"1":
        handle.seek(0)
        handle.write(b"1")
        handle.flush()
    handle.seek(0)
    try:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        handle.close()
        return None
    return handle


def unlink_when_available(path: Path, timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while True:
        try:
            path.unlink(missing_ok=True)
            return
        except PermissionError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.05)


def resize_game_screenshot(path: Path, width: int, height: int) -> tuple[int, int]:
    with Image.open(path) as source:
        source.load()
        if source.size != (width, height):
            raise RuntimeError("Unity screenshot size does not match its metadata.")
        limit = LANDSCAPE_SCREENSHOT_LIMIT if width >= height else PORTRAIT_SCREENSHOT_LIMIT
        if width <= limit[0] and height <= limit[1]:
            return width, height
        resized = source.convert("RGB")

    try:
        resized.thumbnail(limit, Image.Resampling.LANCZOS)
        temporary = path.with_suffix(path.suffix + ".tmp")
        resized.save(temporary, format="PNG", compress_level=6)
        temporary.replace(path)
        return resized.size
    finally:
        resized.close()


# As computer-use's scale: a smaller picture for fewer tokens; x/y stay in the full frame's pixels.
def scale_game_screenshot(path: Path, scale: float) -> None:
    with Image.open(path) as source:
        scaled = source.convert("RGB")
    try:
        scaled.thumbnail((max(1, round(scaled.width * scale)), max(1, round(scaled.height * scale))), Image.Resampling.LANCZOS)
        temporary = path.with_suffix(path.suffix + ".tmp")
        scaled.save(temporary, format="PNG", compress_level=6)
        temporary.replace(path)
    finally:
        scaled.close()


def crop_game_screenshot(path: Path, width: int, height: int, frame_width: int, frame_height: int, region: list[float]) -> None:
    x0, y0, x1, y1 = region
    if x0 < 0 or y0 < 0 or x1 > frame_width or y1 > frame_height:
        raise ValueError(f"zoom region must lie inside the last frame {frame_width}x{frame_height}.")
    scale_x = width / frame_width
    scale_y = height / frame_height
    with Image.open(path) as source:
        cropped = source.convert("RGB").crop((round(x0 * scale_x), round(y0 * scale_y), round(x1 * scale_x), round(y1 * scale_y)))
    try:
        limit = LANDSCAPE_SCREENSHOT_LIMIT if cropped.width >= cropped.height else PORTRAIT_SCREENSHOT_LIMIT
        cropped.thumbnail(limit, Image.Resampling.LANCZOS)
        temporary = path.with_suffix(path.suffix + ".tmp")
        cropped.save(temporary, format="PNG", compress_level=6)
        temporary.replace(path)
    finally:
        cropped.close()


def discovery_file(name: str) -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is unavailable.")
    return Path(local_app_data) / "UnityAgentBridge" / name


def load_model(model_cache: Path) -> TextEmbedding:
    model_cache.mkdir(parents=True, exist_ok=True)
    ready_file = model_cache / MODEL_READY_FILE
    installed = ready_file.is_file() and ready_file.read_text(encoding="utf-8").strip() == MODEL_NAME
    if not installed:
        from huggingface_hub import snapshot_download
        from huggingface_hub.utils import close_session

        repository = "qdrant/paraphrase-multilingual-MiniLM-L12-v2-onnx-Q"
        patterns = [
            "config.json",
            "model_optimized.onnx",
            "tokenizer.json",
            "tokenizer_config.json",
            "special_tokens_map.json",
            "preprocessor_config.json",
        ]
        for attempt in range(6):
            try:
                snapshot_download(
                    repo_id=repository,
                    allow_patterns=patterns,
                    cache_dir=str(model_cache),
                    max_workers=1,
                )
                break
            except Exception as error:
                close_session()
                if attempt == 5:
                    raise RuntimeError("NLP model could not be downloaded: " + str(error)) from error
                time.sleep(min(2 ** attempt, 10))

    try:
        model = TextEmbedding(model_name=MODEL_NAME, cache_dir=str(model_cache), local_files_only=True)
        next(model.embed(["Unity Agent Bridge"]))
    except Exception as error:
        raise RuntimeError("NLP model could not be loaded: " + str(error)) from error
    if not installed:
        temporary = ready_file.with_suffix(".tmp")
        temporary.write_text(MODEL_NAME, encoding="utf-8")
        temporary.replace(ready_file)
    return model


def format_megabytes(value: float) -> str:
    return f"{value / 1024:.2f} GB" if value >= 1024 else f"{value:.1f} MB"


def format_count(value: float) -> str:
    if value >= 1_000_000:
        return f"{value / 1_000_000:.1f}M"
    if value >= 100_000:
        return f"{value / 1000:.0f}k"
    return f"{value:.0f}"


class UnityRpc:
    def __init__(self, project: Path) -> None:
        self.requests = project / "Library" / "UnityAgentBridge" / "Requests"
        self.responses = project / "Library" / "UnityAgentBridge" / "Responses"
        # Notes Unity attaches to a response (a Play Mode edit is temporary), shown once with the operation's result.
        self.notes: list[str] = []

    def call(self, command: str, _timeout_seconds: float = 120.0, **arguments: Any) -> dict[str, Any]:
        request_id = f"{time.time_ns():020d}-{uuid.uuid4().hex}"
        payload = {"id": request_id, "command": command, **arguments}
        self.requests.mkdir(parents=True, exist_ok=True)
        self.responses.mkdir(parents=True, exist_ok=True)
        request_path = self.requests / f"{request_id}.request.json"
        response_path = self.responses / f"{request_id}.response.json"
        cancellation_path = request_path.with_suffix(request_path.suffix + ".cancel")
        write_atomic(request_path, payload)

        started = time.monotonic()
        deadline = started + _timeout_seconds
        next_dialog_check = started + 1.0
        dialog = None
        safe_mode = None
        while time.monotonic() < deadline:
            if response_path.is_file():
                # Windows can hold a just-renamed file for a moment (Unity's move, antivirus): read it on the next pass.
                try:
                    result = json.loads(response_path.read_text(encoding="utf-8"))
                except (PermissionError, json.JSONDecodeError):
                    time.sleep(0.025)
                    continue
                try:
                    response_path.unlink()
                except PermissionError:
                    pass
                if not result.get("ok", False):
                    raise RuntimeError(result.get("error", "Unity command failed without an error message."))
                if result.get("note") and result["note"] not in self.notes:
                    self.notes.append(result["note"])
                return result
            if time.monotonic() >= next_dialog_check:
                close_unity_menus(self.requests.parent)
                dialog = unity_dialog(self.requests.parent)
                safe_mode = None if dialog else unity_safe_mode(self.requests.parent.parent.parent)
                if dialog or safe_mode:
                    break
                next_dialog_check = time.monotonic() + 1.0
            time.sleep(0.025)
        cancellation_path.write_text("cancelled", encoding="utf-8")
        response_path.unlink(missing_ok=True)
        if dialog:
            raise UnityDialogError(dialog)
        if safe_mode:
            raise UnitySafeModeError(safe_mode)
        raise TimeoutError(f"Unity did not become ready for the queued request within {_timeout_seconds:g} seconds; the request was cancelled.")


class UnityDialogError(TimeoutError):
    def __init__(self, dialog: str) -> None:
        super().__init__(f"Unity is waiting for its dialog {dialog}; answer it in Unity. The request was cancelled or its result is unknown.")
        self.dialog = dialog


class UnitySafeModeError(TimeoutError):
    def __init__(self, errors: list[str]) -> None:
        super().__init__(
            "Unity is in Safe Mode: scripts have compile errors, so the bridge is not loaded. Fix them in code; "
            "Unity leaves Safe Mode after a clean compile.\n" + "\n".join(errors)
        )
        self.errors = errors


# Unity runs (it writes EditorInstance.json), the bridge never started in it, and Editor.log has compile errors.
def unity_safe_mode(project: Path) -> list[str] | None:
    try:
        pid = int(json.loads((project / "Library" / "EditorInstance.json").read_text(encoding="utf-8"))["process_id"])
    except (OSError, ValueError, KeyError, TypeError):
        return None
    if any((project / "Library" / "UnityAgentBridge").glob(f"Logs-{pid}-*.jsonl")):
        return None
    try:
        lines = (Path(os.environ.get("LOCALAPPDATA", "")) / "Unity" / "Editor" / "Editor.log").read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return None
    errors = list(dict.fromkeys(line.strip() for line in lines if ": error CS" in line))
    return errors[-5:] or None


def unity_process_id(runtime: Path) -> int | None:
    try:
        return int(json.loads((runtime.parent / "EditorInstance.json").read_text(encoding="utf-8"))["process_id"])
    except (OSError, ValueError, KeyError, TypeError):
        try:
            log = max(runtime.glob("Logs-*-*.jsonl"), key=lambda path: path.stat().st_mtime)
            return int(log.name.split("-")[1])
        except (ValueError, OSError):
            return None


# A native popup menu (context menu, dropdown) runs its own modal loop and stops Unity's update until it closes.
def close_unity_menus(runtime: Path) -> None:
    pid = unity_process_id(runtime) if sys.platform == "win32" else None
    if pid is None:
        return
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    visitor = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    menus: list[int] = []

    def visit(handle: int, _: int) -> bool:
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(handle, ctypes.byref(owner))
        buffer = ctypes.create_unicode_buffer(16)
        user32.GetClassNameW(handle, buffer, 16)
        if owner.value == pid and buffer.value == "#32768" and user32.IsWindowVisible(handle):
            menus.append(handle)
        return True

    user32.EnumWindows(visitor(visit), 0)
    for handle in menus:
        user32.PostMessageW(handle, 0x0100, 0x1B, 0)  # WM_KEYDOWN Escape, once per open submenu level
        user32.PostMessageW(handle, 0x0101, 0x1B, 0)


# A modal dialog waiting for an answer, or with progress=True the progress window Unity shows while it is busy.
def unity_dialog(runtime: Path, progress: bool = False) -> str | None:
    if sys.platform != "win32":
        return None
    pid = unity_process_id(runtime)
    if pid is None:
        return None
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    visitor = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def window_text(handle: int, class_name: bool = False) -> str:
        buffer = ctypes.create_unicode_buffer(512)
        (user32.GetClassNameW if class_name else user32.GetWindowTextW)(handle, buffer, 512)
        return buffer.value

    found: list[str] = []

    def visit(handle: int, _: int) -> bool:
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(handle, ctypes.byref(owner))
        if owner.value != pid or not user32.IsWindowVisible(handle) or window_text(handle, True) != "#32770":
            return True
        buttons: list[str] = []
        texts: list[str] = []
        bar = False

        def visit_child(child: int, _: int) -> bool:
            nonlocal bar
            kind = window_text(child, True)
            bar = bar or kind == "msctls_progress32"
            if kind == "Button" and window_text(child):
                buttons.append(window_text(child).replace("&", ""))
            if kind == "Static" and window_text(child).strip():
                texts.append(window_text(child).strip())
            return True

        user32.EnumChildWindows(handle, visitor(visit_child), 0)
        if bar != progress:
            return True
        found.append(" ".join([window_text(handle)] + texts) if progress else f"'{window_text(handle)}' [{'|'.join(buttons)}]")
        return False

    user32.EnumWindows(visitor(visit), 0)
    return found[0] if found else None


class Operations:
    def __init__(self, project: Path, model_cache: Path) -> None:
        self.project = project.resolve()
        self.plugin_version, self.plugin_revision = project_plugin_identity(project)
        self.unity = UnityRpc(project)
        self.model = load_model(model_cache)
        self.embedding_file = model_cache / "embeddings.npz"
        self.embeddings = self.load_embeddings()
        self.queue_condition = threading.Condition()
        self.next_ticket = 0
        self.serving_ticket = 0
        self.last_game_frame_size: tuple[int, int] | None = None

    def invoke(self, request: dict[str, Any]) -> dict[str, Any]:
        if request.get("operation") == "health":
            return self._invoke(request)
        with self.queue_condition:
            ticket = self.next_ticket
            self.next_ticket += 1
            while ticket != self.serving_ticket:
                self.queue_condition.wait()
        try:
            self.unity.notes.clear()
            result = self._invoke(request)
            if self.unity.notes and isinstance(result, dict):
                result["note"] = " ".join(self.unity.notes)
            return result
        finally:
            with self.queue_condition:
                self.serving_ticket += 1
                self.queue_condition.notify_all()

    def _invoke(self, request: dict[str, Any]) -> dict[str, Any]:
        operation = required_string(request, "operation")
        paths = request.get("paths")
        if operation in {"object-info", "component-modify"} and isinstance(paths, list) and len(paths) > 1:
            return self.for_each_path(request, [str(path) for path in paths])
        if operation == "health":
            return {"ok": True, "status": "running", "model": MODEL_NAME, "pid": os.getpid()}
        if operation == "tree":
            scope = optional_string(request.get("path"))
            if scope.startswith("Assets/"):
                depth = request.get("depth")
                objects = self.unity.call("get-prefab-tree", path=scope).get("objects", [])
                return {"ok": True, "objects": [tree_entry(item) for item in objects if depth is None or int(item.get("depth", 0)) <= int(depth)]}
            objects = self.unity.call("get-scene-tree").get("objects", [])
            decoded_scope = urllib.parse.unquote(scope)
            scene_names = {scene_name_from_object_path(item.get("path", "")).casefold() for item in objects}
            scene_scope = decoded_scope if decoded_scope.strip("/") and "/" not in decoded_scope.strip("/") \
                and decoded_scope.strip("/").casefold() in scene_names else ""
            if scene_scope:
                scope = scene_scope
            if scope and not scene_scope:
                scope = self.resolve_object_path(scope)
            depth = request.get("depth")
            if depth is not None:
                if isinstance(depth, bool) or not isinstance(depth, int) or depth < 0:
                    raise ValueError("depth must be a non-negative integer.")
            if scope:
                matches = [item for item in objects if item.get("path", "") == scope]
                if scene_scope:
                    scene_name = scene_scope.strip("/")
                    matches = [
                        item for item in objects
                        if scene_name_from_object_path(item.get("path", "")).casefold() == scene_name.casefold()
                    ]
                    if not matches:
                        raise ValueError("Tree scene was not found: " + scene_scope)
                    base_depth = 0
                    objects = [
                        item for item in matches
                        if depth is None or int(item.get("depth", 0)) <= base_depth + depth
                    ]
                elif len(matches) != 1:
                    raise ValueError("Tree path was not found or is ambiguous: " + scope)
                else:
                    base_depth = int(matches[0].get("depth", 0))
                    prefix = scope.rstrip("/") + "/"
                    objects = [
                        item for item in objects
                        if (item.get("path", "") == scope or item.get("path", "").startswith(prefix))
                        and (depth is None or int(item.get("depth", 0)) <= base_depth + depth)
                    ]
            elif depth is not None:
                objects = [item for item in objects if int(item.get("depth", 0)) <= depth]
            return {"ok": True, "objects": [tree_entry(item) for item in objects]}
        if operation == "object-children":
            scope = self.resolve_object_path(required_string(request, "path"))
            objects = self.unity.call("get-scene-tree").get("objects", [])
            matches = [item for item in objects if item.get("path", "") == scope]
            if len(matches) != 1:
                raise ValueError("Object path was not found or is ambiguous: " + scope)
            return {
                "ok": True,
                "objects": [tree_entry(item) for item in objects if item.get("parentPath", "") == scope],
            }
        if operation == "find":
            return self.find_objects(request)
        if operation == "object-info":
            result = self.unity.call(
                "get-object-info",
                path=required_string(request, "path"),
                componentType=optional_string(request.get("componentType")),
                componentIndex=int(request.get("componentIndex", -1)),
                runtime=bool(request.get("runtime", False)),
                propertyPath=optional_string(request.get("propertyPath")) if request.get("runtime") and request.get("componentType") else "",
            )
            def runtime_value(index: int, name: str) -> Any:
                # component-modify sets C# properties too (Transform position), so they read back without --runtime.
                live = self.unity.call(
                    "get-object-info",
                    path=required_string(request, "path"),
                    componentType=required_string(request, "componentType"),
                    componentIndex=index,
                    runtime=True,
                    propertyPath=name,
                )
                return object_result(live, request["componentType"], -1, name, True)["value"]

            response = object_result(
                result,
                optional_string(request.get("componentType")),
                # Runtime values come for the one component Unity already picked by its index.
                -1 if request.get("runtime") else int(request.get("componentIndex", -1)),
                optional_string(request.get("propertyPath")),
                bool(request.get("runtime", False)),
                None if request.get("runtime") else runtime_value,
            )
            if result.get("note"):
                response["note"] = result["note"]
            return response
        if operation == "component-modify":
            return self.component_mutation("modify-component", request)
        if operation == "component-add":
            return self.component_mutation("add-component", request)
        if operation == "component-remove":
            return self.component_mutation("remove-component", request, include_values=False)
        if operation == "scene-save":
            return message_result(self.unity.call("save-scenes"))
        if operation == "component-action":
            path = required_string(request, "path")
            result = message_result(self.unity.call(
                "execute-component-action",
                path=path,
                componentType=required_string(request, "componentType"),
                componentIndex=int(request.get("componentIndex", -1)),
                action=required_string(request, "action"),
                values=request_values(request),
            ))
            result["path"] = path
            return result
        if operation == "asset-action":
            path = required_string(request, "path")
            result = message_result(self.unity.call(
                "execute-asset-action",
                path=path,
                action=required_string(request, "action"),
                values=request_values(request),
            ))
            result["path"] = path
            return result
        if operation == "component-suggest":
            return self.suggest_components(request)
        if operation == "object-picker":
            result = self.unity.call(
                "object-picker",
                path=required_string(request, "path"),
                componentType=required_string(request, "componentType"),
                componentIndex=int(request.get("componentIndex", -1)),
                propertyPath=required_string(request, "propertyPath"),
                limit=RESULT_COUNT,
            )
            return {
                "ok": True,
                "candidates": result.get("candidates", []),
            }
        if operation == "object-delete":
            return message_result(self.unity.call("delete-object", path=required_string(request, "path")))
        if operation == "object-duplicate":
            return object_result(self.unity.call(
                "duplicate-object",
                path=required_string(request, "path"),
                name=optional_string(request.get("name")),
            ))
        if operation == "asset-find":
            return self.find_assets(request)
        if operation == "object-create":
            name = optional_string(request.get("name"))
            template = optional_string(request.get("template"))
            if not name and not template:
                raise ValueError("name or template is required.")
            return object_result(self.unity.call(
                "create-empty-object",
                destinationPath=required_string(request, "parentPath"),
                name=name,
                templateName=template,
            ))
        if operation == "object-templates":
            templates = self.unity.call("list-object-creation-templates").get("objectTemplates", [])
            query = optional_string(request.get("query"))
            if not query:
                return {"ok": True, "templates": templates}
            lexical = lexical_matches(query, templates)
            if lexical:
                return {"ok": True, "candidates": lexical[:RESULT_COUNT]}
            ranked = self.rank(query, templates, RESULT_COUNT)
            return {"ok": True, "candidates": [templates[index] for index, _score in ranked]}
        if operation == "prefab-save":
            result = self.unity.call(
                "save-prefab",
                path=optional_string(request.get("path")),
                destinationPath=optional_string(request.get("prefab")),
            )
            return {"ok": True, "prefab": (result.get("prefabs") or [{}])[0]}
        if operation == "prefab-apply":
            result = self.unity.call(
                "apply-prefab",
                path=required_string(request, "path"),
                componentType=optional_string(request.get("componentType")),
                componentIndex=request.get("componentIndex", -1),
                propertyPath=optional_string(request.get("propertyPath")),
            )
            response = {"ok": True, "path": (result.get("objectInfo") or {}).get("path", request["path"])}
            if result.get("message"):
                response["message"] = result["message"]
            return response
        if operation == "prefab-instantiate":
            result = self.unity.call("instantiate-prefab", path=required_string(request, "prefab"), destinationPath=required_string(request, "parentPath"))
            # As the developer does right after dropping the prefab: rename it and place it (Transform fields).
            name = optional_string(request.get("name"))
            if name:
                result = self.unity.call("rename-object", path=result["objectInfo"]["path"], destinationPath=name)
            if request.get("values"):
                self.component_mutation("modify-component", {"path": result["objectInfo"]["path"], "componentType": "Transform", "values": request["values"]})
            return object_result(result)
        if operation == "prefab-revert":
            result = self.unity.call(
                "revert-prefab",
                path=required_string(request, "path"),
                componentType=optional_string(request.get("componentType")),
                componentIndex=request.get("componentIndex", -1),
                propertyPath=optional_string(request.get("propertyPath")),
            )
            return {"ok": True, "path": (result.get("objectInfo") or {}).get("path", request["path"])}
        if operation == "prefab-open":
            result = self.unity.call("open-prefab", path=required_string(request, "prefab"))
            return {"ok": True, "prefab": (result.get("prefabs") or [{}])[0]}
        if operation == "prefab-close":
            result = self.unity.call("close-prefab")
            # Saved and closed; scene commands address the scene again.
            return {"ok": True, "closed": ((result.get("prefabs") or [{}])[0]).get("assetPath", "")}
        if operation == "scene-open":
            self.ensure_edit_mode()
            result = self.unity.call("open-scene", path=required_string(request, "scene"))
            return {"ok": True, "scene": (result.get("scenes") or [{}])[0]}
        if operation == "creation-templates":
            # Menu items ("Create/...") learn their extension only when Unity creates the asset.
            # With --path, the entries of that asset's own create menu (Shader Graph nodes).
            path = optional_string(request.get("path"))
            templates = [{key: value for key, value in item.items() if value} for item in self.unity.call("list-creation-templates", path=path).get("templates", [])]
            query = optional_string(request.get("query"))
            if not query:
                return {"ok": True, "candidates": [{"template": item} for item in templates[:RESULT_COUNT]]}
            lexical = lexical_matches(query, templates, lambda item: item.get("name", ""))
            if lexical:
                return {"ok": True, "candidates": [{"template": item} for item in lexical[:RESULT_COUNT]]}
            documents = [
                f"Unity {'Shader Graph Node' if path else 'Creation Template'} {item.get('name', '')}"
                + (f" extension {item['extension']}" if item.get("extension") else "")
                + (f" also known as {item['keywords']}" if item.get("keywords") else "")
                for item in templates]
            ranked = self.rank(query, documents, RESULT_COUNT)
            return {"ok": True, "candidates": [{"score": score, "template": templates[index]} for index, score in ranked]}
        if operation == "asset-create":
            template = required_string(request, "template")
            path = required_string(request, "path")
            component_type = component_class_name(path) if template.casefold() == "c# script".casefold() else None
            if component_type is not None:
                existing = self.unity.call("list-component-types").get("componentTypes", [])
                if component_type in existing:
                    raise ValueError(f"Component type already exists: {component_type}")
            result = self.unity.call("create-asset", templateName=template, path=path, objectPath=optional_string(request.get("source")))
            response = asset_result(result)
            if component_type is not None:
                self.wait_for_component_type(component_type)
                response["componentType"] = component_type
            return response
        if operation == "asset-info":
            property_path = optional_string(request.get("propertyPath"))
            section = optional_string(request.get("section"))
            result = asset_result(
                self.unity.call(
                    "get-asset-info",
                    path=required_string(request, "path"),
                    propertyPath=property_path,
                    section=section,
                ),
                property_path,
                section,
            )
            info = result.get("assetInfo")
            # Shader Graph's view lists its own errors by node.
            if isinstance(info, dict) and info.get("type") == "UnityEngine.Shader" and not str(info.get("importerType", "")).endswith("ShaderGraphImporter"):
                # The Shader object's own fields are compiler internals the Inspector never shows.
                info["properties"] = [item for item in info.get("properties", []) if not str(item.get("path", "")).startswith("asset:")]
                shader = unity_message_json(self.unity.call("get-shader-info", path=info.get("assetPath")))
                info["shaderErrors"] = [
                    f"{item.get('severity')} {item.get('file') or ''}:{item.get('line')} {item.get('message')}".replace(" :0 ", " ")
                    for item in shader.get("errors", [])
                ]
            return result
        if operation == "asset-modify":
            values = request.get("values", [])
            if not isinstance(values, list) or not values:
                raise ValueError("values must be a non-empty list.")
            return asset_result(self.unity.call(
                "modify-asset",
                path=required_string(request, "path"),
                values=values,
                boolValue=bool(request.get("confirm", False)),
            ))
        if operation == "eval":
            return self.eval(required_string(request, "missing"), required_string(request, "code"))
        if operation == "shader-info":
            property_path = optional_string(request.get("propertyPath"))
            if not optional_string(request.get("path")):
                return {"ok": True, "global": unity_message_json(self.unity.call("get-global-shader-properties", propertyPath=property_path))}
            shader = unity_message_json(self.unity.call(
                "get-shader-info",
                path=required_string(request, "path"),
                propertyPath=property_path,
            ))
            return {"ok": True, "shader": shader_view(shader, keep_types=bool(property_path))}
        if operation == "material-modify":
            values = request.get("values", [])
            if not isinstance(values, list) or not values:
                raise ValueError("values must be a non-empty list.")
            material = shader_view(unity_message_json(self.unity.call(
                "modify-material",
                path=required_string(request, "path"),
                values=values,
            )))
            # Like the other *-modify commands: the path and what changed; the shader only when it was switched.
            result = {"ok": True, "path": material.get("path")}
            if any(isinstance(item, dict) and str(item.get("path", "")).lower() == "shader" for item in values):
                result["shader"] = material.get("shader")
            result.update({key: material[key] for key in ("properties", "defaults", "errors") if key in material})
            return result
        if operation == "asset-reimport":
            return asset_result(self.unity.call(
                "reimport-asset",
                path=required_string(request, "path"),
                boolValue=bool(request.get("boolValue", False)),
            ))
        if operation == "asset-import-package":
            return message_result(self.unity.call("import-package", path=required_string(request, "path")))
        if operation == "shader-errors":
            return {"ok": True, **unity_message_json(self.unity.call("get-shader-errors", _timeout_seconds=120.0))}
        if operation in {"refresh", "compile"}:
            play_stopped = operation == "compile" and self.ensure_edit_mode()
            marker = required_string(self.unity.call("refresh-assets", _timeout_seconds=360.0), "message")
            return {"ok": True, "refreshMarker": marker, "playStopped": play_stopped}
        if operation == "sprite-editor":
            return self.sprite_editor(request)
        if operation == "shader-preview":
            # Node previews of a Shader Graph (nodes/chain) and the Inspector preview on shapes, in one grid.
            path = required_string(request, "path")
            nodes = request.get("nodes") or []
            shapes = request.get("shapes") or []
            if not isinstance(nodes, list) or any(not isinstance(node, str) or not node.strip() for node in nodes):
                raise ValueError("nodes must be a list of node ids or names.")
            if not isinstance(shapes, list) or any(not isinstance(shape, str) or not shape.strip() for shape in shapes):
                raise ValueError("shapes must be a list of Sphere, Cube, Cylinder, Torus, Quad or Plane.")
            chain = bool(request.get("chain", False))
            if chain and len(nodes) != 2:
                raise ValueError("chain takes exactly two nodes: the start and the end (\"Output\" for the master preview).")
            if not nodes and not shapes:
                shapes = ["Sphere"]
            result: dict[str, Any] = {"ok": True, "screenshots": [], "labels": []}
            if nodes:
                graph = unity_message_json(self.unity.call("shader-graph-preview", path=path, names=nodes, boolValue=chain))
                result["screenshots"] += graph.get("screenshots", [])
                result["labels"] += graph.get("labels", [])
                if graph.get("errors"):
                    result["errors"] = graph["errors"]
            if shapes:
                material = unity_message_json(self.unity.call("material-preview", path=path, names=shapes))
                result["screenshots"] += material.get("screenshots", [])
                result["labels"] += material.get("labels", [])
            if len(result["screenshots"]) > 16:
                raise ValueError("A preview grid holds at most 16 images.")
            return result
        if operation == "asset-move":
            return asset_result(self.unity.call("move-asset", path=required_string(request, "path"), destinationPath=required_string(request, "destinationPath")))
        if operation == "asset-duplicate":
            return asset_result(self.unity.call("duplicate-asset", path=required_string(request, "path"), destinationPath=optional_string(request.get("destinationPath"))))
        if operation == "asset-delete":
            return message_result(self.unity.call("delete-asset", path=required_string(request, "path")))
        if operation == "asset-object-picker":
            result = self.unity.call("asset-object-picker", path=required_string(request, "path"), propertyPath=required_string(request, "propertyPath"), limit=RESULT_COUNT)
            return {"ok": True, "candidates": result.get("candidates", [])}
        if operation == "object-move":
            sibling_index = request.get("siblingIndex")
            return object_result(self.unity.call(
                "move-object",
                path=required_string(request, "path"),
                destinationPath=required_string(request, "destinationPath"),
                siblingIndex=-1 if sibling_index is None else int(sibling_index),
            ))
        if operation == "object-rename":
            return object_result(self.unity.call("rename-object", path=required_string(request, "path"), destinationPath=required_string(request, "destinationPath")))
        if operation == "object-active":
            return object_result(self.unity.call("set-active", path=required_string(request, "path"), boolValue=required_bool(request, "boolValue")))
        if operation in {"object-layer", "object-static"}:
            path = required_string(request, "path")
            key = "layer" if operation == "object-layer" else "static"
            arguments = {"layer": required_string(request, "layer")} if key == "layer" else {"value": required_string(request, "static")}
            result = self.unity.call("set-" + key, path=path, boolValue=bool(request.get("children", False)), **arguments)
            return {"ok": True, "path": path, key: result.get("message", "")}
        if operation == "object-tag":
            path = required_string(request, "path")
            tag = required_string(request, "tag")
            result = self.unity.call("set-tag", path=path, name=tag)
            return {"ok": True, "path": path, "tag": result.get("message", "")}
        if operation == "logs":
            if request.get("clear") is True:
                return message_result(self.unity.call("clear-logs"))
            return self.logs(request)
        if operation == "status":
            return self.status()
        if operation == "play":
            action = required_action(request, {"start", "stop", "pause", "resume", "step"})
            if action in {"start", "stop"}:
                # Like menu: the errors this start or stop logged, not the Console's earlier ones.
                started = datetime.now(timezone.utc)
                self.unity.call("set-play-mode", action=action)
                self.wait_for_play_mode(action == "stop")
                status = {key: value for key, value in self.status().items() if key not in {"errors", "lastError"}}
                return {**status, **self.errors_since(started)}
            elif action == "step":
                frames = request.get("frames", 1)
                if isinstance(frames, bool) or not isinstance(frames, int) or frames < 1 or frames > 600:
                    raise ValueError("frames must be between 1 and 600.")
                for _frame in range(frames):
                    self.unity.call("step-game")
            else:
                self.unity.call("pause-game" if action == "pause" else "resume-game")
            return self.status()
        if operation == "menu":
            return self.menu(request)
        if operation == "lighting":
            return self.lighting(request)
        if operation == "profiler":
            return self.profiler(request)
        if operation == "profiler-hierarchy":
            limit = request.get("limit", 10)
            if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1 or limit > 100:
                raise ValueError("limit must be between 1 and 100.")
            result = self.unity.call(
                "profiler-hierarchy",
                value=optional_string(request.get("frame")),
                name=optional_string(request.get("thread")),
                query=optional_string(request.get("query")),
                path=optional_string(request.get("path")),
                action=optional_string(request.get("sort")),
                limit=limit,
            )
            return {"ok": True, **unity_message_json(result)}
        if operation.startswith("timeline-"):
            return self.timeline(operation, request)
        if operation == "game-resolutions":
            result = self.unity.call("list-game-resolutions")
            selected = result.get("resolution") or {}
            return {"ok": True, "resolutions": [
                f"{item['width']}x{item['height']}" + (" (selected)" if item == selected else "")
                for item in result.get("resolutions", [])
            ]}
        if operation == "game-resolution":
            width = positive_integer(request.get("width"), "width")
            height = positive_integer(request.get("height"), "height")
            result = self.unity.call("set-game-resolution", width=width, height=height)
            self.last_game_frame_size = None
            return {"ok": True, "resolution": result.get("resolution", {})}
        if operation == "packages":
            result = self.wait_for_package_operation("list-packages")
            return {"ok": True, "packages": [installed_package_entry(item) for item in result.get("packages", [])]}
        if operation == "packages-refresh":
            result = self.wait_for_package_operation("resolve-packages")
            return {
                "ok": True,
                "message": result.get("message", "Packages resolved."),
                "packageCount": len(result.get("packages", [])),
            }
        if operation == "packages-search":
            result = self.wait_for_package_operation("search-packages")
            packages = result.get("packages", [])
            documents = [package_document(item) for item in packages]
            ranked = self.rank(required_string(request, "query"), documents, RESULT_COUNT)
            return {
                "ok": True,
                "candidates": [
                    {"score": round(score, 4), "package": searched_package_entry(packages[index])}
                    for index, score in ranked
                ],
            }
        if operation in {"package-install", "package-update"}:
            names = required_string_list(request, "names")
            result = self.wait_for_package_operation(
                "add-package",
                names=names,
                action=optional_string(request.get("version")),
            )
            return {
                "ok": True,
                "packages": [installed_package_entry(item) for item in result.get("packages", [])],
                "dependencyChanges": result.get("packageChanges", []),
            }
        if operation == "package-remove":
            result = self.wait_for_package_operation("remove-package", names=required_string_list(request, "names"))
            return message_result(result)
        if operation == "input-axes":
            result = self.unity.call("list-input-axes")
            return {"ok": True, "axes": result.get("axes", [])}
        if operation == "input-axis-create":
            values = request.get("values", [])
            if not isinstance(values, list):
                raise ValueError("values must be a list.")
            result = self.unity.call("create-input-axis", name=required_string(request, "name"), values=values)
            return {"ok": True, "axis": result.get("axis", {})}
        if operation == "input-axis-delete":
            return message_result(self.unity.call("delete-input-axis", name=required_string(request, "name")))
        if operation == "build-scenes":
            return {"ok": True, **unity_message_json(self.unity.call("list-build-scenes"))}
        if operation == "build-scene":
            action = required_action(request, {"add", "remove", "enable", "disable", "move"})
            result = unity_message_json(self.unity.call(
                "mutate-build-scene",
                action=action,
                path=required_string(request, "path"),
                siblingIndex=request.get("index", -1),
            ))
            return {"ok": True, **result}
        if operation == "scene-screenshot":
            query = required_string(request, "query")
            mode = (optional_string(request.get("mode")) or "grid").casefold()
            if mode not in {"grid", "flat"}:
                raise ValueError("mode must be grid or flat.")
            objects = [
                item for item in self.unity.call("get-scene-tree").get("objects", [])
                if item.get("activeInHierarchy", False) and item.get("visual", False)
            ]
            if not objects:
                raise RuntimeError("The current scene has no active 2D or 3D geometry to capture.")
            # A path or a name is that object (same-named ones together); only a description goes to the model, which
            # frames its single best match — framing several guesses at once showed the whole building around them.
            if query.startswith("/"):
                paths = [self.resolve_object_path(query)]
            else:
                lexical = ranked_names(query, objects, lambda item: item.get("name", ""), lambda item: urllib.parse.unquote(item.get("path", "")))
                if lexical:
                    best = lexical[0].get("name", "").casefold()
                    paths = [item.get("path", "") for item in lexical if item.get("name", "").casefold() == best][:4]
                else:
                    paths = [objects[index].get("path", "") for index, _score in self.rank(query, [scene_document(item) for item in objects], 1)]
            result = self.unity.call("capture-scene", paths=paths, action=mode, value=optional_string(request.get("from")))
            return {
                "ok": True,
                "screenshots": result.get("screenshots", []),
                "labels": result.get("screenshotLabels", []),
                "targets": paths,
            }
        if operation in {"animation-table", "animation-clip-info"}:
            search_path = optional_string(request.get("path")) or "Assets/Animations"
            if search_path.casefold().endswith(".anim"):
                table = unity_message_json(self.unity.call("get-animation-table", clip=search_path))
            else:
                clips = unity_message_json(self.unity.call("list-animation-clips", path=search_path)).get("clips", [])
                ranked = self.rank(required_string(request, "query"), [animation_clip_document(item) for item in clips], 1)
                if not ranked:
                    raise RuntimeError(search_path + " contains no .anim clips.")
                clip = clips[ranked[0][0]]
                table = unity_message_json(self.unity.call("get-animation-table", clip=clip.get("path", "")))
            if operation == "animation-clip-info":
                return {"ok": True, "clip": compact_animation_clip(table)}
            return {"ok": True, "table": table}
        if operation == "animation-properties":
            query = optional_string(request.get("query"))
            properties = unity_message_json(self.unity.call("get-animation-properties", path=required_string(request, "path"), boolValue=bool(query))).get("properties", [])
            if query:
                ranked = self.rank(query, [animation_property_document(item) for item in properties], RESULT_COUNT)
                properties = [{"score": round(score, 4), "property": properties[index]} for index, score in ranked]
            return {"ok": True, "properties": properties}
        if operation == "animation-clip-create":
            path = required_string(request, "path")
            name = optional_string(request.get("name"))
            if path.casefold().endswith(".anim"):
                path_name = Path(path).stem
                if name and Path(name).stem.casefold() != path_name.casefold():
                    raise ValueError("name must match the .anim filename in path.")
                name = path_name
            if not name:
                raise ValueError("name is required when path is a folder.")
            result = self.unity.call("create-animation-clip", name=name, path=path)
            clip = unity_message_json(result)
            try:
                for setting in request_values(request):
                    value = json.loads(setting["value"])
                    self.unity.call(
                        "animation-clip-setting",
                        action="set",
                        clip=clip.get("path", path),
                        propertyPath=setting["path"],
                        value=str(value),
                    )
            except BaseException as error:
                try:
                    self.unity.call("delete-animation-clip", clip=clip.get("path", path))
                except BaseException as rollback_error:
                    raise RuntimeError(f"{error}; clip rollback failed: {rollback_error}") from error
                raise
            return {"ok": True, "clip": clip}
        if operation == "animation-clip-delete":
            result = self.unity.call("delete-animation-clip", clip=required_string(request, "clip"))
            return {"ok": True, "clip": unity_message_json(result)}
        if operation == "animation-property":
            action = required_action(request, {"get", "create", "modify", "delete"})
            # The Animation window's event row is edited like a property: --property Events --key frame=12 function=Open.
            if (optional_string(request.get("property")) or "").casefold() in {"events", "event"}:
                if action == "get":
                    table = unity_message_json(self.unity.call("get-animation-table", clip=required_string(request, "clip")))
                    return {"ok": True, "events": table.get("events", [])}
                keys = request.get("keys", [])
                if not isinstance(keys, list) or not all(isinstance(key, dict) for key in keys):
                    raise ValueError("keys must be a list of objects.")
                unknown = {name for key in keys for name in key} - {"frame", "time", "function", "float", "int", "string", "object"}
                if unknown:
                    raise ValueError("Event keys take frame|time, function, float|int|string|object; unknown: " + ", ".join(sorted(unknown)))
                events = [{**{name: str(value) if name in {"function", "string", "object"} else value for name, value in key.items()},
                           "hasFrame": "frame" in key, "hasTime": "time" in key} for key in keys]
                result = self.unity.call("mutate-animation-events", action=action, clip=required_string(request, "clip"),
                                         json=json.dumps({"keys": events}, ensure_ascii=False))
                return {"ok": True, "events": unity_message_json(result).get("events", [])}
            if action == "get":
                table = unity_message_json(self.unity.call("get-animation-table", clip=required_string(request, "clip")))
                if not optional_string(request.get("property")):
                    return {"ok": True, "table": table}
                object_path = optional_string(request.get("objectPath"))
                property_path = required_string(request, "property")
                rows = [
                    row for row in table.get("rows", [])
                    if animation_property_matches(row, property_path)
                    and row.get("objectPath", "") == object_path
                ]
                if len(rows) != 1:
                    raise ValueError("Animation property was not found or is ambiguous.")
                row = rows[0]
                return {
                    "ok": True,
                    "property": {
                        "id": row.get("id", ""),
                        "objectPath": row.get("objectPath", ""),
                        "kind": row.get("kind", ""),
                        "keys": [cell for cell in row.get("cells", []) if cell.get("hasKey")],
                    },
                }
            keys = request.get("keys", [])
            if not isinstance(keys, list):
                raise ValueError("keys must be a list.")
            normalized_keys = []
            for key in keys:
                if not isinstance(key, dict):
                    raise ValueError("Each animation key must be an object.")
                normalized_keys.append({
                    **key,
                    "hasTime": "time" in key,
                    "hasValue": "value" in key,
                    "hasInTangent": "inTangent" in key,
                    "hasOutTangent": "outTangent" in key,
                    "hasInWeight": "inWeight" in key,
                    "hasOutWeight": "outWeight" in key,
                    "hasWeightedMode": "weightedMode" in key,
                })
            result = self.unity.call(
                "mutate-animation-property",
                action=action,
                clip=required_string(request, "clip"),
                objectPath=optional_string(request.get("objectPath")),
                propertyPath=required_string(request, "property"),
                json=json.dumps({"keys": normalized_keys}, ensure_ascii=False),
            )
            return {"ok": True, "clip": unity_message_json(result), "property": request["property"]}
        if operation == "animation-clip-setting":
            action = required_action(request, {"get", "set"})
            if action == "set" and "value" not in request:
                raise ValueError("value is required for set.")
            result = self.unity.call(
                "animation-clip-setting",
                action=action,
                clip=required_string(request, "clip"),
                propertyPath=required_string(request, "parameter"),
                value=None if action == "get" else str(request.get("value")),
            )
            return {"ok": True, "setting": unity_message_json(result)}
        if operation == "animator-find":
            path = optional_string(request.get("path"))
            if path:
                return {"ok": True, "animator": unity_message_json(self.unity.call("get-animator", path=path))}
            query = required_string(request, "query")
            animators = unity_message_json(self.unity.call("list-animators")).get("animators", [])
            ranked = self.rank(query, [animator_document(item) for item in animators], RESULT_COUNT)
            return {"ok": True, "candidates": [{"score": round(score, 4), "animator": animators[index]} for index, score in ranked]}
        if operation == "animator-component":
            action = required_action(request, {"create", "delete"})
            result = self.unity.call(
                "mutate-animator",
                action=action,
                path=required_string(request, "path"),
                controller=optional_string(request.get("controller")),
            )
            return {"ok": True, "animator": unity_message_json(result)}
        if operation == "animator-controller-assign":
            action = required_action(request, {"assign", "detach"})
            result = self.unity.call(
                "assign-animator-controller",
                action=action,
                path=required_string(request, "path"),
                controller=optional_string(request.get("controller")),
            )
            return {"ok": True, "animator": unity_message_json(result)}
        if operation == "animator-motions":
            path, controller = self.animator_source(request)
            result = self.unity.call("get-animator-motions", path=path, controller=controller)
            data = unity_message_json(result)
            return {"ok": True, "states": [compact_motion_state(item) for item in data.get("states", [])]}
        if operation == "animator-graph":
            path = optional_string(request.get("path"))
            controller = optional_string(request.get("controller"))
            if path and path.replace("\\", "/").startswith("Assets/"):
                controller = path
                path = None
            result = self.unity.call(
                "get-animator-controller",
                path=path,
                controller=controller,
            )
            return {"ok": True, "controller": compact_controller_graph(unity_message_json(result))}
        if operation == "animator-state":
            result = self.unity.call(
                "mutate-animator-state",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                state=required_string(request, "state"),
                stateMachine=optional_string(request.get("stateMachine")),
                motion=optional_string(request.get("motion")),
                values=request_values(request),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-state-motion":
            result = self.unity.call(
                "assign-animator-state-motion",
                action=required_action(request, {"assign", "detach"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                state=required_string(request, "state"),
                stateMachine=optional_string(request.get("stateMachine")),
                motion=optional_string(request.get("motion")),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-transition":
            conditions = request.get("conditions")
            if conditions is not None and not isinstance(conditions, list):
                raise ValueError("conditions must be a list.")
            result = self.unity.call(
                "mutate-animator-transition",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                stateMachine=optional_string(request.get("stateMachine")),
                fromState=required_string(request, "fromState"),
                toState=required_string(request, "toState"),
                componentIndex=int(request.get("transitionIndex", -1)),
                values=request_values(request),
                json="" if conditions is None else json.dumps({"conditions": conditions}, ensure_ascii=False),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-parameter":
            result = self.unity.call(
                "mutate-animator-parameter",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                name=required_string(request, "name"),
                parameterType=optional_string(request.get("type")),
                value=None if request.get("value") is None else str(request.get("value")),
                destinationPath=optional_string(request.get("newName")),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-layer":
            result = self.unity.call(
                "mutate-animator-layer",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                values=request_values(request),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-state-machine":
            result = self.unity.call(
                "mutate-animator-state-machine",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                stateMachine=required_string(request, "name"),
                objectPath=optional_string(request.get("parent")),
                name=optional_string(request.get("newName")),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-blend-tree":
            settings = request.get("settings")
            if settings is not None and not isinstance(settings, dict):
                raise ValueError("settings must be an object.")
            result = self.unity.call(
                "mutate-animator-blend-tree",
                action=required_action(request, {"create", "modify", "delete"}),
                controller=required_string(request, "controller"),
                layer=required_string(request, "layer"),
                state=required_string(request, "state"),
                stateMachine=optional_string(request.get("stateMachine")),
                name=required_string(request, "name"),
                json="" if settings is None else json.dumps(settings, ensure_ascii=False),
            )
            return {"ok": True, "controller": unity_message_json(result)}
        if operation == "animator-control":
            result = self.unity.call(
                "control-animator",
                path=required_string(request, "path"),
                state=optional_string(request.get("state")),
                layer=optional_string(request.get("layer")),
                values=request_values(request),
            )
            return {"ok": True, "animator": compact_runtime_animator(unity_message_json(result))}
        if operation == "animator-runtime-state":
            result = self.unity.call("get-animator-runtime-state", path=required_string(request, "path"))
            return {"ok": True, "animator": compact_runtime_animator(unity_message_json(result))}
        if operation == "game-actions":
            return self.game_actions(request)
        raise ValueError(f"Unknown or excluded operation: {operation}")

    def game_actions(self, request: dict[str, Any]) -> dict[str, Any]:
        try:
            return self._game_actions(request)
        except BaseException as error:
            try:
                self.unity.call("pause-game")
            except BaseException as pause_error:
                raise RuntimeError(f"{error}; Unity also failed to pause: {pause_error}") from error
            raise

    def sprite_editor(self, request: dict[str, Any]) -> dict[str, Any]:
        asset_path = urllib.parse.unquote(required_string(request, "path")).replace("\\", "/")
        action = required_string(request, "action")
        if action == "preview":
            result = self.unity.call("get-sprite-layout", path=asset_path)
        elif action in {"auto", "manual", "border"}:
            payload: dict[str, Any] = {}
            if action == "manual":
                slices = request.get("slices")
                if not isinstance(slices, list) or not slices:
                    raise ValueError("manual action requires a non-empty slices array.")
                payload["slices"] = slices
            if action == "border":
                border = request.get("border")
                if not isinstance(border, dict):
                    raise ValueError("border action requires border.")
                payload["border"] = border
            result = self.unity.call(
                "mutate-sprite-layout",
                path=asset_path,
                action=action,
                json=json.dumps(payload, ensure_ascii=False, separators=(",", ":")),
            )
        else:
            raise ValueError("action must be preview, auto, manual or border.")

        try:
            layout = json.loads(required_string(result, "message"))
        except json.JSONDecodeError as error:
            raise RuntimeError("Unity returned an invalid sprite layout.") from error
        screenshot = self.render_sprite_layout(asset_path, layout)
        return {"ok": True, "screenshot": str(screenshot)}

    def render_sprite_layout(self, asset_path: str, layout: dict[str, Any]) -> Path:
        if not asset_path.startswith("Assets/") or ".." in Path(asset_path).parts:
            raise ValueError("Sprite path must point below Assets.")
        source_path = (self.project / Path(asset_path)).resolve()
        assets_root = (self.project / "Assets").resolve()
        if assets_root not in source_path.parents or not source_path.is_file():
            raise ValueError("Sprite asset was not found below Assets.")
        output_folder = self.project / "Library" / "UnityAgentBridge" / "SpritePreviews"
        output_folder.mkdir(parents=True, exist_ok=True)
        output_path = output_folder / f"sprite-{time.time_ns()}-{uuid.uuid4().hex}.png"

        with Image.open(source_path) as source:
            image = source.convert("RGBA")
        draw = ImageDraw.Draw(image)
        green = (0, 255, 64, 255)
        black = (0, 0, 0, 220)
        line_width = max(2, round(max(image.size) / 400))
        if layout.get("mode") == "multiple":
            slices = layout.get("slices")
            if not isinstance(slices, list):
                raise RuntimeError("Unity returned an invalid sprite slice list.")
            for index, item in enumerate(slices):
                if not isinstance(item, dict):
                    raise RuntimeError("Unity returned an invalid sprite slice.")
                x = float(item.get("x", 0))
                y = float(item.get("y", 0))
                width = float(item.get("width", 0))
                height = float(item.get("height", 0))
                top = image.height - y - height
                box = (round(x), round(top), round(x + width), round(top + height))
                draw.rectangle(box, outline=green, width=line_width)
                label = str(index)
                label_box = draw.textbbox((box[0] + 2, box[1] + 2), label)
                draw.rectangle((label_box[0] - 2, label_box[1] - 2, label_box[2] + 2, label_box[3] + 2), fill=black)
                draw.text((box[0] + 2, box[1] + 2), label, fill=green)
        else:
            border = layout.get("border")
            if not isinstance(border, dict):
                raise RuntimeError("Unity returned an invalid sprite border.")
            left = round(float(border.get("left", 0)))
            right = image.width - round(float(border.get("right", 0)))
            top = round(float(border.get("top", 0)))
            bottom = image.height - round(float(border.get("bottom", 0)))
            draw.line((left, 0, left, image.height), fill=green, width=line_width)
            draw.line((right, 0, right, image.height), fill=green, width=line_width)
            draw.line((0, top, image.width, top), fill=green, width=line_width)
            draw.line((0, bottom, image.width, bottom), fill=green, width=line_width)
        image.save(output_path, format="PNG", compress_level=6)
        image.close()
        return output_path

    # Profiler window modules of the last capture: CPU Usage by Unity chart group, Rendering and Memory counters.
    def render_profiler_chart(self, marker: str | None, frame_range: Any) -> Path:
        data = unity_message_json(self.unity.call("profiler-frames", query=marker))
        groups = data.get("categories") or []
        counter_names = data.get("counters") or []
        frames = data.get("frames") or []
        if frame_range is not None:
            if (not isinstance(frame_range, list) or len(frame_range) != 2
                    or not all(isinstance(value, int) and not isinstance(value, bool) for value in frame_range)
                    or frame_range[0] > frame_range[1]):
                raise ValueError("range must be [firstFrame, lastFrame].")
            frames = [frame for frame in frames if frame_range[0] <= frame[0] <= frame_range[1]]
        if not frames:
            raise RuntimeError("Profiler capture has no game frames in this range.")

        first, last = frames[0][0], frames[-1][0]
        count = len(frames)
        averages = [sum(frame[2][index] for frame in frames) / count for index in range(len(groups))]
        peak = max(frame[1] for frame in frames)
        step = next(value for value in (2, 5, 10, 20, 50, 100, 200, 500, 1000) if peak / value <= 6)
        top = max(20.0, math.ceil(peak * 1.08 / step) * step)

        width, height = 1280, 640
        left, right = 64, 16
        panels = {"cpu": (70, 330), "rendering": (372, 462), "memory": (504, 594)}
        image = Image.new("RGB", (width, height), (30, 30, 30))
        draw = ImageDraw.Draw(image)
        try:
            font = ImageFont.load_default(size=14)
            small = ImageFont.load_default(size=12)
        except TypeError:
            font = small = ImageFont.load_default()
        slot = (width - left - right) / (last - first + 1)
        grey, light = (150, 150, 150), (215, 215, 215)

        def x_of(frame: int, center: bool = False) -> float:
            return left + (frame - first + (0.5 if center else 0)) * slot

        def legend(y: float, items: list[tuple[str, Any]]) -> None:
            x = left
            for text, color in items:
                draw.rectangle((x, y - 6, x + 11, y + 5), fill=color)
                draw.text((x + 16, y), text, fill=light, font=small, anchor="lm")
                x += 16 + draw.textlength(text, font=small) + 18

        def panel(name: str, title: str) -> tuple[float, float]:
            y0, y1 = panels[name]
            draw.rectangle((left, y0, width - right, y1), outline=(55, 55, 55))
            draw.text((8, y0 - 22), title, fill=(235, 235, 235), font=font)
            return y0, y1

        # Each counter gets its own band scaled min..max, so small changes stay visible and lines never overlap.
        def lines(name: str, series: list[tuple[str, int, Any, Any]]) -> list[tuple[str, Any]]:
            y0, y1 = panels[name]
            recorded = []
            for label, index, color, text in series:
                values = [(frame[0], frame[5][index]) for frame in frames if index < len(frame[5]) and frame[5][index] >= 0]
                if values:
                    recorded.append((label, values, color, text))
            labels = []
            band = (y1 - y0) / max(1, len(recorded))
            for position, (label, values, color, text) in enumerate(recorded):
                bottom = y0 + band * (position + 1) - 4
                low = min(value for _, value in values)
                high = max(value for _, value in values)
                spread = high - low
                points = [(x_of(frame, True), bottom - (band - 8) * (0.5 if spread <= 0 else (value - low) / spread)) for frame, value in values]
                if len(points) > 1:
                    draw.line(points, fill=color, width=2)
                else:
                    draw.ellipse((points[0][0] - 2, points[0][1] - 2, points[0][0] + 2, points[0][1] + 2), fill=color)
                average = sum(value for _, value in values) / len(values)
                span = text(low) if spread <= 0 else f"{text(low)}-{text(high)}"
                labels.append((f"{label} {span}" if spread <= 0 else f"{label} {text(average)} ({span})", color))
            return labels

        # CPU Usage.
        y0, y1 = panel("cpu", "CPU Usage")

        def y_of(ms: float) -> float:
            return y1 - (y1 - y0) * min(ms, top) / top

        tick = 0.0
        while tick <= top + 1e-6:
            draw.line((left, y_of(tick), width - right, y_of(tick)), fill=(48, 48, 48))
            draw.text((left - 6, y_of(tick)), f"{tick:g}", fill=grey, font=small, anchor="rm")
            tick += step
        order = [index for index in range(len(groups)) if averages[index] > 0]
        for frame in frames:
            x0 = x_of(frame[0])
            x1 = max(x0 + 1, x0 + slot - (1 if slot >= 4 else 0))
            base, stacked = y1, 0.0
            for index in order:
                if frame[2][index] <= 0:
                    continue
                stacked += frame[2][index]
                draw.rectangle((x0, y_of(stacked), x1, base), fill=groups[index]["color"])
                base = y_of(stacked)
        for ms, label in ((1000 / 60, "16ms (60FPS)"), (1000 / 30, "33ms (30FPS)")):
            if ms <= top:
                for x in range(left, width - right, 10):
                    draw.line((x, y_of(ms), min(x + 5, width - right), y_of(ms)), fill=(230, 230, 230))
                draw.text((left + 4, y_of(ms) - 2), label, fill=(230, 230, 230), font=small, anchor="lb")
        if marker:
            points = [(x_of(frame[0], True), y_of(frame[4])) for frame in frames]
            if len(points) > 1:
                draw.line(points, fill=(255, 230, 0), width=2)
        for frame in sorted(frames, key=lambda item: -item[1])[:5]:
            draw.text((x_of(frame[0], True), y_of(frame[1]) - 3), str(frame[0]), fill=(255, 255, 255), font=small, anchor="mb")
        cpu_legend = [(f"{groups[index]['name']} {averages[index]:.2f}", groups[index]["color"])
                      for index in sorted(order, key=lambda index: -averages[index]) if averages[index] >= 0.01]
        if marker:
            cpu_legend.append((f"{marker} {sum(frame[4] for frame in frames) / count:.2f}", (255, 230, 0)))
        average = sum(frame[1] for frame in frames) / count
        draw.text((left, 8), f"frames {first}-{last}   avg {average:.2f} ms   max {peak:.2f} ms", fill=(235, 235, 235), font=font)
        legend(40, cpu_legend)

        # Rendering.
        panel("rendering", "Rendering")
        rendering = [("SetPass", 0, (76, 155, 232), format_count), ("Batches", 1, (139, 195, 74), format_count),
                     ("Triangles", 2, (240, 160, 48), format_count), ("Vertices", 3, (64, 192, 192), format_count)]
        legend(panels["rendering"][1] + 12, lines("rendering", [item for item in rendering if item[1] < len(counter_names)]))

        # Memory: used memory lines and GC allocated in each frame.
        y0, y1 = panel("memory", "Memory")
        gc_high = max(frame[3] for frame in frames)
        if gc_high > 0:
            for frame in frames:
                if frame[3] > 0:
                    x0 = x_of(frame[0])
                    draw.rectangle((x0, y1 - (y1 - y0 - 8) * frame[3] / gc_high, max(x0 + 1, x0 + slot - 1), y1), fill=(150, 50, 50))
        memory_legend = lines("memory", [("Total Used", 4, (215, 215, 215), format_megabytes), ("GC Used", 5, (230, 120, 60), format_megabytes)])
        memory_legend.append((f"GC Alloc {sum(frame[3] for frame in frames):.1f} KB (max {gc_high:.1f} KB/frame)", (150, 50, 50)))
        legend(y1 + 12, memory_legend)

        labels = max(1, round((last - first + 1) / 12))
        labels = next(value for value in (1, 2, 5, 10, 20, 25, 50, 100, 200, 500) if value >= labels)
        for index in range((first + labels - 1) // labels * labels, last + 1, labels):
            draw.text((x_of(index, True), height - 14), str(index), fill=grey, font=small, anchor="mm")

        folder = self.project / "Library" / "UnityAgentBridge" / "Screenshots"
        folder.mkdir(parents=True, exist_ok=True)
        for old_chart in folder.glob("profiler-*.png"):
            old_chart.unlink(missing_ok=True)
        output_path = folder / f"profiler-{time.time_ns()}.png"
        image.save(output_path, format="PNG", compress_level=6)
        image.close()
        return output_path

    def _game_actions(self, request: dict[str, Any]) -> dict[str, Any]:
        actions = validated_game_actions(request.get("actions"))
        frames = request.get("frames")
        if frames is not None and (isinstance(frames, bool) or not isinstance(frames, int) or not 4 <= frames <= 16):
            raise ValueError("frames must be an integer from 4 to 16.")
        first_state = unity_message_json(self.unity.call("prepare-game-interaction"))
        started = first_state.get("state") == "starting"
        if started:
            self.last_game_frame_size = None
        view = self.wait_for_game_view(first_state)

        if self.last_game_frame_size is None:
            frame_size = (
                positive_integer(view.get("renderWidth"), "Game View render width"),
                positive_integer(view.get("renderHeight"), "Game View render height"),
            )
        else:
            frame_size = self.last_game_frame_size

        screenshots: list[str] = []
        times: list[float] = []
        folder = self.project / "Library" / "UnityAgentBridge" / "Screenshots"
        for old_frame in folder.glob("game-*.png"):
            old_frame.unlink(missing_ok=True)

        def capture(action: dict[str, Any]) -> None:
            frame = unity_message_json(self.unity.call("capture-game", name=f"game-{len(screenshots) + 1}"))
            game_input.captured = True
            path = Path(required_string(frame, "screenshot"))
            width = positive_integer(frame.get("width"), "screenshot width")
            height = positive_integer(frame.get("height"), "screenshot height")
            if action["action"] == "zoom":
                crop_game_screenshot(path, width, height, game_input.frame_width, game_input.frame_height, action["region"])
            else:
                game_input.frame_width, game_input.frame_height = resize_game_screenshot(path, width, height)
                self.last_game_frame_size = (game_input.frame_width, game_input.frame_height)
            if action.get("scale", 1) < 1:
                scale_game_screenshot(path, action["scale"])
            screenshots.append(str(path))
            times.append(round(float(frame.get("time", 0)), 2))

        game_input = UnityGameInput(self.unity, frame_size[0], frame_size[1], capture)
        profile = request.get("profile") is True
        if profile:
            self.unity.call("profiler-start")
        if frames:
            self.unity.call("game-clip-start")
        self.unity.call("game-sounds-start")
        action_error: BaseException | None = None
        try:
            game_input.perform(actions)
        except BaseException as error:
            action_error = error
        try:
            game_input.release_all()
        except BaseException as error:
            if action_error is None:
                action_error = error
        if action_error is None and actions and actions[-1]["action"] in {"screenshot", "zoom"}:
            self.unity.call("pause-game")
        else:
            time.sleep(0.1)
            frame = unity_message_json(self.unity.call("pause-and-capture-game"))
            width = positive_integer(frame.get("width"), "screenshot width")
            height = positive_integer(frame.get("height"), "screenshot height")
            screenshot = required_string(frame, "screenshot")
            width, height = resize_game_screenshot(Path(screenshot), width, height)
            self.last_game_frame_size = (width, height)
            screenshots.append(screenshot)
            times.append(round(float(frame.get("time", 0)), 2))
        sounds: list[str] = []
        try:
            sounds = unity_message_json(self.unity.call("game-sounds-stop")).get("sounds") or []
        except BaseException as error:
            if action_error is None:
                action_error = error
        # Frames evenly spaced over the batch's game time, up to the paused end.
        clip: dict[str, Any] | None = None
        if frames:
            try:
                clip = unity_message_json(self.unity.call("game-clip-stop", limit=frames))
            except BaseException as error:
                if action_error is None:
                    action_error = error
        # Stopped after the last frame: the Profiler window it opens may cover the Game View.
        profile_summary: dict[str, Any] | None = None
        if profile:
            try:
                profile_summary = unity_message_json(self.unity.call("profiler-read", limit=0))
            except BaseException as error:
                if action_error is None:
                    action_error = error
        result: dict[str, Any] = {
            "ok": action_error is None,
            "screenshot": screenshots[-1],
            "screenshots": screenshots,
            "times": times,
            "executedActions": game_input.executed_actions,
            "requestedActions": len(actions),
        }
        if clip is not None:
            result["clip"] = clip
        if sounds:
            result["sounds"] = sounds
        if profile_summary is not None:
            result["profile"] = profile_summary
            result["screenshots"] = screenshots + [str(self.render_profiler_chart(None, None))]
        if action_error is not None:
            result["error"] = f"{type(action_error).__name__}: {action_error}"
        return result

    def wait_for_game_view(self, state: dict[str, Any]) -> dict[str, Any]:
        started = time.monotonic()
        layout_deadline: float | None = None
        no_camera_since: float | None = None
        while state.get("state") != "ready":
            if state.get("state") == "no-camera":
                no_camera_since = no_camera_since or time.monotonic()
                if time.monotonic() - no_camera_since >= 0.5:
                    raise RuntimeError(optional_string(state.get("message")) or "Unity Game View has no active camera rendering.")
            elif state.get("state") == "starting":
                no_camera_since = None
                if time.monotonic() - started >= PLAY_START_SECONDS:
                    raise TimeoutError(f"Unity did not enter Play Mode within {PLAY_START_SECONDS:g} seconds.")
            elif state.get("state") == "layout":
                no_camera_since = None
                layout_deadline = layout_deadline or time.monotonic() + 30.0
                if time.monotonic() >= layout_deadline:
                    raise TimeoutError("Unity Game View did not render a usable frame within 30 seconds.")
            else:
                raise RuntimeError(f"Unexpected Unity game state: {state.get('state')}")
            time.sleep(0.1)
            state = unity_message_json(self.unity.call("prepare-game-interaction", _timeout_seconds=PLAY_START_SECONDS))
        return state

    def ensure_edit_mode(self) -> bool:
        status = str(self.unity.call("get-status").get("status", ""))
        if status == "игра оффлайн":
            return False
        if status != "игра останавливается":
            self.unity.call("set-play-mode", action="stop")
        self.wait_for_play_mode(True)
        return True

    def wait_for_play_mode(self, offline: bool) -> None:
        expected = "игра оффлайн" if offline else "игра запущена"
        started = time.monotonic()
        while True:
            result = self.unity.call("get-status", _timeout_seconds=PLAY_START_SECONDS)
            status = str(result.get("status", ""))
            if status == expected:
                return
            if not offline and status == "игра оффлайн" and time.monotonic() - started > 10.0:
                errors = unity_message_json(result).get("compileErrors", 0)
                raise RuntimeError("Scripts have compile errors; Play Mode did not start." if errors else "Unity did not start Play Mode.")
            if time.monotonic() - started >= PLAY_START_SECONDS:
                raise TimeoutError(f"Unity did not reach expected Play Mode state: {expected}.")
            time.sleep(0.1)

    def status(self) -> dict[str, Any]:
        busy = self.busy()
        if busy == "compiling":
            return {"ok": True, "busy": busy}
        try:
            result = self.unity.call("get-status", _timeout_seconds=5.0)
        except UnityDialogError as error:
            return {"ok": True, "busy": "dialog " + error.dialog}
        except UnitySafeModeError as error:
            return {"ok": True, "busy": "safe mode", "compileErrors": error.errors}
        except TimeoutError:
            return {"ok": True, "busy": busy or unity_dialog(self.unity.requests.parent, progress=True) or "editor"}
        info = unity_message_json(result)
        state = {
            "игра запускается": "starting",
            "игра запущена": "playing",
            "игра останавливается": "stopping",
            "игра оффлайн": "stopped",
        }.get(str(result.get("status", "")), "unknown")
        if state == "playing" and info.get("paused"):
            state = "paused"
        response: dict[str, Any] = {"ok": True, "state": state}
        if state == "paused" and info.get("pausedBy"):
            response["pausedBy"] = info["pausedBy"]
        if state in {"playing", "paused"}:
            response["time"] = info.get("time", 0)
            if info.get("timeScale", 1) != 1:
                response["timeScale"] = info.get("timeScale")
        if info.get("lightmapping"):
            busy = f"lightmapping {info.get('progress', 0):g}%"
        elif info.get("occlusion"):
            busy = "occlusion culling"
        if busy:
            response["busy"] = busy
        for key in ("errors", "lastError", "compileErrors"):
            if info.get(key) and (key != "lastError" or info.get("errors")):
                response[key] = info[key]
        return response

    def busy(self) -> str:
        runtime = self.project / "Library" / "UnityAgentBridge"
        if (runtime / "busy.txt").is_file():
            return "compiling"
        for marker in (runtime / "Menu").glob("*.state"):
            try:
                state = marker.read_text(encoding="utf-8")
            except OSError:
                continue
            if state.startswith(("running:", "scheduled:")):
                return "menu " + state.split(":", 1)[1]
            marker.unlink(missing_ok=True)
        return ""

    def wait_for_idle(self, deadline: float) -> str:
        while True:
            busy = self.busy()
            if not busy:
                try:
                    info = unity_message_json(self.unity.call("get-status", _timeout_seconds=max(1.0, deadline - time.monotonic())))
                    busy = f"lightmapping {info.get('progress', 0):g}%" if info.get("lightmapping") else "occlusion culling" if info.get("occlusion") else ""
                except UnityDialogError as error:
                    return "dialog " + error.dialog
                except UnitySafeModeError:
                    return "safe mode"
                except TimeoutError:
                    busy = unity_dialog(self.unity.requests.parent, progress=True) or "editor"
            if not busy:
                return ""
            if time.monotonic() >= deadline:
                return busy
            time.sleep(0.25)

    def errors_since(self, started: datetime) -> dict[str, Any]:
        result = self.unity.call("get-logs")
        logs = [
            item for item in result.get("logs", [])
            if str(item.get("type", "")) in {"Error", "Exception", "Assert"} and log_timestamp(item) >= started
        ]
        response: dict[str, Any] = {}
        if logs:
            response["errors"] = [
                {key: value for key, value in compact_log(item).items() if key != "ageSeconds"}
                for item in collapse_logs(logs)[-10:]
            ]
        compilation = result.get("currentCompilationErrors", [])
        if compilation:
            response["compileErrors"] = [compact_log(item)["message"] for item in compilation[-10:]]
        return response

    def menu(self, request: dict[str, Any]) -> dict[str, Any]:
        path = optional_string(request.get("path"))
        if not path:
            query = optional_string(request.get("query"))
            items = self.unity.call("list-menu-items", boolValue=not query).get("items", [])
            if not query:
                return {"ok": True, "items": items}
            # Items of a matching submenu come too, as opening that submenu shows them (Recorder/Quick Recording).
            lexical = ranked_names(query, items, lambda item: item.rsplit("/", 1)[-1], lambda item: item, keep_path_matches=True)
            if lexical:
                return {"ok": True, "items": lexical[:RESULT_COUNT]}
            ranked = self.rank(query, [f"Unity menu item {item}" for item in items], RESULT_COUNT)
            return {"ok": True, "items": [items[index] for index, _score in ranked]}
        started = datetime.now(timezone.utc)
        deadline = time.monotonic() + WAIT_BUDGET_SECONDS
        marker = Path(required_string(self.unity.call("execute-menu-item", path=path), "message"))
        while True:
            try:
                state = marker.read_text(encoding="utf-8")
            except OSError:
                state = ""
            if state == "complete":
                break
            if state.startswith("error:"):
                marker.unlink(missing_ok=True)
                raise RuntimeError(state[6:])
            if time.monotonic() >= deadline:
                return {"ok": True, "busy": "menu " + path}
            time.sleep(0.1)
        marker.unlink(missing_ok=True)
        busy = self.wait_for_idle(deadline)
        if busy:
            return {"ok": True, "busy": busy}
        return {"ok": True, **self.errors_since(started)}

    def lighting(self, request: dict[str, Any]) -> dict[str, Any]:
        action = required_action(request, {"bake", "cancel", "clear"})
        play_stopped = self.ensure_edit_mode()
        response = message_result(self.unity.call("lighting", action=action))
        if play_stopped:
            response["playStopped"] = True
        return response

    # Like game_actions: starts the game when needed, records, pauses and answers with the chart in one call.
    def profiler(self, request: dict[str, Any]) -> dict[str, Any]:
        frames = request.get("frames")
        summary: dict[str, Any] | None = None
        if frames is not None:
            if isinstance(frames, bool) or not isinstance(frames, int) or frames < 10 or frames > 4000:
                raise ValueError("frames must be between 10 and 4000.")
            target = optional_string(request.get("target")) or "Play Mode"
            if target not in {"Play Mode", "Edit Mode"}:
                raise ValueError("target must be Play Mode or Edit Mode.")
            self.wait_for_game_view(unity_message_json(self.unity.call("prepare-game-interaction")))
            try:
                self.unity.call("profiler-start", limit=frames, value=target)
                seconds = 30 + frames // 20
                deadline = time.monotonic() + seconds
                while True:
                    result = self.unity.call("profiler-read", limit=frames)
                    if not result.get("pending"):
                        break
                    if time.monotonic() >= deadline:
                        # Stops the recording, which would otherwise go on while the game stays in Play Mode.
                        with contextlib.suppress(Exception):
                            self.unity.call("profiler-read", limit=0)
                        raise TimeoutError(f"Profiler recorded {result.get('message')} of {frames} frames within {seconds} seconds.")
                    time.sleep(0.25)
            finally:
                self.unity.call("pause-game")
            summary = unity_message_json(result)
        response: dict[str, Any] = {
            "ok": True,
            "screenshot": str(self.render_profiler_chart(optional_string(request.get("marker")), request.get("range"))),
        }
        if summary is not None:
            response["profile"] = summary
        return response

    def timeline(self, operation: str, request: dict[str, Any]) -> dict[str, Any]:
        path = optional_string(request.get("path"))
        timeline = optional_string(request.get("timeline"))
        if operation == "timeline-info":
            query = optional_string(request.get("query"))
            if not query:
                return {"ok": True, "timeline": unity_message_json(self.unity.call("get-timeline", path=path, timeline=timeline))}
            directors = unity_message_json(self.unity.call("list-timelines")).get("directors", [])
            lexical = ranked_names(query, directors, lambda item: item.get("path", "").rsplit("/", 1)[-1],
                                   lambda item: f"{item.get('path', '')} {item.get('timeline') or ''}")
            if lexical:
                return {"ok": True, "candidates": lexical[:RESULT_COUNT]}
            documents = [f"Unity Timeline PlayableDirector {item.get('path', '')} timeline {item.get('timeline') or ''}" for item in directors]
            ranked = self.rank(query, documents, RESULT_COUNT)
            return {"ok": True, "candidates": [directors[index] for index, _score in ranked]}
        commands = {
            "timeline-track": ("mutate-timeline-track", {"create", "modify", "delete"}),
            "timeline-clip": ("mutate-timeline-clip", {"create", "modify", "delete"}),
            "timeline-marker": ("mutate-timeline-marker", {"create", "modify", "delete"}),
        }
        if operation not in commands:
            raise ValueError(f"Unknown or excluded operation: {operation}")
        command, actions = commands[operation]
        marker = request.get("marker")
        result = self.unity.call(
            command,
            action=required_action(request, actions),
            path=path,
            timeline=timeline,
            track=optional_string(request.get("track")),
            name=optional_string(request.get("clip")),
            clip=optional_string(request.get("asset")),
            componentType=optional_string(request.get("type")),
            destinationPath=optional_string(request.get("parent")),
            siblingIndex=-1 if marker is None else int(marker),
            values=request_values(request),
        )
        return {"ok": True, "timeline": unity_message_json(result)}

    # The escape hatch: logs what the plugin lacked, so it can become a real command.
    def eval(self, missing: str, code: str) -> dict[str, Any]:
        runtime = self.project / "Library" / "UnityAgentBridge"
        entry: dict[str, Any] = {"time": datetime.now(timezone.utc).isoformat(timespec="seconds"), "missing": missing, "code": code}
        gaps = runtime / "eval-gaps.jsonl"
        hint = self.eval_hint(missing, gaps)
        try:
            context = unity_message_json(self.unity.call("eval-context"))
            folder = runtime / "Eval"
            folder.mkdir(parents=True, exist_ok=True)
            for old in folder.glob("*"):
                old.unlink(missing_ok=True)
            name = f"UabEval{time.time_ns()}"
            body = code.strip()
            if ";" not in body and "return" not in body:
                body = f"return {body};"
            # Leading using directives go above the class; their lines stay blank so error lines still match.
            lines = body.split("\n")
            usings: list[str] = []
            for index, line in enumerate(lines):
                if re.fullmatch(r"\s*using\s+(static\s+)?[\w.]+(\s*=\s*[\w.<>, ]+)?\s*;\s*", line):
                    # The template's own directives (Object = UnityEngine.Object) are not repeated.
                    if " ".join(line.split()) not in EVAL_TEMPLATE:
                        usings.append(line.strip())
                    lines[index] = ""
                elif line.strip():
                    break
            source = folder / f"{name}.cs"
            source.write_text("".join(item + "\n" for item in usings) + EVAL_TEMPLATE.replace("__BODY__", "\n".join(lines)), encoding="utf-8")
            assembly = folder / f"{name}.dll"
            arguments = ["-nologo", "-noconfig", "-nostdlib+", "-preferreduilang:en-US", "-target:library", "-langversion:9.0", "-optimize-", "-debug:portable",
                         "-nowarn:CS0105,CS0162,CS1701,CS1702,CS8019", f'-out:"{assembly}"']
            arguments += [f'-r:"{reference}"' for reference in context.get("references", [])]
            arguments.append(f'"{source}"')
            response_file = folder / f"{name}.rsp"
            response_file.write_text("\n".join(arguments), encoding="utf-8")
            compiled = subprocess.run(
                [context["dotnet"], context["compiler"], f"@{response_file}"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            errors = [line.strip().replace(f"{folder}{os.sep}", "") for line in compiled.stdout.splitlines() if ": error " in line]
            if compiled.returncode != 0 or not assembly.is_file():
                raise ValueError("\n".join(errors[:5]) or compiled.stdout.strip()[-500:] or "C# compilation failed.")
            result = self.unity.call("eval-run", path=str(assembly))
            entry["ok"] = True
            response: dict[str, Any] = {"ok": True, "result": result.get("message", "")}
            if hint:
                response["note"] = f"Ближайшие строки справки к «{missing}»: {hint}"
                response["report"] = f"Сообщи пользователю: использован eval для «{missing}»; если строка из note решала задачу, скажи, что команда есть, но не была найдена."
            else:
                response["report"] = f"Сообщи пользователю: использован eval для «{missing}»."
            return response
        except BaseException as error:
            entry["ok"] = False
            entry["error"] = str(error)[:300]
            if hint:
                raise type(error)(f"{error}\nБлижайшие строки справки: {hint}") from error
            raise
        finally:
            with gaps.open("a", encoding="utf-8") as log:
                log.write(json.dumps(entry, ensure_ascii=False) + "\n")

    # First eval for a reason: quote the closest skill lines; the agent judges them, the small model only ranks.
    def eval_hint(self, missing: str, gaps: Path) -> str:
        wanted = missing.strip().casefold()
        if gaps.is_file():
            for line in gaps.read_text(encoding="utf-8").splitlines():
                try:
                    if str(json.loads(line).get("missing", "")).strip().casefold() == wanted:
                        return ""
                except ValueError:
                    continue
        quotes, vectors = self.skill_index()
        if not quotes:
            return ""
        query = np.asarray(next(self.model.embed([missing])), dtype=np.float32)
        scores = vectors @ (query / (np.linalg.norm(query) or 1.0))
        # The small model ranks unrelated lines just as high; a quote must also share a rare word stem with the reason
        # (words such as "объект" are in every other line of the skill).
        quote_stems = [{word[:7] for word in re.findall(r"\w{5,}", quote.split(": ", 1)[-1].casefold())} for quote in quotes]
        stems = {word[:7] for word in re.findall(r"\w{5,}", wanted)}
        stems = {stem for stem in stems if sum(stem in item for item in quote_stems) <= 6}
        related = [quotes[int(index)] for index in np.argsort(-scores)[:20] if stems & quote_stems[int(index)]]
        return " | ".join(related[:3])

    def skill_index(self) -> tuple[list[str], Any]:
        root = Path(__file__).resolve().parent.parent / "CodexPlugin~" / "unity-agent-bridge" / "skills" / "unity-agent-bridge"
        files = [file for file in (root / "SKILL.md", *sorted((root / "references").glob("*.md"))) if file.is_file()]
        stamp = tuple(file.stat().st_mtime_ns for file in files)
        cached = getattr(self, "_skill_index", None)
        if cached and cached[0] == stamp:
            return cached[1], cached[2]
        quotes: list[str] = []
        documents: list[str] = []
        for file in files:
            heading = ""
            for line in file.read_text(encoding="utf-8").splitlines():
                text = line.strip()
                if text.startswith("#"):
                    heading = text.lstrip("# ")
                    continue
                if len(text) < 8 or text.startswith(("```", "---", "name:", "description:")):
                    continue
                for sentence in re.split(r"(?<=[.;])\s+(?=[`A-ZА-ЯЁ])", text):
                    if len(sentence) < 8:
                        continue
                    command = re.match(r"`?([a-z]+(?:-[a-z]+)*)(?: ([a-z|]+))?", sentence)
                    words = " ".join(part.replace("-", " ").replace("|", " ") for part in command.groups() if part) if command else ""
                    quotes.append(f"{file.name}: {sentence[:120]}")
                    documents.append(f"{heading}. {words}. {sentence}")
        vectors = np.asarray(list(self.model.embed(documents)), dtype=np.float32) if quotes else np.zeros((0, 1), dtype=np.float32)
        if len(quotes):
            vectors /= np.maximum(np.linalg.norm(vectors, axis=1, keepdims=True), 1e-9)
        self._skill_index = (stamp, quotes, vectors)
        return quotes, vectors

    def wait_for_package_operation(self, command: str, **arguments: Any) -> dict[str, Any]:
        deadline = time.monotonic() + 180.0
        while True:
            try:
                result = self.unity.call(command, **arguments)
            except RuntimeError as error:
                if "Cannot connect to 'api.unity.com'" in str(error):
                    raise RuntimeError("Unity Package Manager cannot reach api.unity.com.") from None
                raise
            if not result.get("pending", False):
                return result
            if time.monotonic() >= deadline:
                raise TimeoutError("Unity Package Manager did not finish within 180 seconds.")
            time.sleep(0.2)

    def component_mutation(self, command: str, request: dict[str, Any], include_values: bool = True) -> dict[str, Any]:
        arguments: dict[str, Any] = {
            "path": required_string(request, "path"),
            "componentType": required_string(request, "componentType"),
            "componentIndex": int(request.get("componentIndex", -1)),
        }
        if include_values:
            values = request.get("values", [])
            if not isinstance(values, list):
                raise ValueError("values must be a list.")
            arguments["values"] = [dict(item, value=unity_event_arguments(item["value"])) if isinstance(item, dict) and isinstance(item.get("value"), str) else item for item in values]
        values_after_add = arguments.pop("values", []) if command == "add-component" else []
        result = self.unity.call(command, **arguments)
        warning = str(result.get("message") or "")
        if values_after_add and "single allowed" not in warning and "did not add" not in warning:
            # Edited after Unity finished adding it, as in the Inspector: some components (HDRP lights) set their own
            # defaults on the next editor update and would overwrite values applied in the same call.
            added_index = int(result.get("componentIndex", 0))
            try:
                modified = self.unity.call("modify-component", path=arguments["path"], componentType=arguments["componentType"], componentIndex=added_index, values=values_after_add)
                result = {**modified, "componentIndex": added_index, "message": " ".join(item for item in (warning, str(modified.get("message") or "")) if item)}
            except Exception:
                self.unity.call("remove-component", path=arguments["path"], componentType=arguments["componentType"], componentIndex=added_index)
                raise
        if command == "remove-component":
            compact_result = dict(result)
            object_info = dict(compact_result.get("objectInfo") or {})
            object_info["components"] = []
            compact_result["objectInfo"] = object_info
            return object_result(compact_result)
        component_index = int(result.get("componentIndex", arguments["componentIndex"]))
        response = object_result(result, arguments["componentType"], component_index)
        if command == "add-component":
            response["componentIndex"] = component_index
        return response

    def wait_for_component_type(self, component_type: str) -> None:
        deadline = time.monotonic() + 90.0
        while time.monotonic() < deadline:
            types = self.unity.call("list-component-types").get("componentTypes", [])
            if component_type in types:
                return
            time.sleep(0.5)
        raise RuntimeError(f"Unity did not compile and register component type within 90 seconds: {component_type}")

    def animator_source(self, request: dict[str, Any]) -> tuple[str, str]:
        path = optional_string(request.get("path"))
        controller = optional_string(request.get("controller"))
        query = optional_string(request.get("query"))
        supplied = sum(bool(value) for value in (path, controller, query))
        if supplied != 1:
            raise ValueError("Provide exactly one of path, controller, or query.")
        if not query:
            return path, controller
        animators = [item for item in unity_message_json(self.unity.call("list-animators")).get("animators", []) if item.get("controller")]
        ranked = self.rank(query, [animator_document(item) for item in animators], 1)
        if not ranked:
            raise RuntimeError("The current scene has no Animator with an Animator Controller.")
        return animators[ranked[0][0]].get("path", ""), ""

    # Several --path act like a multi-selection in the Inspector: one edit goes to every object, a read comes back
    # per object. An edit stops at the first object that rejects it and names the objects already changed.
    def for_each_path(self, request: dict[str, Any], paths: list[str]) -> dict[str, Any]:
        results: dict[str, Any] = {}
        if request["operation"] == "component-modify":
            # Every object must exist before the first one changes, as a selection can hold only real objects.
            for path in paths:
                self.resolve_object_path(path)
        for path in paths:
            single = {key: value for key, value in request.items() if key != "paths"}
            single["path"] = path
            try:
                result = self._invoke(single)
            except Exception as error:
                if request["operation"] == "component-modify" and results:
                    raise type(error)(f"{path}: {error} (already changed: {', '.join(results)})") from error
                raise type(error)(f"{path}: {error}") from error
            if request["operation"] == "component-modify":
                results[path] = result.get("message") or ""
            elif "value" in result:
                results[path] = result["value"]
            elif "values" in result:
                results[path] = result["values"]
            else:
                results[path] = result.get("objectInfo", result)
        if request["operation"] == "component-modify":
            response: dict[str, Any] = {"ok": True, "paths": list(results)}
            messages = {path: message for path, message in results.items() if message}
            if messages:
                response["messages"] = messages
            return response
        return {"ok": True, "byPath": results}

    def find_objects(self, request: dict[str, Any]) -> dict[str, Any]:
        query = optional_string(request.get("query"))
        component_type = optional_string(request.get("componentType"))
        reference = optional_string(request.get("ref"))
        near = optional_string(request.get("near"))
        if not query and not component_type and not reference and not near:
            raise ValueError("Provide name, component, ref or near.")
        offset, limit = result_page(request)
        scope = request.get("path")
        tree = self.unity.call("get-scene-tree").get("objects", [])
        if reference:
            used = set(unity_message_json(self.unity.call("search-scene-references", path=reference)).get("paths", []))
            tree = [item for item in tree if item.get("path") in used]
        if scope:
            scope = self.resolve_object_path(str(scope))
            tree = [item for item in tree if item.get("path") == scope or item.get("path", "").startswith(scope + "/")]
        if component_type:
            tree = [
                item for item in tree
                if any(component_type_matches(str(component.get("type", "")), component_type) for component in item.get("components", []))
            ]
        hits: dict[str, dict[str, Any]] = {}
        if near:
            radius = float(request.get("radius") or 1.0)
            found = unity_message_json(self.unity.call("objects-near", value=near, query=repr(radius))).get("objects", [])
            hits = {hit["path"]: hit for hit in found}
            order = {path: index for index, path in enumerate(hits)}
            tree = sorted((item for item in tree if item.get("path") in hits), key=lambda item: order[item["path"]])

        def entry(item: dict[str, Any]) -> dict[str, Any]:
            hit = hits.get(item.get("path", ""))
            return {**tree_entry(item), **{key: hit[key] for key in ("distance", "size", "hidden") if key in hit}} if hit else tree_entry(item)

        if not query:
            return paged([{"object": entry(item)} for item in tree], offset, limit)
        lexical = ranked_names(query, tree, lambda item: item.get("name", ""), lambda item: urllib.parse.unquote(item.get("path", "")))
        if lexical:
            return paged([{"object": entry(item)} for item in lexical], offset, limit)
        ranked = self.rank(query, [scene_document(item) for item in tree], len(tree))
        return paged([{"score": round(score, 4), "object": entry(tree[index])} for index, score in ranked], offset, limit)

    # Unity Search syntax built from the same options as object-find; the Search window shows the query.
    def find_assets(self, request: dict[str, Any]) -> dict[str, Any]:
        query = optional_string(request.get("query"))
        filters = []
        if optional_string(request.get("type")):
            filters.append(f"t:{request['type']}")
        if optional_string(request.get("path")):
            filters.append(f'dir:"{request["path"].rstrip("/")}"')
        if optional_string(request.get("ref")):
            filters.append(f'ref="{request["ref"]}"')
        if not query and not filters:
            raise ValueError("Provide name, type, path or ref.")
        offset, limit = result_page(request)
        assets = unity_message_json(self.unity.call("search-assets", query="p: " + (" ".join(filters) or "*"))).get("assets", [])

        def entry(item: dict[str, Any]) -> dict[str, Any]:
            found = {"path": item.get("path", ""), "type": item.get("type", "")}
            if item.get("name") not in {Path(found["path"]).stem, Path(found["path"]).name}:
                found["name"] = item.get("name", "")
            return found

        if not query:
            return paged([{"asset": entry(item)} for item in assets], offset, limit)
        lexical = ranked_names(query, assets, lambda item: item.get("name", ""), lambda item: item.get("path", ""))
        if lexical:
            return paged([{"asset": entry(item)} for item in lexical], offset, limit)
        documents = [f"{item.get('type', '')} {' '.join(wordninja.split(item.get('name', '')))} {item.get('path', '')}" for item in assets]
        ranked = self.rank(query, documents, len(assets))
        return paged([{"score": round(score, 4), "asset": entry(assets[index])} for index, score in ranked], offset, limit)

    def resolve_object_path(self, path: str) -> str:
        decoded = urllib.parse.unquote(path)
        return required_string(self.unity.call("resolve-object-path", path=decoded), "message")

    def suggest_components(self, request: dict[str, Any]) -> dict[str, Any]:
        path = required_string(request, "path")
        component_name = required_string(request, "componentName")
        query = required_string(request, "query")
        type_names = self.unity.call("list-component-types").get("componentTypes", [])
        object_info = self.unity.call("get-object-info", path=path).get("objectInfo", {})
        attached = {item.get("type") for item in object_info.get("components", [])}
        documents = [component_document(name) for name in type_names]
        ranked = self.rank(f"{component_name} {query}", documents, RESULT_COUNT)
        return {
            "ok": True,
            "candidates": [
                {"score": score, "type": type_names[index], "alreadyAttached": type_names[index] in attached}
                for index, score in ranked
            ],
        }

    def logs(self, request: dict[str, Any]) -> dict[str, Any]:
        unity_result = self.unity.call("get-logs")
        raw_logs = unity_result.get("logs", [])
        since = optional_string(request.get("since"))
        if since:
            if since not in {"play", "compile"}:
                raise ValueError("since must be play or compile.")
            mark = optional_string(json.loads(unity_result.get("message") or "{}").get(since))
            if not mark:
                raise ValueError("Play Mode has not started in this editor session." if since == "play" else "Scripts have not compiled in this editor session.")
            cutoff = log_timestamp({"timestampUtc": mark})
            raw_logs = [item for item in raw_logs if log_timestamp(item) >= cutoff]
        since_minutes = request.get("sinceMinutes")
        if since_minutes is not None:
            if isinstance(since_minutes, bool) or not isinstance(since_minutes, (int, float)) or since_minutes <= 0:
                raise ValueError("sinceMinutes must be positive.")
            cutoff = datetime.now(timezone.utc) - timedelta(minutes=float(since_minutes))
            raw_logs = [item for item in raw_logs if log_timestamp(item) >= cutoff]
        logs = collapse_logs(raw_logs)
        compilation_errors = collapse_logs(unity_result.get("currentCompilationErrors", []))
        level = optional_string(request.get("level"))
        if level:
            requested_level = level.casefold()
            accepted_levels = {"error", "assert", "exception"} if requested_level == "error" else {requested_level}
            logs = [item for item in logs if str(item.get("type", "")).casefold() in accepted_levels]
        limit = request.get("limit", 20)
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1 or limit > 100:
            raise ValueError("limit must be between 1 and 100.")
        include_stack = request.get("stackTrace") is True
        query = request.get("query")
        if not query:
            result = {"ok": True, "logs": [compact_log(item, include_stack) for item in logs[-limit:]]}
            if compilation_errors and (not level or level in {"error", "exception"}):
                result["currentCompilationErrors"] = [
                    compact_log(item, include_stack) for item in compilation_errors[-min(limit, 10):]
                ]
            return result
        needle = str(query).casefold()
        lexical = [item for item in logs if needle in str(item.get("message", "")).casefold()]
        if lexical:
            return {"ok": True, "logs": [compact_log(item, include_stack) for item in lexical[-limit:]]}
        # No message contains the text: a single word (a tag, an identifier) has no hits; a phrase gets the closest
        # by meaning, kept apart from exact hits.
        if not re.search(r"\s", str(query).strip()):
            return {"ok": True, "logs": []}
        documents = [f"{item.get('type', '')} {item.get('message', '')} {item.get('stackTrace', '')}" for item in logs]
        ranked = self.rank(str(query), documents, min(3, limit))
        return {"ok": True, "logs": [], "similar": [compact_log(logs[index], include_stack) for index, _score in ranked]}

    def load_embeddings(self) -> dict[str, np.ndarray]:
        try:
            with np.load(self.embedding_file) as data:
                if str(data["model"]) != MODEL_NAME:
                    return {}
                return dict(zip(data["texts"].tolist(), data["vectors"]))
        except (OSError, KeyError, ValueError):
            return {}

    def save_embeddings(self) -> None:
        temporary = self.embedding_file.with_name("embeddings.tmp.npz")
        try:
            np.savez(
                temporary,
                model=np.array(MODEL_NAME),
                texts=np.array(list(self.embeddings), dtype=str),
                vectors=np.array(list(self.embeddings.values()), dtype=np.float16),
            )
            temporary.replace(self.embedding_file)
        except OSError:
            pass

    def rank(self, query: str, documents: list[str], limit: int) -> list[tuple[int, float]]:
        if not documents:
            return []
        # Document vectors repeat between calls and bridge restarts (scene objects, asset names); only new texts reach the model.
        cache = self.embeddings
        missing = [text for text in dict.fromkeys(documents) if text not in cache]
        if len(cache) + len(missing) > 50000:
            cache.clear()
            missing = list(dict.fromkeys(documents))
        for text, vector in zip(missing, self.model.embed(missing)):
            cache[text] = np.asarray(vector, dtype=np.float16)
        if missing:
            self.save_embeddings()
        vectors = [next(self.model.embed([query])), *(cache[text] for text in documents)]
        query_vector = np.asarray(vectors[0], dtype=np.float32)
        query_norm = float(np.linalg.norm(query_vector))
        if query_norm == 0.0:
            raise RuntimeError("The NLP model produced a zero query vector.")
        scores: list[tuple[int, float]] = []
        for index, vector in enumerate(vectors[1:]):
            document_vector = np.asarray(vector, dtype=np.float32)
            denominator = query_norm * float(np.linalg.norm(document_vector))
            score = float(np.dot(query_vector, document_vector) / denominator) if denominator else -1.0
            if not math.isfinite(score):
                raise RuntimeError("The NLP model produced a non-finite similarity score.")
            scores.append((index, score))
        scores.sort(key=lambda item: item[1], reverse=True)
        return scores[:limit]


def component_document(type_name: str) -> str:
    simple_name = type_name.rsplit(".", 1)[-1]
    words = " ".join(wordninja.split(simple_name))
    namespace = type_name.rsplit(".", 1)[0].replace(".", " ") if "." in type_name else ""
    return f"Unity component {type_name} {namespace} {words}"


def animation_clip_document(item: dict[str, Any]) -> str:
    return f"Unity animation clip {item.get('name', '')} {item.get('path', '')}"


def animation_property_document(item: dict[str, Any]) -> str:
    return (
        f"Unity animation property {item.get('property', '')} {item.get('componentType', '')} "
        f"object {item.get('objectPath', '')} kind {item.get('kind', '')}"
    )


CLIP_KEYS_SHOWN = 12


# Keys as the curve text of object-info ("0:0 30:1"); a baked curve (a key every frame or two) reads as its summary.
def compact_animation_keys(row: dict[str, Any]) -> str:
    cells = [cell for cell in row.get("cells", []) if cell.get("hasKey")]
    reference = row.get("kind") == "object"
    values = [(cell.get("reference") or "None") if reference else cell.get("value", 0) for cell in cells]
    frames = [cell.get("frame", 0) for cell in cells]
    if not cells:
        return "no keys"
    if len(set(map(str, values))) == 1:
        return f"{len(cells)} keys {frames[0]}..{frames[-1]}, constant {values[0]}"
    if len(cells) <= CLIP_KEYS_SHOWN:
        return " ".join(f"{frame}:{value}" for frame, value in zip(frames, values))
    if reference:
        changes = [f"{frame}:{value}" for index, (frame, value) in enumerate(zip(frames, values)) if index == 0 or value != values[index - 1]]
        return f"{len(cells)} keys; changes " + " ".join(changes[:CLIP_KEYS_SHOWN]) + (" …" if len(changes) > CLIP_KEYS_SHOWN else "")
    return (f"{len(cells)} keys {frames[0]}..{frames[-1]}, values {min(values):g}..{max(values):g}, "
            f"start {values[0]:g}, end {values[-1]:g}; every key: animation-property get")


def compact_animation_clip(table: dict[str, Any]) -> dict[str, Any]:
    properties = [{
        "id": row.get("id", ""),
        "objectPath": row.get("objectPath", ""),
        "keys": compact_animation_keys(row),
    } for row in table.get("rows", [])]
    frames = table.get("frames", [])
    result = {
        "name": table.get("name", ""),
        "path": table.get("path", ""),
        "frameRate": table.get("frameRate", 0),
        "frameRange": [frames[0], frames[-1]] if frames else [],
        "properties": properties,
    }
    if table.get("events"):
        result["events"] = table["events"]
    return result


def animator_document(item: dict[str, Any]) -> str:
    return f"Unity Animator object {item.get('path', '')} controller {item.get('controller', '')}"


def compact_motion_state(item: dict[str, Any]) -> dict[str, Any]:
    return {
        "layer": item.get("layer", ""),
        "state": item.get("state", ""),
        "path": item.get("path", ""),
        "motion": compact_motion(item.get("motion")),
    }


def compact_motion(value: Any) -> dict[str, Any] | None:
    if not isinstance(value, dict) or value.get("kind") in {None, "", "None"}:
        return None
    result = {"kind": value.get("kind"), "name": value.get("name", ""), "path": value.get("path", "")}
    if value.get("kind") != "BlendTree":
        return result
    result.update({
        "blendType": value.get("blendType", ""),
        "blendParameter": value.get("blendParameter", ""),
        "blendParameterY": value.get("blendParameterY", ""),
        "useAutomaticThresholds": value.get("useAutomaticThresholds", False),
        "minThreshold": value.get("minThreshold", 0.0),
        "maxThreshold": value.get("maxThreshold", 0.0),
        "children": [
            {
                "motion": compact_motion({
                    "kind": child.get("kind"),
                    "name": child.get("name", ""),
                    "path": child.get("assetPath", ""),
                    "blendType": child.get("blendType", ""),
                    "blendParameter": child.get("blendParameter", ""),
                    "blendParameterY": child.get("blendParameterY", ""),
                    "useAutomaticThresholds": child.get("useAutomaticThresholds", False),
                    "minThreshold": child.get("minThreshold", 0.0),
                    "maxThreshold": child.get("maxThreshold", 0.0),
                    "children": [],
                }),
                "threshold": child.get("threshold", 0.0),
                "position": child.get("position", {}),
                "timeScale": child.get("timeScale", 1.0),
                "cycleOffset": child.get("cycleOffset", 0.0),
                "directBlendParameter": child.get("directBlendParameter", ""),
                "mirror": child.get("mirror", False),
            }
            for child in value.get("children", [])
        ],
    })
    return result


def compact_controller_graph(controller: dict[str, Any]) -> dict[str, Any]:
    def visit(machine: dict[str, Any]) -> None:
        for state in machine.get("states", []):
            state["motion"] = compact_motion(state.get("motion"))
        for child in machine.get("stateMachines", []):
            visit(child)

    for layer in controller.get("layers", []):
        machine = layer.get("stateMachine")
        if isinstance(machine, dict):
            visit(machine)
    return controller


def compact_runtime_animator(animator: dict[str, Any]) -> dict[str, Any]:
    def state(value: Any) -> dict[str, Any] | None:
        if not isinstance(value, dict) or value.get("path") in {None, "", "0"}:
            return None
        return {
            "name": value.get("name", ""),
            "path": value.get("path", ""),
            "normalizedTime": value.get("normalizedTime", 0.0),
            "length": value.get("length", 0.0),
            "speed": value.get("speed", 0.0),
        }

    parameters = []
    for parameter in animator.get("parameters", []):
        parameter_type = parameter.get("type", "")
        value_key = {"Float": "floatValue", "Int": "intValue", "Bool": "boolValue", "Trigger": "boolValue"}.get(parameter_type)
        parameters.append({"name": parameter.get("name", ""), "type": parameter_type, "value": parameter.get(value_key)})
    return {
        "path": animator.get("path", ""),
        "enabled": animator.get("enabled", False),
        "activeInHierarchy": animator.get("activeInHierarchy", False),
        "state": animator.get("state", ""),
        "speed": animator.get("speed", 0.0),
        "layers": [
            {
                "index": layer.get("index", 0),
                "name": layer.get("name", ""),
                "weight": layer.get("weight", 0.0),
                "current": state(layer.get("current")),
                "inTransition": layer.get("inTransition", False),
                "next": state(layer.get("next")),
                "transitionTime": layer.get("transitionTime", 0.0),
            }
            for layer in animator.get("layers", [])
        ],
        "parameters": parameters,
    }


def request_values(request: dict[str, Any]) -> list[dict[str, str]]:
    values = request.get("values", [])
    if not isinstance(values, list):
        raise ValueError("values must be a list.")
    for entry in values:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str) or not isinstance(entry.get("value"), str):
            raise ValueError("Each value must contain string path and value fields.")
        if "append" in entry and not isinstance(entry.get("append"), bool):
            raise ValueError("A value append flag must be boolean.")
        if "remove" in entry and not isinstance(entry.get("remove"), bool):
            raise ValueError("A value remove flag must be boolean.")
    return values


def required_action(request: dict[str, Any], allowed: set[str]) -> str:
    action = required_string(request, "action").casefold()
    if action not in allowed:
        raise ValueError("action must be one of: " + ", ".join(sorted(allowed)))
    return action


def package_document(item: dict[str, Any]) -> str:
    dependencies = " ".join(dependency.get("name", "") for dependency in item.get("dependencies", []))
    return (
        f"Unity package {item.get('name', '')} {item.get('displayName', '')} "
        f"{item.get('description', '')} dependencies {dependencies}"
    )


def installed_package_entry(item: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": item.get("name", ""),
        "version": item.get("version", ""),
        "dependencies": item.get("dependencies", []),
        "minimumUnity": item.get("minimumUnity"),
        "compatible": item.get("compatible", True),
    }


def searched_package_entry(item: dict[str, Any]) -> dict[str, Any]:
    description = " ".join(str(item.get("description", "")).split())
    return {
        "name": item.get("name", ""),
        "displayName": item.get("displayName", ""),
        "version": item.get("version", ""),
        "description": description[:300],
        "dependencies": item.get("dependencies", []),
        "minimumUnity": item.get("minimumUnity"),
        "compatible": item.get("compatible", True),
    }


def optional_string(value: Any) -> str:
    return value.strip() if isinstance(value, str) else ""


def project_plugin_identity(project: Path) -> tuple[str, str]:
    manifest = project / "Assets" / "UnityAgentBridge" / "CodexPlugin~" / "unity-agent-bridge" / "codex-plugin" / "plugin.json"
    try:
        value = json.loads(manifest.read_text(encoding="utf-8"))["version"]
    except (FileNotFoundError, KeyError, TypeError, json.JSONDecodeError, OSError) as error:
        raise RuntimeError("Unity Agent Bridge Codex plugin manifest is invalid.") from error
    if not isinstance(value, str) or not value:
        raise RuntimeError("Unity Agent Bridge Codex plugin version is invalid.")
    return value, plugin_revision(value)


def plugin_revision(version: str) -> str:
    _base, separator, suffix = version.partition("+")
    product, dot, revision = suffix.partition(".")
    if not separator or product not in {"codex", "claude"} or not dot or not revision:
        raise RuntimeError("Unity Agent Bridge plugin version has no revision.")
    return revision


def component_class_name(asset_path: str) -> str:
    name = re.sub(r"[^A-Za-z0-9_]", "", Path(asset_path).stem)
    if not name or name[0].isdigit():
        raise ValueError(f"Asset file name cannot form a valid C# class name: {asset_path}")
    return name


def scene_document(item: dict[str, Any]) -> str:
    component_names = " ".join(component.get("type", "") for component in item.get("components", []))
    return f"Unity scene object {item.get('name', '')} path {item.get('path', '')} tag {item.get('tag', '')} layer {item.get('layer', '')} components {component_names}"


def tree_entry(item: dict[str, Any]) -> dict[str, Any]:
    return {
        "path": item.get("path", ""),
        "name": item.get("name", ""),
        "depth": item.get("depth", 0),
    }


def scene_name_from_object_path(path: str) -> str:
    segment = urllib.parse.unquote(path).strip("/").split("/", 1)[0]
    return re.sub(r"\[\d+\]$", "", segment)


def search_entry(item: dict[str, Any]) -> dict[str, Any]:
    return {
        **tree_entry(item),
        "active": item.get("activeInHierarchy", False),
        "tag": item.get("tag", ""),
        "layer": item.get("layer", 0),
        "components": [component.get("type", "") for component in item.get("components", [])],
    }


# Without --component the object reads like the Inspector with components folded: their types, Transform values
# and any component warnings; a component's fields come with --component.
def detailed_object(item: dict[str, Any], overview: bool = False) -> dict[str, Any]:
    result = search_entry(item)
    result["activeSelf"] = item.get("activeSelf", False)
    if item.get("staticFlags"):
        result["static"] = item["staticFlags"]
    world_position = item.get("worldPosition") or {}
    result["worldPosition"] = {
        axis: float(f"{float(world_position.get(axis, 0.0)):.6g}") for axis in ("x", "y", "z")
    }
    components = [component_values(component) for component in item.get("components", [])]
    if overview:
        result["components"] = [component["type"] for component in components]
        transform = next((component for component in components if component["type"] in {"UnityEngine.Transform", "UnityEngine.RectTransform"}), None)
        if transform:
            result["transform"] = transform["values"]
        warnings = [f"{component['type'].rsplit('.', 1)[-1]}: {warning}" for component in components for warning in component.get("warnings", [])]
        if warnings:
            result["warnings"] = warnings
    else:
        result["components"] = components
    prefab_asset_path = item.get("prefabAssetPath", "")
    if prefab_asset_path:
        result["prefab"] = {
            "assetPath": prefab_asset_path,
            "instanceRootPath": item.get("prefabInstanceRootPath", ""),
        }
    return result


def compact_log(item: dict[str, Any], include_stack: bool = False) -> dict[str, Any]:
    lines = str(item.get("message", "")).splitlines()
    timestamp = log_timestamp(item)
    age_seconds = max(0, int((datetime.now(timezone.utc) - timestamp).total_seconds()))
    result = {
        "ageSeconds": age_seconds,
        "type": item.get("type", ""),
        "message": lines[0] if lines else "",
        "count": int(item.get("count", 1)),
    }
    if include_stack:
        result["stackTrace"] = item.get("stackTrace", "")
    return result


def log_timestamp(item: dict[str, Any]) -> datetime:
    value = str(item.get("timestampUtc", ""))
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)
    except ValueError:
        return datetime.min.replace(tzinfo=timezone.utc)


def collapse_logs(logs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[tuple[str, str], dict[str, Any]] = {}
    for index, item in enumerate(logs):
        key = (
            str(item.get("type", "")),
            str(item.get("message", "")),
        )
        group = groups.get(key)
        if group is None:
            group = dict(item)
            group["count"] = 0
            groups[key] = group
        group["count"] += 1
        group["timestampUtc"] = item.get("timestampUtc", "")
        group["stackTrace"] = item.get("stackTrace", "")
        group["lastIndex"] = index
    return sorted(groups.values(), key=lambda item: int(item["lastIndex"]))


def component_values(component: dict[str, Any]) -> dict[str, Any]:
    raw = component.get("json", "")
    references = component.get("references", [])
    values = normalize_serialized_value(json.loads(raw)) if raw else {}
    if isinstance(values, dict) and len(values) == 1:
        wrapper, wrapped = next(iter(values.items()))
        short_type = str(component.get("type", "")).rsplit(".", 1)[-1]
        if isinstance(wrapped, dict) and wrapper in {short_type, "MonoBehaviour"}:
            values = wrapped
    apply_reference_values(values, references)
    simplify_unity_events(values)
    apply_enum_values(values, component.get("enums", []))
    if isinstance(values, dict):
        hidden = set(component.get("hidden") or []) | {"serializedVersion"}
        values = {key: compact_curves(item) for key, item in values.items() if key not in hidden}
    result = {
        "type": component.get("type", ""),
        "values": values,
    }
    warnings = component.get("warnings", [])
    actions = component.get("actions", [])
    if warnings:
        result["warnings"] = warnings
    if actions:
        # An action whose label only repeats its id is the id alone.
        result["actions"] = [
            item.get("id") if str(item.get("label", "")).casefold() == str(item.get("id", "")).casefold() else item
            for item in actions
        ]
    return result


# As the Inspector shows them: a switched-off module is false, MinMaxCurve "0.5" or "0.2..0.8",
# MinMaxGradient "r,g,b,a" or "r,g,b,a..r,g,b,a", curves and gradients as their keys.
def compact_curves(value: Any) -> Any:
    if isinstance(value, list):
        return [compact_curves(item) for item in value]
    if not isinstance(value, dict):
        return value
    if "minMaxState" in value and "scalar" in value:
        return min_max_curve(value)
    if "minMaxState" in value and "maxColor" in value:
        return min_max_gradient(value)
    if isinstance(value.get("m_Curve"), list):
        return curve_keys(value)
    if "m_NumColorKeys" in value:
        return gradient_keys(value)
    if value.get("enabled") is False and len(value) >= 2:
        return False
    return {key: compact_curves(item) for key, item in value.items() if key != "serializedVersion"}


def min_max_curve(value: dict[str, Any]) -> Any:
    state = value.get("minMaxState")
    if state == 0:
        return value.get("scalar")
    if state == 3:
        return f"{value.get('minScalar')}..{value.get('scalar')}"
    curves = curve_keys(value.get("maxCurve", {}))
    if state == 2:
        curves = f"{curve_keys(value.get('minCurve', {}))} .. {curves}"
    multiplier = value.get("scalar", 1)
    return curves if multiplier == 1 else f"{curves} *{multiplier:g}"


def color_text(color: Any) -> str:
    if not isinstance(color, dict):
        return str(color)
    return ",".join(f"{float(color.get(channel, 0)):.4g}" for channel in ("r", "g", "b", "a"))


def min_max_gradient(value: dict[str, Any]) -> Any:
    state = value.get("minMaxState")
    if state == 0:
        return color_text(value.get("maxColor"))
    if state == 2:
        return f"{color_text(value.get('minColor'))}..{color_text(value.get('maxColor'))}"
    if state == 3:
        return f"{gradient_keys(value.get('minGradient', {}))} .. {gradient_keys(value.get('maxGradient', {}))}"
    return gradient_keys(value.get("maxGradient", {}))


# Curves and gradients print as the text --set takes back: "0:0 0.5:1 1:0", "0:#FF8000 1:#0000FF / 0:1 1:0".
def curve_keys(value: Any) -> Any:
    keys = value.get("m_Curve") if isinstance(value, dict) else None
    if not isinstance(keys, list):
        return value
    return " ".join(f"{float(key.get('time', 0)):.4g}:{float(key.get('value', 0)):.4g}" for key in keys if isinstance(key, dict))


def gradient_color(key: dict[str, Any]) -> str:
    channels = [float(key.get(channel, 0)) for channel in ("r", "g", "b")]
    if all(0 <= channel <= 1 for channel in channels):
        return "#" + "".join(f"{round(channel * 255):02X}" for channel in channels)
    return ",".join(f"{channel:.4g}" for channel in channels)


def gradient_keys(value: Any) -> Any:
    if not isinstance(value, dict) or "m_NumColorKeys" not in value:
        return value
    colors = [
        f"{int(value.get(f'ctime{index}', 0)) / 65535:.4g}:{gradient_color(value.get(f'key{index}', {}))}"
        for index in range(int(value.get("m_NumColorKeys", 0)))
    ]
    alphas = [
        (int(value.get(f"atime{index}", 0)) / 65535, float(value.get(f"key{index}", {}).get("a", 1)))
        for index in range(int(value.get("m_NumAlphaKeys", 0)))
    ]
    text = " ".join(colors)
    if any(alpha != 1 for _, alpha in alphas):
        text += " / " + " ".join(f"{moment:.4g}:{alpha:.4g}" for moment, alpha in alphas)
    return text


def apply_enum_values(values: Any, enums: Any) -> None:
    if not isinstance(values, dict) or not isinstance(enums, list):
        return
    for entry in enums:
        if isinstance(entry, dict) and isinstance(entry.get("path"), str):
            assign_serialized_path(values, entry["path"], entry.get("value"))


def normalize_serialized_value(value: Any) -> Any:
    if isinstance(value, float):
        return float(f"{value:.6g}")
    if isinstance(value, list):
        return [normalize_serialized_value(item) for item in value]
    if not isinstance(value, dict):
        return value
    if set(value) == {"instanceID"}:
        return None
    rect_offset = {"m_Left", "m_Right", "m_Top", "m_Bottom"}
    if rect_offset.issubset(value):
        result = {key: normalize_serialized_value(item) for key, item in value.items() if key not in rect_offset}
        result.update({
            "left": value["m_Left"],
            "right": value["m_Right"],
            "top": value["m_Top"],
            "bottom": value["m_Bottom"],
        })
        return result
    return {key: normalize_serialized_value(item) for key, item in value.items()}


def apply_reference_values(values: Any, references: Any) -> None:
    if not isinstance(values, dict) or not isinstance(references, list):
        return
    for reference in references:
        if not isinstance(reference, dict):
            continue
        path = reference.get("path")
        if not isinstance(path, str) or not path:
            continue
        assign_serialized_path(values, path, reference.get("value") or None)


def assign_serialized_path(root: Any, path: str, value: Any) -> None:
    tokens: list[str | int] = []
    parts = path.split(".")
    index = 0
    while index < len(parts):
        part = parts[index]
        if part == "Array" and index + 1 < len(parts):
            match = re.fullmatch(r"data\[(\d+)\]", parts[index + 1])
            if match:
                tokens.append(int(match.group(1)))
                index += 2
                continue
        tokens.append(part)
        index += 1
    current = root
    if tokens and isinstance(tokens[0], str) and tokens[0] not in current and len(current) == 1:
        wrapped = next(iter(current.values()))
        if isinstance(wrapped, dict):
            current = wrapped
    for token in tokens[:-1]:
        if isinstance(token, int):
            if not isinstance(current, list) or token >= len(current):
                return
            current = current[token]
        else:
            if not isinstance(current, dict) or token not in current:
                return
            current = current[token]
    final = tokens[-1]
    if isinstance(final, int):
        if isinstance(current, list) and final < len(current):
            current[final] = value
    elif isinstance(current, dict) and final in current:
        current[final] = value


def property_name_key(name: str) -> str:
    name = name[2:] if name[:2].casefold() == "m_" else name
    return "".join(character for character in name if character.isalnum()).casefold()


def serialized_path_value(root: Any, path: str) -> Any:
    tokens: list[str | int] = []
    parts = path.split(".")
    index = 0
    while index < len(parts):
        part = parts[index]
        if part == "Array" and index + 1 < len(parts):
            match = re.fullmatch(r"data\[(\d+)\]", parts[index + 1])
            if match:
                tokens.append(int(match.group(1)))
                index += 2
                continue
        tokens.append(part)
        index += 1
    current = root
    if tokens and isinstance(tokens[0], str) and isinstance(current, dict) and tokens[0] not in current and len(current) == 1:
        wrapped = next(iter(current.values()))
        if isinstance(wrapped, dict):
            current = wrapped
    for token_index, token in enumerate(tokens):
        if isinstance(token, int):
            if not isinstance(current, list) or token >= len(current):
                raise KeyError(path)
            current = current[token]
            continue
        if not isinstance(current, dict):
            raise KeyError(path)
        candidates = [token]
        if token_index == 0 and not token.startswith("m_"):
            candidates.append("m_" + token[:1].upper() + token[1:])
        key = next((candidate for candidate in candidates if candidate in current), None)
        if key is None:
            # Names match as component-modify matches them: with or without m_, in any case (OutputAudioMixerGroup).
            key = next((candidate for candidate in current if property_name_key(candidate) == property_name_key(token)), None)
        if key is None:
            raise KeyError(path)
        current = current[key]
    return current


def animation_property_matches(row: dict[str, Any], requested: str) -> bool:
    if row.get("id") == requested:
        return True
    component = str(row.get("componentType", ""))
    property_name = str(row.get("property", ""))
    return requested in {property_name, component + "/" + property_name, component.rsplit(".", 1)[-1] + "/" + property_name}


def lexical_matches(query: str, values: list[Any], text=lambda value: str(value)) -> list[Any]:
    needle = re.sub(r"[^a-z0-9а-яё]+", "", query.casefold())
    if not needle:
        return []
    return [value for value in values if needle in re.sub(r"[^a-z0-9а-яё]+", "", text(value).casefold())]


def result_page(request: dict[str, Any]) -> tuple[int, int]:
    offset = request.get("offset", 0)
    limit = request.get("limit", RESULT_COUNT)
    if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
        raise ValueError("offset must be 0 or more.")
    if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1 or limit > 100:
        raise ValueError("limit must be between 1 and 100.")
    return offset, limit


# total appears only when more results remain past this page.
def paged(candidates: list[Any], offset: int, limit: int) -> dict[str, Any]:
    result: dict[str, Any] = {"ok": True, "candidates": candidates[offset:offset + limit]}
    if len(candidates) > offset + limit:
        result["total"] = len(candidates)
    return result


def ranked_names(query: str, values: list[Any], name=lambda value: str(value), path=lambda value: str(value), keep_path_matches: bool = False) -> list[Any]:
    def plain(text: str) -> str:
        return re.sub(r"[^a-z0-9а-яё]+", "", text.casefold())

    wanted = query.strip().casefold()
    squeezed = plain(query)
    words = [word for word in re.split(r"[^a-z0-9а-яё]+", wanted) if word]
    if not squeezed:
        return []
    ranked = []
    for order, value in enumerate(values):
        label = name(value).casefold()
        location = path(value).casefold()
        if label == wanted:
            tier = 0
        elif plain(label) == squeezed:
            tier = 1
        elif label.startswith(wanted):
            tier = 2
        elif words and all(word in label for word in words):
            tier = 3
        elif squeezed in plain(label):
            tier = 4
        elif words and all(word in location for word in words):
            tier = 5
        else:
            continue
        ranked.append((tier, location.count("/"), len(label), order, value))
    ranked.sort(key=lambda item: item[:4])
    if ranked and ranked[0][0] < 5 and not keep_path_matches:
        ranked = [item for item in ranked if item[0] < 5]
    return [item[-1] for item in ranked]


def simplify_unity_events(value: Any) -> None:
    if isinstance(value, list):
        for item in value:
            simplify_unity_events(item)
        return
    if not isinstance(value, dict):
        return
    for key, item in list(value.items()):
        if isinstance(item, dict) and isinstance(item.get("m_PersistentCalls"), dict):
            calls = item["m_PersistentCalls"].get("m_Calls", [])
            if isinstance(calls, list):
                value[key] = [unity_event_call(call) for call in calls if isinstance(call, dict)]
                continue
        simplify_unity_events(item)


def unity_event_call(call: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {
        "target": call.get("m_Target"),
        "method": call.get("m_MethodName", ""),
    }
    mode = int(call.get("m_Mode", 1))
    state = int(call.get("m_CallState", 2))
    mode_names = {0: "EventDefined", 1: "Void", 2: "Object", 3: "Int", 4: "Float", 5: "String", 6: "Bool"}
    state_names = {0: "Off", 1: "EditorAndRuntime", 2: "RuntimeOnly"}
    if mode != 1:
        result["mode"] = mode_names.get(mode, mode)
    if state != 2:
        result["state"] = state_names.get(state, state)
    arguments = call.get("m_Arguments", {})
    argument_names = {
        2: "m_ObjectArgument",
        3: "m_IntArgument",
        4: "m_FloatArgument",
        5: "m_StringArgument",
        6: "m_BoolArgument",
    }
    argument_name = argument_names.get(mode)
    if argument_name and isinstance(arguments, dict):
        result["argument"] = arguments.get(argument_name)
    return result


UNITY_EVENT_ARGUMENTS = {"Object": "objectArgument", "Int": "intArgument", "Float": "floatArgument", "String": "stringArgument", "Bool": "boolArgument"}


# A listener is written as object-info shows it: `argument` with its mode; the mode follows the value when omitted.
def unity_event_arguments(value: str) -> str:
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError:
        return value
    calls = parsed if isinstance(parsed, list) else [parsed]
    changed = False
    for call in calls:
        if not isinstance(call, dict) or "argument" not in call or "method" not in call:
            continue
        argument = call.pop("argument")
        mode = call.get("mode") or ("Bool" if isinstance(argument, bool) else "Int" if isinstance(argument, int)
                                    else "Float" if isinstance(argument, float) else "String")
        if mode not in UNITY_EVENT_ARGUMENTS:
            raise ValueError(f"UnityEvent argument needs mode {'|'.join(UNITY_EVENT_ARGUMENTS)}; got {mode}.")
        call["mode"] = mode
        call[UNITY_EVENT_ARGUMENTS[mode]] = argument
        changed = True
    return json.dumps(parsed, ensure_ascii=False, separators=(",", ":")) if changed else value


def object_result(
    result: dict[str, Any],
    component_type: str | None = None,
    component_index: int = -1,
    property_path: str | None = None,
    runtime: bool = False,
    runtime_value: Callable[[int, str], Any] | None = None,
) -> dict[str, Any]:
    object_info = result.get("objectInfo", {})
    if component_type:
        components = [
            component for component in object_info.get("components", [])
            if component_type_matches(str(component.get("type", "")), component_type)
        ]
        if not components:
            present = ", ".join(str(item.get("type", "")).rsplit(".", 1)[-1] for item in object_info.get("components", []))
            raise ValueError(f"Component was not found on object: {component_type}; its components: {present}")
        if component_index >= len(components):
            raise ValueError(f"Component index {component_index} is outside 0..{len(components) - 1}.")
        # Without --component-index every matching component comes, in Inspector order (its index).
        indexes = range(len(components)) if component_index < 0 else [component_index]
        if property_path:
            names = [name.strip() for name in property_path.split(",") if name.strip()]
            found_by_index: list[dict[str, Any]] = []
            for index in indexes:
                values = component_values(components[index]).get("values", {})
                found: dict[str, Any] = {}
                for name in names:
                    try:
                        found[name] = values[name] if runtime else serialized_path_value(values, name)
                    except KeyError as error:
                        try:
                            if runtime_value is None:
                                raise
                            found[name] = runtime_value(index, name)
                        except (KeyError, ValueError, RuntimeError):
                            raise ValueError(f"Component property was not found: {name}") from error
                found_by_index.append(found)
            result = {"ok": True, "path": object_info.get("path", ""), "component": components[indexes[0]].get("type", "")}
            if len(found_by_index) > 1:
                result["byIndex"] = [found[names[0]] if len(names) == 1 else found for found in found_by_index]
                if len(names) == 1:
                    result["property"] = names[0]
            elif len(names) == 1:
                result.update({"property": names[0], "value": found_by_index[0][names[0]]})
            else:
                result["values"] = found_by_index[0]
            return result
        object_info = dict(object_info)
        object_info["components"] = [dict(components[index], index=index) if len(indexes) > 1 else components[index] for index in indexes]
    elif property_path:
        # Fields of the object itself (layer, tag, active…) read like component properties.
        names = [name.strip() for name in property_path.split(",") if name.strip()]
        missing = [name for name in names if name not in object_info or name == "components"]
        if missing:
            fields = ", ".join(key for key in object_info if key != "components")
            raise ValueError(f"Object field was not found: {', '.join(missing)}; object fields: {fields}; component properties need --component.")
        # Read as the overview shows them (worldPosition rounded like everywhere else).
        shown = {**object_info, **detailed_object(object_info, overview=True)}
        result = {"ok": True, "path": object_info.get("path", "")}
        if len(names) == 1:
            result.update({"property": names[0], "value": shown[names[0]]})
        else:
            result["values"] = {name: shown[name] for name in names}
        return result
    response: dict[str, Any] = {"ok": True, "objectInfo": detailed_object(object_info, overview=not component_type)}
    message = result.get("message")
    if message:
        response["message"] = message
    return response


def component_type_matches(actual: str, requested: str) -> bool:
    actual_name = actual.rsplit(".", 1)[-1]
    requested_name = requested.rsplit(".", 1)[-1]
    return actual.casefold() == requested.casefold() or actual_name.casefold() == requested_name.casefold()


def message_result(result: dict[str, Any]) -> dict[str, Any]:
    response: dict[str, Any] = {"ok": True}
    message = result.get("message")
    if message:
        response["message"] = message
    return response


def asset_result(
    result: dict[str, Any],
    property_path: str | None = None,
    section: str | None = None,
) -> dict[str, Any]:
    asset_info = result.get("assetInfo", {})
    # A view (Project Settings, Volume Profile, Audio Mixer…) replaces serialized paths with Inspector labels.
    view = asset_info.pop("view", None)
    if view:
        asset_info.update(json.loads(view))
    for key in ("subAssets", "changes", "importerType"):
        if not asset_info.get(key):
            asset_info.pop(key, None)
    if section == "model":
        model = asset_info.get("model")
        if not isinstance(model, dict):
            raise ValueError("Asset is not a model with mesh data.")
        return {"ok": True, "path": asset_info.get("assetPath", ""), "model": model}
    if view and not asset_info.get("properties"):
        asset_info.pop("properties", None)
    # One part of a view comes alone, as one property of an asset does; the asset's header and actions were in the full read.
    if property_path and view and "properties" not in asset_info:
        header = {"name", "assetPath", "type", "importerType", "actions", "subAssets", "model"}
        return {"ok": True, "path": asset_info.get("assetPath", ""), **{key: value for key, value in asset_info.items() if key not in header}}
    if not property_path or view and "properties" not in asset_info:
        asset_info.pop("model", None)
        if not asset_info.get("actions"):
            asset_info.pop("actions", None)
        else:
            # Ids are the labels in another spelling; asset-action takes either.
            asset_info["actions"] = [item.get("id") if isinstance(item, dict) else item for item in asset_info["actions"]]
        return {"ok": True, "assetInfo": asset_info}
    properties = asset_info.get("properties", [])
    candidates = {property_path}
    if not property_path.startswith(("asset:", "importer:")):
        candidates.update({"asset:" + property_path, "importer:" + property_path})
    matches = [item for item in properties if item.get("path") in candidates]
    if not matches:
        raise ValueError(f"Asset property was not found: {property_path}")
    if len(matches) > 1:
        raise ValueError(f"Asset property is ambiguous; use its asset: or importer: path: {property_path}")
    item = matches[0]
    if len(properties) > 1:
        return {
            "ok": True,
            "path": asset_info.get("assetPath", ""),
            "property": item.get("path", ""),
            "properties": properties,
        }
    return {
        "ok": True,
        "path": asset_info.get("assetPath", ""),
        "property": item.get("path", ""),
        "type": item.get("type", ""),
        "value": item.get("value", ""),
        "writable": bool(item.get("writable", False)),
    }


# Properties read as the Inspector's name → value; one requested property keeps its type.
def shader_view(shader: dict[str, Any], keep_types: bool = False) -> dict[str, Any]:
    if not keep_types:
        shader["properties"] = {item.get("name"): item.get("value") for item in shader.get("properties", [])}
    for key in ("defaults", "errors"):
        if not shader.get(key):
            shader.pop(key, None)
    defaults = shader.get("defaults") or []
    if len(defaults) > 30:
        shader["defaults"] = f"{len(defaults)} properties at the shader's defaults: shader-info --path {shader.get('shaderPath')}"
    return shader


def unity_message_json(result: dict[str, Any]) -> dict[str, Any]:
    message = result.get("message")
    if not isinstance(message, str) or not message:
        raise RuntimeError("Unity returned no game interaction state.")
    value = json.loads(message)
    if not isinstance(value, dict):
        raise RuntimeError("Unity returned an invalid game interaction state.")
    return value


def validated_game_actions(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        raise ValueError("actions must be a JSON list.")
    if len(value) > 100:
        raise ValueError("A game action batch cannot contain more than 100 actions.")

    schemas = {
        "click": ({"x", "y"}, {"button"}),
        "double_click": ({"x", "y"}, {"button"}),
        "hover": ({"x", "y"}, set()),
        "drag": ({"x", "y", "targetX", "targetY"}, {"button", "duration"}),
        "scroll": ({"x", "y"}, {"scrollX", "scrollY"}),
        "press_key": ({"key"}, {"duration"}),
        "key_down": ({"key"}, set()),
        "key_up": ({"key"}, set()),
        "type_text": ({"text"}, set()),
        "wait": ({"seconds"}, {"timeScale", "object", "state"}),
        "screenshot": (set(), {"scale"}),
        "zoom": ({"region"}, {"scale"}),
    }
    normalized: list[dict[str, Any]] = []
    duration_total = 0.0
    for index, raw in enumerate(value):
        if not isinstance(raw, dict):
            raise ValueError(f"actions[{index}] must be an object.")
        action = required_string(raw, "action")
        if action not in schemas:
            raise ValueError(f"Unsupported game action: {action}")
        required, optional = schemas[action]
        missing = required.difference(raw)
        unknown = set(raw).difference(required | optional | {"action"})
        if missing:
            raise ValueError(f"actions[{index}] is missing: {', '.join(sorted(missing))}")
        if unknown:
            raise ValueError(f"actions[{index}] has unknown fields: {', '.join(sorted(unknown))}")

        current = dict(raw)
        for coordinate in ("x", "y", "targetX", "targetY"):
            if coordinate in current:
                current[coordinate] = finite_number(current, coordinate)
        if "button" in current:
            button = required_string(current, "button").casefold()
            if button not in GAME_BUTTONS:
                raise ValueError(f"actions[{index}].button must be left, right, or middle.")
            current["button"] = button
        if action == "scroll":
            current["scrollX"] = finite_number(current, "scrollX") if "scrollX" in current else 0.0
            current["scrollY"] = finite_number(current, "scrollY") if "scrollY" in current else 0.0
            if current["scrollX"] == 0 and current["scrollY"] == 0:
                raise ValueError(f"actions[{index}] requires non-zero scrollX or scrollY.")
        if action == "drag":
            duration = finite_number(current, "duration") if "duration" in current else 0.5
            if duration < 0 or duration > 10:
                raise ValueError(f"actions[{index}].duration must be between 0 and 10 seconds.")
            current["duration"] = duration
            duration_total += duration
        if action in {"press_key", "key_down", "key_up"}:
            current["key"] = required_string(current, "key")
        if action == "press_key":
            duration = finite_number(current, "duration") if "duration" in current else 0.15
            if duration < 0 or duration > 10:
                raise ValueError(f"actions[{index}].duration must be between 0 and 10 seconds.")
            current["duration"] = duration
            duration_total += duration
        if action == "type_text":
            current["text"] = required_string(current, "text")
        if action == "wait":
            seconds = finite_number(current, "seconds")
            if seconds < 0 or seconds > 30:
                raise ValueError(f"actions[{index}].seconds must be between 0 and 30.")
            scale = finite_number(current, "timeScale") if "timeScale" in current else 1.0
            if scale <= 0 or scale > 100:
                raise ValueError(f"actions[{index}].timeScale must be above 0 and at most 100.")
            current["seconds"] = seconds
            current["timeScale"] = scale
            if "state" in current and "object" not in current:
                raise ValueError(f"actions[{index}].state needs object.")
            if "object" in current:
                current["object"] = required_string(current, "object")
                current["state"] = current.get("state", "visible")
                if current["state"] not in {"visible", "hidden", "attached", "detached"}:
                    raise ValueError(f"actions[{index}].state must be visible, hidden, attached or detached.")
            duration_total += seconds / scale
        if action == "zoom":
            region = current.get("region")
            if (not isinstance(region, list) or len(region) != 4
                    or any(isinstance(item, bool) or not isinstance(item, (int, float)) or not math.isfinite(item) for item in region)):
                raise ValueError(f"actions[{index}].region must be [x0, y0, x1, y1].")
            if region[0] >= region[2] or region[1] >= region[3]:
                raise ValueError(f"actions[{index}].region needs x0 < x1 and y0 < y1.")
            current["region"] = [float(item) for item in region]
        if "scale" in current:
            scale = finite_number(current, "scale")
            if scale < 0.1 or scale > 1:
                raise ValueError(f"actions[{index}].scale must be between 0.1 and 1.")
            current["scale"] = scale
        normalized.append(current)

    if duration_total > 90:
        raise ValueError("The combined wait and drag duration cannot exceed 90 seconds.")
    return normalized


def finite_number(value: dict[str, Any], name: str) -> float:
    number = value.get(name)
    if isinstance(number, bool) or not isinstance(number, (int, float)) or not math.isfinite(float(number)):
        raise ValueError(f"{name} must be a finite number.")
    return float(number)


def positive_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError(f"{name} must be a positive integer.")
    return value


def required_string(request: dict[str, Any], name: str) -> str:
    value = request.get(name)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{name} must be a non-empty string.")
    return value


def required_string_list(request: dict[str, Any], name: str) -> list[str]:
    value = request.get(name)
    if not isinstance(value, list) or not value:
        raise ValueError(f"{name} must be a non-empty string list.")
    result = []
    for item in value:
        if not isinstance(item, str) or not item.strip():
            raise ValueError(f"{name} must be a non-empty string list.")
        result.append(item.strip())
    return result


def required_bool(request: dict[str, Any], name: str) -> bool:
    value = request.get(name)
    if not isinstance(value, bool):
        raise ValueError(f"{name} must be a boolean.")
    return value


class Handler(BaseHTTPRequestHandler):
    server_version = "UnityAgentBridge/1.0"

    def do_GET(self) -> None:
        if self.path != "/health":
            self.send_json(404, {"ok": False, "error": "Not found."})
            return
        if not self.authorized():
            return
        self.send_json(200, self.server.operations.invoke({"operation": "health"}))

    def do_POST(self) -> None:
        if not self.authorized():
            return
        if self.path == "/shutdown":
            self.send_json(200, {"ok": True, "message": "Server shutdown scheduled."})
            threading.Thread(target=self.server.shutdown, daemon=True).start()
            return
        if self.path != "/invoke":
            self.send_json(404, {"ok": False, "error": "Not found."})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > MAX_BODY_BYTES:
                raise ValueError("Request body size is invalid.")
            request = json.loads(self.rfile.read(length).decode("utf-8"))
            result = self.server.operations.invoke(request)
            self.send_json(200, result)
        except Exception as error:
            message = " ".join(str(error).split())[:500]
            self.send_json(400, {"ok": False, "error": f"{type(error).__name__}: {message}"})

    def authorized(self) -> bool:
        if self.headers.get("X-Unity-Agent-Token") == self.server.token:
            return True
        self.send_json(403, {"ok": False, "error": "Invalid bridge token."})
        return False

    def send_json(self, status: int, value: dict[str, Any]) -> None:
        body = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: Any) -> None:
        return


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address: tuple[str, int], token: str, operations: Operations) -> None:
        super().__init__(address, Handler)
        self.token = token
        self.operations = operations


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--model-cache", type=Path, required=True)
    arguments = parser.parse_args()

    project = arguments.project.resolve()
    runtime = project / "Library" / "UnityAgentBridge"
    config_path = runtime / "server.json"
    error_path = runtime / "server-error.json"
    active_project_file = discovery_file("active-project.json")
    mcp_error_files = [discovery_file("mcp-error-codex.txt"), discovery_file("mcp-error-claude.txt")]
    runtime.mkdir(parents=True, exist_ok=True)
    start_lock = acquire_start_lock(runtime)
    if start_lock is None:
        return 0
    unlink_when_available(error_path)

    try:
        operations = Operations(project, arguments.model_cache.resolve())
        server = Server(("127.0.0.1", arguments.port), arguments.token, operations)
        write_atomic(config_path, {
            "projectRoot": str(project),
            "port": arguments.port,
            "token": arguments.token,
            "pid": os.getpid(),
            "model": MODEL_NAME,
            "bridgeVersion": operations.plugin_version,
            "bridgeRevision": operations.plugin_revision,
        })
        write_atomic(active_project_file, {"projectRoot": str(project)})
        for mcp_error_file in mcp_error_files:
            mcp_error_file.unlink(missing_ok=True)
        try:
            server.serve_forever(poll_interval=0.2)
        finally:
            server.server_close()
            unlink_when_available(config_path)
            # Keep the selected project so MCP can restart its stopped Bridge.
        return 0
    except Exception as error:
        write_atomic(error_path, {"error": f"{type(error).__name__}: {error}", "traceback": traceback.format_exc()})
        unlink_when_available(config_path)
        # Keep the selected project so MCP can report its startup error.
        return 1
    finally:
        start_lock.close()


if __name__ == "__main__":
    raise SystemExit(main())
