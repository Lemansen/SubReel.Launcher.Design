using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Updates;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>
/// Автообновление лаунчера в интерфейсе: баннер сверху справа и раздел «О лаунчере» в настройках — одна ViewModel на оба места.
/// Состояние берётся из <see cref="ILauncherUpdateService"/>; его события приходят из фонового потока и переносятся в поток интерфейса.
/// </summary>
public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherUpdateService _updates;
    private readonly NavigationService _nav;
    private readonly IToastService _toast;
    private readonly IAppShutdown _shutdown;
    private readonly ILogger<UpdateViewModel> _logger;
    private readonly TimeSpan _interval;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current; // создаётся в потоке интерфейса
    private readonly CancellationTokenSource _lifetime = new();
    private double _progress;
    private string _stageText = "";
    private string? _applyError;
    private bool _isInstalling;
    private string? _bannerShownFor;   // для какой версии баннер уже показывали
    private bool _bannerAutoHidden;    // баннер убран по таймеру: об обновлении остаётся напоминание в настройках («О лаунчере»)

    /// <summary>Сколько баннер висит после того, как найдено новое обновление. Обязательные обновления не скрываются.</summary>
    private static readonly TimeSpan BannerLifetime = TimeSpan.FromSeconds(15);

    public UpdateViewModel(ILauncherUpdateService updates, NavigationService nav, IToastService toast, IAppShutdown shutdown,
        IOptions<UpdateOptions> options, ILogger<UpdateViewModel> logger)
    {
        _updates = updates;
        _nav = nav;
        _toast = toast;
        _shutdown = shutdown;
        _logger = logger;
        _interval = TimeSpan.FromHours(Math.Clamp(options.Value.CheckIntervalHours, 0.25, 168));

        updates.StateChanged += OnServiceChanged;
        nav.PropertyChanged += OnNavChanged;

        DownloadCommand = new AsyncRelayCommand(InstallAsync, () => _updates.Available is not null && !_isInstalling, OnCommandError);
        PostponeCommand = new RelayCommand(Postpone, () => _updates.Available is { Mandatory: false } && !_isInstalling);
        CheckCommand = new AsyncRelayCommand(() => _updates.CheckAsync(_lifetime.Token), () => _updates.IsConfigured && !_updates.IsChecking && !_isInstalling, OnCommandError);
    }

    public ICommand DownloadCommand { get; }
    public ICommand PostponeCommand { get; }
    public ICommand CheckCommand { get; }

    // ───────────────────────── Баннер ─────────────────────────

    /// <summary>Баннер виден, когда есть неотложенная версия и поверх главного экрана ничего не открыто.</summary>
    public bool IsBannerVisible => ((_updates.IsBannerVisible && !_bannerAutoHidden) || _isInstalling) && !_nav.IsChromeHidden;

    public string BannerTitle => _updates.Available is { } info ? $"Доступна версия {info.Version}" : "";
    public string BannerDetail => _updates.Available is { } info
        ? $"Размер: {UpdateRules.FormatSize(info.SizeBytes)}" + (info.Mandatory ? " · обновление обязательное" : "")
        : "";

    public bool CanPostpone => _updates.Available is { Mandatory: false } && !_isInstalling;
    public bool IsInstalling => _isInstalling;
    public bool IsIdle => !_isInstalling;

    /// <summary>0..100 — под шкалу <c>LaunchProgressBar</c>.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string StageText { get => _stageText; private set => SetProperty(ref _stageText, value); }

    public string? ApplyError
    {
        get => _applyError;
        private set
        {
            if (SetProperty(ref _applyError, value)) OnPropertyChanged(nameof(HasApplyError));
        }
    }

    public bool HasApplyError => !string.IsNullOrEmpty(_applyError);

    // ───────────────────────── Раздел «О лаунчере» ─────────────────────────

    public string CurrentVersionText => $"Версия {_updates.CurrentVersion}";

    public string StatusText
    {
        get
        {
            if (!_updates.IsConfigured) return "Проверка обновлений не подключена: в appsettings.json не задан адрес манифеста (Updates:ManifestUrl).";
            if (_updates.IsChecking) return "Проверяю обновления…";
            if (_updates.Available is { } info) return $"Доступна версия {info.Version}" + (info.Mandatory ? " (обязательная)." : ".");
            if (_updates.LastError is { } error) return error;
            return _updates.LastCheckedUtc is { } checkedAt
                ? $"У тебя последняя версия. Проверено в {checkedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}."
                : "Ещё не проверялось.";
        }
    }

    public string ReleaseNotes => _updates.Available?.ReleaseNotes ?? "";
    public bool HasReleaseNotes => !string.IsNullOrEmpty(ReleaseNotes);
    public bool HasUpdate => _updates.Available is not null;
    public bool IsConfigured => _updates.IsConfigured;

    // ───────────────────────── Работа ─────────────────────────

    /// <summary>Фоновая проверка: сразу при запуске и затем раз в несколько часов, пока лаунчер открыт.</summary>
    public async Task RunPeriodicChecksAsync(CancellationToken ct)
    {
        if (!_updates.IsConfigured) return;

        using var timer = new PeriodicTimer(_interval);
        do
        {
            await _updates.CheckAsync(ct).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private void Postpone()
    {
        _updates.Postpone();
        _toast.Show("Напомню об обновлении позже");
    }

    private async Task InstallAsync()
    {
        _isInstalling = true;
        ApplyError = null;
        Progress = 0;
        StageText = "Подготовка…";
        RefreshAll();

        try
        {
            var progress = new System.Progress<UpdateProgress>(OnProgress); // создан в потоке интерфейса: отчёты приходят в него же
            var result = await _updates.ApplyAsync(progress, _lifetime.Token);
            if (result.Succeeded)
            {
                StageText = "Перезапускаю лаунчер…";
                _logger.LogInformation("Обновление подготовлено: лаунчер закрывается.");
                _shutdown.Shutdown();
                return;
            }

            ApplyError = result.Error;
        }
        finally
        {
            _isInstalling = false;
            RefreshAll();
        }
    }

    private void OnProgress(UpdateProgress p)
    {
        (StageText, Progress) = p.Stage switch
        {
            UpdateStage.Downloading => ($"Скачиваю… {p.Fraction:P0}", p.Fraction * 85),
            UpdateStage.Verifying => ("Проверяю файл…", 88),
            UpdateStage.Unpacking => ("Распаковываю…", 94),
            _ => ("Запускаю установку…", 100),
        };
    }

    private void OnCommandError(Exception ex)
    {
        _logger.LogError(ex, "Сбой при работе с обновлением лаунчера.");
        ApplyError = "Не удалось выполнить действие. Подробности — в журнале лаунчера.";
        _isInstalling = false;
        RefreshAll();
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_ui is null) RefreshAll();
        else _ui.Post(_ => RefreshAll(), null);
    }

    private void OnNavChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(NavigationService.IsChromeHidden)) OnPropertyChanged(nameof(IsBannerVisible));
    }

    private void RefreshAll()
    {
        TrackBannerLifetime();
        OnPropertyChanged(string.Empty); // «изменились все свойства»: баннер и раздел «О лаунчере» обновятся разом
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Баннер появляется, когда найдена новая версия, и сам исчезает через несколько секунд (кроме обязательных обновлений и самой установки).
    /// Для следующей, ещё более новой версии он появится снова; найденное обновление всегда видно в настройках в разделе «О лаунчере».
    /// </summary>
    private void TrackBannerLifetime()
    {
        var version = _updates.Available?.Version.ToString();
        if (version is null || version == _bannerShownFor) return;

        _bannerShownFor = version;
        _bannerAutoHidden = false;
        if (_updates.Available is { Mandatory: true }) return;

        _ = HideBannerLaterAsync(version);
    }

    private async Task HideBannerLaterAsync(string version)
    {
        try
        {
            await Task.Delay(BannerLifetime, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        void Hide()
        {
            if (_bannerShownFor != version || _isInstalling) return;
            _bannerAutoHidden = true;
            OnPropertyChanged(nameof(IsBannerVisible));
        }

        if (_ui is null) Hide();
        else _ui.Post(_ => Hide(), null);
    }

    public void Dispose()
    {
        _updates.StateChanged -= OnServiceChanged;
        _nav.PropertyChanged -= OnNavChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
