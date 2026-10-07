# Анимации

Animator Controller и Timeline создаются `asset-create` с шаблонами `Animator Controller` и `Timeline`, удаляются `asset-delete`.

## AnimationClip

`clip-info --path PATH.anim` — компактные Property и ключи (`кадр:значение`; длинная запечённая кривая — сводкой). `animation-table --path PATH.anim` — полная таблица. Обе команды принимают `--query ТЕКСТ [--path Assets/ПАПКА]` вместо пути к клипу.

`animation-properties --path ОБЪЕКТ|PATH.anim [--query ТЕКСТ]` — доступные Property; `--query` ищет и в дочерних объектах. Возвращаемый `id` передавать без изменений.

Для поворота в градусах использовать `localEulerAnglesRaw.x/y/z`.

`animation-clip-create --path Assets/Animations/ИМЯ.anim [--set "Loop Time" true]`

`animation-clip-delete --path PATH.anim`

`animation-property get|create|modify|delete --path PATH.anim [--object-path PATH_ВНУТРИ_КЛИПА] [--property ID_ИЛИ_TYPE/PROPERTY] [--key frame=0 value=0.72 ...] [--keys JSON]`

События клипа — `--property Events` с ключами `frame|time`, `function` и одним из `float|int|string|object`.

Ключ задаётся через повторяемый `--key` или массив `--keys`. Нужен `frame` либо `time`; для float — `value`, для object — `reference`. Доступны `inTangent`, `outTangent`, `inWeight`, `outWeight`, `weightedMode`; без tangent Unity применяет автоматический. `modify` заменяет кривую; удаление без ключей удаляет Property.

`animation-clip-setting --path PATH.anim --parameter "Loop Time|Loop Pose|Cycle Offset|..."`

`animation-clip-setting --path PATH.anim --set "Loop Time" true`

## Animator

`animator-info --path ОБЪЕКТ`, `animator-info --controller CONTROLLER` или `animator-info --query ТЕКСТ`; остальные объекты ищет `object-find`.

`animator-component create|delete --path PATH [--controller ASSET_PATH]`

Если Animator уже есть, контроллер назначать через `animator-controller-assign`.

`animator-controller-assign assign|detach --path PATH [--controller ASSET_PATH]`

`animator-motions --path PATH|--controller ASSET_PATH|--query ТЕКСТ`

`animator-graph --path PATH|--controller ASSET_PATH`

`animator-state create|modify|delete --controller PATH --layer ИМЯ|ИНДЕКС --state ИМЯ [--state-machine PATH] [--motion PATH_КЛИПА_ИЛИ_BLENDTREE] [--set ПАРАМЕТР=JSON]`

`animator-state-motion assign|detach --controller PATH --layer ИМЯ --state ИМЯ [--state-machine PATH] [--motion PATH_КЛИПА_ИЛИ_BLENDTREE]`

`animator-transition create|modify|delete --controller PATH --layer ИМЯ --from STATE|AnyState --to STATE|Exit [--state-machine PATH] [--transition-index N] [--set ПАРАМЕТР=JSON] [--conditions JSON]`

`animator-parameter create|modify|delete --controller PATH --name ИМЯ [--type Float|Int|Bool|Trigger] [--value ЗНАЧЕНИЕ] [--new-name ИМЯ]`

Без `--value` создаётся `0` или `false`.

`animator-layer create|modify|delete --controller PATH --layer ИМЯ [--set ПАРАМЕТР=JSON]`

`animator-state-machine create|modify|delete --controller PATH --layer ИМЯ --name ИМЯ [--parent PATH] [--new-name ИМЯ]`

`animator-blend-tree create|modify|delete --controller PATH --layer ИМЯ --state ИМЯ --name ИМЯ [--state-machine PATH] [--settings JSON]`

`animator-control --path PATH [--state STATE] [--layer ИМЯ_ИЛИ_INDEX] [--set PARAMETER=JSON]` — Play Mode.

`animator-runtime-state --path PATH` — State, переходы, время и Parameters в Play Mode.

Несколько свойств можно передать одним `--set свойство1=значение свойство2=значение`.

Допустимые `--set`: State — `name`, `speed`, `speedParameter`, `timeParameter`, `mirror`, `mirrorParameter`, `cycleOffset`, `cycleOffsetParameter`, `tag`, `writeDefaultValues`, `iKOnFeet`; Transition — `hasExitTime`, `exitTime`, `duration`, `offset`, `hasFixedDuration`, `interruptionSource`, `orderedInterruption`, `canTransitionToSelf`, `mute`, `solo`; Layer — `name`, `defaultWeight`, `avatarMask`, `blendingMode`, `iKPass`, `syncedLayerIndex`, `syncedLayerAffectsTiming`.

`--conditions`: массив `{ "mode", "parameter", "threshold" }`. `--settings`: объект с `blendType`, `blendParameter`, `blendParameterY`, `useAutomaticThresholds`, `minThreshold`, `maxThreshold`, `children`; дочерний элемент содержит `motion`, `threshold`, `x`, `y`, `timeScale`, `cycleOffset`, `directBlendParameter`, `mirror`.

## Timeline

`timeline-info --path ОБЪЕКТ`, `timeline-info --timeline ASSET.playable` или `timeline-info --query ТЕКСТ`

Частота и длительность Timeline: `asset-modify --path ASSET.playable --set asset:m_EditorSettings.m_Framerate=24 asset:m_DurationMode=FixedLength asset:m_FixedDuration=8`.

`timeline-track create|modify|delete --path ОБЪЕКТ|--timeline ASSET --track ИМЯ [--type Animation|Audio|Activation|Signal|Control|Group|Cinemachine] [--parent GROUP] [--set muted=true binding=/Сцена/Объект]`

`timeline-clip create|modify|delete --path ОБЪЕКТ|--timeline ASSET --track ИМЯ [--clip ИМЯ|ИНДЕКС] [--asset PATH.anim|АУДИО] [--set start=1.6 duration=2 easeIn=0.2 ПОЛЕ=ЗНАЧЕНИЕ]`

`timeline-marker create|modify|delete --path ОБЪЕКТ|--timeline ASSET [--track ИМЯ] [--marker ИНДЕКС] [--type SignalEmitter] [--set time=1.6 asset=PATH.signal]`

PlayableDirector добавляется через `component-add --component PlayableDirector --set playableAsset=ASSET.playable`. `binding` требует `--path` директора; `binding=null` отвязывает.

Имена и значения берутся из `timeline-info`; повторяющиеся имена различаются суффиксом `[i]`. Кроме `start`, `duration`, `clipIn`, `easeIn`, `easeOut`, `speed`, `name`, `preExtrapolation`, `postExtrapolation` клип принимает поля своего PlayableAsset, в том числе ExposedReference по пути объекта: `--set VirtualCamera=/Сцена/Камера`. Маркер без `--track` попадает на дорожку Markers. Воспроизведение и перемотка — `component-action --component PlayableDirector`, живое время — `object-info --runtime --component PlayableDirector`.
