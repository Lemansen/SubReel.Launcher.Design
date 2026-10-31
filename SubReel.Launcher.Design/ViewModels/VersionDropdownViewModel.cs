using System.Windows.Input;

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

/// <summary>Кнопка «Версия» в подвале и выпадающий список установленных версий над ней.</summary>
public sealed class VersionDropdownViewModel : ObservableObject
{
    private readonly VersionStore _store;
    private readonly NavigationService _nav;

    private IReadOnlyList<object> _rows = Array.Empty<object>();
    private string _statusText = "";
    private bool _showMissingNotice;
    private string _missingNoticeText = "";
    private bool _showEmptyState;
    private string _emptyHint = "";

    public VersionDropdownViewModel(VersionStore store, NavigationService nav)
    {
        _store = store;
        _nav = nav;
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
        };
    }

    public ICommand ToggleCommand { get; }
    public ICommand ShowAllCommand { get; }

    // ───────────────────────── Кнопка «Версия» ─────────────────────────

    public string VersionLabel => _store.VersionLabel;

    /// <summary>До окончания сканирования индикатор скрыт — не гадаем.</summary>
    public bool ShowStatusDot => _store.InstalledScanned && !string.IsNullOrEmpty(_store.SelectedVersion);

    public bool IsSelectedInstalled => _store.SelectedInstall is not null;

    public string? StatusToolTip => !ShowStatusDot ? null
        : IsSelectedInstalled ? "Версия установлена" : "Версия не установлена — запустить её пока нельзя";

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

        // Выбранная версия не установлена: говорим об этом прямо, а не выдаём её за готовую к запуску.
        ShowMissingNotice = _store.SelectedInstall is null && !string.IsNullOrEmpty(_store.SelectedVersion);
        MissingNoticeText = $"Выбрана {_store.VersionLabel} — не установлена";

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
        Rows = rows;
    }

    private void PickInstalled(InstalledVersion version)
    {
        _store.Select(version.McVersion, version);
        _nav.CloseVersionDropdown();
    }
}
