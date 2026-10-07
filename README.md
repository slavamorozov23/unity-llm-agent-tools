Unity Agent Bridge

Lets a coding agent work in the Unity 6 Editor the way a developer does, with short responses and commands modeled on the official Codex plugins.

| Target | Support |
|---|---|
| Windows | ✅ |
| macOS, Linux | ❌ installer and CLI are Windows only |
| Unity 6 | ✅ tested on 6000.4 |
| Older than Unity 6 | ❌ uses Unity 6-only APIs |
| Built-in, URP, HDRP | ✅ |
| Shader Graph, Timeline | ✅ when installed |
| Codex, Claude Code, Z.ai ZCode | ✅ |
| Agent help in English | ❌ Russian; commands, parameters and responses are English |

Install

1. Copy the UnityAgentBridge folder into your project's Assets.
2. Open Tools > Unity Agent Bridge and start the server. The first start downloads uv, Python 3.13, pip packages and an embedding model into Library/UnityAgentBridge (a few hundred MB, once per project).
3. Run python Assets/UnityAgentBridge/install_plugins.py (needs Python on PATH). It edits the agents' plugin configs and restarts Claude Desktop and the bridge server if they are running.
    - Default: Codex, Claude Code and ZCode; --codex-only, --claude-only or --zai-only picks one.
    - ZCode: add the printed path in Plugin Marketplace > + New, then click Install.

What it covers

About 90 CLI commands through uab.ps1 and five MCP tools: game_actions, scene_screenshot, profiler, sprite_editor, shader_preview.

- Scenes, objects, components, prefabs, assets, materials, Shader Graph.
- Project Settings by the labels shown in the window, Package Manager, InputManager, build scene list.
- Lighting, Light Explorer and Occlusion Culling windows, HDRP reflection probe bake.
- AnimationClip, Animator Controller, Timeline.
- Console, Editor status, Play Mode, menu items, Profiler with a hierarchy view.
- Game View control like computer use: batched input, screenshots and zoom, waits for objects to appear or disappear, a played-sound log, frame grids.
- Scene screenshots from any point or from an object's center.
- Asset, package and menu search by meaning with a local model.
- C# eval when no command fits; each call logs the reason so missing commands get added.

Principles

- Save agent tokens on requests and especially on plugin responses.
- Make commands work the way a developer works in the Editor, not through a hidden internal API, so results are predictable.
- Cover what developers actually use in the Editor and keep the Editor UI updating as commands run.
- Model commands on the official Codex plugins (computer use, browser use) so names, parameters and responses are what an agent expects.
- Extend existing commands instead of adding new ones, so the help stays short.
- Keep the obvious out of the help.

Not doing for now

- Build: giving the agent the whole build puts game development at risk. The agent configures the build, you press Build.
- Knowledge skills like the official Unity plugin's (UI Toolkit, tiles, 2D physics guides): they would clutter your own instructions for your game.
- Also skipped: Test Runner, running the built game, Frame Debugger, asset generation, Unity services, Unity Hub, project creation, CI, 300+ tools.

License

MIT, copyright Slava Morozov (https://github.com/slavamorozov23). Keep this notice in all copies and forks.
