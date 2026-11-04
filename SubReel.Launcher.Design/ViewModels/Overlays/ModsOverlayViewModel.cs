using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Строка вкладки «Установленные»: мод сборки, обновление для него (если есть) и кнопки «Обновить» и «Удалить».</summary>
public sealed class InstalledModRow : ObservableObject
{
    private ModUpdateInfo? _update;
    private string _warning = "";
    private bool _confirmForce;

    public InstalledModRow(InstalledMod mod, Func<InstalledModRow, Task> update, Func<InstalledModRow, Task> remove, Func<bool> canRun, Action<Exception> onError)
    {
        Mod = mod;
        UpdateCommand = new AsyncRelayCommand(() => update(this), () => HasUpdate && canRun(), onError);
        RemoveCommand = new AsyncRelayCommand(() => remove(this), canRun, onError);
    }

    public InstalledMod Mod { get; }
    public string ProjectId => Mod.ProjectId;
    public string Title => Mod.Title;
    public string Initial => ModTextHelpers.Initial(Mod.Title);
    public string VersionText => $"{Mod.VersionNumber} · {Mod.FileName}{(Mod.Source == ModSource.CurseForge ? " · CurseForge" : "")}";

    /// <summary>Зависимость, которой больше никто не требует, подписана отдельно: её можно удалить.</summary>
    public string BadgeText => Mod.Reason == ModInstallReason.Dependency ? (Mod.RequiredBy.Count > 0 ? "зависимость" : "больше не нужен") : "";
    public bool HasBadge => BadgeText.Length > 0;

    public ModUpdateInfo? Update
    {
        get => _update;
        set
        {
            if (!SetProperty(ref _update, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is { } u ? $"Обновить до {u.Latest.VersionNumber}" : "";

    /// <summary>Предупреждение под названием: например, какие моды потеряют зависимость при удалении.</summary>
    public string Warning
    {
        get => _warning;
        set { if (SetProperty(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); }
    }

    public bool HasWarning => _warning.Length > 0;

    /// <summary>Первое нажатие «Удалить» предупредило о зависимых модах; второе удалит всё равно.</summary>
    public bool ConfirmForce
    {
        get => _confirmForce;
        set { if (SetProperty(ref _confirmForce, value)) OnPropertyChanged(nameof(RemoveText)); }
    }

    public string RemoveText => ConfirmForce ? "Удалить всё равно" : "Удалить";

    public ICommand UpdateCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>Карточка мода в результатах поиска.</summary>
public sealed class ModCardRow : ObservableObject
{
    private bool _isInstalled;

    public ModCardRow(ModProject project, bool installed, Func<ModCardRow, Task> install, Func<bool> canRun, Action<Exception> onError)
    {
        Project = project;
        _isInstalled = installed;
        InstallCommand = new AsyncRelayCommand(() => install(this), () => !IsInstalled && canRun(), onError);
    }

    public ModProject Project { get; }
    public string Title => Project.Title;
    public string Initial => ModTextHelpers.Initial(Project.Title);
    public string Summary => Project.Summary;
    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "" : $"от {Project.Author}";
    public string DownloadsText => ModTextHelpers.Downloads(Project.Downloads);

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (!SetProperty(ref _isInstalled, value)) return;
            OnPropertyChanged(nameof(InstallText));
        }
    }

    public string InstallText => IsInstalled ? "Установлен" : "Установить";

    public ICommand InstallCommand { get; }
}

internal static class ModTextHelpers
{
    public static string Initial(string title) =>
        string.IsNullOrWhiteSpace(title) ? "•" : title.Trim()[..1].ToUpper(CultureInfo.CurrentCulture);

    /// <summary>1 234 → «1,2 тыс.», 2 500 000 → «2,5 млн».</summary>
    public static string Downloads(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000d:0.#} млн загрузок",
        >= 1_000 => $"{count / 1_000d:0.#} тыс. загрузок",
        _ => $"{count} загрузок",
    };
}

/// <summary>
/// Окно «Моды» выбранной сборки: вкладка «Установленные» (обновить, удалить, зависимости) и «Найти моды» (поиск по Modrinth для версии игры
/// и загрузчика этой сборки). Всё, что меняет файлы, идёт по очереди: пока выполняется одна операция, остальные кнопки неактивны.
/// Закрытие окна отменяет текущую операцию; недоустановленное менеджер модов убирает сам.
/// </summary>
public sealed class ModsOverlayViewModel : OverlayViewModel, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(450);

    private readonly Instance _instance;
    private readonly IModManager _mods;
    private readonly IReadOnlyList<IModCatalog> _catalogs;
    private ModSource _source = ModSource.Modrinth;
    private readonly IToastService _toast;
    private readonly ILogger<ModsOverlayViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCts;
    private HashSet<string> _installedIds = new(StringComparer.Ordinal);
    private bool _isBrowseTab;
    private bool _browsed;
    private bool _isBusy;
    private bool _isLoading = true;
    private bool _isSearching;
    private string _status = "";
    private string _error = "";
    private string _updateNote = "";
    private string _searchText = "";
    private string _searchNote = "";
    private string _unrecognizedNote = "";
    private double _progress;
    private IReadOnlyList<InstalledModRow> _installed = Array.Empty<InstalledModRow>();
    private IReadOnlyList<ModCardRow> _results = Array.Empty<ModCardRow>();

    public ModsOverlayViewModel(Instance instance, IModManager mods, IEnumerable<IModCatalog> catalogs, IToastService toast, ILogger<ModsOverlayViewModel> logger)
    {
        _instance = instance;
        _mods = mods;
        _catalogs = catalogs.ToList();
        _toast = toast;
        _logger = logger;

        ShowInstalledTabCommand = new RelayCommand(() => IsBrowseTab = false);
        ShowBrowseTabCommand = new RelayCommand(OpenBrowseTab);
        SearchCommand = new RelayCommand(() => ScheduleSearch(immediate: true));
        ShowModrinthCommand = new RelayCommand(() => SwitchSource(ModSource.Modrinth));
        ShowCurseForgeCommand = new RelayCommand(() => SwitchSource(ModSource.CurseForge));
        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckUpdatesAsync(_lifetime.Token), () => !IsBusy, OnUnexpectedError);

        LoadInstalledAsync(_lifetime.Token).FireAndForget(_logger, "список модов сборки");
    }

    public override string Title => $"Моды · {_instance.Name}";
    public override double CardWidth => 900;
    public override double CardMaxHeight => 640;

    /// <summary>Моды ставятся в сборки с загрузчиком; у ванили их нет.</summary>
    public static bool Supports(LoaderKind loader) => loader is LoaderKind.Fabric or LoaderKind.Quilt or LoaderKind.Forge or LoaderKind.NeoForge;

    public string Subtitle => $"Minecraft {_instance.McVersion} · {LoaderInfo.Name(_instance.Loader)}";

    public bool IsBrowseTab
    {
        get => _isBrowseTab;
        private set
        {
            if (!SetProperty(ref _isBrowseTab, value)) return;
            OnPropertyChanged(nameof(IsInstalledTab));
        }
    }

    public bool IsInstalledTab => !_isBrowseTab;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(IsEmptyInstalled)); } }

    /// <summary>Что сейчас делает окно: «Скачиваем…», «Удаляем…». Пусто — ничего.</summary>
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    // ───────────────────────── Установленные ─────────────────────────

    public IReadOnlyList<InstalledModRow> Installed
    {
        get => _installed;
        private set
        {
            if (!SetProperty(ref _installed, value)) return;
            OnPropertyChanged(nameof(HasInstalled));
            OnPropertyChanged(nameof(IsEmptyInstalled));
        }
    }

    public bool HasInstalled => _installed.Count > 0;
    public bool IsEmptyInstalled => !_isLoading && _installed.Count == 0;

    /// <summary>Итог проверки обновлений: «Доступно обновлений: 2», «Все моды свежие», «Не удалось проверить».</summary>
    public string UpdateNote { get => _updateNote; private set { if (SetProperty(ref _updateNote, value)) OnPropertyChanged(nameof(HasUpdateNote)); } }
    public bool HasUpdateNote => UpdateNote.Length > 0;

    /// <summary>Файлы в папке mods, которых нет в каталоге: лаунчер их не трогает.</summary>
    public string UnrecognizedNote { get => _unrecognizedNote; private set { if (SetProperty(ref _unrecognizedNote, value)) OnPropertyChanged(nameof(HasUnrecognizedNote)); } }
    public bool HasUnrecognizedNote => UnrecognizedNote.Length > 0;

    public ICommand ShowInstalledTabCommand { get; }
    public ICommand ShowBrowseTabCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand SearchCommand { get; }

    private async Task LoadInstalledAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var listing = await _mods.ListAsync(_instance, ct).ConfigureAwait(true);
            ApplyListing(listing);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }

        if (HasInstalled) await CheckUpdatesAsync(ct).ConfigureAwait(true);
    }

    private void ApplyListing(ModListing listing)
    {
        _installedIds = listing.Mods.Select(m => m.ProjectId).ToHashSet(StringComparer.Ordinal);
        Installed = listing.Mods
            .Select(m => new InstalledModRow(m, UpdateModAsync, RemoveModAsync, () => !IsBusy, OnUnexpectedError))
            .ToList();
        UnrecognizedNote = listing.Unrecognized.Count == 0
            ? ""
            : $"Файлов, которых нет в каталоге: {listing.Unrecognized.Count}. Лаунчер их не трогает ({string.Join(", ", listing.Unrecognized.Take(3))}{(listing.Unrecognized.Count > 3 ? "…" : "")}).";

        foreach (var card in _results) card.IsInstalled = _installedIds.Contains(card.Project.Id);
        OnPropertyChanged(nameof(HasInstalled));
    }

    private async Task CheckUpdatesAsync(CancellationToken ct)
    {
        UpdateNote = "Проверяем обновления…";
        try
        {
            var updates = await _mods.CheckUpdatesAsync(_instance, ct).ConfigureAwait(true);
            foreach (var row in Installed) row.Update = updates.FirstOrDefault(u => u.Installed.ProjectId == row.ProjectId);
            UpdateNote = updates.Count == 0 ? "Все моды свежие." : $"Доступно обновлений: {updates.Count}.";
            CommandManager.InvalidateRequerySuggested();
        }
        catch (ModCatalogException ex)
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogInformation("Обновления модов не проверены: {Message}", ex.Message);
            UpdateNote = "Не удалось проверить обновления: " + ex.Message;
        }
    }

    private async Task UpdateModAsync(InstalledModRow row)
    {
        if (!BeginOperation($"Обновляем «{row.Title}»…")) return;
        try
        {
            var result = await _mods.UpdateAsync(_instance, row.ProjectId, ProgressReporter(), _lifetime.Token).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                Error = result.Error ?? "Не удалось обновить мод.";
                return;
            }

            _toast.Show(result.Installed.Count > 1 ? $"«{row.Title}» обновлён, добавлено зависимостей: {result.Installed.Count - 1}" : $"«{row.Title}» обновлён");
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        finally { EndOperation(); }
    }

    private async Task RemoveModAsync(InstalledModRow row)
    {
        if (!BeginOperation($"Удаляем «{row.Title}»…")) return;
        try
        {
            var result = await _mods.RemoveAsync(_instance, row.ProjectId, force: row.ConfirmForce, removeOrphans: false, _lifetime.Token).ConfigureAwait(true);

            if (result.Blocked.Count > 0)
            {
                row.Warning = $"Нужен модам: {string.Join(", ", result.Blocked)}. Без него они могут не запуститься.";
                row.ConfirmForce = true;
                return;
            }

            if (!result.Removed)
            {
                Error = result.Error ?? "Не удалось удалить мод.";
                return;
            }

            _toast.Show(result.Orphans.Count > 0
                ? $"«{row.Title}» удалён. Больше не нужны: {string.Join(", ", result.Orphans.Select(o => o.Title))}"
                : $"«{row.Title}» удалён", 3800);
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        finally { EndOperation(); }
    }

    // ───────────────────────── Поиск ─────────────────────────

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ScheduleSearch(immediate: false);
        }
    }

    public string SearchHint => $"{(_source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}: моды для {Subtitle}";

    public bool IsModrinthSource => _source == ModSource.Modrinth;
    public bool IsCurseForgeSource => _source == ModSource.CurseForge;
    public ICommand ShowModrinthCommand { get; }
    public ICommand ShowCurseForgeCommand { get; }

    private void SwitchSource(ModSource source)
    {
        if (_source == source) return;
        _source = source;
        OnPropertyChanged(nameof(IsModrinthSource));
        OnPropertyChanged(nameof(IsCurseForgeSource));
        OnPropertyChanged(nameof(SearchHint));
        Results = Array.Empty<ModCardRow>();
        ScheduleSearch(immediate: true);
    }

    public IReadOnlyList<ModCardRow> Results
    {
        get => _results;
        private set
        {
            if (!SetProperty(ref _results, value)) return;
            OnPropertyChanged(nameof(HasResults));
        }
    }

    public bool HasResults => _results.Count > 0;
    public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
    public string SearchNote { get => _searchNote; private set { if (SetProperty(ref _searchNote, value)) OnPropertyChanged(nameof(HasSearchNote)); } }
    public bool HasSearchNote => SearchNote.Length > 0;

    private void OpenBrowseTab()
    {
        IsBrowseTab = true;
        if (_browsed) return;
        _browsed = true;
        ScheduleSearch(immediate: true); // сразу показываем популярное, не дожидаясь запроса
    }

    private void ScheduleSearch(bool immediate)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;
        _browsed = true;
        SearchAsync(immediate, cts.Token).FireAndForget(_logger, "поиск модов");
    }

    private async Task SearchAsync(bool immediate, CancellationToken ct)
    {
        if (!immediate) await Task.Delay(SearchDelay, ct).ConfigureAwait(true); // ждём, пока пользователь допечатает
        IsSearching = true;
        try
        {
            var catalog = _catalogs.FirstOrDefault(c => c.Source == _source) ?? throw new ModCatalogException("Этот источник модов недоступен.");
            var result = await catalog.SearchAsync(new ModSearchQuery(SearchText, _instance.McVersion, _instance.Loader), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            Results = result.Hits
                .Select(p => new ModCardRow(p, _installedIds.Contains(p.Id), InstallModAsync, () => !IsBusy, OnUnexpectedError))
                .ToList();
            SearchNote = result.Hits.Count == 0
                ? "Ничего не найдено. Попробуй другое название."
                : result.TotalHits > result.Hits.Count
                    ? $"Показаны первые {result.Hits.Count} из {result.TotalHits}. Уточни запрос, чтобы найти нужный мод."
                    : "";
        }
        catch (ModCatalogException ex)
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogInformation("Поиск модов не удался: {Message}", ex.Message);
            Results = Array.Empty<ModCardRow>();
            SearchNote = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    // ───────────────────────── Установка ─────────────────────────

    private async Task InstallModAsync(ModCardRow card)
    {
        if (!BeginOperation($"Подбираем версию «{card.Title}»…")) return;
        try
        {
            var ct = _lifetime.Token;
            var plan = await _mods.PlanInstallAsync(_instance, card.Project.Id, _source, ct).ConfigureAwait(true);
            if (!plan.CanInstall)
            {
                Error = plan.Error ?? $"«{card.Title}» несовместим с установленными модами: {string.Join(", ", plan.Conflicts)}.";
                return;
            }

            var dependencies = plan.Items.Where(i => i.Reason == ModInstallReason.Dependency).Select(i => i.Project.Title).ToList();
            Status = dependencies.Count == 0 ? $"Скачиваем «{card.Title}»…" : $"Скачиваем «{card.Title}» и зависимости: {string.Join(", ", dependencies)}…";

            var result = await _mods.InstallAsync(_instance, plan, ProgressReporter(), ct).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                Error = result.Error ?? "Не удалось установить мод.";
                return;
            }

            card.IsInstalled = true;
            _toast.Show(dependencies.Count == 0 ? $"«{card.Title}» установлен" : $"«{card.Title}» установлен вместе с: {string.Join(", ", dependencies)}", 4200);
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        finally { EndOperation(); }
    }

    // ───────────────────────── Общее ─────────────────────────

    private Progress<double> ProgressReporter() => new(v => Progress = Math.Round(Math.Clamp(v, 0, 1) * 100));

    private bool BeginOperation(string status)
    {
        if (IsBusy) return false;
        Error = "";
        Progress = 0;
        Status = status;
        IsBusy = true;
        return true;
    }

    private void EndOperation()
    {
        Status = "";
        Progress = 0;
        IsBusy = false;
    }

    private async Task ReloadAfterChangeAsync()
    {
        var listing = await _mods.ListAsync(_instance, _lifetime.Token).ConfigureAwait(true);
        ApplyListing(listing);
        await CheckUpdatesAsync(_lifetime.Token).ConfigureAwait(true);
    }

    private void OnUnexpectedError(Exception ex)
    {
        _logger.LogError(ex, "Операция с модами завершилась ошибкой.");
        EndOperation();
        Error = "Не удалось выполнить действие. Подробности — в журнале.";
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _searchCts?.Dispose();
        _lifetime.Dispose();
    }
}
