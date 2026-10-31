namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// Защищённое хранилище токенов (на Windows — DPAPI). Ключ — UUID профиля Minecraft.
/// Методы синхронные: записи крошечные, а удаление должно быть детерминированным (при удалении аккаунта).
/// Повреждённая или чужая (другой пользователь Windows) запись читается как «нет данных», а не как ошибка.
/// </summary>
public interface ITokenStore
{
    StoredCredentials? Load(string profileId);
    void Save(string profileId, StoredCredentials credentials);
    bool Exists(string profileId);

    /// <summary>Удаляет запись; если её нет — ничего не происходит.</summary>
    void Delete(string profileId);
}
