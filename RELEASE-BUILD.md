# SubReel Launcher — Release Builder

## Быстрый запуск

1. Установи .NET 10 SDK. Для создания EXE-установщика дополнительно установи Inno Setup 6.
2. Помести `BuildTool.ps1`, `Build-Release.ps1` и `SubReel-Build-Tool.bat` в корень проекта рядом с `.sln`.
3. Запусти `SubReel-Build-Tool.bat`.
4. Сначала нажми **Проверить Build + Test**, затем **Собрать релиз**.

## Что делает сборщик

- Собирает всю solution в `Release`.
- Запускает тесты (`dotnet test --no-build`).
- Публикует WPF-лаунчер для `win-x64` с выбранными параметрами Self-contained и Single-file.
- Публикует `SubReel.Updater` отдельно в `Updater/` как автономное приложение `win-x64`. Обновлятор не является `ProjectReference` лаунчера.
- Проверяет наличие EXE лаунчера, EXE обновлятора, runtimeconfig и deps-файлов.
- Добавляет `README-RELEASE.txt` и `release-info.json`.
- Создаёт ZIP, SHA-256 (`.sha256`) и отдельный JSON-манифест (`.release.json`).
- Если установлен Inno Setup 6 и включён флажок установщика, собирает `installer/SubReel-Setup-win-x64.exe`.
- Публикует результат через временную папку и сохраняет предыдущую версию, пока новая не прошла проверки и упаковку.

## Результаты по умолчанию

```text
publish/
├── SubReel-win-x64/
│   ├── SubReel.Launcher.exe
│   ├── зависимости и runtime лаунчера
│   ├── README-RELEASE.txt
│   ├── release-info.json
│   └── Updater/
│       ├── SubReel.Updater.exe
│       └── остальные файлы автономной публикации
├── SubReel-win-x64.zip
├── SubReel-win-x64.zip.sha256
├── SubReel-win-x64.release.json
└── installer/
    └── SubReel-Setup-win-x64.exe  (если установлен Inno Setup 6)
```

`Self-contained` включён по умолчанию: на целевом ПК не требуется отдельно устанавливать .NET Desktop Runtime. `Single-file` выключен по умолчанию: обычная папочная публикация проще для диагностики и обновления. Обновлятор всегда публикуется автономно и отдельно.

## Установщик

Используется Inno Setup 6 (`installer/SubReel.iss`). Установка выполняется в `%LocalAppData%\Programs\SubReel` без запроса прав администратора. Ярлык в меню «Пуск» создаётся автоматически; ярлык на рабочем столе — по выбору. После установки пользователь может сразу запустить лаунчер.

Если Inno Setup не установлен, сборка папки и ZIP всё равно завершается; в журнале появится предупреждение, а EXE-установщик не будет создан.

## Важно

Распространяй целиком ZIP или EXE-установщик. Не копируй один `SubReel.Launcher.exe`: рядом должны оставаться DLL, runtime-файлы и папка `Updater`. Пользовательские данные Minecraft, профили, моды и настройки должны храниться вне каталога установки (например, в `%LocalAppData%\SubReel`).
