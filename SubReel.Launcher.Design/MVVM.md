# Архитектура представления (MVVM, v15)

Внешний вид и поведение остались теми же, что в v14. С v15 проект разделён на слои (см. [../ARCHITECTURE.md](../ARCHITECTURE.md)); этот файл описывает WPF-проект `SubReel.Launcher.Design` — слой представления.

## Слои и направление зависимостей

```
Views ──► ViewModels ──► Application (VersionStore, AccountService, INewsService, …) ──► Domain
  │            │                    ▲
  │            └─► Services (WPF)   └── реализации: Infrastructure (файлы, сеть, журнал)
  └─► Rendering, Converters
```

`ViewModels` не знают о `Views` (нет ссылок на окно, кнопки, `Dispatcher`). Прямых ссылок на `Infrastructure` из ViewModel-ей нет: они получают только интерфейсы и сервисы Application. Исключение одно — `RenderingServices` для статического `CosmeticsRenderer` (вызывается из XAML, контейнер туда не передать).

| Папка WPF-проекта | Что внутри |
|---|---|
| `ViewModels/` | `MainViewModel` и по одной на экран: `Home`, `Launch`, `Versions` (+ `VersionPage`), `VersionDropdown`, `News`, `Profile`, `Hub`, `Toast`; `Overlays/` — содержимое модальных окон и `IOverlayFactory`; `NavigationService` |
| `Views/` | `MainWindow` (XAML + code-behind только для анимаций и ввода окна), `Templates/` (DataTemplate-ы), `Controls/` (`PhotoBorder`, `AvatarControl`, `LoaderTile`, `InfoBlock`) |
| `Services/` | то, что привязано к WPF: `ImageService` + `ImageDecoder` (байты → `BitmapImage`), `DialogService`, `BrowserService` и их интерфейсы |
| `Rendering/` | отрисовка оформления профиля и пиксельных заглушек (`CosmeticsRenderer`, `ProfileLook`, `PlaceholderArt`, `LoaderAccent`) |
| `Infrastructure/` | `ObservableObject`, `RelayCommand` / `AsyncRelayCommand`, `TaskExtensions.FireAndForget`, `TextHelpers` |
| `DependencyInjection/` | `AddSubReelPresentation` — регистрация ViewModel-ей, сервисов WPF и окна |
| `Converters/`, `Resources/` | значения-преобразователи; `Theme.xaml`, `ControlStyles.xaml`, `Converters.xaml` |

Модели, правила и сервисы, которые раньше лежали в `Models/` и `Services/` этого проекта, переехали: модели — в `SubReel.Launcher.Domain`, `VersionStore` / `AccountService` / `NewsService` — в `SubReel.Launcher.Application`, диск и сеть — в `SubReel.Launcher.Infrastructure`.

## Как это работает

1. **Корень композиции** — `App.OnStartup`: `Host.CreateApplicationBuilder`, регистрация по слоям (`AddSubReelInfrastructure` → `AddSubReelApplication` → `AddSubReelPresentation`), окно получает `MainViewModel` из контейнера. Ни один класс не создаёт свои зависимости сам; при выходе хост останавливается и освобождает singleton-ы.
2. **Состояние навигации** — `NavigationService`: что сейчас открыто (страница, «Все версии», страница версии, новости, меню, модальное окно). Другие ViewModel-и просят его перейти (`nav.OpenNews()`), а правила («нельзя открыть новости поверх модального окна») лежат только там. Каждый метод меняет состояние целиком и вызывает `Commit()` один раз.
3. **Общий выбор версии** — `VersionStore` (Application): каталог, выбранная версия, установленные версии. Им пользуются главная, «Все версии», выпадающий список и «Играть»; события `Changed` / `CatalogReady` / `VersionSelected`.
4. **Окно только показывает.** Привязки берут данные и команды; тексты, видимость, выбранные кнопки, цвета — из ViewModel. Модальные окна — это `Nav.Overlay` + `DataTemplate` по типу (`Views/Templates/OverlayTemplates.xaml`); создаёт их `IOverlayFactory`.
5. **Анимации остались в коде окна**, но не принимают решений: `NavigationService.Changed` → `MainWindow.ApplyNavigation()` сравнивает «что показано» с «что должно быть» и запускает нужные анимации (`MainWindow.Navigation.cs`). Остальные ViewModel-и сообщают о событиях для анимации обычными событиями (`Home.InfoUpdated`, `Versions.FilterApplied`, …).
6. **Фоновые операции** запускаются через `FireAndForget(logger, "название")`: сбой попадает в журнал, процесс не падает; отмена ошибкой не считается. Команды, которые могут упасть, создаются с `onError` (`AsyncRelayCommand`). Долгие запросы получают `CancellationToken` (новости — на каждый запрос, предзагрузка картинок — от `MainViewModel`).

## Что осталось в коде окна и почему

Анимации (`BeginAnimation`), перетаскивание и сворачивание окна, колесо мыши и `Esc`, фокус и прокрутка, позиция выпадающего списка над кнопкой — это поведение *вида*, а не данных. ViewModel не знает ни про одно из них. Правила, от которых зависят колесо и `Esc`, берутся из ViewModel (`Nav.CanSwitchModeByWheel`, `MainViewModel.GoBack()`).

## Подписки на события

Все издатели (`VersionStore`, `NavigationService`, ViewModel-и) и подписчики (ViewModel-и, окно) — singleton-ы одного контейнера, поэтому отписка не нужна. Объекты с коротким временем жизни (оверлеи, элементы списков) не подписываются ни на что. **Если такой объект когда-нибудь должен подписаться на singleton, он обязан реализовать `IDisposable` и отписаться.**

## Как добавить…

- **…новый экран/раздел**: ViewModel с данными и командами → регистрация в `AddSubReelPresentation` → `DataTemplate` (или блок в `MainWindow.xaml`) → если нужен переход, метод в `NavigationService`; анимацию добавить в `ApplyNavigation`.
- **…новое модальное окно**: класс от `OverlayViewModel` + `DataTemplate` в `OverlayTemplates.xaml` + метод в `IOverlayFactory`; открыть — `nav.ShowOverlay(overlays.CreateX())`.
- **…новый источник данных или действие с диском/сетью**: интерфейс в `SubReel.Launcher.Application`, реализация в `SubReel.Launcher.Infrastructure`, регистрация в её `AddSubReelInfrastructure`. ViewModel получает только интерфейс.
- **…новый источник новостей**: реализация `INewsSource` в Infrastructure и регистрация `services.AddSingleton<INewsSource>(...)`.
- **…тест**: правила и сервисы тестируются в `SubReel.Launcher.Tests` (кросс-платформенный xUnit). ViewModel-и и `NavigationService` лежат в WPF-проекте (`net10.0-windows`), для них нужен отдельный тестовый проект на `net10.0-windows` и STA-поток из-за типов `System.Windows.Media`.

## Проверка

Результаты статической проверки XAML (привязки, ресурсы, обработчики, конвертеры), code-behind и подписок — в [../docs/stage4-audit.md](../docs/stage4-audit.md). Сборка и запуск в среде автора не выполнялись (нет .NET SDK и Windows). Первым делом: `dotnet build SubReel.Launcher.Design.sln`, `dotnet test`, затем запуск и проход сценариев: «Все версии» (колесо, поиск, карточка → страница → «Выбрать»), выпадающий список версий, новости, меню профиля (добавить/переключить/удалить аккаунт), «Личный кабинет» (ник, оформление), «Играть», Hub. В окне *Output* Visual Studio не должно быть сообщений `System.Windows.Data Error`.

`ObservableObject` / `RelayCommand` / `AsyncRelayCommand` из `Infrastructure/` по форме совпадают с `CommunityToolkit.Mvvm`; переход на библиотеку возможен без правок XAML, пересмотр — когда новые экраны добавят более 10 ViewModel (см. [../ROADMAP.md](../ROADMAP.md)).
