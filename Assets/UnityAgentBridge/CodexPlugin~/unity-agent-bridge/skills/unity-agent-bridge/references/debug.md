# Диагностика и Play Mode

```text
health
version
compile
logs [--query "ошибка загрузки"] [--level Error|Assert|Warning|Log|Exception] [--since play|compile] [--since-minutes N] [--limit N] [--stacktrace]
logs --clear
status
play start|stop|pause|resume|status
play step [--frames N]
profiler-hierarchy [--frame N|worst|all] [--query GC.Alloc] [--path PlayerLoop/…] [--sort total|self|gc|calls] [--thread main|render|ИМЯ] [--limit 10]
menu [--query "build"]
menu --path "Tools/Build"
game-resolutions
game-resolution --width <ширина> --height <высота>
```

`logs` сворачивает одинаковые записи и возвращает `count`; stack trace включается только через `--stacktrace`. `--query` ищет подстроку; фраза без совпадений даёт `similar` — ближайшие по смыслу. `--clear` очищает и окно Console.

`status` и все варианты `play` отвечают одинаково: `state`, игровое `time`, `pausedBy` у паузы (`game_actions`, `play pause|step`, `error` — Error Pause, `editor` — кнопка Pause или `Debug.Break`), `busy` (компиляция, пункт меню, запекание, диалог, Safe Mode) и счётчики `errors`/`compileErrors`, если они не нулевые; `lastError` — последняя ошибка и место её появления. `errors` обнуляется как Console: `logs --clear`, перекомпиляция и Clear on Play. `play start|stop` вместо счётчика возвращают ошибки, появившиеся за запуск или остановку.

Запись делает MCP `profiler` с `frames` или `game_actions` с `profile: true` (только время пакета); сводка содержит худшие кадры `PlayerLoop` с тремя самыми тяжёлыми маркерами, `frameMs` — весь кадр вместе с редактором (он задаёт FPS), `editorMs` — доля `EditorLoop`. `profiler-hierarchy` читает последнюю запись как вкладка Hierarchy: `--query` работает как поле поиска, и путь совпадения показывает вызывающих, `--path` раскрывает узел, `--frame all` усредняет запись, `objects` — объекты сцены сэмпла.

`menu` без аргументов показывает пункты меню проекта. `menu --path` нажимает пункт, ждёт компиляцию, импорт и запекание и возвращает только новые ошибки. Если ожидание длиннее лимита вызова, ответ содержит `busy`; дальше смотреть `status`.
