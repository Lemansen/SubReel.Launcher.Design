using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Что сейчас показывает окно входа.</summary>
public enum SignInPhase
{
    /// <summary>Просим у Microsoft код устройства.</summary>
    Starting,
    /// <summary>Код получен: пользователь вводит его на сайте Microsoft.</summary>
    WaitingForCode,
    /// <summary>Код введён, лаунчер обменивает токены (Xbox, Minecraft).</summary>
    Working,
    /// <summary>Вход не удался; показан понятный текст ошибки и кнопка «Повторить».</summary>
    Failed,
}

/// <summary>
/// Вход через Microsoft по коду устройства. Окно само запускает вход при открытии, показывает код и адрес,
/// а при закрытии (крестик, Esc, переход на другую страницу) отменяет незавершённый вход — см. <see cref="Dispose"/>.
/// Токены сюда не попадают вообще: ViewModel видит только этапы входа и итоговый аккаунт.
/// </summary>
public sealed class MicrosoftSignInOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly SessionManager _sessions;
    private readonly ProfileViewModel _profile;
    private readonly NavigationService _nav;
    private readonly IBrowserService _browser;
    private readonly IClipboardService _clipboard;
    private readonly IToastService _toast;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private bool _closed;
    private SignInPhase _phase = SignInPhase.Starting;
    private string _statusText = "Запрашиваем код у Microsoft…";
    private string _userCode = "";
    private string _verificationUri = "";
    private string _expiresText = "";
    private string _errorText = "";
    private string _copyText = "Скопировать код";

    public MicrosoftSignInOverlayViewModel(SessionManager sessions, ProfileViewModel profile, NavigationService nav,
        IBrowserService browser, IClipboardService clipboard, IToastService toast, ILogger logger)
    {
        _sessions = sessions;
        _profile = profile;
        _nav = nav;
        _browser = browser;
        _clipboard = clipboard;
        _toast = toast;
        _logger = logger;

        CopyCodeCommand = new RelayCommand(CopyCode);
        OpenPageCommand = new RelayCommand(() => _browser.TryOpen(_verificationUri));
        RetryCommand = new RelayCommand(() => _ = RunAsync());
        CancelCommand = new RelayCommand(_nav.CloseOverlay);

        // Окно создаётся в потоке интерфейса: Progress<T> вернёт обновления туда же.
        _ = RunAsync();
    }

    public override string Title => "Вход через Microsoft";

    public SignInPhase Phase
    {
        get => _phase;
        private set
        {
            if (!SetProperty(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsWaitingForCode));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    public bool IsWaitingForCode => Phase == SignInPhase.WaitingForCode;
    public bool IsBusy => Phase is SignInPhase.Starting or SignInPhase.Working;
    public bool IsFailed => Phase == SignInPhase.Failed;

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string UserCode { get => _userCode; private set => SetProperty(ref _userCode, value); }
    public string VerificationUri { get => _verificationUri; private set => SetProperty(ref _verificationUri, value); }
    public string ExpiresText { get => _expiresText; private set => SetProperty(ref _expiresText, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string CopyText { get => _copyText; private set => SetProperty(ref _copyText, value); }

    public ICommand CopyCodeCommand { get; }
    public ICommand OpenPageCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Закрытие окна отменяет вход: код устройства перестаёт ожидаться, токены не сохраняются.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* вход уже завершился */ }
    }

    private async Task RunAsync()
    {
        if (_closed || (IsBusy && _cts is not null)) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        ErrorText = "";
        UserCode = "";
        CopyText = "Скопировать код";
        StatusText = "Запрашиваем код у Microsoft…";
        Phase = SignInPhase.Starting;

        try
        {
            var progress = new Progress<SignInProgress>(OnProgress);
            var account = await _sessions.SignInAsync(AccountProviderType.Microsoft, progress, cts.Token);

            // Аккаунт уже добавлен в настройки — список в меню обновляем, даже если окно успели закрыть.
            _profile.Reload();
            _toast.Show($"Вход выполнен: {account.Name}");
            if (!_closed && ReferenceEquals(_nav.Overlay, this)) _nav.CloseOverlay();
        }
        catch (OperationCanceledException)
        {
            // Окно закрыто пользователем — показывать нечего.
        }
        catch (AuthenticationException ex)
        {
            if (!_closed) Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Непредвиденная ошибка при входе через Microsoft.");
            if (!_closed) Fail("Не удалось выполнить вход. Подробности записаны в журнал лаунчера.");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private void OnProgress(SignInProgress progress)
    {
        if (_closed || Phase == SignInPhase.Failed) return;

        if (progress.Prompt is { } prompt)
        {
            UserCode = prompt.UserCode;
            VerificationUri = prompt.VerificationUri;
            ExpiresText = $"Код действует до {prompt.ExpiresAt.ToLocalTime():HH:mm}";
        }

        switch (progress.Stage)
        {
            case SignInStage.RequestingCode:
                StatusText = "Запрашиваем код у Microsoft…";
                Phase = SignInPhase.Starting;
                break;
            case SignInStage.WaitingForUser:
                StatusText = "Ждём, пока вы введёте код на сайте Microsoft…";
                Phase = SignInPhase.WaitingForCode;
                break;
            case SignInStage.SigningInXbox:
                StatusText = "Входим в Xbox Live…";
                Phase = SignInPhase.Working;
                break;
            case SignInStage.SigningInXsts:
                StatusText = "Получаем разрешение XSTS…";
                Phase = SignInPhase.Working;
                break;
            case SignInStage.SigningInMinecraft:
                StatusText = "Входим в Minecraft…";
                Phase = SignInPhase.Working;
                break;
            case SignInStage.CheckingOwnership:
                StatusText = "Проверяем, что игра куплена…";
                Phase = SignInPhase.Working;
                break;
            case SignInStage.LoadingProfile:
                StatusText = "Загружаем профиль…";
                Phase = SignInPhase.Working;
                break;
        }
    }

    private void Fail(string message)
    {
        ErrorText = message;
        Phase = SignInPhase.Failed;
    }

    private void CopyCode()
    {
        if (UserCode.Length == 0) return;
        CopyText = _clipboard.TrySetText(UserCode) ? "Скопировано" : "Не удалось скопировать";
    }
}
