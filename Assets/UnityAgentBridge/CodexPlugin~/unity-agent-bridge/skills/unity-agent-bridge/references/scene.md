# Сцена, префабы и Assets

## Объекты и компоненты

```text
tree [--path|--root <ветка>|<Assets/...prefab>] [--depth <уровни>]
object-children --path <объект>
object-find [--name "камера игрока"] [--component <тип>] [--ref <ассет>] [--near x,y,z [--radius 1]] [--path <ветка>] [--limit 10] [--offset 0]
object-info --path <объект> [--component <тип> [--component-index 0] [--runtime]] [--property <поле>[,<поле>]]
component-suggest --path <объект> --component "Rigidbody" --query "твёрдое тело"
component-add --path <объект> --component <тип> [--set <property> <JSON>]
component-modify --path <объект|ссылка-компонента> [--component <тип>] [--component-index 0] (--set <property> <JSON> | --append <UnityEvent> <вызов JSON> | --remove <UnityEvent> <индекс>)
component-remove --path <объект|ссылка-компонента> [--component <тип>] [--component-index 0]
component-action --path <объект|ссылка-компонента> [--component <тип>] [--component-index 0] --action <id из object-info> [--set time=<сек>]
object-picker --path <объект|ссылка-компонента> [--component <тип>] [--component-index 0] --property <поле>
object-templates [--query <запрос>]
object-create --parent <сцена-или-объект> [--name <имя>] [--template <путь из object-templates>]
object-delete --path <объект>
object-duplicate --path <объект> [--name <имя-копии>]
object-move --path <объект> --parent <новый-родитель> [--index <порядок>]
object-rename --path <объект> --name <новое-имя>
object-active --path <объект> --active true|false
object-tag --path <объект-или-prefаб> --tag <имя-или-Untagged>
object-layer --path <объект-или-prefab> --layer <имя|индекс> [--children]
object-static --path <объект-или-prefab> --static true|false|"Contribute GI, Occluder Static" [--children]
scene-save
refresh
```

`object-info` без `--component` возвращает список компонентов и значения Transform, с `--component` без `--property` — все поля выбранного компонента. Адрес `<объект>#<тип>[<индекс>]` можно передать целиком как `--path`. В `--set` строку можно передать без JSON-кавычек; несколько полей — одним `--set поле1=значение поле2=значение`; составное значение — как `--set поле x=1 y=2`; Color — как `--set поле=0.9,0.5,0.2,1`; кривые, градиенты и модули Particle System — в том виде, в каком их показывает `object-info` (`0:0 0.5:1 1:0`, `0:#FF8000 1:#0000FF / 0:1 1:0`, `0.2..0.8`, `true`). Префикс `m_` у поля можно опустить. UnityEvent задаётся как `--set onClick target=/Scene/Button method=Run`, очищается через `--set onClick=[]`, новый слушатель добавляется через `--append onClick target=/Scene/Button method=Run`, удаляется через `--remove onClick 0`; дополнительно доступны `mode=Void|Object|Int|Float|String|Bool`, `state=Off|EditorAndRuntime|RuntimeOnly` и `argument` (режим выводится из значения, ссылке на объект нужен `mode=Object`). Остальные списки задаются целиком (`--set поле=[a, b]`) или через `<поле>.Array.size` и `<поле>.Array.data[<индекс>]`. Для Object Reference и ExposedReference использовать `references[].value` из `object-info` или путь объекта; `object-picker` работает только с Object Reference.

Углы Transform задаются через `localEulerAngles|eulerAngles` либо их `.x/.y/.z`.

`--ref` отбирает то, что использует ассет; поиск показывается в окне Search. `--near` отбирает объекты, чьи Renderer или Collider ближе `--radius` к точке, по расстоянию, с `size`. Новое имя слоя занимает свободный пользовательский слот. Ответ с `total` значит, что есть следующие страницы.

Несколько `--path` у `object-info` и `component-modify` — как выделение нескольких объектов: правка идёт во все, чтение возвращает `byPath`.

Path пишется как в Hierarchy: `/Сцена/Объект/Дочерний`, имя сцены можно опустить. Индекс `[i]` нужен только при одинаковых именах соседей.
`--runtime` читает живые C#-свойства компонента: `--property particleCount,isPlaying` или вложенное `main.duration` (названные поля — в том числе private и static); у `Transform` без `--property` — фактическая геометрия.
`component-action` у PlayableDirector (`play|pause|stop|evaluate`) в Edit Mode двигает плейхед окна Timeline. У скрипта действия — пункты его `[ContextMenu]`, как в меню ⋮ компонента, в Play Mode тоже.

## Префабы, сцены и Assets

```text
prefab-save [--path <объект>] [--prefab <asset-path>]
prefab-apply --path <экземпляр> [--component <тип> [--component-index 0] [--property <поле>]]
prefab-instantiate --prefab <имя-или-asset-path GameObject> --parent <сцена-или-объект> [--name <имя>] [--set <поле Transform>=<значение> ...]
prefab-revert --path <экземпляр> [--component <тип> [--component-index 0] [--property <поле>]]
prefab-open --prefab <имя-или-asset-path>
prefab-close
asset-find [--name "ткань стола"] [--type Material|Prefab|Scene|...] [--ref <ассет>] [--path <папка>] [--limit 10] [--offset 0]
scene-open --scene <имя-или-asset-path>
creation-templates [--query "C# script"] [--path <.shadergraph|.shadersubgraph>]
asset-create --template <точное-имя-шаблона> --path <Assets/...> [--source <ассет>]
asset-info --path <Assets/...> [--property <поле>|--section model]
shader-info [--path <материал-или-шейдер>] [--property <имя>[,<имя>]]
material-modify --path <материал> --set <shader-property|подпись>=<значение>|Shader=<шейдер> [...]
asset-action --path <Assets/...> --action <id из asset-info> [--set поле=значение ...]
asset-modify --path <Assets/...> --set property=JSON [--confirm] | --code <текст> | --file <файл>
asset-reimport --path <Assets/...> [--as-sprite]
asset-move --path <Assets/...> --destination <Assets/...>
asset-duplicate --path <Assets/...> [--destination <Assets/...>]
asset-delete --path <Assets/...>
asset-object-picker --path <Assets/...> --property <поле>
```

`prefab-open` и `scene-open` сохраняют текущее содержимое перед переходом. После `prefab-open` команды сцены работают с префабом. Относительные пути префабов считаются от стандартной папки; явный путь может указывать на любой префаб внутри `Assets`. В Prefab Mode `prefab-save` без `--path` сохраняет корень. При частичном `prefab-apply` внешние ссылки сцены пропускаются и остаются override экземпляра.

`shader-info` без `--path` читает глобальные значения (`Shader.SetGlobal*`) по именам из `--property`.
Пути из `asset-info` вида `asset:<property>` и `importer:<property>` передавать без изменений; элемент массива читается как `<поле>.Array.data[i]`. `asset-create` с шаблоном `C# Script` возвращает `componentType` после регистрации MonoBehaviour в Unity.
Шаблоны `Create/...` — пункты меню Assets > Create; `--source` выделяет ассет, из которого пункт создаёт новый (шрифт для TMP Font Asset, модель, текстура, шейдер для Material). Audio Mixer, Sprite Atlas, TMP Font Asset, Volume Profile, URP Renderer и Shader Graph читаются и меняются по подписям своего окна или Inspector; кнопки — действия `asset-action`.

Shader Graph: узел адресуется как `<id>` или `<id> Имя` из `asset-info`, вход — `<узел>.<вход>=значение|<узел>.<выход>|<свойство Blackboard>|None`, блоки стека — по имени (`Base Color=...`). `add-node` принимает `node=` из `creation-templates --path <граф>`, `from=<узел>.<выход>`, `connect=<узел>.<вход>`, остальные `--set` (входы и настройки с подписями окна: `X`, `From`) заполняют новый узел до подключения; служебные `node|from|connect|group|x|y` пишутся строчными (Custom Function: `Type`, `Name`, `Body`, `Source`, `Inputs="In: Float"`, `Outputs="Out: Vector3"`). Картинка материала или узлов — MCP `shader_preview`.

Граф кодом: `asset-info --property Code` (или `Code/<группа>`) — строка на узел `id = Узел(Настройка: значение, Вход: значение|id[.Выход]|_Свойство);`, группы `group "Имя" { … }`, блоки стека `Base Color = …;`. `asset-modify --code|--file` принимает этот текст и HLSL (`float3 n = lerp(a, _Tint.rgb, saturate(id.Foam * 2));`, операторы, `float3(...)`, `tex2D`, тело Custom Function в `{ }` после вызова) и меняет только разницу: строка существующего id задаёт его целиком (неуказанные входы отключаются), `id.Вход = …` — один вход, новое имя — новые узлы (их id в `names`); узлы записанного блока `group`, которых текст не упоминает, удаляются. Действие `convert-to-nodes --set node=<id>` раскладывает Custom Function в узлы на месте.

Rig, Avatar и клипы FBX читаются и меняются через `asset-info`/`asset-modify` по путям `importer:`, которые возвращает `asset-info`.
