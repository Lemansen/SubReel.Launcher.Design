using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.Instances;

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
public sealed class InstalledVersionRowViewModel : IPickableRow
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

/// <summary>Строка выпадающего списка, которую можно выбрать (Enter в поиске выбирает первую такую строку).</summary>
public interface IPickableRow
{
    ICommand PickCommand { get; }
}

/// <summary>
/// Сборка, созданная вручную («Мои сборки»): выбор запоминается, «Играть» запускает именно её. Справа — когда запускали в последний раз;
/// при наведении вместо этого появляются шестерёнка и удаление (удаление в два нажатия: первое взводит кнопку, второе удаляет).
/// </summary>
public sealed class InstanceRowViewModel : IPickableRow
{
    public InstanceRowViewModel(Instance instance, bool isSelected, bool isDeleteArmed, string lastPlayedText,
        Action<Instance> pick, Action openSettings, Func<Instance, Task> delete, Action<Exception> onError)
    {
        Instance = instance;
        IsSelected = isSelected;
        IsDeleteArmed = isDeleteArmed;
        InfoText = lastPlayedText;
        PlayTimeText = "Наиграно: " + VersionSearch.FormatPlayTime(instance.PlayTimeSeconds);
        StatsText = instance.LastPlayedUtc is null
            ? "Ещё не запускалась"
            : instance.PlayTimeSeconds > 0
                ? $"Играл {lastPlayedText} · наиграно {VersionSearch.FormatPlayTime(instance.PlayTimeSeconds)}"
                : $"Играл {lastPlayedText}";
        McVersionText = $"Minecraft {instance.McVersion}";
        LoaderText = instance.Loader == LoaderKind.Vanilla
            ? "Vanilla"
            : string.IsNullOrEmpty(instance.LoaderVersion) ? LoaderInfo.Name(instance.Loader) : $"{LoaderInfo.Name(instance.Loader)} {instance.LoaderVersion}";
        PickCommand = new RelayCommand(() => pick(instance));
        SettingsCommand = new RelayCommand(openSettings);
        DeleteCommand = new AsyncRelayCommand(() => delete(instance), onError: onError);
        Kind = instance.Loader;
        Title = instance.Name;
        Detail = instance.Loader == LoaderKind.Vanilla
            ? $"Minecraft {instance.McVersion}"
            : string.IsNullOrEmpty(instance.LoaderVersion)
                ? $"Minecraft {instance.McVersion} · {LoaderInfo.Name(instance.Loader)}"
                : $"Minecraft {instance.McVersion} · {LoaderInfo.Name(instance.Loader)} {instance.LoaderVersion}";
    }

    public Instance Instance { get; }
    public LoaderKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public string InfoText { get; }
    public string PlayTimeText { get; }
    public string StatsText { get; }
    public string McVersionText { get; }
    public string LoaderText { get; }
    public bool IsSelected { get; }
    public bool IsDeleteArmed { get; }
    public string DeleteToolTip => IsDeleteArmed ? "Нажми ещё раз — сборка будет удалена" : "Удалить сборку";
    public ICommand PickCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DeleteCommand { get; }
}

/// <summary>Кнопка-фильтр над списком: «Все», «Мои сборки» или один загрузчик.</summary>
public sealed class FilterChipViewModel
{
    public FilterChipViewModel(string title, bool isSelected, Action select)
    {
        Title = title;
        IsSelected = isSelected;
        SelectCommand = new RelayCommand(select);
    }

    public string Title { get; }
    public bool IsSelected { get; }
    public ICommand SelectCommand { get; }
}

/// <summary>Заголовок раздела списка («Доступно для установки · Minecraft 1.21.1»).</summary>
public sealed class SectionHeaderRowViewModel
{
    public SectionHeaderRowViewModel(string title) => Title = title;

    public string Title { get; }
}

/// <summary>Сборка загрузчика, которой ещё нет в папке игры: её можно выбрать, лаунчер поставит её при первом запуске.</summary>
public sealed class AvailableLoaderRowViewModel : IPickableRow
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
    private enum DropdownScope { All, MyBuilds, Loader }

    /// <summary>Сколько новых сборок каждого загрузчика показывать: список не должен превращаться в каталог на сотни строк.</summary>
    private const int MaxOffersPerLoader = 3;

    private static readonly LoaderKind[] OfferedLoaders = { LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Fabric, LoaderKind.Quilt };

    private readonly VersionStore _store;
    private readonly NavigationService _nav;
    private readonly ILoaderCatalog _catalog;
    private readonly InstanceService _instances;
    private readonly IToastService _toast;
    private readonly ILogger<VersionDropdownViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<Instance> _myBuilds = Array.Empty<Instance>();
    private IReadOnlyList<LoaderBuild> _available = Array.Empty<LoaderBuild>();
    private string _availableFor = "";
    private int _availableToken;
    private int _deleteToken;
    private string? _armedDeleteId;

    private string _searchText = "";
    private DropdownScope _scope = DropdownScope.All;
    private LoaderKind _loaderFilter;
    private IReadOnlyList<FilterChipViewModel> _chips = Array.Empty<FilterChipViewModel>();
    private bool _showNoResults;
    private string _noResultsText = "";

    private IReadOnlyList<object> _rows = Array.Empty<object>();
    private string _statusText = "";
    private bool _showMissingNotice;
    private string _missingNoticeText = "";
    private bool _showEmptyState;
    private string _emptyHint = "";

    public VersionDropdownViewModel(VersionStore store, NavigationService nav, ILoaderCatalog catalog, InstanceService instances,
        IToastService toast, ILogger<VersionDropdownViewModel> logger)
    {
        _toast = toast;
        _store = store;
        _nav = nav;
        _catalog = catalog;
        _instances = instances;
        _logger = logger;
        ToggleCommand = new RelayCommand(nav.ToggleVersionDropdown);
        ShowAllCommand = new RelayCommand(nav.OpenAllVersions);
        OpenBuildSettingsCommand = new RelayCommand(OpenBuildSettings);
        DeleteBuildCommand = new AsyncRelayCommand(DeleteSelectedBuildAsync, onError: OnDeleteError);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        PickFirstCommand = new RelayCommand(PickFirst);
        EscapeCommand = new RelayCommand(OnEscape);

        store.Changed += (_, _) =>
        {
            RaiseButtonChanged();
            if (nav.IsVersionDropdownOpen) BuildRows();
        };

        nav.VersionDropdownOpened += (_, _) =>
        {
            ResetFilters(); // каждое открытие — с чистого листа: без прошлого запроса и фильтра
            BuildRows();
            // Список мог измениться за пределами лаунчера — перечитываем папку в фоне; готовый результат перерисует список.
            _store.RefreshInstalled();
            RefreshAvailable();
            RefreshMyBuilds();
        };
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    public ICommand ToggleCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand OpenBuildSettingsCommand { get; }
    public ICommand DeleteBuildCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand PickFirstCommand { get; }
    public ICommand EscapeCommand { get; }

    // ───────────────────────── Кнопка «Версия» ─────────────────────────

    public string VersionLabel => _store.VersionLabel;

    /// <summary>До окончания сканирования индикатор скрыт — не гадаем.</summary>
    public bool ShowStatusDot => _store.InstalledScanned && !string.IsNullOrEmpty(_store.SelectedVersion) && _store.SelectedInstance is null;

    public bool IsSelectedInstalled => _store.SelectedInstall is not null;

    public string? StatusToolTip => !ShowStatusDot ? null
        : IsSelectedInstalled ? "Версия установлена" : "Нет в папке .minecraft — лаунчер скачает файлы при первом запуске";

    // ───────────────────────── Шестерёнка и «Удалить»: в подвале и в строках «Моих сборок» ─────────────────────────

    /// <summary>Кнопки настроек и удаления в подвале показываются только у сборки, созданной вручную: у обычной версии нет своей папки с настройками.</summary>
    public bool HasSelectedBuild => _store.SelectedInstance is not null;

    /// <summary>Первое нажатие «Удалить» взвело кнопку: второе в течение нескольких секунд удалит сборку.</summary>
    public bool IsDeleteArmed => _armedDeleteId is not null && string.Equals(_armedDeleteId, _store.SelectedInstance?.Id, StringComparison.Ordinal);

    public string DeleteToolTip => IsDeleteArmed ? "Нажми ещё раз — сборка будет удалена" : "Удалить выбранную сборку";

    private void OpenBuildSettings() => _toast.Show("Страница настроек сборки появится в следующем обновлении.", 3200);

    private Task DeleteSelectedBuildAsync() => _store.SelectedInstance is { } build ? DeleteBuildAsync(build) : Task.CompletedTask;

    private async Task DeleteBuildAsync(Instance build)
    {
        if (!string.Equals(_armedDeleteId, build.Id, StringComparison.Ordinal))
        {
            ArmDelete(build.Id);
            _toast.Show($"Сборка «{build.Name}» будет удалена вместе с модами, мирами и настройками. Нажми кнопку удаления ещё раз, чтобы подтвердить.", 4500);
            return;
        }

        SetArmed(null);

        var deleted = await _instances.DeleteAsync(build.Id, _lifetime.Token);
        if (!deleted)
        {
            _logger.LogWarning("Сборка {Id} не удалена.", build.Id);
            _toast.Show("Не удалось удалить сборку: возможно, игра запущена или файлы заняты другой программой.", 4200);
            return;
        }

        _logger.LogInformation("Сборка {Id} удалена пользователем.", build.Id);
        _myBuilds = _myBuilds.Where(b => b.Id != build.Id).ToList();
        _store.ForgetInstance(build.Id); // если она была выбрана — вернёт выбор к обычной версии
        if (_nav.IsVersionDropdownOpen) BuildRows();
        RefreshMyBuilds();
        _toast.Show($"Сборка «{build.Name}» удалена");
    }

    private void ArmDelete(string id)
    {
        var token = ++_deleteToken;
        SetArmed(id);
        DisarmAfterAsync(token, _lifetime.Token).FireAndForget(_logger, "сброс подтверждения удаления сборки");
    }

    private async Task DisarmAfterAsync(int token, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        if (token == _deleteToken) SetArmed(null);
    }

    private void SetArmed(string? id)
    {
        if (string.Equals(_armedDeleteId, id, StringComparison.Ordinal)) return;
        _armedDeleteId = id;
        OnPropertyChanged(nameof(IsDeleteArmed));
        OnPropertyChanged(nameof(DeleteToolTip));
        if (_nav.IsVersionDropdownOpen) BuildRows(); // строка перерисуется красной
    }

    private void OnDeleteError(Exception ex)
    {
        _logger.LogError(ex, "Сбой при удалении сборки.");
        SetArmed(null);
        _toast.Show("Не удалось удалить сборку. Подробности — в журнале лаунчера.", 4200);
    }

    private void RaiseButtonChanged()
    {
        _deleteToken++; // выбрали другую сборку или версию: подтверждение удаления сбрасывается
        _armedDeleteId = null;
        OnPropertyChanged(nameof(IsDeleteArmed));
        OnPropertyChanged(nameof(DeleteToolTip));
        OnPropertyChanged(nameof(HasSelectedBuild));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(ShowStatusDot));
        OnPropertyChanged(nameof(IsSelectedInstalled));
        OnPropertyChanged(nameof(StatusToolTip));
    }

    // ───────────────────────── Поиск и фильтры ─────────────────────────

    /// <summary>Строка поиска: слова ищутся в названии, версии игры, загрузчике и версии загрузчика.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(HasSearchText));
            BuildRows();
        }
    }

    public bool HasSearchText => _searchText.Length > 0;

    /// <summary>Кнопки-фильтры: «Все», «Мои сборки» (если они есть) и загрузчики, которые сейчас есть в списке.</summary>
    public IReadOnlyList<FilterChipViewModel> Chips { get => _chips; private set => SetProperty(ref _chips, value); }

    /// <summary>Поиск или фильтр дали пустой список.</summary>
    public bool ShowNoResults { get => _showNoResults; private set => SetProperty(ref _showNoResults, value); }
    public string NoResultsText { get => _noResultsText; private set => SetProperty(ref _noResultsText, value); }

    private bool FiltersActive => _searchText.Trim().Length > 0 || _scope != DropdownScope.All;

    private void ResetFilters()
    {
        _scope = DropdownScope.All;
        if (_searchText.Length == 0) return;
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
    }

    private void SetScope(DropdownScope scope, LoaderKind loader = default)
    {
        _scope = scope;
        _loaderFilter = loader;
        BuildRows();
    }

    private void PickFirst()
    {
        // Enter в поиске: выбрать первую подходящую строку (без мыши — «набрал, нажал Enter, играй»).
        if (Rows.OfType<IPickableRow>().FirstOrDefault() is { } row) row.PickCommand.Execute(null);
    }

    private void OnEscape()
    {
        if (HasSearchText) SearchText = ""; // первое Esc очищает запрос, второе закрывает список
        else _nav.CloseVersionDropdown();
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
            Chips = Array.Empty<FilterChipViewModel>();
            ShowMissingNotice = false;
            ShowEmptyState = false;
            ShowNoResults = false;
            StatusText = "Поиск установленных версий…";
            return;
        }

        var installed = _store.Installed;
        var offers = CurrentOffers(installed);
        BuildChips(installed, offers);

        // Выбранной версии нет в папке игры: это не помеха (файлы лаунчер скачает сам при первом запуске), но говорим об этом прямо.
        ShowMissingNotice = _store.SelectedInstance is null && _store.SelectedInstall is null && !string.IsNullOrEmpty(_store.SelectedVersion);
        MissingNoticeText = $"Выбрана {_store.VersionLabel} — нет в папке игры";

        ShowEmptyState = installed.Count == 0 && _myBuilds.Count == 0 && !FiltersActive;
        EmptyHint = _store.GameDirectoryFound
            ? $"В папке {_store.GameDirectory}\\versions нет готовых версий."
            : $"Папка игры не найдена: {_store.GameDirectory}";

        var query = _searchText;
        var showVersions = _scope is DropdownScope.All or DropdownScope.Loader;
        var now = DateTime.Now;

        var rows = new List<object>(installed.Count + _myBuilds.Count + 7);

        {
            var builds = _myBuilds
                .Where(b => _scope != DropdownScope.Loader || b.Loader == _loaderFilter)
                .Where(b => VersionSearch.Matches(query, b.Name, b.McVersion, LoaderInfo.Name(b.Loader), b.LoaderVersion))
                .ToList();
            if (builds.Count > 0)
            {
                rows.Add(new SectionHeaderRowViewModel(builds.Count == _myBuilds.Count ? "МОИ СБОРКИ" : $"МОИ СБОРКИ · {builds.Count}"));
                var selectedId = _store.SelectedInstance?.Id;
                foreach (var build in builds)
                {
                    rows.Add(new InstanceRowViewModel(build, string.Equals(build.Id, selectedId, StringComparison.Ordinal),
                        string.Equals(build.Id, _armedDeleteId, StringComparison.Ordinal),
                        VersionSearch.FormatLastPlayed(build.LastPlayedUtc?.ToLocalTime(), now),
                        PickInstance, OpenBuildSettings, DeleteBuildAsync, OnDeleteError));
                }
            }
        }

        if (showVersions)
        {
            var versions = installed
                .Where(v => _scope != DropdownScope.Loader || v.Loader == _loaderFilter)
                .Where(v => VersionSearch.Matches(query, v.Id, v.McVersion, LoaderInfo.Name(v.Loader), v.LoaderVersion))
                .ToList();
            foreach (var group in versions.GroupBy(i => i.Loader).OrderBy(g => g.Key))
            {
                rows.Add(new LoaderHeaderRowViewModel(group.Key, group.Count()));
                foreach (var version in group)
                    rows.Add(new InstalledVersionRowViewModel(version, _store.SelectedInstall?.Id == version.Id, PickInstalled));
            }

            var shownOffers = offers
                .Where(b => _scope != DropdownScope.Loader || b.Kind == _loaderFilter)
                .Where(b => VersionSearch.Matches(query, LoaderInfo.Name(b.Kind), b.Version, b.McVersion))
                .ToList();
            if (shownOffers.Count > 0)
            {
                rows.Add(new SectionHeaderRowViewModel($"ДОСТУПНО ДЛЯ УСТАНОВКИ · MINECRAFT {_store.SelectedVersion}"));
                foreach (var build in shownOffers) rows.Add(new AvailableLoaderRowViewModel(build, IsSelectedOffer(build), PickAvailable));
            }
        }

        var shown = rows.OfType<IPickableRow>().Count();
        ShowNoResults = FiltersActive && shown == 0;
        NoResultsText = _searchText.Trim().Length > 0 ? $"Ничего не найдено по «{_searchText.Trim()}»" : "В этом разделе пока пусто";

        StatusText = FiltersActive
            ? $"Найдено: {shown}"
            : installed.Count == 0 && _myBuilds.Count == 0
                ? "Пока ничего не установлено"
                : string.Join(" · ", new[]
                {
                    _myBuilds.Count > 0 ? $"{_myBuilds.Count} {TextHelpers.Plural(_myBuilds.Count, "сборка", "сборки", "сборок")}" : null,
                    installed.Count > 0 ? $"{installed.Count} {TextHelpers.Plural(installed.Count, "версия", "версии", "версий")} в папке игры" : null,
                }.Where(x => x is not null));

        Rows = rows;
    }

    /// <summary>Фильтры строятся по тому, что реально есть в списке: пустых загрузчиков нет. Выбранный фильтр, у которого не осталось строк, сбрасывается.</summary>
    private void BuildChips(IReadOnlyList<InstalledVersion> installed, IReadOnlyList<LoaderBuild> offers)
    {
        var kinds = _myBuilds.Select(b => b.Loader)
            .Concat(installed.Select(v => v.Loader))
            .Concat(offers.Select(o => o.Kind))
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        if (_scope == DropdownScope.Loader && !kinds.Contains(_loaderFilter)) _scope = DropdownScope.All;
        if (_scope == DropdownScope.MyBuilds && _myBuilds.Count == 0) _scope = DropdownScope.All;

        var chips = new List<FilterChipViewModel> { new("Все", _scope == DropdownScope.All, () => SetScope(DropdownScope.All)) };
        if (_myBuilds.Count > 0) chips.Add(new("Мои сборки", _scope == DropdownScope.MyBuilds, () => SetScope(DropdownScope.MyBuilds)));
        // Один загрузчик — не фильтр (всё равно будет то же самое): показываем кнопки загрузчиков, когда их хотя бы два.
        if (kinds.Count > 1)
        {
            foreach (var kind in kinds)
                chips.Add(new(LoaderInfo.Name(kind), _scope == DropdownScope.Loader && _loaderFilter == kind, () => SetScope(DropdownScope.Loader, kind)));
        }
        Chips = chips;
    }

    /// <summary>Сборки загрузчиков для выбранной версии игры, которых ещё нет в папке игры.</summary>
    private IReadOnlyList<LoaderBuild> CurrentOffers(IReadOnlyList<InstalledVersion> installed)
    {
        if (!string.Equals(_availableFor, _store.SelectedVersion, StringComparison.Ordinal)) return Array.Empty<LoaderBuild>();
        if (_store.SelectedInstance is not null) return Array.Empty<LoaderBuild>(); // у выбранной вручную сборки свой загрузчик: предложения для обычных версий не нужны

        var installedIds = installed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return _available.Where(b => !installedIds.Contains(b.ProfileId)).ToList();
    }

    private bool IsSelectedOffer(LoaderBuild build) =>
        _store.SelectedInstance is null
        && _store.SelectedInstall is null
        && _store.SelectedLoader == build.Kind
        && string.Equals(_store.SelectedLoaderVersion, build.Version, StringComparison.Ordinal)
        && string.Equals(_store.SelectedVersion, build.McVersion, StringComparison.Ordinal);

    private void PickInstance(Instance build)
    {
        _store.SelectInstance(build);
        _nav.CloseVersionDropdown();
    }

    private void RefreshMyBuilds() => LoadMyBuildsAsync(_lifetime.Token).FireAndForget(_logger, "список моих сборок");

    private async Task LoadMyBuildsAsync(CancellationToken ct)
    {
        IReadOnlyList<Instance> all;
        try
        {
            all = await _instances.ListAsync(ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать список сборок.");
            return;
        }

        _myBuilds = all
            .Where(i => !IsAutoBuild(i))
            .OrderByDescending(i => i.LastPlayedUtc ?? i.CreatedUtc)
            .ToList();

        // Выбранную сборку удалили (например, вручную в папке): возвращаемся к обычной версии.
        if (_store.SelectedInstance is { } selected && all.All(i => i.Id != selected.Id)) _store.ForgetInstance(selected.Id);

        if (_nav.IsVersionDropdownOpen) BuildRows();
    }

    private static bool IsAutoBuild(Instance i) =>
        string.Equals(i.Name.Trim(), GamePlayService.AutoName(i.McVersion, i.Loader), StringComparison.OrdinalIgnoreCase);

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
