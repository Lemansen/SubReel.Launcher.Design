using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Games;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>
/// Корневая ViewModel окна: собирает экраны и раздаёт команды навигации. Сами правила переходов — в <see cref="NavigationService"/>,
/// данные экранов — в дочерних ViewModel-ях. Здесь только то, что связывает их между собой.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VersionStore _store;
    private readonly IVersionMetadataProvider _metadata;
    private readonly IImageService _images;
    private readonly IOverlayFactory _overlays;
    private readonly ILogger<MainViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly INetworkMonitor _network;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current; // создаётся в потоке интерфейса
    private readonly SemaphoreSlim _recoverGate = new(1, 1);
    private bool _initialized;

    /// <summary>Как часто проверять, не пора ли догрузить данные, если событие ОС о появлении сети пропущено (сеть есть, а Mojang был недоступен).</summary>
    private static readonly TimeSpan RecoveryPoll = TimeSpan.FromSeconds(30);

    public MainViewModel(
        NavigationService nav, VersionStore store, ProfileViewModel profile, HomeViewModel home, LaunchViewModel launch,
        VersionsViewModel versions, VersionDropdownViewModel versionDropdown, NewsViewModel news, BuildsViewModel builds, HubViewModel hub,
        ToastViewModel toast, UpdateViewModel update, IImageService images, IOverlayFactory overlays, INetworkMonitor network, IVersionMetadataProvider metadata,
        ILogger<MainViewModel> logger)
    {
        _metadata = metadata;
        _network = network;
        network.Restored += OnNetworkRestored;
        Nav = nav;
        _store = store;
        Profile = profile;
        Home = home;
        Launch = launch;
        Versions = versions;
        VersionDropdown = versionDropdown;
        News = news;
        Builds = builds;
        Hub = hub;
        Toast = toast;
        Update = update;
        _images = images;
        _overlays = overlays;
        _logger = logger;

        ShowHomeCommand = new RelayCommand(() => nav.ShowPage(MainPage.Home));
        ShowCommunityCommand = new RelayCommand(() => nav.ShowPage(MainPage.Community));
        ShowHubCommand = new RelayCommand(() => nav.ShowPage(MainPage.Hub));
        OpenNewsCommand = new RelayCommand(() => nav.OpenNews());
        EnterVersionsCommand = new RelayCommand(() => nav.EnterVersions());
        ExitVersionsCommand = new RelayCommand(nav.ExitVersions);
        // Нижняя точка индикатора: со страницы версии — назад к списку, иначе — открыть «Все версии».
        DotVersionsCommand = new RelayCommand(() =>
        {
            if (nav.IsVersionPageOpen) nav.CloseVersionPage(); else nav.EnterVersions();
        });
        ToggleProfileMenuCommand = new RelayCommand(nav.ToggleProfileMenu);
        CloseProfileMenuCommand = new RelayCommand(nav.CloseProfileMenu);
        CloseVersionDropdownCommand = new RelayCommand(nav.CloseVersionDropdown);
        OpenAccountCommand = new RelayCommand(OpenAccount);
        OpenAddAccountCommand = new RelayCommand(OpenAddAccount);
        SignInMicrosoftCommand = new RelayCommand(SignInMicrosoft);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenBuildsCommand = new RelayCommand(() => nav.OpenBuilds());
        OpenModsCommand = new RelayCommand(OpenMods);
        CloseOverlayCommand = new RelayCommand(nav.CloseOverlay);
    }

    public NavigationService Nav { get; }
    public UpdateViewModel Update { get; }
    public ProfileViewModel Profile { get; }
    public HomeViewModel Home { get; }
    public LaunchViewModel Launch { get; }
    public VersionsViewModel Versions { get; }
    public VersionDropdownViewModel VersionDropdown { get; }
    public NewsViewModel News { get; }
    public BuildsViewModel Builds { get; }
    public HubViewModel Hub { get; }
    public ToastViewModel Toast { get; }

    public ICommand ShowHomeCommand { get; }
    public ICommand ShowCommunityCommand { get; }
    public ICommand ShowHubCommand { get; }
    public ICommand OpenNewsCommand { get; }
    public ICommand EnterVersionsCommand { get; }
    public ICommand ExitVersionsCommand { get; }
    public ICommand DotVersionsCommand { get; }
    public ICommand ToggleProfileMenuCommand { get; }
    public ICommand CloseProfileMenuCommand { get; }
    public ICommand CloseVersionDropdownCommand { get; }
    public ICommand OpenAccountCommand { get; }
    public ICommand OpenAddAccountCommand { get; }
    public ICommand SignInMicrosoftCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenBuildsCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand CloseOverlayCommand { get; }

    private void OpenAccount()
    {
        Nav.CloseProfileMenu();
        Nav.ShowOverlay(_overlays.CreateAccount());
    }

    private void OpenAddAccount()
    {
        Nav.CloseProfileMenu();
        Nav.ShowOverlay(_overlays.CreateAddAccount());
    }

    private void SignInMicrosoft()
    {
        Nav.CloseProfileMenu();
        Nav.ShowOverlay(_overlays.CreateMicrosoftSignIn());
    }

    /// <summary>Моды выбранной сборки. Для обычной версии и ванили объясняем, что нужна сборка с загрузчиком.</summary>
    private void OpenMods()
    {
        var build = _store.SelectedInstance;
        if (build is null)
        {
            Toast.Show("Моды ставятся в сборку: создай сборку с загрузчиком и выбери её.", 3800);
            return;
        }
        if (!ModsOverlayViewModel.Supports(build.Loader))
        {
            Toast.Show("У ванильной сборки нет модов. Создай сборку с Fabric, Quilt, Forge или NeoForge.", 3800);
            return;
        }

        Nav.CloseVersionDropdown();
        Nav.ShowOverlay(_overlays.CreateMods(build));
    }

    private void OpenSettings()
    {
        Nav.CloseProfileMenu();
        Nav.ShowOverlay(_overlays.CreateSettings());
    }

    /// <summary>Загрузка данных при запуске: версии, картинка на фоне, картинки карточек. Сеть и диск не должны ронять лаунчер.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _store.InitializeAsync(_lifetime.Token);
            await Home.RefreshHeroImageAsync();
            await Versions.LoadCardImagesAsync();

            // Пока идёт приветствие, тихо докачиваем остальные картинки (в т.ч. свои у отдельных версий) в кэш.
            PrefetchImagesAsync(_lifetime.Token).FireAndForget(_logger, "предзагрузка картинок версий");
            PrefetchMetadataAsync(_lifetime.Token).FireAndForget(_logger, "предзагрузка описаний версий");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Начальная загрузка прервана: лаунчер закрывается.");
        }
        catch (Exception ex)
        {
            // Остаётся встроенный список версий и заглушки вместо картинок.
            _logger.LogError(ex, "Не удалось выполнить начальную загрузку данных.");
        }

        // Если сети не было, данные догрузятся сами, как только она появится: без перезапуска лаунчера.
        _initialized = true;
        Update.RunPeriodicChecksAsync(_lifetime.Token).FireAndForget(_logger, "проверка обновлений лаунчера");
        PollRecoveryAsync(_lifetime.Token).FireAndForget(_logger, "догрузка данных при появлении сети");
    }

    /// <summary>
    /// Фоном скачивает небольшие описания выбранной и самой новой версии (если их ещё нет на диске): первая установка начнётся без паузы на их загрузку.
    /// Файлы игры не качаются. Ошибки сети молча пропускаются: описание скачается при установке.
    /// </summary>
    private async Task PrefetchMetadataAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), ct); // сначала даём окну спокойно открыться
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;

        foreach (var id in new[] { _store.SelectedVersion, _store.Catalog.LatestRelease }.Where(id => !string.IsNullOrEmpty(id)).Distinct())
        {
            try
            {
                await _metadata.ResolveAsync(id, allowNetwork: false, ct); // уже на диске
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await _metadata.ResolveAsync(id, allowNetwork: true, ct);
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    _logger.LogDebug(inner, "Описание версии {Id} не загружено заранее.", id);
                }
            }
        }
    }

    // ───────────────────────── Восстановление после отсутствия сети ─────────────────────────

    /// <summary>Что-то осталось без данных: каталог встроенный или из копии, у фона или карточек нет картинок.</summary>
    private bool NeedsRecovery => _store.NeedsCatalogRefresh || Home.HeroImageMissing || Versions.HasMissingPhotos;

    private void OnNetworkRestored(object? sender, EventArgs e)
    {
        // Событие приходит из фонового потока: дальнейшая работа с ViewModel-ями идёт в потоке интерфейса.
        void Run()
        {
            if (_lifetime.IsCancellationRequested) return;
            RecoverAsync(_lifetime.Token).FireAndForget(_logger, "обновление данных после появления сети");
        }

        if (_ui is null) Run(); else _ui.Post(_ => Run(), null);
    }

    private async Task PollRecoveryAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RecoveryPoll);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) continue;
            try
            {
                await RecoverAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Один неудачный проход не должен останавливать повторные попытки.
                _logger.LogWarning(ex, "Не удалось догрузить данные, попробуем ещё раз.");
            }
        }
    }

    /// <summary>Обновляет каталог версий и догружает фон и картинки карточек. Одновременно выполняется один проход.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        if (!_initialized || !NeedsRecovery) return;
        if (!await _recoverGate.WaitAsync(0, ct)) return;
        try
        {
            var refreshed = await _store.RefreshCatalogAsync(ct);
            if (refreshed || Home.HeroImageMissing) await Home.RefreshHeroImageAsync();
            if (refreshed || Versions.HasMissingPhotos) await Versions.LoadCardImagesAsync();
            if (refreshed) PrefetchImagesAsync(ct).FireAndForget(_logger, "предзагрузка картинок версий");

            if (refreshed) Toast.Show("Интернет появился — данные обновлены");
        }
        finally
        {
            _recoverGate.Release();
        }
    }

    // Исключения (кроме отмены при закрытии) логирует FireAndForget в месте вызова: отдельный try/catch здесь не нужен.
    private Task PrefetchImagesAsync(CancellationToken ct)
    {
        var urls = _store.Catalog.Releases.Select(v => v.ImageUrl).Where(u => u != null).Select(u => u!).ToList();
        return _images.PrefetchAsync(urls, ct);
    }

    public void Dispose()
    {
        _network.Restored -= OnNetworkRestored;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Esc / «назад»: закрывает самое верхнее открытое. false — закрывать нечего.</summary>
    public bool GoBack()
    {
        if (Nav.IsVersionPageOpen) { Nav.CloseVersionPage(); return true; }
        if (Nav.IsNewsOpen)
        {
            if (News.IsDetailOpen) News.IsDetailOpen = false; else News.Close();
            return true;
        }
        if (Nav.IsBuildsOpen) { Builds.GoBack(); return true; }
        if (Nav.IsVersionDropdownOpen) { Nav.CloseVersionDropdown(); return true; }
        if (Nav.IsProfileMenuOpen) { Nav.CloseProfileMenu(); return true; }
        if (Nav.IsVersionsOpen && !Nav.IsOverlayOpen) { Nav.ExitVersions(); return true; }
        return false;
    }
}
