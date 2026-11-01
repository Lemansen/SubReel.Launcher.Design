using System.Windows.Input;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>
/// «Какой аккаунт добавить?»: две карточки — лицензионный Microsoft (вход по коду) и офлайн (только ник).
/// Что открывать дальше, подсказывают фабрики окон, поэтому класс не знает о других окнах.
/// </summary>
public sealed class AddAccountOverlayViewModel : OverlayViewModel
{
    private readonly ProfileViewModel _profile;
    private readonly NavigationService _nav;
    private readonly Func<OverlayViewModel> _microsoft;
    private readonly Func<OverlayViewModel> _accounts;
    private string _nickname = "";
    private bool _hasError;

    public AddAccountOverlayViewModel(ProfileViewModel profile, NavigationService nav, Func<OverlayViewModel> microsoft, Func<OverlayViewModel> accounts)
    {
        _profile = profile;
        _nav = nav;
        _microsoft = microsoft;
        _accounts = accounts;
        MicrosoftCommand = new RelayCommand(() => _nav.ShowOverlay(_microsoft()));
        AddOfflineCommand = new RelayCommand(AddOffline);
        BackCommand = new RelayCommand(() => _nav.ShowOverlay(_accounts()));
    }

    public override string Title => "Какой аккаунт добавить?";
    public override double CardWidth => 800;
    public override double CardMaxHeight => 520;

    /// <summary>Вход через Microsoft доступен в этой сборке (провайдер подключён).</summary>
    public bool IsMicrosoftAvailable => _profile.IsMicrosoftAvailable;

    public string Nickname
    {
        get => _nickname;
        set
        {
            if (SetProperty(ref _nickname, value)) HasError = false;
        }
    }

    /// <summary>Ник не подошёл — рамка поля краснеет, под ним появляется подсказка.</summary>
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    public ICommand MicrosoftCommand { get; }
    public ICommand AddOfflineCommand { get; }
    public ICommand BackCommand { get; }

    private void AddOffline()
    {
        if (!_profile.TryAddOffline(Nickname))
        {
            HasError = true;
            return;
        }

        // Новый аккаунт активен: показываем его профиль.
        _nav.ShowOverlay(_accounts());
    }
}
