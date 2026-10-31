using System.Windows.Input;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Редактор оформления профиля: предпросмотр кнопки и три ряда вариантов (ник, рамка, аватарка). Изменения применяются сразу.</summary>
public sealed class CosmeticsEditorViewModel : ObservableObject
{
    private readonly AccountItemViewModel _account;
    private readonly ProfileViewModel _profile;
    private readonly IDialogService _dialogs;
    private readonly IAvatarStorage _avatars;

    private IReadOnlyList<ChipViewModel> _nickChips = Array.Empty<ChipViewModel>();
    private IReadOnlyList<ChipViewModel> _frameChips = Array.Empty<ChipViewModel>();
    private IReadOnlyList<ChipViewModel> _avatarChips = Array.Empty<ChipViewModel>();

    public CosmeticsEditorViewModel(AccountItemViewModel account, ProfileViewModel profile, IDialogService dialogs, IAvatarStorage avatars)
    {
        _account = account;
        _profile = profile;
        _dialogs = dialogs;
        _avatars = avatars;
        Rebuild();
    }

    /// <summary>Аккаунт, оформление которого редактируется (из него берутся ник и внешний вид для предпросмотра).</summary>
    public AccountItemViewModel Account => _account;

    public IReadOnlyList<ChipViewModel> NickChips { get => _nickChips; private set => SetProperty(ref _nickChips, value); }
    public IReadOnlyList<ChipViewModel> FrameChips { get => _frameChips; private set => SetProperty(ref _frameChips, value); }
    public IReadOnlyList<ChipViewModel> AvatarChips { get => _avatarChips; private set => SetProperty(ref _avatarChips, value); }

    private void Rebuild()
    {
        var c = _account.Cosmetics;

        NickChips = CosmeticsPresets.Nicks.Select(p => new ChipViewModel(
            p.Title,
            c.NickMode == p.Mode && (p.Mode == ColorMode.None || (c.NickColorA == p.A && c.NickColorB == p.B)),
            new RelayCommand(() =>
            {
                c.NickMode = p.Mode;
                c.NickColorA = p.A;
                c.NickColorB = p.B;
                c.NickAnimated = p.Animated;
                Commit();
            }),
            ProfileLook.NickBrushFor(p))).ToList();

        FrameChips = CosmeticsPresets.Frames.Select(p => new ChipViewModel(
            p.Title,
            c.FrameMode == p.Mode && (p.Mode == ColorMode.None || (c.FrameColorA == p.A && c.FrameColorB == p.B)),
            new RelayCommand(() =>
            {
                c.FrameMode = p.Mode;
                c.FrameColorA = p.A;
                c.FrameColorB = p.B;
                c.FrameAnimated = p.Animated;
                c.FrameGlow = p.Glow;
                Commit();
            }))).ToList();

        AvatarChips = new[]
        {
            new ChipViewModel("Инициалы", c.Avatar == AvatarKind.Initials, new RelayCommand(() => PickAvatar(AvatarKind.Initials))),
            new ChipViewModel("Голова скина", c.Avatar == AvatarKind.SkinHead, new RelayCommand(() => PickAvatar(AvatarKind.SkinHead))),
            new ChipViewModel("Своя картинка…", c.Avatar == AvatarKind.Image, new RelayCommand(PickAvatarImage)),
        };
    }

    private void PickAvatar(AvatarKind kind)
    {
        _account.Cosmetics.Avatar = kind;
        Commit();
    }

    private void PickAvatarImage()
    {
        var path = _dialogs.PickImageFile();
        if (path is null) return;

        // Копируем в папку лаунчера: аватарка не пропадёт, если исходный файл переместят или удалят.
        var saved = _avatars.Save(_account.Name, path);
        if (saved is null) return;

        _account.Cosmetics.Avatar = AvatarKind.Image;
        _account.Cosmetics.AvatarImagePath = saved;
        Commit();
    }

    private void Commit()
    {
        _profile.CommitCosmetics(_account);
        Rebuild();
        OnPropertyChanged(nameof(Account));
    }
}

/// <summary>Личный кабинет: ник и оформление профиля.</summary>
public sealed class AccountOverlayViewModel : OverlayViewModel
{
    private readonly ProfileViewModel _profile;
    private readonly NavigationService _nav;
    private readonly SessionManager _sessions;
    private readonly IToastService _toast;
    private readonly LauncherAccount _account;
    private string _nickname;
    private bool _hasNicknameError;

    public AccountOverlayViewModel(ProfileViewModel profile, NavigationService nav, IDialogService dialogs, IAvatarStorage avatars,
        SessionManager sessions, IToastService toast)
    {
        _profile = profile;
        _nav = nav;
        _sessions = sessions;
        _toast = toast;
        _account = profile.Active.Model;
        _nickname = profile.ActiveName;
        Cosmetics = new CosmeticsEditorViewModel(profile.Active, profile, dialogs, avatars);
        SaveNicknameCommand = new RelayCommand(SaveNickname);
        SignOutCommand = new RelayCommand(SignOut);
    }

    public override string Title => "Личный кабинет";

    public string Nickname
    {
        get => _nickname;
        set
        {
            if (SetProperty(ref _nickname, value)) HasNicknameError = false;
        }
    }

    /// <summary>Ник не прошёл проверку — рамка поля краснеет.</summary>
    public bool HasNicknameError { get => _hasNicknameError; private set => SetProperty(ref _hasNicknameError, value); }

    /// <summary>Аккаунт Microsoft: ник задаёт Minecraft, а не лаунчер, поэтому поле ника скрыто.</summary>
    public bool IsMicrosoft => _account.Provider == AccountProviderType.Microsoft;
    public bool IsOffline => !IsMicrosoft;

    public string AccountTitle => IsMicrosoft ? "Аккаунт Microsoft" : "Офлайн-профиль";

    public string AccountDetail => IsMicrosoft
        ? $"{AuthStateText} Ник задаётся в Minecraft (minecraft.net) и подтягивается при входе."
        : "Офлайн-аккаунт работает без входа и подходит для одиночной игры и серверов без проверки лицензии.";

    private string AuthStateText => _sessions.GetState(_account) switch
    {
        AccountAuthState.SignedIn => "Вход выполнен.",
        AccountAuthState.Restorable => "Сохранённый вход обновится автоматически при запуске игры.",
        _ => "Нужен повторный вход через Microsoft.",
    };

    /// <summary>«Выйти» доступно только если токены есть (вошли или вход можно восстановить).</summary>
    public bool CanSignOut => IsMicrosoft && _sessions.GetState(_account) != AccountAuthState.SignInRequired;

    public CosmeticsEditorViewModel Cosmetics { get; }
    public ICommand SignOutCommand { get; }
    public ICommand SaveNicknameCommand { get; }

    private void SignOut()
    {
        _sessions.SignOut(_account);
        _toast.Show($"Выход из аккаунта {_account.Name} выполнен");
        _nav.CloseOverlay();
    }

    private void SaveNickname()
    {
        // Ник Minecraft: 3–16 символов, латиница, цифры и «_» (так же, как при добавлении аккаунта).
        if (_profile.RenameActiveAccount(Nickname)) _nav.CloseOverlay();
        else HasNicknameError = true;
    }
}
