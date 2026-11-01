using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Loaders;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Заголовок секции загрузчика в выпадающем списке.</summary>
public sealed class LoaderHeaderRowViewModel
{
    public LoaderHeaderRowViewModel(LoaderKind kind, int count)
    {
        Kind = kind;
        Title = LoaderInfo.Name(kind).ToUpperInvariant();
        CountText = count.ToString();
    }

    public LoaderKind Kind { get; }
    public string Title { get; }
    public string CountText { get; }
}

/// <summary>Строка установленной версии в выпадающем списке.</summary>
public sealed class InstalledVersionRowViewModel
{
    public InstalledVersionRowViewModel(InstalledVersion version, bool isSelected, Action<InstalledVersion> pick)
    {
        Version = version;
        IsSelected = isSelected;
        PickCommand = new RelayCommand(() => pick(version));
        Kind = version.Loader;
        Title = version.Loader == LoaderKind.Other ? version.Id : version.McVersion;
        Detail = version.Loader switch
        {
            LoaderKind.Vanilla => "Vanilla",
            LoaderKind.Other => $"Основа: Minecraft {version.McVersion}",
            _ => string.IsNullOrEmpty(version.LoaderVersion)
                ? LoaderInfo.Name(version.Loader)
                : $"{LoaderInfo.Name(version.Loader)} {version.LoaderVersion}",
        };
    }

    public InstalledVersion Version { get; }
    public LoaderKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsSelected { get; }
    public ICommand PickCommand { get; }
}

/// <summary>Заголовок раздела списка («Доступно для установки · Minecraft 1.21.1»).</summary>
public sealed class SectionHeaderRowViewModel
{
    public SectionHeaderRowViewModel(string title) => Title = title;

    public string Title { get; }
}

/// <summary>Сборка загрузчика, которой ещё нет в папке игры: её можно выбрать, лаунчер поставит её при первом запуске.</summary>
public sealed class AvailableLoaderRowViewModel
{
    public AvailableLoaderRowViewModel(LoaderBuild build, bool isSelected, Action<LoaderBuild> pick)
    {
        Build = build;
        IsSelected = isSelected;
        PickCommand = new RelayCommand(() => pick(build));
        Kind = build.Kind;
        Title = $"{LoaderInfo.Name(build.Kind)} {build.Version}";
        Detail = build.Stable ? $"Minecraft {build.McVersion}" : $"Minecraft {build.McVersion} · бета";
    }

    public LoaderBuild Build { get; }
    public LoaderKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsSelected { get; }
    public ICommand PickCommand { get; }
}

/// <summary>
/// Кнопка «Версия» в подвале и выпадающий список над ней: установленные версии из папки игры и сборки загрузчиков (Fabric, Quilt),
/// доступные для выбранной версии Minecraft. Вторые подгружаются с мета-серверов при открытии списка; нет сети — берётся сохранённая копия,
/// нет и её — раздел просто не показывается.
/// </summary>
public sealed class VersionDropdownViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько новых сборок каждого загрузчика показывать: список не должен превращаться в каталог на сотни строк.</summary>
    private const int MaxOffersPerLoader = 3;

    private static readonly LoaderKind[] OfferedLoaders = { LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Fabric, LoaderKind.Quilt };

    private readonly VersionStore _store;
    private readonly NavigationService _nav;
    private readonly ILoaderCatalog _catalog;
    private readonly ILogger<VersionDropdownViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<LoaderBuild> _available = Array.Empty<LoaderBuild>();
    private string _availableFor = "";
    private int _availableToken;

    private IReadOnlyList<object> _rows = Array.Empty<object>();
    private string _statusText = "";
    private bool _showMissingNotice;
    private string _missingNoticeText = "";
    private bool _showEmptyState;
    private string _emptyHint = "";

    public VersionDropdownViewModel(VersionStore store, NavigationService nav, ILoaderCatalog catalog, ILogger<VersionDropdownViewModel> logger)
    {
        _store = store;
        _nav = nav;
        _catalog = catalog;
        _logger = logger;
        ToggleCommand = new RelayCommand(nav.ToggleVersionDropdown);
        ShowAllCommand = new RelayCommand(nav.OpenAllVersions);

        store.Changed += (_, _) =>
        {
            RaiseButtonChanged();
            if (nav.IsVersionDropdownOpen) BuildRows();
        };

        nav.VersionDropdownOpened += (_, _) =>
        {
            BuildRows();
            // Список мог измениться за пределами лаунчера — перечитываем папку в фоне; готовый результат перерисует список.
            _store.RefreshInstalled();
            RefreshAvailable();
        };
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    public ICommand ToggleCommand { get; }
    public ICommand ShowAllCommand { get; }

    // ───────────────────────── Кнопка «Версия» ─────────────────────────

    public string VersionLabel => _store.VersionLabel;

    /// <summary>До окончания сканирования индикатор скрыт — не гадаем.</summary>
    public bool ShowStatusDot => _store.InstalledScanned && !string.IsNullOrEmpty(_store.SelectedVersion);

    public bool IsSelectedInstalled => _store.SelectedInstall is not null;

    public string? StatusToolTip => !ShowStatusDot ? null
        : IsSelectedInstalled ? "Версия установлена" : "Нет в папке .minecraft — лаунчер скачает файлы при первом запуске";

    private void RaiseButtonChanged()
    {
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(ShowStatusDot));
        OnPropertyChanged(nameof(IsSelectedInstalled));
        OnPropertyChanged(nameof(StatusToolTip));
    }

    // ───────────────────────── Список ─────────────────────────

    /// <summary>Заголовки секций и строки версий одним плоским списком (в окне он виртуализируется — длинный список не подвешивает интерфейс).</summary>
    public IReadOnlyList<object> Rows { get => _rows; private set => SetProperty(ref _rows, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool ShowMissingNotice { get => _showMissingNotice; private set => SetProperty(ref _showMissingNotice, value); }
    public string MissingNoticeText { get => _missingNoticeText; private set => SetProperty(ref _missingNoticeText, value); }
    public bool ShowEmptyState { get => _showEmptyState; private set => SetProperty(ref _showEmptyState, value); }
    public string EmptyHint { get => _emptyHint; private set => SetProperty(ref _emptyHint, value); }

    private void BuildRows()
    {
        if (!_store.InstalledScanned)
        {
            Rows = Array.Empty<object>();
            ShowMissingNotice = false;
            ShowEmptyState = false;
            StatusText = "Поиск установленных версий…";
            return;
        }

        var installed = _store.Installed;
        StatusText = installed.Count == 0
            ? "Нет установленных версий"
            : $"{installed.Count} {TextHelpers.Plural(installed.Count, "версия установлена", "версии установлены", "версий установлено")}";

        // Выбранной версии нет в папке игры: это не помеха (файлы лаунчер скачает сам при первом запуске), но говорим об этом прямо.
        ShowMissingNotice = _store.SelectedInstall is null && !string.IsNullOrEmpty(_store.SelectedVersion);
        MissingNoticeText = $"Выбрана {_store.VersionLabel} — нет в папке игры";

        ShowEmptyState = installed.Count == 0;
        EmptyHint = _store.GameDirectoryFound
            ? $"В папке {_store.GameDirectory}\\versions нет готовых версий."
            : $"Папка игры не найдена: {_store.GameDirectory}";

        var rows = new List<object>(installed.Count + 6);
        foreach (var group in installed.GroupBy(i => i.Loader).OrderBy(g => g.Key))
        {
            rows.Add(new LoaderHeaderRowViewModel(group.Key, group.Count()));
            foreach (var version in group)
                rows.Add(new InstalledVersionRowViewModel(version, _store.SelectedInstall?.Id == version.Id, PickInstalled));
        }

        AddOffers(rows, installed);
        Rows = rows;
    }

    /// <summary>Сборки загрузчиков для выбранной версии игры, которых ещё нет в папке игры.</summary>
    private void AddOffers(List<object> rows, IReadOnlyList<InstalledVersion> installed)
    {
        if (!string.Equals(_availableFor, _store.SelectedVersion, StringComparison.Ordinal)) return;

        var installedIds = installed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var offers = _available.Where(b => !installedIds.Contains(b.ProfileId)).ToList();
        if (offers.Count == 0) return;

        rows.Add(new SectionHeaderRowViewModel($"ДОСТУПНО ДЛЯ УСТАНОВКИ · MINECRAFT {_store.SelectedVersion}"));
        foreach (var build in offers) rows.Add(new AvailableLoaderRowViewModel(build, IsSelectedOffer(build), PickAvailable));
    }

    private bool IsSelectedOffer(LoaderBuild build) =>
        _store.SelectedInstall is null
        && _store.SelectedLoader == build.Kind
        && string.Equals(_store.SelectedLoaderVersion, build.Version, StringComparison.Ordinal)
        && string.Equals(_store.SelectedVersion, build.McVersion, StringComparison.Ordinal);

    private void PickInstalled(InstalledVersion version)
    {
        _store.Select(version.McVersion, version);
        _nav.CloseVersionDropdown();
    }

    private void PickAvailable(LoaderBuild build)
    {
        _store.SelectLoader(build);
        _nav.CloseVersionDropdown();
    }

    // ───────────────────────── Сборки загрузчиков ─────────────────────────

    private void RefreshAvailable()
    {
        var mcVersion = _store.SelectedVersion;
        if (string.IsNullOrEmpty(mcVersion)) return;

        var token = ++_availableToken;
        LoadAvailableAsync(mcVersion, token, _lifetime.Token).FireAndForget(_logger, "список сборок загрузчиков");
    }

    private async Task LoadAvailableAsync(string mcVersion, int token, CancellationToken ct)
    {
        var results = await Task.WhenAll(OfferedLoaders.Select(kind => FetchBuildsAsync(kind, mcVersion, ct)));

        // За время запроса список закрыли и открыли снова (или выбрали другую версию): результат уже не нужен.
        if (token != _availableToken) return;

        _available = results.SelectMany(builds => builds).ToList();
        _availableFor = mcVersion;
        if (_nav.IsVersionDropdownOpen) BuildRows();
    }

    private async Task<IReadOnlyList<LoaderBuild>> FetchBuildsAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        try
        {
            var builds = await _catalog.GetBuildsAsync(kind, mcVersion, ct);
            return builds.Take(MaxOffersPerLoader).ToList();
        }
        catch (LoaderCatalogException ex)
        {
            // Нет сети и нет копии: раздел загрузчика просто не показывается, ошибкой это не считается.
            _logger.LogInformation("Сборки {Loader} для Minecraft {Version} недоступны: {Reason}", kind, mcVersion, ex.Message);
            return Array.Empty<LoaderBuild>();
        }
    }
}
