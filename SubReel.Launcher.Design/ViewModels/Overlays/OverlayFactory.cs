using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Создаёт модальные окна вместе с их зависимостями, чтобы MainViewModel не собирал их через <c>new</c>.</summary>
public interface IOverlayFactory
{
    OverlayViewModel CreateAccount();
    OverlayViewModel CreateMicrosoftSignIn();
    OverlayViewModel CreateSettings();
    OverlayViewModel CreateModpack();
}

public sealed class OverlayFactory : IOverlayFactory
{
    private readonly ProfileViewModel _profile;
    private readonly NavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IAvatarStorage _avatars;
    private readonly SessionManager _sessions;
    private readonly IBrowserService _browser;
    private readonly IClipboardService _clipboard;
    private readonly IToastService _toast;
    private readonly ILoggerFactory _loggers;
    private readonly IDataLocationService _dataLocation;

    public OverlayFactory(ProfileViewModel profile, NavigationService nav, IDialogService dialogs, IAvatarStorage avatars,
        SessionManager sessions, IBrowserService browser, IClipboardService clipboard, IToastService toast, ILoggerFactory loggers, IDataLocationService dataLocation)
    {
        _profile = profile;
        _nav = nav;
        _dialogs = dialogs;
        _avatars = avatars;
        _sessions = sessions;
        _browser = browser;
        _clipboard = clipboard;
        _toast = toast;
        _loggers = loggers;
        _dataLocation = dataLocation;
    }

    public OverlayViewModel CreateAccount() => new AccountOverlayViewModel(_profile, _nav, _dialogs, _avatars, _sessions, _toast);
    public OverlayViewModel CreateMicrosoftSignIn() =>
        new MicrosoftSignInOverlayViewModel(_sessions, _profile, _nav, _browser, _clipboard, _toast, _loggers.CreateLogger<MicrosoftSignInOverlayViewModel>());
    public OverlayViewModel CreateSettings() => new SettingsOverlayViewModel(_dataLocation, _dialogs, _toast);
    public OverlayViewModel CreateModpack() => new ModpackOverlayViewModel(_nav);
}
