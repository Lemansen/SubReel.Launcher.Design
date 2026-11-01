using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Games;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Idle — можно играть; Loading — идёт подготовка (сессия, файлы, Java); Running — игра запущена.</summary>
public enum LaunchState { Idle, Loading, Running }

/// <summary>
/// Кнопка «Играть» и индикатор подготовки рядом с ней. Подготовку и запуск делает <see cref="GamePlayService"/>
/// (своя сборка на версию, общий кэш файлов); здесь только отображение этапов, ошибок и состояния «игра запущена».
/// Пока игра работает, кнопка открывает окно с остановкой: случайный клик по большой кнопке не должен обрывать игру.
/// </summary>
public sealed class LaunchViewModel : ObservableObject, IDisposable
{
    private const string IdleStatus = "Подготовка...";
    private const string IdlePlayText = "ИГРАТЬ";
    private const int CrashOutputLines = 12;

    private readonly VersionStore _store;
    private readonly AccountService _accounts;
    private readonly GamePlayService _play;
    private readonly NavigationService _nav;
    private readonly IToastService _toast;
    private readonly ILogger<LaunchViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current; // ViewModel создаётся в потоке интерфейса

    private LaunchState _state;
    private string _playText = IdlePlayText;
    private double _progress;
    private string _progressText = "0%";
    private string _statusText = IdleStatus;
    private bool _isProgressExpanded;
    private IGameProcess? _process;
    private string _runningLabel = "";

    public LaunchViewModel(
        VersionStore store, AccountService accounts, GamePlayService play, NavigationService nav, IToastService toast, ILogger<LaunchViewModel> logger)
    {
        _store = store;
        _accounts = accounts;
        _play = play;
        _nav = nav;
        _toast = toast;
        _logger = logger;
        PlayCommand = new AsyncRelayCommand(PlayAsync, onError: OnPlayFailed);

        // Выбрали другую версию — подпись подготовки больше не верна (если ничего не идёт и не запущено).
        store.VersionSelected += (_, _) => ResetIfIdle();
    }

    public LaunchState State { get => _state; private set => SetProperty(ref _state, value); }
    public string PlayText { get => _playText; private set => SetProperty(ref _playText, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>Панель прогресса раскрыта между левыми кнопками и «Играть» (окно анимирует раскрытие).</summary>
    public bool IsProgressExpanded { get => _isProgressExpanded; private set => SetProperty(ref _isProgressExpanded, value); }

    public ICommand PlayCommand { get; }

    public void Dispose()
    {
        _lifetime.Cancel(); // подготовка прерывается; уже запущенная игра остаётся работать сама по себе
        _lifetime.Dispose();
        if (_process is { } process) process.StatusChanged -= OnProcessStatusChanged;
    }

    private void OnPlayFailed(Exception ex)
    {
        _logger.LogError(ex, "Сбой при запуске версии {Version}.", _store.SelectedVersion);
        ToIdle();
        _toast.Show("Не удалось запустить игру. Подробности в журнале.");
    }

    private void ResetIfIdle()
    {
        if (State == LaunchState.Idle) ToIdle();
    }

    private void ToIdle()
    {
        State = LaunchState.Idle;
        PlayText = IdlePlayText;
        IsProgressExpanded = false;
        Progress = 0;
        ProgressText = "0%";
        StatusText = IdleStatus;
    }

    private async Task PlayAsync()
    {
        if (State == LaunchState.Loading) return;

        if (State == LaunchState.Running)
        {
            ShowStopOffer();
            return;
        }

        var version = _store.SelectedVersion;
        if (string.IsNullOrEmpty(version)) return; // каталог версий ещё не загружен

        var label = _store.VersionLabel;
        var loader = _store.SelectedLoader;
        var loaderVersion = _store.SelectedLoaderVersion;

        State = LaunchState.Loading;
        IsProgressExpanded = true;
        PlayText = "ЗАГРУЗКА...";
        Progress = 0;
        ProgressText = "0%";
        StatusText = IdleStatus;

        var started = false;
        try
        {
            // Progress<T> доставляет отчёты в поток интерфейса, но с задержкой: запоздавший отчёт после старта игнорируется.
            var progress = new Progress<LaunchProgress>(ShowProgress);
            var result = await _play.PlayAsync(new PlayRequest(version, loader, _accounts.Active, loaderVersion), progress, _lifetime.Token);

            if (result.Launch.Process is { } process)
            {
                started = true;
                OnStarted(process, label);
            }
            else
            {
                ShowFailure(result.Launch, label);
            }
        }
        catch (OperationCanceledException)
        {
            // лаунчер закрывается — подготовка прервана, сообщать нечего
        }
        finally
        {
            if (!started) ToIdle();
        }
    }

    private void ShowProgress(LaunchProgress p)
    {
        if (State != LaunchState.Loading) return;

        var percent = Math.Clamp((int)Math.Round(p.Fraction * 100), 0, 100);
        Progress = percent;
        ProgressText = $"{percent}%";
        StatusText = p.Stage switch
        {
            LaunchStage.SigningIn => "Проверка аккаунта...",
            LaunchStage.PreparingFiles => "Проверка файлов...",
            LaunchStage.InstallingFiles => "Загрузка файлов игры...",
            LaunchStage.PreparingJava => "Подготовка Java...",
            LaunchStage.PreparingNatives => "Подготовка библиотек...",
            LaunchStage.Starting => "Запуск...",
            LaunchStage.PreparingLoader => "Подготовка загрузчика...",
            _ => IdleStatus,
        };
    }

    private void OnStarted(IGameProcess process, string label)
    {
        _process = process;
        _runningLabel = label;
        State = LaunchState.Running;
        PlayText = "ИГРА ЗАПУЩЕНА";
        Progress = 100;
        ProgressText = "100%";
        StatusText = $"Minecraft {label} · запущена";

        process.StatusChanged += OnProcessStatusChanged;
        // Игра могла закрыться до подписки (мгновенный сбой): событие уже прошло.
        if (process.Status != GameStatus.Running) OnProcessStatusChanged(process, EventArgs.Empty);
    }

    /// <summary>Событие приходит из потока процесса: дальше работаем только в потоке интерфейса.</summary>
    private void OnProcessStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not IGameProcess process) return;
        if (_ui is null) OnProcessChangedOnUi(process);
        else _ui.Post(_ => OnProcessChangedOnUi(process), null);
    }

    private void OnProcessChangedOnUi(IGameProcess process)
    {
        if (!ReferenceEquals(_process, process) || process.Status == GameStatus.Running) return;

        process.StatusChanged -= OnProcessStatusChanged;
        _process = null;
        ToIdle();

        if (process.Status == GameStatus.Crashed) ShowCrash(process);
    }

    private void ShowStopOffer()
    {
        _nav.ShowOverlay(new MessageOverlayViewModel("Игра запущена", new[]
        {
            new InfoBlockItem($"Minecraft {_runningLabel}", "Игра работает. Закрой её окно как обычно или останови отсюда: несохранённый прогресс в мире будет потерян."),
        }, "Остановить игру", new RelayCommand(StopGame)));
    }

    private void StopGame()
    {
        _nav.CloseOverlay();
        _process?.Kill(); // остановленная игра получает статус Exited и снимает «запущена» через событие
    }

    private void ShowCrash(IGameProcess process)
    {
        var blocks = new List<InfoBlockItem>
        {
            new("Игра закрылась с ошибкой", $"Код завершения: {process.ExitCode}."),
        };

        var lines = process.RecentOutput.TakeLast(CrashOutputLines).ToList();
        if (lines.Count > 0) blocks.Add(new InfoBlockItem("Последние строки вывода", string.Join(Environment.NewLine, lines)));
        if (!string.IsNullOrEmpty(process.LogFilePath)) blocks.Add(new InfoBlockItem("Полный журнал игры", process.LogFilePath));

        _logger.LogWarning("Игра завершилась с кодом {Code}. Журнал: {Log}.", process.ExitCode, process.LogFilePath);
        _nav.ShowOverlay(new MessageOverlayViewModel("Игра упала", blocks, "Понятно", new RelayCommand(_nav.CloseOverlay)));
    }

    private void ShowFailure(LaunchResult launch, string label)
    {
        _logger.LogWarning("Запуск Minecraft {Version} не удался: {Failure}: {Error}", label, launch.Failure, launch.Error);

        if (launch.Failure == LaunchFailure.AlreadyRunning)
        {
            _toast.Show("Эта версия уже запущена или запускается.");
            return;
        }

        var (title, hint) = launch.Failure switch
        {
            LaunchFailure.UnsupportedLoader => ("Загрузчик пока не поддерживается", "Ваниль, Fabric, Quilt, Forge и NeoForge лаунчер запускает сам. Особые сборки (OptiFine, LabyMod и подобные) запустить из лаунчера пока нельзя: выбери другую версию в списке «Версия» в нижней панели."),
            LaunchFailure.LoaderNotReady => ("Не удалось подготовить загрузчик", "Проверь подключение к интернету и попробуй ещё раз. Если ошибка повторяется, выбери другую сборку загрузчика в списке «Версия»."),
            LaunchFailure.SignInRequired => ("Нужен вход", "Открой меню профиля и войди в аккаунт заново."),
            LaunchFailure.SignInFailed => ("Не удалось войти", "Проверь подключение к интернету и попробуй ещё раз."),
            LaunchFailure.FilesNotReady => ("Не удалось подготовить файлы", "Проверь подключение к интернету и свободное место на диске, затем попробуй ещё раз."),
            LaunchFailure.JavaUnavailable => ("Не найдена Java", "Лаунчер не нашёл подходящую Java и не смог скачать её. Проверь подключение к интернету."),
            _ => ("Не удалось запустить игру", "Подробности записаны в журнал лаунчера."),
        };

        _nav.ShowOverlay(new MessageOverlayViewModel(title, new[]
        {
            new InfoBlockItem($"Minecraft {label}", launch.Error ?? "Причина неизвестна."),
            new InfoBlockItem("Что сделать", hint),
        }, "Понятно", new RelayCommand(_nav.CloseOverlay)));
    }
}
