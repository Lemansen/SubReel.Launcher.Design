namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// То, что кладётся в защищённое хранилище: refresh-токен Microsoft и последний токен Minecraft с временем жизни.
/// Токены Xbox и XSTS не сохраняются вовсе — они нужны только в момент обмена.
/// </summary>
public sealed class StoredCredentials
{
    public string RefreshToken { get; set; } = "";
    public string? MinecraftAccessToken { get; set; }
    public DateTimeOffset? MinecraftTokenExpiresAt { get; set; }
    public string? Name { get; set; }
    public string? SkinUrl { get; set; }

    public override string ToString() => "StoredCredentials(<скрыто>)";
}
