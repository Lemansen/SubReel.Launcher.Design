namespace SubReel.Launcher.Authentication;

/// <summary>Раздел <c>Authentication</c> в appsettings.json. Client ID — не секрет (приложение публичное), Client Secret не используется вообще.</summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Application (client) ID из регистрации приложения в Microsoft Entra. Пусто — вход Microsoft не настроен.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Каталог Microsoft Entra. Для личных аккаунтов Minecraft — «consumers».</summary>
    public string Tenant { get; set; } = "consumers";
}
