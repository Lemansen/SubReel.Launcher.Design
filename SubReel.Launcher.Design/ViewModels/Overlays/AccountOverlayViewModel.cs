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

/// <summary>
/// Окно «Аккаунты»: слева профиль выбранного аккаунта (две вкладки — «Профиль» и «Оформление»), справа список аккаунтов.
/// Активный аккаунт, список и внешний вид берутся из <see cref="ProfileViewModel"/> привязками (<see cref="Profile"/>): окно ни на что не подписывается.
/// </summary>
public sealed class AccountOverlayViewModel : OverlayViewModel
{
    private readonly ProfileViewModel _profile;
    private readonly NavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IAvatarStorage _avatars;
    private readonly SessionManager _sessions;
    private readonly IToastService _toast;
    private readonly Func<OverlayViewModel> _addAccount;
    private CosmeticsEditorViewModel _cosmetics;
    private bool _isProfileTab = true;
    private bool _isEditingNick;
    private string _nickname = "";
    private bool _hasNicknameError;

    public AccountOverlayViewModel(ProfileViewModel profile, NavigationService nav, IDialogService dialogs, IAvatarStorage avatars,
        SessionManager sessions, IToastService toast, Func<OverlayViewModel> addAccount)
    {
        _profile = profile;
        _nav = nav;
        _dialogs = dialogs;
        _avatars = avatars;
        _sessions = sessions;
        _toast = toast;
        _addAccount = addAccount;
        _cosmetics = new CosmeticsEditorViewModel(profile.Active, profile, dialogs, avatars);

        ShowProfileTabCommand = new RelayCommand(() => IsProfileTab = true);
        ShowCosmeticsTabCommand = new RelayCommand(() => IsProfileTab = false);
        StartEditNickCommand = new RelayCommand(StartEditNick);
        CancelEditNickCommand = new RelayCommand(() => IsEditingNick = false);
        SaveNicknameCommand = new RelayCommand(SaveNickname);
        SignOutCommand = new RelayCommand(SignOut);
        SelectAccountCommand = new RelayCommand<AccountItemViewModel>(SelectAccount);
        AddAccountCommand = new RelayCommand(() => _nav.ShowOverlay(_addAccount()));
    }

    public override string Title => "Аккаунты";
    public override double CardWidth => 900;
    public override double CardMaxHeight => 580;

    /// <summary>Активный аккаунт, список и оформление: окно привязывается к ним напрямую.</summary>
    public ProfileViewModel Profile => _profile;

    private LauncherAccount Account => _profile.Active.Model;

    public bool IsProfileTab
    {
        get => _isProfileTab;
        private set
        {
            if (!SetProperty(ref _isProfileTab, value)) return;
            OnPropertyChanged(nameof(IsCosmeticsTab));
        }
    }

    public bool IsCosmeticsTab => !_isProfileTab;
    public CosmeticsEditorViewModel Cosmetics { get => _cosmetics; private set => SetProperty(ref _cosmetics, value); }

    // ───────────────────────── Профиль выбранного аккаунта ─────────────────────────

    public bool IsMicrosoft => Account.Provider == AccountProviderType.Microsoft;
    public bool IsOffline => !IsMicrosoft;
    public string KindText => IsMicrosoft ? "Microsoft" : "Офлайн";

    /// <summary>Фигура игрока с minotar.net по нику (как и голова скина); нет сети или скина — под ней остаётся аватарка.</summary>
    public string BodyUrl => $"https://minotar.net/armor/body/{Uri.EscapeDataString(Account.Name)}/260.png";

    public string AccountTitle => IsMicrosoft ? "Аккаунт Microsoft" : "Офлайн-профиль";

    public string AccountDetail => IsMicrosoft
        ? $"{AuthStateText} Ник задаётся в Minecraft (minecraft.net) и подтягивается при входе."
        : "Работает без входа: подходит для одиночной игры и серверов без проверки лицензии.";

    private string AuthStateText => _sessions.GetState(Account) switch
    {
        AccountAuthState.SignedIn => "Вход выполнен.",
        AccountAuthState.Restorable => "Сохранённый вход обновится автоматически при запуске игры.",
        _ => "Нужен повторный вход через Microsoft.",
    };

    /// <summary>«Выйти» доступно только если токены есть (вошли или вход можно восстановить).</summary>
    public bool CanSignOut => IsMicrosoft && _sessions.GetState(Account) != AccountAuthState.SignInRequired;

    // ───────────────────────── Ник (только офлайн) ─────────────────────────

    public bool IsEditingNick { get => _isEditingNick; private set => SetProperty(ref _isEditingNick, value); }

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

    // ───────────────────────── Команды ─────────────────────────

    public ICommand ShowProfileTabCommand { get; }
    public ICommand ShowCosmeticsTabCommand { get; }
    public ICommand StartEditNickCommand { get; }
    public ICommand CancelEditNickCommand { get; }
    public ICommand SaveNicknameCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand SelectAccountCommand { get; }
    public ICommand AddAccountCommand { get; }

    private void StartEditNick()
    {
        Nickname = _profile.ActiveName;
        HasNicknameError = false;
        IsEditingNick = true;
    }

    private void SaveNickname()
    {
        // Ник Minecraft: 3–16 символов, латиница, цифры и «_» (так же, как при добавлении аккаунта).
        if (!_profile.RenameActiveAccount(Nickname))
        {
            HasNicknameError = true;
            return;
        }

        IsEditingNick = false;
        Refresh();
    }

    private void SelectAccount(AccountItemViewModel? account)
    {
        if (account is null || account.IsActive) return;
        _profile.SwitchAccount(account);
        IsEditingNick = false;
        Refresh();
    }

    private void SignOut()
    {
        _sessions.SignOut(Account);
        _toast.Show($"Выход из аккаунта {Account.Name} выполнен");
        Refresh();
    }

    /// <summary>Активный аккаунт сменился или изменился: перечитываем всё, что зависит от него, и пересобираем редактор оформления.</summary>
    private void Refresh()
    {
        Cosmetics = new CosmeticsEditorViewModel(_profile.Active, _profile, _dialogs, _avatars);
        OnPropertyChanged(string.Empty);
    }
}
