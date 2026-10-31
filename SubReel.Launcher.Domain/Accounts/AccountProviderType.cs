namespace SubReel.Launcher.Domain.Accounts;

/// <summary>
/// Откуда взят аккаунт. Значения записываются в <c>design-settings.json</c>: порядок и числа не менять.
/// Офлайн = 0, поэтому у старых аккаунтов без этого поля читается офлайн.
/// </summary>
public enum AccountProviderType
{
    Offline = 0,
    Microsoft = 1,
}
