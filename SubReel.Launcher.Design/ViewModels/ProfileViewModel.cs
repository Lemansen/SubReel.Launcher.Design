using System.Windows.Input;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Один аккаунт в списке меню профиля.</summary>
public sealed class AccountItemViewModel : ObservableObject
{
    private ProfileLook _look;

    public AccountItemViewModel(LauncherAccount model, ProfileViewModel owner)
    {
        Model = model;
        _look = new ProfileLook(model.Cosmetics);
        SwitchCommand = new RelayCommand(() => owner.SwitchAccount(this));
        RemoveCommand = new RelayCommand(() => owner.RemoveAccount(this));
    }

    public LauncherAccount Model { get; }
    public string Name => Model.Name;
    public string Kind => Model.Kind;
    public ProfileCosmetics Cosmetics => Model.Cosmetics;

    /// <summary>Кисти ника, рамки и кольца аватарки. Пересоздаётся при смене оформления.</summary>
    public ProfileLook Look => _look;

    public bool IsActive { get; private set; }
    public bool CanRemove { get; private set; }

    public ICommand SwitchCommand { get; }
    public ICommand RemoveCommand { get; }

    internal void SetState(bool isActive, bool canRemove)
    {
        if (IsActive != isActive) { IsActive = isActive; OnPropertyChanged(nameof(IsActive)); }
        if (CanRemove != canRemove) { CanRemove = canRemove; OnPropertyChanged(nameof(CanRemove)); }
    }

    internal void RefreshLook()
    {
        _look = new ProfileLook(Model.Cosmetics);
        OnPropertyChanged(nameof(Look));
    }

    internal void NotifyRenamed() => OnPropertyChanged(nameof(Name));
}

/// <summary>Аккаунты и оформление профиля: список в меню, активный аккаунт, добавление / переключение / удаление / переименование.</summary>
public sealed class ProfileViewModel : ObservableObject
{
    private readonly AccountService _service;
    private readonly NavigationService _nav;
    private readonly SessionManager _sessions;

    private IReadOnlyList<AccountItemViewModel> _accounts = Array.Empty<AccountItemViewModel>();
    private bool _isAddAccountOpen;
    private string _newAccountName = "";
    private bool _hasAddError;

    public ProfileViewModel(AccountService service, NavigationService nav, SessionManager sessions)
    {
        _service = service;
        _nav = nav;
        _sessions = sessions;
        RebuildAccounts();

        ToggleAddAccountCommand = new RelayCommand(() =>
        {
            IsAddAccountOpen = !IsAddAccountOpen;
            if (IsAddAccountOpen) HasAddError = false;
        });
        AddAccountCommand = new RelayCommand(AddAccount);

        // При каждом открытии меню форма добавления сворачивается.
        nav.ProfileMenuOpened += (_, _) =>
        {
            IsAddAccountOpen = false;
            NewAccountName = "";
            HasAddError = false;
        };
    }

    public IReadOnlyList<AccountItemViewModel> Accounts { get => _accounts; private set => SetProperty(ref _accounts, value); }

    public AccountItemViewModel Active => _accounts.First(a => a.IsActive);
    public string ActiveName => Active.Name;
    public string ActiveKindText => $"{Active.Kind}-аккаунт";
    public ProfileLook Look => Active.Look;

    public bool IsAddAccountOpen { get => _isAddAccountOpen; private set => SetProperty(ref _isAddAccountOpen, value); }

    public string NewAccountName
    {
        get => _newAccountName;
        set
        {
            if (SetProperty(ref _newAccountName, value)) HasAddError = false;
        }
    }

    /// <summary>Введён неверный ник — рамка поля краснеет.</summary>
    public bool HasAddError { get => _hasAddError; private set => SetProperty(ref _hasAddError, value); }

    /// <summary>Вход через Microsoft доступен в этой сборке (провайдер подключён). Настроен ли Client ID, выяснится при попытке входа.</summary>
    public bool IsMicrosoftAvailable => _sessions.IsAvailable(AccountProviderType.Microsoft);

    public ICommand ToggleAddAccountCommand { get; }
    public ICommand AddAccountCommand { get; }

    /// <summary>Ник Minecraft: 3–16 символов, латиница, цифры и «_».</summary>
    public static bool IsValidNickname(string name) => AccountService.IsValidNickname(name);

    // ───────────────────────── Действия ─────────────────────────

    public void SwitchAccount(AccountItemViewModel account) => SwitchTo(account.Name);

    private void SwitchTo(string name)
    {
        _service.Activate(name);
        RebuildAccounts();
        _nav.CloseProfileMenu();
    }

    public void RemoveAccount(AccountItemViewModel account)
    {
        if (_service.Remove(account.Model)) RebuildAccounts();
    }

    private void AddAccount()
    {
        var name = NewAccountName.Trim();
        if (!_service.AddAndActivate(name))
        {
            HasAddError = true;
            return;
        }

        RebuildAccounts();
        _nav.CloseProfileMenu();
    }

    /// <summary>Перечитывает список аккаунтов из настроек (после входа через Microsoft аккаунт добавлен и активен).</summary>
    public void Reload() => RebuildAccounts();

    /// <summary>«Сохранить ник» в личном кабинете переименовывает активный аккаунт.</summary>
    /// <returns>false — ник не подходит (формат Minecraft или такой аккаунт уже есть), ничего не изменено.</returns>
    public bool RenameActiveAccount(string newName)
    {
        if (!_service.RenameActive(newName)) return false;

        Active.NotifyRenamed();
        RebuildAccounts();
        return true;
    }

    /// <summary>Вызывается после изменения оформления: сохраняет и обновляет внешний вид (если это активный аккаунт).</summary>
    public void CommitCosmetics(AccountItemViewModel account)
    {
        _service.SaveChanges();
        account.RefreshLook();
        if (account.IsActive) NotifyActiveChanged();
    }

    // ───────────────────────── Служебное ─────────────────────────

    private void RebuildAccounts()
    {
        // Уже созданные элементы переиспользуем: вместе с ними остаются построенные кисти оформления.
        var existing = _accounts.ToDictionary(a => a.Model);
        var items = new List<AccountItemViewModel>();
        foreach (var model in _service.Accounts)
            items.Add(existing.TryGetValue(model, out var item) ? item : new AccountItemViewModel(model, this));

        var active = _service.Active.Name;
        foreach (var item in items)
            item.SetState(item.Name == active, items.Count > 1 && item.Name != active);

        Accounts = items;
        NotifyActiveChanged();
    }

    private void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(ActiveKindText));
        OnPropertyChanged(nameof(Look));
    }
}
