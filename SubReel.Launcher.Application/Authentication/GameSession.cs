namespace SubReel.Launcher.Application.Authentication;

/// <summary>Обычная сессия в памяти. <c>ToString</c> не раскрывает токен.</summary>
public sealed class GameSession : ISession
{
    public const string MicrosoftUserType = "msa";
    public const string OfflineUserType = "legacy";
    public const string OfflineAccessToken = "0";

    public GameSession(AccountInfo account, string accessToken, string userType, DateTimeOffset? expiresAt)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        AccessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        UserType = userType ?? throw new ArgumentNullException(nameof(userType));
        ExpiresAt = expiresAt;
    }

    public AccountInfo Account { get; }
    public string AccessToken { get; }
    public string UserType { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public bool IsExpiringSoon(DateTimeOffset now, TimeSpan margin) => ExpiresAt is { } expires && expires - now <= margin;

    public override string ToString() => $"Session({Account.Provider}, {Account.Name}, token=<скрыт>)";
}
