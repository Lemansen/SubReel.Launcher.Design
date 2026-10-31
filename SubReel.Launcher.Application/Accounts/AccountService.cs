using System.Text.RegularExpressions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Accounts;

/// <summary>
/// Правила работы с аккаунтами: проверка ника, добавление, переключение, удаление, переименование.
/// ViewModel только показывает результат, а все изменения настроек проходят здесь и сохраняются одним способом.
/// </summary>
public sealed class AccountService
{
    private static readonly Regex NicknameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    private readonly ISettingsService _settings;

    public AccountService(ISettingsService settings)
    {
        _settings = settings;
        EnsureConsistent();
    }

    /// <summary>Ник Minecraft: 3–16 символов, латиница, цифры и «_».</summary>
    public static bool IsValidNickname(string name) => NicknameRegex.IsMatch(name);

    /// <summary>Аккаунт удалён из списка (уже сохранено). По этому событию стираются токены Microsoft-аккаунта.</summary>
    public event Action<LauncherAccount>? Removed;

    public IReadOnlyList<LauncherAccount> Accounts => _settings.Current.Accounts;

    public LauncherAccount Active => _settings.Current.Accounts.First(a => a.Name == _settings.Current.ActiveAccount);

    /// <summary>Удалять можно любой аккаунт, кроме активного и единственного.</summary>
    public bool CanRemove(LauncherAccount account) =>
        _settings.Current.Accounts.Count > 1 && account.Name != _settings.Current.ActiveAccount;

    public void Activate(string name)
    {
        _settings.Current.ActiveAccount = name;
        _settings.Save();
    }

    public bool Remove(LauncherAccount account)
    {
        if (!CanRemove(account)) return false;
        _settings.Current.Accounts.Remove(account);
        _settings.Save();
        Removed?.Invoke(account);
        return true;
    }

    /// <summary>Добавляет офлайн-аккаунт (если такого ещё нет) и делает его активным.</summary>
    /// <returns>false — ник не подходит, ничего не изменено.</returns>
    public bool AddAndActivate(string name)
    {
        name = name.Trim();
        if (!IsValidNickname(name)) return false;

        if (_settings.Current.Accounts.All(a => a.Name != name))
            _settings.Current.Accounts.Add(new LauncherAccount { Name = name, Kind = LauncherAccount.OfflineKind });
        Activate(name);
        return true;
    }

    /// <summary>
    /// Добавляет Microsoft-аккаунт по UUID профиля или обновляет существующий (ник в Minecraft можно сменить) и делает его активным.
    /// Оформление профиля и аватарка при этом сохраняются.
    /// </summary>
    /// <exception cref="AuthenticationException"><see cref="AuthFailureReason.NameConflict"/> — другой аккаунт лаунчера уже носит этот ник; ничего не изменено.</exception>
    public LauncherAccount AddOrUpdateMicrosoft(string profileId, string name)
    {
        var accounts = _settings.Current.Accounts;
        var existing = accounts.FirstOrDefault(a => a.Provider == AccountProviderType.Microsoft && a.ProfileId == profileId);

        if (accounts.Any(a => a != existing && a.Name == name))
            throw new AuthenticationException(AuthFailureReason.NameConflict,
                $"В лаунчере уже есть аккаунт с ником {name}. Удалите или переименуйте его и повторите вход.");

        if (existing is null)
        {
            existing = new LauncherAccount { Name = name, Kind = LauncherAccount.MicrosoftKind, Provider = AccountProviderType.Microsoft, ProfileId = profileId };
            accounts.Add(existing);
        }
        else
        {
            existing.Name = name;
        }

        Activate(name);
        return existing;
    }

    /// <summary>Переименовывает активный аккаунт.</summary>
    /// <returns>false — ник не подходит (формат Minecraft или такой аккаунт уже есть), ничего не изменено.</returns>
    public bool RenameActive(string newName)
    {
        newName = newName.Trim();
        if (!IsValidNickname(newName)) return false;

        var current = Active;
        if (current.Provider != AccountProviderType.Offline) return false; // ник Microsoft-аккаунта задаёт Minecraft, а не лаунчер
        if (_settings.Current.Accounts.Any(a => a != current && a.Name == newName)) return false;

        current.Name = newName;
        _settings.Current.ActiveAccount = newName;
        _settings.Save();
        return true;
    }

    /// <summary>Сохраняет изменения оформления, внесённые прямо в модель аккаунта.</summary>
    public void SaveChanges() => _settings.Save();

    /// <summary>Гарантирует, что есть хотя бы один аккаунт и активный существует (на случай пустого или рукописного файла).</summary>
    private void EnsureConsistent()
    {
        var data = _settings.Current;
        if (data.Accounts.Count == 0)
            data.Accounts.Add(new LauncherAccount { Name = "Gamer", Kind = LauncherAccount.OfflineKind });
        if (data.Accounts.All(a => a.Name != data.ActiveAccount))
            data.ActiveAccount = data.Accounts[0].Name;
    }
}
