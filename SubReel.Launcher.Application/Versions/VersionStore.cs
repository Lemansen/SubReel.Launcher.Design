using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Versions;

/// <summary>
/// Общее состояние выбора версии: каталог релизов, выбранная версия, список установленных версий.
/// Им пользуются сразу несколько экранов (главная, «Все версии», выпадающий список, кнопка «Играть»),
/// поэтому оно живёт отдельно от любой одной ViewModel. Меняется только через методы; об изменениях сообщают события.
/// </summary>
public sealed class VersionStore
{
    private readonly ISettingsService _settings;
    private readonly IVersionCatalogService _catalogs;
    private readonly IInstalledVersionService _installedService;
    private readonly ILogger<VersionStore> _logger;
    private int _scanToken;

    public VersionStore(ISettingsService settings, IVersionCatalogService catalogs, IInstalledVersionService installedService, ILogger<VersionStore> logger)
    {
        _settings = settings;
        _logger = logger;
        _catalogs = catalogs;
        _installedService = installedService;
        Catalog = catalogs.CreateFallback();
        GameDirectory = installedService.DefaultGameDirectory;
    }

    /// <summary>Что-то изменилось (выбор, каталог, список установленных) — пора обновить подписи.</summary>
    public event EventHandler? Changed;

    /// <summary>Каталог загружен (или заменён встроенным, если сети нет) и выбор версии определён. Срабатывает один раз при запуске.</summary>
    public event EventHandler? CatalogReady;

    /// <summary>Каталог заменён свежим после того, как сети не было (или он был взят из копии на диске).</summary>
    public event EventHandler? CatalogRefreshed;

    /// <summary>Пользователь выбрал версию (карточка, страница версии, выпадающий список).</summary>
    public event EventHandler? VersionSelected;

    public VersionCatalog Catalog { get; private set; }
    public bool CatalogLoaded { get; private set; }
    public string SelectedVersion { get; private set; } = "";
    public InstalledVersion? SelectedInstall { get; private set; }

    /// <summary>
    /// Загрузчик выбранной версии: у установленной версии — её загрузчик, у сборки Fabric или Quilt, которой ещё нет на диске
    /// (выбрана из раздела «Доступно для установки»), — загрузчик из её имени. Иначе ваниль.
    /// </summary>
    public LoaderKind SelectedLoader { get; private set; } = LoaderKind.Vanilla;

    /// <summary>Версия загрузчика выбранной версии; у ванили и у особых сборок без разобранной версии — null.</summary>
    public string? SelectedLoaderVersion { get; private set; }
    public IReadOnlyList<InstalledVersion> Installed { get; private set; } = Array.Empty<InstalledVersion>();
    public string GameDirectory { get; private set; }
    public bool GameDirectoryFound { get; private set; } = true;
    public bool InstalledScanned { get; private set; }

    /// <summary>Последнее запущенное сканирование (кнопка «Играть» дожидается его, прежде чем говорить «версии нет»).</summary>
    public Task ScanTask { get; private set; } = Task.CompletedTask;

    /// <summary>Подпись выбранной версии: «1.21.1», «1.21.1 · Fabric».</summary>
    public string VersionLabel
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedVersion)) return "…";
            return SelectedLoader != LoaderKind.Vanilla
                ? $"{SelectedVersion} · {(SelectedLoader == LoaderKind.Other ? "особая" : LoaderInfo.Name(SelectedLoader))}"
                : SelectedVersion;
        }
    }

    /// <summary>Недавно выбранные версии (новые первыми).</summary>
    public IReadOnlyList<string> RecentVersions => _settings.Current.RecentVersions;

    // ───────────────────────── Запуск ─────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_settings.Current.SelectedVersion is { Length: > 0 } saved) SelectedVersion = saved;
        ApplySelectedLoader(); // подпись «1.21.1 · Fabric» нужна сразу, не дожидаясь сканирования папки
        Changed?.Invoke(this, EventArgs.Empty);

        // Установленные версии ищем параллельно с загрузкой каталога: оба шага работают в фоне.
        _ = RefreshInstalled(ct);
        Catalog = await _catalogs.LoadAsync(ct);
        CatalogLoaded = true;

        // Первый заход: встречаем самым новым ванильным релизом (в офлайне не запоминаем встроенный список).
        if (string.IsNullOrEmpty(_settings.Current.SelectedVersion))
        {
            SelectedVersion = Catalog.LatestRelease;
            if (!Catalog.IsOffline)
            {
                _settings.Current.SelectedVersion = SelectedVersion;
                _settings.Save();
            }
        }

        if (InstalledScanned) ResolveSelectedInstall();
        Changed?.Invoke(this, EventArgs.Empty);
        CatalogReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Каталог встроенный или из копии на диске — его нужно обновить, как только появится сеть.</summary>
    public bool NeedsCatalogRefresh => CatalogLoaded && (Catalog.IsOffline || Catalog.IsStale);

    /// <summary>
    /// Повторная загрузка каталога (вызывается, когда сеть вернулась). Заменяет каталог, только если загрузка удалась;
    /// иначе остаётся прежний. Выбранная версия не меняется, кроме случая «первый запуск был без сети».
    /// </summary>
    /// <returns>true — каталог обновлён.</returns>
    public async Task<bool> RefreshCatalogAsync(CancellationToken ct = default)
    {
        if (!NeedsCatalogRefresh) return false;

        var fresh = await _catalogs.LoadAsync(ct);
        if (fresh.IsOffline || fresh.IsStale) return false;

        Catalog = fresh;
        if (string.IsNullOrEmpty(_settings.Current.SelectedVersion))
        {
            SelectedVersion = Catalog.LatestRelease;
            _settings.Current.SelectedVersion = SelectedVersion;
            _settings.Save();
        }

        if (InstalledScanned) ResolveSelectedInstall();
        _logger.LogInformation("Каталог версий обновлён после появления сети.");
        Changed?.Invoke(this, EventArgs.Empty);
        CatalogRefreshed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ───────────────────────── Установленные версии ─────────────────────────

    /// <summary>Перечитывает папку игры в фоне. Готовый результат сообщается событием <see cref="Changed"/>.</summary>
    public Task RefreshInstalled(CancellationToken ct = default) => ScanTask = RefreshInstalledAsync(ct);

    private async Task RefreshInstalledAsync(CancellationToken ct)
    {
        var token = ++_scanToken;
        InstalledScanResult result;
        try
        {
            result = await _installedService.ScanAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return; // лаунчер закрывается — результат уже не нужен
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось просканировать папку игры {Directory}.", GameDirectory);
            result = new InstalledScanResult { GameDirectory = GameDirectory, DirectoryFound = false };
        }
        if (token != _scanToken) return; // за это время запустили новое сканирование

        Installed = result.Versions;
        GameDirectory = result.GameDirectory;
        GameDirectoryFound = result.DirectoryFound;
        InstalledScanned = true;
        ResolveSelectedInstall();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Находит среди установленных версию, соответствующую сохранённому выбору. Нет в списке = не установлена.</summary>
    private void ResolveSelectedInstall()
    {
        SelectedInstall = _settings.Current.SelectedInstallId is { Length: > 0 } id
            ? Installed.FirstOrDefault(i => i.Id == id)
            : Installed.FirstOrDefault(i => i.Loader == LoaderKind.Vanilla && i.McVersion == SelectedVersion);
        ApplySelectedLoader();
    }

    /// <summary>
    /// Определяет загрузчик выбранной версии. Выбранная сборка Fabric, Quilt, Forge или NeoForge, которой нет в папке игры, хранится в настройках
    /// именем профиля (<see cref="LauncherSettings.SelectedInstallId"/>), а загрузчик и его версия читаются из этого имени.
    /// </summary>
    private void ApplySelectedLoader()
    {
        if (SelectedInstall is { } install)
        {
            SelectedLoader = install.Loader;
            SelectedLoaderVersion = install.LoaderVersion;
            return;
        }

        if (LoaderInfo.TryParseProfileId(_settings.Current.SelectedInstallId) is { } profile
            && (profile.McVersion is null || string.Equals(profile.McVersion, SelectedVersion, StringComparison.Ordinal)))
        {
            SelectedLoader = profile.Kind;
            SelectedLoaderVersion = profile.LoaderVersion;
            return;
        }

        SelectedLoader = LoaderKind.Vanilla;
        SelectedLoaderVersion = null;
    }

    // ───────────────────────── Выбор ─────────────────────────

    /// <summary>Единая точка выбора версии. install — конкретная установленная версия (Fabric/Forge/…) из выпадающего списка.</summary>
    public void Select(string id, InstalledVersion? install = null)
    {
        // Выбор по номеру — ваниль, если она установлена.
        var chosen = install ?? Installed.FirstOrDefault(i => i.Loader == LoaderKind.Vanilla && i.McVersion == id);
        Commit(id, chosen, chosen?.Id);
    }

    /// <summary>
    /// Выбор сборки загрузчика из раздела «Доступно для установки». Если её профиль уже есть среди установленных в папке игры,
    /// выбирается он; иначе запоминается имя профиля, и лаунчер поставит сборку при первом запуске.
    /// </summary>
    public void SelectLoader(LoaderBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);

        var profileId = build.ProfileId;
        var installed = Installed.FirstOrDefault(i => string.Equals(i.Id, profileId, StringComparison.Ordinal));
        if (installed is not null)
        {
            Select(build.McVersion, installed);
            return;
        }

        Commit(build.McVersion, null, profileId);
    }

    private void Commit(string mcVersion, InstalledVersion? install, string? installId)
    {
        SelectedVersion = mcVersion;
        _settings.Current.SelectedVersion = mcVersion;
        SelectedInstall = install;
        _settings.Current.SelectedInstallId = installId;
        ApplySelectedLoader();

        // «Недавние»: новые первыми, без повторов, не больше 6.
        var recent = _settings.Current.RecentVersions;
        recent.Remove(mcVersion);
        recent.Insert(0, mcVersion);
        if (recent.Count > 6) recent.RemoveRange(6, recent.Count - 6);
        _settings.Save();

        Changed?.Invoke(this, EventArgs.Empty);
        VersionSelected?.Invoke(this, EventArgs.Empty);
    }
}
