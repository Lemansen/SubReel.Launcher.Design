# SubReel Launcher — архитектура (v15, этапы 1–10)

## Слои и зависимости

```
Design (WPF: Views, ViewModels, Rendering, Services для WPF)
   │        └──────────────┐
   ▼                       ▼
Infrastructure ───► Application ───► Domain
```

| Проект | Платформа | Назначение |
|---|---|---|
| `SubReel.Launcher.Domain` | net10.0 | Модели без ввода-вывода и без WPF: версии, каталог, аккаунты, оформление, настройки, новости |
| `SubReel.Launcher.Application` | net10.0 | Сервисы и контракты: `VersionStore`, `AccountService`, `NewsService`, интерфейсы хранилищ и источников |
| `SubReel.Launcher.Minecraft` | net10.0 | Игровое ядро (фаза 5): `HttpDownloadManager` (лимит параллелизма, SHA-1, докачка, повторы), `JsonInstanceRepository` (`instances/<id>/instance.json` + папка `minecraft`); регистрация `AddSubReelMinecraft`. Ссылается на Application, Domain и Infrastructure (`AppPaths`, `JsonFileStore`) |
| `SubReel.Launcher.Infrastructure` | net10.0 | Файлы, сеть, журнал: `JsonFileStore`, `SettingsService`, `DiskImageCache`, источники новостей, `MojangVersionCatalogService`, `InstalledVersionService`, `FileLoggerProvider` |
| `SubReel.Launcher.Design` | net10.0-windows | WPF-приложение (корневое пространство имён сохранено, XAML не менялся) |
| `SubReel.Launcher.Tests` | net10.0 | xUnit |

## Хранение данных
- Пути — только через `AppPaths`. Все данные лежат в одном корне (по умолчанию `%LocalAppData%\SubReel`, см. раздел «Папка данных» ниже).
- Запись: `.tmp` → flush на диск → `File.Replace` с копией предыдущей версии в `.bak`.
- Повреждённый файл не удаляется: переименовывается в `.corrupt-<время>`, данные берутся из `.bak`.
- Файл настроек получил поле `SchemaVersion` (старые файлы читаются как версия 0 и мигрируются; исходник остаётся в `.bak`).
- Настройки более новой версии лаунчера и недоступный для чтения файл не перезаписываются.

## Журнал
`<корень>\launcher\logs\launcher-ГГГГММДД.log`, хранится 14 дней. Туда попадают необработанные исключения и сбои загрузки.

## Этап 2: Host и DI, сервисы Application

- **Composition Root** — `App.OnStartup`: `Host.CreateApplicationBuilder`, регистрация по слоям (`AddSubReelInfrastructure` → `AddSubReelApplication` → `AddSubReelPresentation`). Окно и все ViewModel-и создаёт контейнер; при выходе `IHost` останавливается и освобождает сервисы (в том числе `MainViewModel`, отменяющую начальную загрузку, и файловый журнал).
- **Application**: `VersionStore` (выбор версии, список установленных) и `AccountService` (правила ников, добавление, переключение, удаление, переименование) перенесены из WPF-проекта; `IAvatarStorage` теперь контракт Application, реализация — в Infrastructure.
- **Infrastructure**: `HttpClient` каталога Mojang выдаёт `IHttpClientFactory` (`PooledConnectionLifetime` 5 минут); пути аватарок берутся из `AppPaths`.
- **Presentation**: `IOverlayFactory` вместо `new` оверлеев в `MainViewModel`; `AsyncRelayCommand` не роняет процесс, если задан `onError`; `ToastViewModel.Show` больше не `async void`.

## Этап 3: новости и кэш картинок

- **Application**: `INewsService`, `INewsSource`, `NewsService` (слияние источников, свежесть кэша 20 минут, откат на сохранённые новости), контракты `INewsCache` и `IImageCache`, `NewsOptions`.
- **Infrastructure**: `MojangNewsSource`, `SubReelNewsSource` (клиенты из `IHttpClientFactory`), `JsonNewsCache` (формат `news.json` прежний, атомарная запись и `.bak`), `DiskImageCache` (те же имена файлов: уже скачанное сохраняется).
- **Исправлено в кэше картинок**: словарь загрузок больше не растёт (запись удаляется по завершении), единый лимит 6 одновременных загрузок вместо двух несвязанных, повторный запрос проверяет свежесть файла на диске (скины обновляются раз в 12 часов и внутри сеанса), сбои логируются.
- **Presentation**: `ImageService` декодирует байты (`ImageDecoder`), битый файл удаляет из кэша; `FireAndForget` для фоновых операций ViewModel; `async void OpenDetail` заменён на обычный метод.
- **Конфигурация**: адрес ленты SubReel задаётся разделом `News:SubReelEndpoint` (appsettings.json рядом с exe или переменная окружения `News__SubReelEndpoint`) вместо константы `NewsConfig`. Пока не задан — источник «не подключён», как и раньше.
- **Осознанное исключение**: `RenderingServices` — единственные глобальные ссылки на сервисы (картинки и журнал для статического `CosmeticsRenderer`, который вызывается из XAML).

## Этап 4: аудит и доводка

- **Проверено без изменений**: XAML-привязки (232), ключи ресурсов, обработчики, конвертеры; code-behind окна (только анимации и ввод окна); подписки на события (утечек нет, все издатели — singleton-ы). Отчёт — [docs/stage4-audit.md](docs/stage4-audit.md).
- **Исправлено**: отмена загрузки новостей (`NewsViewModel` — токен на запрос и `IDisposable`), токен жизненного цикла в предзагрузке картинок, `DiskImageCache.Dispose` не освобождает объекты, которые ещё используют незавершённые загрузки.
- **Конфигурация**: хост берёт `appsettings.json` из папки с exe (`ContentRootPath = AppContext.BaseDirectory`), файл входит в проект; сообщения консольного жизненного цикла отключены.
- **Тесты**: сквозные сценарии сохранения данных (`PersistenceIntegrationTests`) и сборка контейнера Infrastructure + Application с `ValidateOnBuild` (`CompositionTests`).

## Дальнейшие планы

Порядок работ, границы будущих проектов и критерии готовности — в [ROADMAP.md](ROADMAP.md).

## Этап 5: Download Manager и экземпляры
- **Domain**: `Instance`, `InstanceRules` (проверка названия и памяти, безопасное имя папки без зарезервированных имён Windows).
- **Application**: контракты `IDownloadManager`, `IInstanceRepository`, модели загрузок, `DownloadOptions` (раздел `Downloads`), сценарии `InstanceService`.
- **Minecraft**: реализации. Ожидаемые сбои загрузки возвращаются результатом `Failed`, отмена — исключением, недокачанный `.part` остаётся для докачки. Файл от более новой версии лаунчера не читается и не перезаписывается; папка удаляется только если в ней есть `instance.json`.
- Не сделано: экран экземпляров и подключение кнопки «Создать сборку» (`ModpackOverlayViewModel` по-прежнему демо), `DiskImageCache` пока не использует общий механизм загрузок.

## Этап 6: установка версий, библиотеки и ассеты
- **Domain/Games**: модели `version.json` (`GameVersionMetadata`, `Library`, `Rule`), `RuleEvaluator` (правила по ОС, архитектуре, возможностям; побеждает последнее подошедшее правило), `VersionResolver` (склейка `inheritsFrom`: библиотеки потомка первыми, одинаковые по ключу без версии заменяются, аргументы родителя раньше), `MavenCoordinate`.
- **Application/Games**: `IVersionMetadataProvider`, `IGameInstaller` (установка и проверка), `INativesExtractor`.
- **Minecraft**: `VersionMetadataParser`, `VersionManifestSource` (список версий на диске, обновление раз в 10 минут, устаревшая копия при отсутствии сети), `VersionMetadataProvider`, `GameFilePlanner` (один список файлов для установки и проверки), `GameInstaller`, `NativesExtractor`, `GameFileLayout`.
- **Общий кэш** `<корень>\shared\game`: `versions`, `libraries`, `assets/indexes`, `assets/objects/<2 символа>/<sha1>`, `assets/log_configs`. Пути из `version.json` не доверяются: выход за корень кэша — ошибка; в архивах нативов запрещён path traversal.
- **Не сделано**: `map_to_resources` (копирование ресурсов в папку игры — при запуске), библиотеки с пустым адресом (создаёт установщик загрузчика, фаза 9), подключение к интерфейсу.

## Этап 7: Java Manager
- **Domain/Java**: `JavaInstallation`, `JavaRequirement` (из `javaVersion` версии; без поля — Java 8 и рантайм `jre-legacy`), `JavaSelector`, `JavaVersionParser`.
- **Правило подбора**: подходит только та же мажорная версия, что требует игра (старые версии ломаются на новой Java). Приоритет: путь пользователя → рантайм лаунчера → рантайм официального лаунчера → остальные; затем 64-бит, затем более свежая сборка. 32-битные Java исключаются на 64-битной системе.
- **Application/Java**: `IJavaManager` (`GetInstallationsAsync`, `ProbeAsync`, `SelectAsync`, `EnsureAsync`), `JavaOptions` (раздел `Java`, адрес списка рантаймов Mojang).
- **Minecraft/Java**: `JavaVersionReader` (файл `release`, иначе `java -version` с тайм-аутом 10 с), источники папок `IJavaHomeSource` (JAVA_HOME и PATH, типовые папки, официальный лаунчер, реестр Windows), `JavaRuntimeIndex`, `ManagedJavaInstaller`, `JavaManager`.
- **Рантаймы лаунчера**: `<корень>\shared\runtimes\<компонент>`. Установка идёт через Download Manager (SHA-1, докачка); файл `.subreel-runtime.json` записывается последним, пока его нет, папка в поиске не участвует. Пути из описания рантайма проверяются на выход за папку.
- **Не сделано**: экран «Настройки» и глобальные значения Java и памяти (нужны новые поля `LauncherSettings` с `SchemaVersion`), автообновление рантайма до свежей сборки, проверка целостности установленного рантайма.

## Этап 8: Authentication
- **Application/Authentication**: `ISession` (ник, UUID, токен, `userType`; токен только в памяти), `IAccountProvider`, `ITokenStore`, `AccountInfo`, `SessionManager` (одна точка получения сессии: офлайн сразу, Microsoft с тихим обновлением под замком аккаунта; удаление аккаунта стирает токены), `AuthenticationException` с причиной `AuthFailureReason`. `LauncherAccount` получил необязательные `Provider` и `ProfileId` (старые настройки читаются как офлайн).
- **Authentication** (`SubReel.Launcher.Authentication`): вход по коду устройства → Xbox Live → XSTS → Minecraft Services (владение + профиль), `MicrosoftAccountProvider`, понятные русские сообщения для кодов XSTS. Client Secret не используется; `Authentication:ClientId` берётся из `appsettings.json`.
- **Хранение токенов (8.3)**: `ProtectedFileTokenStore` — по файлу `<корень>\launcher\auth\<uuid>.bin` на аккаунт, содержимое шифруется `ISecretProtector` (на Windows `DpapiSecretProtector`: DPAPI, область текущего пользователя, с «солью» лаунчера). Запись через временный файл и подмену. Повреждённый, обрезанный или «чужой» файл читается как «нет данных» (нужен повторный вход). UUID в имени файла проверяется строго (32 hex), чтобы исключить выход из папки. Сохраняются только refresh-токен Microsoft и токен Minecraft; токены Xbox и XSTS не хранятся. В настройках и журнале токенов нет.
- **Интерфейс (8.4)**: кнопка «Войти через Microsoft» в меню профиля открывает `MicrosoftSignInOverlayViewModel`: код и адрес для ввода, «Открыть страницу», «Скопировать код», этапы входа, ошибка с «Повторить». Закрытие окна отменяет вход (`NavigationService` освобождает `IDisposable`-оверлей). В «Профиле» у Microsoft-аккаунта ник не редактируется (его задаёт Minecraft), есть «Выйти из аккаунта». Голова скина берётся с minotar по нику.
- **Не сделано**: запуск игры с сессией (фаза 9, `SessionManager.GetSessionAsync`), SubReel ID, регистрация приложения в Microsoft Entra и одобрение Client ID в Mojang (без него Minecraft Services отвечают «Invalid app registration»).

## Работа без сети (после этапа 8)
- **Копия каталога**: `MojangVersionCatalogService` сохраняет список версий и патчноуты в `<корень>\launcher\cache\version_manifest.json` и `patch_notes.json` (запись через временный файл). Без сети каталог собирается из копии: даты выхода, годы и ссылки на картинки на месте. Такой каталог помечен `VersionCatalog.IsStale`; встроенный список (`IsOffline`) используется, только если копии нет или она повреждена. Если не загрузились только патчноуты, берутся сохранённые.
- **Автообновление**: `INetworkMonitor` (`NetworkMonitor`, события ОС с паузой 2 с) сообщает о появлении сети. `MainViewModel` в потоке интерфейса вызывает `VersionStore.RefreshCatalogAsync` и догружает фон и картинки карточек; `VersionsViewModel` перестраивает карточки по событию `CatalogRefreshed`. Кроме события работает проверка раз в 30 с (нужна, если сеть есть, а серверы Mojang были недоступны). Выбранная версия не меняется; исключение — первый запуск без сети, тогда выбирается настоящий последний релиз.
- **Картинки**: уже скачанные берутся с диска (`DiskImageCache`); предзагрузка при запуске с сетью кладёт на диск картинки всех версий, поэтому следующий запуск без сети показывает их.

## Этап 9: запуск игры
- **Domain/Games**: `LaunchCommandBuilder` (чистая логика) собирает аргументы по `ResolvedVersion`: память → JVM-аргументы версии с правилами по ОС и возможностям (`has_custom_resolution`, `is_demo_user`) → логирование → аргументы пользователя → `mainClass` → аргументы игры. У версий до 1.13 (нет `arguments.jvm`) добавляются стандартные JVM-аргументы; если у версии нет `-cp` или `java.library.path`, они дописываются. Старая строка `minecraftArguments` делится по пробелам до подстановки, поэтому пути с пробелами остаются одним аргументом. Подстановка `${…}` не просматривает значения повторно (ник вида `${…}` ничего не раскрывает); неизвестные переменные остаются как есть и попадают в `UnresolvedPlaceholders`. `CommandLineTokenizer` делит поле «Аргументы JVM» экземпляра (кавычки поддерживаются); аргументы пользователя подставляются без обработки `${…}`. `LaunchCommand.ToDisplayString` скрывает токен (значения короче 8 символов, например офлайн «0», не скрываются).
- **Application**: `IGameLauncher` (`LaunchAsync`, `FindRunning`), `LaunchRequest` (версия и папка игры задаются явно; `ForInstance` собирает запрос из экземпляра), `LaunchProgress` (общая шкала 0–1 и этап), `LaunchFailure` (причины отказа с готовым текстом), `IGameProcess`. `IGameSessionSource` — узкий контракт сессии; его реализует `SessionManager`, поэтому ядро не зависит от входа Microsoft.
- **Minecraft/Launching**: `GameLauncher`, `ClasspathBuilder` (библиотеки по правилам + клиентский jar; архивы нативов старого формата не входят), `LegacyAssetsPreparer` (копии для `virtual` и `map_to_resources`, имя ресурса не может выйти за целевую папку), `GameProcess` и `IGameProcessStarter` (подмена в тестах). Шкала прогресса: сессия 0–5 %, проверка 5–10 %, установка 10–75 %, Java 75–90 %, нативы 90–95 %, старт 95–100 %.
- **Порядок и правила**: сначала сессия (нужен вход — узнаём до загрузок); затем `VerifyAsync`, и только при проблемах `InstallAsync` (запуск установленной версии не обращается к сети). Папка нативов `<папка игры>/natives` принадлежит лаунчеру и очищается перед распаковкой. Одну папку игры нельзя запустить дважды (`AlreadyRunning`), пока идёт подготовка или игра работает. Вывод игры пишется в `<папка игры>/logs/subreel-game-output.log` (заголовок: версия, Java, команда без токена). Код 0 или остановка пользователем — `Exited`, иначе `Crashed`. События статуса приходят не из потока интерфейса.
- **Регистрация**: `AddSubReelGameLauncher()` вызывается после `AddSubReelMinecraft()` и `AddSubReelAuthentication()` (нужен `IGameSessionSource`); `GameFileLayout` получил `AssetsRoot`, `LibrariesDirectory`, `VirtualAssetsDirectory`.
- **9.4, кнопка «Играть»**: `GamePlayService` (Application, `PlayAsync(PlayRequest)`) для выбранной версии находит сборку «Minecraft <версия>» или создаёт её через `InstanceService`, запускает игру в папке сборки (`LaunchRequest.ForInstance`) и после успеха отмечает `LastPlayedUtc`. Версия с загрузчиком отклоняется до создания сборки. Если имя занято сборкой другой версии, берётся «… (2)», «… (3)». Файлы версии общие (`<корень>\shared\game`), saves/mods/options.txt — у каждой сборки свои. Регистрация — внутри `AddSubReelGameLauncher()`.
- **`LaunchViewModel`** (Design): состояния Idle / Loading / Running (`Tag` кнопки: `loading`, `running`). Отчёты `Progress<LaunchProgress>` превращаются в процент и подпись этапа; статус процесса приходит из чужого потока и переносится в поток интерфейса через `SynchronizationContext`. Во время игры кнопка открывает окно «Остановить игру» (без случайной остановки одним кликом). Падение игры (`Crashed`) показывает код, последние строки вывода и путь к полному журналу. Ожидаемые отказы (`LaunchFailure`) показываются окном с текстом причины и подсказкой; `AlreadyRunning` — тостом. Закрытие лаунчера отменяет подготовку, но не трогает уже запущенную игру.
- **Не сделано**: запуск Forge и NeoForge (фаза 10.4: `UnsupportedLoader`; Fabric и Quilt — этап 10); экран экземпляров (переименование, настройки памяти и Java в интерфейсе); `clientid` и `xuid` передаются пустыми — проверить при первом запуске на настоящей сессии; Quick Play и демо-режим не используются; отдельная настройка «закрывать лаунчер после запуска» не подключена.

## Этап 10: загрузчики Fabric и Quilt
- **Domain/Versions**: `LoaderInfo.CanInstall` (Fabric, Quilt), `ProfileId` и `TryParseProfileId` (имя профиля `fabric-loader-<загрузчик>-<игра>`, бета-версии вида `0.27.0-beta.1` разбираются), `LoaderBuild`.
- **Application/Loaders**: `ILoaderCatalog` (список сборок для версии игры), `ILoaderInstaller` (`EnsureAsync`: профиль на месте, «последняя» = первая стабильная), `LoaderCatalogException`. `PlayRequest` получил `LoaderVersion`, `LaunchStage.PreparingLoader`, `LaunchFailure.LoaderNotReady`, `InstanceService.SetLoaderVersionAsync`.
- **Minecraft/Loaders**: `LoaderEndpoints` (адреса, подменяются в тестах), `LoaderMetaParser`, `LoaderCatalog` (ответ мета-сервера лежит в `shared\game\loaders\<загрузчик>\<версия>.json`: свежий — 30 минут — используется как есть, без сети берётся устаревший, битый удаляется), `LoaderInstaller` (профиль скачивается во временный файл, проверяется — годный JSON, `inheritsFrom` совпадает с версией игры, есть `mainClass` — и только потом ложится в `versions\<профиль>\<профиль>.json` общего кэша). Версии из сети и из файла сборки проходят `IsSafeVersionText` до попадания в адрес и путь.
- **Запуск**: `GamePlayService` для Fabric/Quilt сначала готовит профиль, затем находит или создаёт сборку «Minecraft <версия> Fabric» (одна на пару версия+загрузчик; другая версия загрузчика обновляет запись сборки, моды и миры остаются) и запускает `LaunchRequest` с `VersionId` = имя профиля. `GameLauncher` пропускает Fabric/Quilt как обычную версию; Forge, NeoForge и особые сборки по-прежнему `UnsupportedLoader`.
- **Выбор в интерфейсе**: `VersionStore.SelectedLoader/SelectedLoaderVersion`, `SelectLoader(LoaderBuild)`. Сборка, которой нет в папке игры, хранится в настройках именем профиля в `SelectedInstallId` (формат настроек не менялся) и восстанавливается после перезапуска. Выпадающий список (`VersionDropdownViewModel`) при открытии подгружает до 3 новых сборок каждого загрузчика для выбранной версии Minecraft; нет сети и нет копии — раздел скрыт. Тексты о «нельзя запустить, не установлена» заменены: файлы скачиваются при первом запуске.
- **Не сделано**: Forge и NeoForge (10.4); выбор сборок старше трёх новых; удаление неиспользуемых профилей; проверка на живом запуске (код не компилировался в среде автора).

## Папка данных (выбор корня и три зоны)
- **Корень** по умолчанию одинаков для всех: `%LocalAppData%\SubReel`. Пользователь может выбрать другую папку (например, `D:\SubReel`) в «Настройках» → «Папка данных»; кнопка «По умолчанию» возвращает стандартную.
- **Три зоны** внутри корня (`AppPaths`): `launcher\` — настройки (`design-settings.json`), аватарки, кэш, журналы, токены входа; `shared\` — общие тяжёлые файлы: `game` (версии, библиотеки, ассеты) и `runtimes` (Java), по одному экземпляру на все сборки; `profiles\<имя>\` — сборки (`instance.json` и папка `minecraft` с saves, mods, options.txt).
- **Указатель** `location.json` лежит всегда в папке по умолчанию (в выбранной его хранить нельзя: чтобы её найти, он нужен раньше). `DataLocationService.Start` вызывается в `App.OnStartup` до создания журнала и любых файлов данных.
- **Смена папки** (`IDataLocationService.Change`) только запоминает выбор и применяется при следующем запуске: зоны переносятся целиком (`StorageMover`: переименование, а для другого диска — копия во временную «.moving», затем переименование и только потом удаление источника; ничего не перезаписывается). Чужая непустая папка получает подпапку `SubReel`; папка внутри текущей (и наоборот) и недоступная для записи отклоняются. Если перенос не удался из-за ошибки ввода-вывода, указатель на старое место остаётся и перенос повторится при следующем запуске; если в новой зоне уже есть данные, старая папка остаётся на месте нетронутой.
- **Недоступный корень** (диск отключён): на этот сеанс используется папка по умолчанию, выбор не стирается.
- **Миграция прежней раскладки** при первом запуске после обновления: `%AppData%\SubReel` (настройки и их `.bak`, аватарки, `instances` → `profiles`) и корень без зон (`cache`, `logs`, `auth` → `launcher\`; `game`, `runtimes` → `shared\`). Существующие данные в новой раскладке не перезаписываются; что сделано, попадает в журнал записями «Папка данных: …».
- **Не сделано**: подтверждение переноса с показом объёма и прогрессом (сейчас перенос идёт при запуске без индикации); перенос «живьём» без перезапуска.
