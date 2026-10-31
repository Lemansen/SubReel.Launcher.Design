# SubReel Launcher — архитектура (v15, этапы 1–8)

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
- Пути — только через `AppPaths` (раскладка прежняя: `%AppData%\SubReel`, `%LocalAppData%\SubReel`).
- Запись: `.tmp` → flush на диск → `File.Replace` с копией предыдущей версии в `.bak`.
- Повреждённый файл не удаляется: переименовывается в `.corrupt-<время>`, данные берутся из `.bak`.
- Файл настроек получил поле `SchemaVersion` (старые файлы читаются как версия 0 и мигрируются; исходник остаётся в `.bak`).
- Настройки более новой версии лаунчера и недоступный для чтения файл не перезаписываются.

## Журнал
`%LocalAppData%\SubReel\logs\launcher-ГГГГММДД.log`, хранится 14 дней. Туда попадают необработанные исключения и сбои загрузки.

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
- **Общий кэш** `%LocalAppData%\SubReel\game`: `versions`, `libraries`, `assets/indexes`, `assets/objects/<2 символа>/<sha1>`, `assets/log_configs`. Пути из `version.json` не доверяются: выход за корень кэша — ошибка; в архивах нативов запрещён path traversal.
- **Не сделано**: `map_to_resources` (копирование ресурсов в папку игры — при запуске), библиотеки с пустым адресом (создаёт установщик загрузчика, фаза 9), подключение к интерфейсу.

## Этап 7: Java Manager
- **Domain/Java**: `JavaInstallation`, `JavaRequirement` (из `javaVersion` версии; без поля — Java 8 и рантайм `jre-legacy`), `JavaSelector`, `JavaVersionParser`.
- **Правило подбора**: подходит только та же мажорная версия, что требует игра (старые версии ломаются на новой Java). Приоритет: путь пользователя → рантайм лаунчера → рантайм официального лаунчера → остальные; затем 64-бит, затем более свежая сборка. 32-битные Java исключаются на 64-битной системе.
- **Application/Java**: `IJavaManager` (`GetInstallationsAsync`, `ProbeAsync`, `SelectAsync`, `EnsureAsync`), `JavaOptions` (раздел `Java`, адрес списка рантаймов Mojang).
- **Minecraft/Java**: `JavaVersionReader` (файл `release`, иначе `java -version` с тайм-аутом 10 с), источники папок `IJavaHomeSource` (JAVA_HOME и PATH, типовые папки, официальный лаунчер, реестр Windows), `JavaRuntimeIndex`, `ManagedJavaInstaller`, `JavaManager`.
- **Рантаймы лаунчера**: `%LocalAppData%\SubReel\runtimes\<компонент>`. Установка идёт через Download Manager (SHA-1, докачка); файл `.subreel-runtime.json` записывается последним, пока его нет, папка в поиске не участвует. Пути из описания рантайма проверяются на выход за папку.
- **Не сделано**: экран «Настройки» и глобальные значения Java и памяти (нужны новые поля `LauncherSettings` с `SchemaVersion`), автообновление рантайма до свежей сборки, проверка целостности установленного рантайма.

## Этап 8: Authentication
- **Application/Authentication**: `ISession` (ник, UUID, токен, `userType`; токен только в памяти), `IAccountProvider`, `ITokenStore`, `AccountInfo`, `SessionManager` (одна точка получения сессии: офлайн сразу, Microsoft с тихим обновлением под замком аккаунта; удаление аккаунта стирает токены), `AuthenticationException` с причиной `AuthFailureReason`. `LauncherAccount` получил необязательные `Provider` и `ProfileId` (старые настройки читаются как офлайн).
- **Authentication** (`SubReel.Launcher.Authentication`): вход по коду устройства → Xbox Live → XSTS → Minecraft Services (владение + профиль), `MicrosoftAccountProvider`, понятные русские сообщения для кодов XSTS. Client Secret не используется; `Authentication:ClientId` берётся из `appsettings.json`.
- **Хранение токенов (8.3)**: `ProtectedFileTokenStore` — по файлу `%LocalAppData%\SubReel\auth\<uuid>.bin` на аккаунт, содержимое шифруется `ISecretProtector` (на Windows `DpapiSecretProtector`: DPAPI, область текущего пользователя, с «солью» лаунчера). Запись через временный файл и подмену. Повреждённый, обрезанный или «чужой» файл читается как «нет данных» (нужен повторный вход). UUID в имени файла проверяется строго (32 hex), чтобы исключить выход из папки. Сохраняются только refresh-токен Microsoft и токен Minecraft; токены Xbox и XSTS не хранятся. В настройках и журнале токенов нет.
- **Интерфейс (8.4)**: кнопка «Войти через Microsoft» в меню профиля открывает `MicrosoftSignInOverlayViewModel`: код и адрес для ввода, «Открыть страницу», «Скопировать код», этапы входа, ошибка с «Повторить». Закрытие окна отменяет вход (`NavigationService` освобождает `IDisposable`-оверлей). В «Профиле» у Microsoft-аккаунта ник не редактируется (его задаёт Minecraft), есть «Выйти из аккаунта». Голова скина берётся с minotar по нику.
- **Не сделано**: запуск игры с сессией (фаза 9, `SessionManager.GetSessionAsync`), SubReel ID, регистрация приложения в Microsoft Entra и одобрение Client ID в Mojang (без него Minecraft Services отвечают «Invalid app registration»).

## Работа без сети (после этапа 8)
- **Копия каталога**: `MojangVersionCatalogService` сохраняет список версий и патчноуты в `%LocalAppData%\SubReel\cache\version_manifest.json` и `patch_notes.json` (запись через временный файл). Без сети каталог собирается из копии: даты выхода, годы и ссылки на картинки на месте. Такой каталог помечен `VersionCatalog.IsStale`; встроенный список (`IsOffline`) используется, только если копии нет или она повреждена. Если не загрузились только патчноуты, берутся сохранённые.
- **Автообновление**: `INetworkMonitor` (`NetworkMonitor`, события ОС с паузой 2 с) сообщает о появлении сети. `MainViewModel` в потоке интерфейса вызывает `VersionStore.RefreshCatalogAsync` и догружает фон и картинки карточек; `VersionsViewModel` перестраивает карточки по событию `CatalogRefreshed`. Кроме события работает проверка раз в 30 с (нужна, если сеть есть, а серверы Mojang были недоступны). Выбранная версия не меняется; исключение — первый запуск без сети, тогда выбирается настоящий последний релиз.
- **Картинки**: уже скачанные берутся с диска (`DiskImageCache`); предзагрузка при запуске с сетью кладёт на диск картинки всех версий, поэтому следующий запуск без сети показывает их.
