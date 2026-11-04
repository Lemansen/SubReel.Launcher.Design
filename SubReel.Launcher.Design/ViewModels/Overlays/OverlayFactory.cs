using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Создаёт модальные окна вместе с их зависимостями, чтобы MainViewModel не собирал их через <c>new</c>.</summary>
public interface IOverlayFactory
{
    OverlayViewModel CreateAccount();
    OverlayViewModel CreateAddAccount();
    OverlayViewModel CreateMicrosoftSignIn();
    OverlayViewModel CreateSettings();
    OverlayViewModel CreateMods(Instance instance);
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
    private readonly IModManager _mods;
    private readonly IReadOnlyList<IModCatalog> _modCatalogs;
    private readonly ICurseForgeKey _curseForgeKey;
    private readonly UpdateViewModel _update;

    public OverlayFactory(ProfileViewModel profile, NavigationService nav, IDialogService dialogs, IAvatarStorage avatars,
        SessionManager sessions, IBrowserService browser, IClipboardService clipboard, IToastService toast, ILoggerFactory loggers, IDataLocationService dataLocation, IModManager mods, IEnumerable<IModCatalog> modCatalogs, ICurseForgeKey curseForgeKey, UpdateViewModel update)
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
        _mods = mods;
        _modCatalogs = modCatalogs.ToList();
        _curseForgeKey = curseForgeKey;
        _update = update;
    }

    public OverlayViewModel CreateAccount() => new AccountOverlayViewModel(_profile, _nav, _dialogs, _avatars, _sessions, _toast, CreateAddAccount);
    public OverlayViewModel CreateAddAccount() => new AddAccountOverlayViewModel(_profile, _nav, CreateMicrosoftSignIn, CreateAccount);
    public OverlayViewModel CreateMicrosoftSignIn() =>
        new MicrosoftSignInOverlayViewModel(_sessions, _profile, _nav, _browser, _clipboard, _toast, _loggers.CreateLogger<MicrosoftSignInOverlayViewModel>());
    public OverlayViewModel CreateSettings() => new SettingsOverlayViewModel(_dataLocation, _dialogs, _toast, _curseForgeKey, _update);
    public OverlayViewModel CreateMods(Instance instance) =>
        new ModsOverlayViewModel(instance, _mods, _modCatalogs, _toast, _loggers.CreateLogger<ModsOverlayViewModel>());
}
