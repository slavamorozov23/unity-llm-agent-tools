# Структура и настройки проекта

Стандартные пути: `Assets/Scripts`, `Assets/Scenes`, `Assets/Animations`, `Assets/Tiles`, `Assets/UnityAgentBridge/Prefabs`, `ProjectSettings`.

## Package Manager

```text
packages
packages-refresh
packages-search --query "новая система ввода"
package-install --name <имя[@версия]...> [--version <версия>]
package-update --name <имя[@версия]...> [--version <версия>]
package-remove --name <имя...>
```

## Project Settings

`asset-info --path "Project Settings"` перечисляет разделы; `"Project Settings/<раздел>[/<вкладка>]"` читается и меняется через `asset-info`/`asset-modify`/`asset-action` с подписями окна Edit > Project Settings. Изменение, требующее перезапуска Unity, выполняется только с `asset-modify --confirm`.

## Assets

`asset-import-package --path <путь к .unitypackage>`

```text
build-scenes
build-scene add|remove|enable|disable|move --path Assets/Сцена.unity [--index N]
```

## Освещение

Окна Window > Rendering — пути для `asset-info`/`asset-modify`/`asset-action` с подписями и кнопками окна: `"Lighting"` (вкладка Scene, ещё `"Lighting/Baked Lightmaps"`), `"Light Explorer[/<вкладка>]"`, `"Occlusion Culling"`. Запекание сразу отвечает; прогресс показывает `status`.

## InputManager

```text
input-axes
input-axis-create --name <имя> [--set <property> <JSON>]
input-axis-delete --name <имя>
```

Поля Axis: `descriptiveName`, `descriptiveNegativeName`, `negativeButton`, `positiveButton`, `altNegativeButton`, `altPositiveButton`, `gravity`, `dead`, `sensitivity`, `snap`, `invert`, `type`, `axis`, `joyNum`.

`type`: `0` KeyOrMouseButton, `1` MouseMovement, `2` JoystickAxis. `axis`: `0..27`. `joyNum`: `0` все, `1..11` конкретный джойстик.

При стандартизации сцены использовать только набор компонентов, явно заданный проектом.
