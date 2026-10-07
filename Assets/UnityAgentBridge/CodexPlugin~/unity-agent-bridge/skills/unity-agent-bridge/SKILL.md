---
name: unity-agent-bridge
description: "Управлять Unity через локальный Unity Agent Bridge: сценами, объектами, компонентами, префабами и Assets; Package Manager, InputManager, освещением, AnimationClip, Animator Controller и Timeline; диагностикой, профайлером, пунктами меню, Play Mode и кадрами сцены или игры. Использовать для чтения и изменения Unity-проекта, анимаций и действий мышью/клавиатурой в Game View. Не использовать для тайлмапов, произвольного редактирования кода и терминала."
---

# Unity Agent Bridge

CLI-команды выполнять через `~/plugins/unity-agent-bridge/skills/unity-agent-bridge/scripts/uab.ps1`; в справках указана часть команды после имени скрипта. MCP содержит `game_actions`, `scene_screenshot`, `profiler`, `sprite_editor` и `shader_preview`; остальные возможности плагина выполняются через CLI. Не определять состав Bridge по списку MCP-инструментов. Читать только нужную справку:

Вне папки Unity-проекта добавить к CLI-вызову `--project <путь>`.

- [scene.md](references/scene.md) — сцены, объекты, компоненты, префабы, Assets, материалы и Shader Graph;
- [project.md](references/project.md) — структура, настройки проекта и запекание света;
- [animation.md](references/animation.md) — AnimationClip, Animator и Timeline;
- [debug.md](references/debug.md) — состояние Unity, логи, Play Mode, профайлер, меню и разрешение;
- [game-control.md](references/game-control.md) — снимки сцены и управление игрой;
- [scope-and-placement.md](references/scope-and-placement.md) — применимость широкого редактирования и позиционирование.

Если MCP-инструменты недоступны, выполнить этот же `uab.ps1 mcp-status` и сообщить его `error`, не угадывая причину.

## Правила

1. Выполнять `tree` только когда нужен путь или иерархия.
2. Не выполнять `object-info` при точном path и однозначной операции.
3. После изменения проверять только изменённое значение. `tree` повторять лишь ради обновлённой иерархии.
4. НЛП-команды по умолчанию возвращают 10 результатов локальной модели. Балл задаёт порядок, а не достоверность. Не подменять модель другим поиском.
5. Не изменять `.unity`, `.prefab`, `.asset` и `.meta` через файловые инструменты.
6. При одинаковых компонентах указывать `--component-index`.
7. Команды плагина экономят токены и дают предсказуемый результат. Только если нужной команды нет: `eval --missing "чего не хватило" --code "C#"|--file snippet.cs` (выражение или тело метода с `return`); `report` из ответа передать пользователю.
