using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Application.Modpacks;
using SubReel.Launcher.Domain.Instances;
using System.Windows.Input;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>
/// Страница «Создать сборку» (полноэкранный раздел, как «Новости» и «Все версии»). Первый экран — карточки источников
/// (своя сборка, Modrinth, CurseForge, импорт файла), второй — форма своей сборки, которая создаёт настоящий экземпляр.
/// Остальные источники появятся в фазе 11 и пока показаны как «Скоро» без кнопок, чтобы не выглядеть рабочими.
/// </summary>
public sealed class BuildsViewModel : ObservableObject
{
    private const string DefaultName = "Моя сборка";

    private readonly NavigationService _nav;
    private readonly InstanceService _instances;
    private readonly VersionStore _versions;
    private readonly ILoaderCatalog _loaders;
    private readonly IToastService _toast;
    private readonly IModpackImporter _importer;
    private readonly ICurseForgeImporter _curseForgeImporter;
    private readonly IDialogService _dialogs;
    private readonly ILogger<BuildsViewModel> _logger;
    private CancellationTokenSource? _importCts;
    private bool _isImporting;
    private double _importProgress;
    private string _importStatus = "";
    private string _importError = "";
    private bool _isFormOpen;
    private bool _isBusy;
    private string _name = DefaultName;
    private IReadOnlyList<string> _mcVersions = Array.Empty<string>();
    private string? _selectedMcVersion;
    private LoaderKind _selectedLoader = LoaderKind.Fabric;
    private string _status = "";
    private CancellationTokenSource? _refreshCts;
    private IReadOnlyList<LoaderBuild> _loaderBuilds = Array.Empty<LoaderBuild>();
    private LoaderBuild? _selectedBuild;
    private bool _isLoadingBuilds;
    private string _buildsNote = "";

    public BuildsViewModel(NavigationService nav, InstanceService instances, VersionStore versions, ILoaderCatalog loaders,
        IToastService toast, IModpackImporter importer, ICurseForgeImporter curseForgeImporter, IDialogService dialogs, ILogger<BuildsViewModel> logger)
    {
        _importer = importer;
        _curseForgeImporter = curseForgeImporter;
        _dialogs = dialogs;
        _nav = nav;
        _instances = instances;
        _versions = versions;
        _loaders = loaders;
        _toast = toast;
        _logger = logger;

        StartCustomCommand = new RelayCommand(() => IsFormOpen = true);
        BackCommand = new RelayCommand(GoBack);
        ClearNameCommand = new RelayCommand(() => Name = "");
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy, OnUnexpectedError);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy && !IsImporting, OnImportUnexpectedError);
        CancelImportCommand = new RelayCommand(() => _importCts?.Cancel());

        // Каждое открытие страницы начинается с карточек и свежих значений формы.
        nav.BuildsOpened += (_, _) => Reset();
    }

    public string Subtitle => IsFormOpen ? "Настрой версию игры и загрузчик" : "Выбери, как создать сборку";

    public bool IsFormOpen
    {
        get => _isFormOpen;
        private set
        {
            if (!SetProperty(ref _isFormOpen, value)) return;
            OnPropertyChanged(nameof(IsSourcesOpen));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public bool IsSourcesOpen => !_isFormOpen;

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(NameCounter));
            OnPropertyChanged(nameof(HasName));
        }
    }
    public IReadOnlyList<string> McVersions { get => _mcVersions; private set => SetProperty(ref _mcVersions, value); }
    public string? SelectedMcVersion
    {
        get => _selectedMcVersion;
        set
        {
            if (SetProperty(ref _selectedMcVersion, value)) RefreshBuilds();
        }
    }
    public IReadOnlyList<LoaderKind> Loaders { get; } = new[] { LoaderKind.Vanilla, LoaderKind.Forge, LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.NeoForge };
    public LoaderKind SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (!SetProperty(ref _selectedLoader, value)) return;
            OnPropertyChanged(nameof(LoaderHint));
            OnPropertyChanged(nameof(HasLoaderVersions));
            RefreshBuilds();
        }
    }

    /// <summary>Счётчик под названием: «10/64».</summary>
    public string NameCounter => $"{Name.Length}/{InstanceRules.MaxNameLength}";
    public int MaxNameLength => InstanceRules.MaxNameLength;
    public bool HasName => Name.Length > 0;

    /// <summary>Сборки выбранного загрузчика для выбранной версии игры (новые первыми).</summary>
    public IReadOnlyList<LoaderBuild> LoaderBuilds { get => _loaderBuilds; private set => SetProperty(ref _loaderBuilds, value); }
    public LoaderBuild? SelectedBuild { get => _selectedBuild; set => SetProperty(ref _selectedBuild, value); }
    public bool HasLoaderVersions => SelectedLoader != LoaderKind.Vanilla;
    public bool IsLoadingBuilds { get => _isLoadingBuilds; private set => SetProperty(ref _isLoadingBuilds, value); }

    /// <summary>Пояснение под списком версий загрузчика: нет сборок для этой версии игры, нет сети.</summary>
    public string BuildsNote
    {
        get => _buildsNote;
        private set { if (SetProperty(ref _buildsNote, value)) OnPropertyChanged(nameof(HasBuildsNote)); }
    }

    public bool HasBuildsNote => BuildsNote.Length > 0;

    public string LoaderHint => SelectedLoader switch
    {
        LoaderKind.Vanilla => "Чистая игра без модов.",
        LoaderKind.Fabric => "Лёгкий загрузчик, быстро получает новые версии игры.",
        LoaderKind.Quilt => "Ответвление Fabric, совместимо с большинством его модов.",
        LoaderKind.Forge => "Классика для больших модпаков. Поддерживается 1.12.2 и новее.",
        LoaderKind.NeoForge => "Современный преемник Forge для свежих версий игры.",
        _ => "",
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CreateText));
        }
    }

    public string CreateText => IsBusy ? "Создаём..." : "Создать сборку";
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;

    public ICommand StartCustomCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ClearNameCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelImportCommand { get; }

    // ───────────────────────── Импорт из файла (.mrpack, .zip CurseForge) ─────────────────────────

    public bool IsImporting { get => _isImporting; private set { if (SetProperty(ref _isImporting, value)) OnPropertyChanged(nameof(IsImportCardVisible)); } }
    public double ImportProgress { get => _importProgress; private set => SetProperty(ref _importProgress, value); }
    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }
    public string ImportError
    {
        get => _importError;
        private set { if (SetProperty(ref _importError, value)) { OnPropertyChanged(nameof(HasImportError)); OnPropertyChanged(nameof(IsImportCardVisible)); } }
    }
    public bool HasImportError => ImportError.Length > 0;

    /// <summary>Карточка под источниками: ход импорта или причина, по которой он не удался.</summary>
    public bool IsImportCardVisible => IsImporting || HasImportError;

    private async Task ImportAsync()
    {
        var path = _dialogs.PickModpackFile();
        if (path is null) return;

        using var cts = new CancellationTokenSource();
        _importCts = cts;
        ImportError = "";
        ImportProgress = 0;
        ImportStatus = "Читаем сборку…";
        IsImporting = true;
        try
        {
            // Progress<T> создан в потоке интерфейса и доставляет отчёты туда же.
            var progress = new Progress<ModpackImportProgress>(p =>
            {
                if (!IsImporting) return;
                ImportProgress = Math.Round(Math.Clamp(p.Fraction, 0, 1) * 100);
                ImportStatus = p.Stage switch
                {
                    ModpackImportStage.CreatingBuild => "Создаём сборку…",
                    ModpackImportStage.DownloadingFiles => "Скачиваем моды и файлы…",
                    ModpackImportStage.CheckingFiles => "Проверяем файлы…",
                    ModpackImportStage.ExtractingFiles => "Раскладываем настройки…",
                    _ => "Читаем сборку…",
                };
            });

            // .zip — сборка CurseForge (manifest.json), остальное — Modrinth (.mrpack).
            var result = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? await _curseForgeImporter.ImportAsync(path, progress, cts.Token).ConfigureAwait(true)
                : await _importer.ImportMrpackAsync(path, progress, cts.Token).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ImportError = result.Error ?? "Не удалось импортировать сборку.";
                return;
            }

            // Как и созданная вручную, импортированная сборка сразу выбирается: «Играть» запустит её.
            var imported = result.Instance!;
            _versions.SelectInstance(imported);
            _toast.Show($"Сборка «{imported.Name}» импортирована и выбрана");
            _nav.CloseBuilds();
        }
        catch (OperationCanceledException)
        {
            ImportError = "Импорт отменён. Недоделанная сборка удалена.";
        }
        finally
        {
            IsImporting = false;
            _importCts = null;
        }
    }

    private void OnImportUnexpectedError(Exception ex)
    {
        _logger.LogError(ex, "Импорт сборки завершился ошибкой.");
        IsImporting = false;
        ImportError = "Не удалось импортировать сборку. Подробности — в журнале.";
    }

    /// <summary>Esc / «Назад»: из формы — к карточкам, с карточек — закрыть страницу. Пока идёт создание, ничего не делает.</summary>
    public void GoBack()
    {
        if (IsBusy || IsImporting) return; // во время импорта страницу закрывает только «Отмена»
        if (IsFormOpen)
        {
            IsFormOpen = false;
            Status = "";
        }
        else _nav.CloseBuilds();
    }

    private void Reset()
    {
        IsFormOpen = false;
        IsBusy = false;
        Status = "";
        ImportError = "";
        Name = DefaultName;
        SelectedLoader = LoaderKind.Fabric;
        McVersions = _versions.Catalog.Releases.Select(r => r.Id).ToList();
        SelectedMcVersion = McVersions.Contains(_versions.SelectedVersion) ? _versions.SelectedVersion : McVersions.FirstOrDefault();
        RefreshBuilds();
    }

    private void RefreshBuilds()
    {
        _refreshCts?.Cancel();
        LoaderBuilds = Array.Empty<LoaderBuild>();
        SelectedBuild = null;
        BuildsNote = "";
        if (SelectedLoader == LoaderKind.Vanilla || string.IsNullOrWhiteSpace(SelectedMcVersion))
        {
            _refreshCts = null;
            IsLoadingBuilds = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        LoadBuildsAsync(SelectedLoader, SelectedMcVersion, cts.Token).FireAndForget(_logger, "список сборок загрузчика");
    }

    private async Task LoadBuildsAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        IsLoadingBuilds = true;
        try
        {
            var builds = await _loaders.GetBuildsAsync(kind, mcVersion, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            LoaderBuilds = builds;
            SelectedBuild = builds.FirstOrDefault(b => b.Stable) ?? builds.FirstOrDefault();
            if (builds.Count == 0) BuildsNote = $"Для Minecraft {mcVersion} нет сборок {LoaderInfo.Name(kind)}. Выбери другую версию игры.";
        }
        catch (LoaderCatalogException ex)
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogWarning(ex, "Каталог {Loader} недоступен.", kind);
            BuildsNote = $"Не удалось получить список {LoaderInfo.Name(kind)}. Проверь подключение к интернету.";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingBuilds = false;
        }
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedMcVersion))
        {
            Status = "Выбери версию игры.";
            return;
        }

        IsBusy = true;
        Status = "";
        try
        {
            // Версию загрузчика выбрал пользователь (по умолчанию — последняя стабильная); у ванили её нет.
            string? loaderVersion = null;
            if (SelectedLoader != LoaderKind.Vanilla)
            {
                if (SelectedBuild is null)
                {
                    Status = IsLoadingBuilds ? "Список версий загрузчика ещё загружается." : HasBuildsNote ? BuildsNote : "Выбери версию загрузчика.";
                    return;
                }
                loaderVersion = SelectedBuild.Version;
            }

            var result = await _instances.CreateAsync(Name, SelectedMcVersion, SelectedLoader, loaderVersion).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                Status = Describe(result);
                return;
            }

            // Созданная сборка сразу выбирается: «Играть» запустит именно её.
            var created = result.Instance!;
            _versions.SelectInstance(created);
            _toast.Show($"Сборка «{created.Name}» создана и выбрана");
            _nav.CloseBuilds();
        }
        finally { IsBusy = false; }
    }

    private static string Describe(InstanceOperationResult result) => result.StorageFailed
        ? "Не удалось сохранить сборку. Проверь, что папка данных лаунчера доступна для записи."
        : result.Error switch
        {
            InstanceValidationError.NameEmpty => "Введи название сборки.",
            InstanceValidationError.NameTooLong => "Название слишком длинное.",
            InstanceValidationError.NameDuplicate => "Сборка с таким названием уже есть.",
            InstanceValidationError.McVersionEmpty => "Выбери версию игры.",
            _ => "Не удалось создать сборку.",
        };

    private void OnUnexpectedError(Exception ex)
    {
        _logger.LogError(ex, "Создание сборки завершилось ошибкой.");
        IsBusy = false;
        Status = "Не удалось создать сборку. Подробности — в журнале.";
    }
}
