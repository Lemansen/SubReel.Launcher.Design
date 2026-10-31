using System.Text.Json;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Authentication.Http;

namespace SubReel.Launcher.Authentication.XboxLive;

/// <summary>Результат XSTS: токен и «user hash» для заголовка <c>XBL3.0 x=…</c>. Токен в <c>ToString</c> не показывается.</summary>
internal sealed class XstsToken
{
    public XstsToken(string token, string userHash)
    {
        Token = token;
        UserHash = userHash;
    }

    public string Token { get; }
    public string UserHash { get; }
    public override string ToString() => "XstsToken(<скрыто>)";
}

/// <summary>Xbox Live (user.auth) и XSTS с доверенной стороной Minecraft Services.</summary>
internal sealed class XboxLiveClient
{
    private const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftRelyingParty = "rp://api.minecraftservices.com/";

    private readonly HttpClient _http;

    public XboxLiveClient(HttpClient http) => _http = http;

    /// <summary>Обменивает токен Microsoft на токен Xbox Live (XBL).</summary>
    public async Task<string> AuthenticateAsync(string microsoftAccessToken, CancellationToken ct)
    {
        var request = AuthHttp.Json(HttpMethod.Post, UserAuthUrl, new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftAccessToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        });
        request.Headers.Add("x-xbl-contract-version", "1");

        var (status, body) = await AuthHttp.SendAsync(_http, request, ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Xbox Live");

        using var doc = AuthHttp.TryParse(body);
        if (AuthHttp.IsSuccess(status) && doc?.RootElement.Str("Token") is { } token) return token;

        throw new AuthenticationException(AuthFailureReason.Unknown, $"Xbox Live отклонил вход (код {(int)status}). Повторите вход через Microsoft.");
    }

    /// <summary>Получает токен XSTS для Minecraft. Типичные отказы (нет Xbox-профиля, детский аккаунт, страна) превращаются в понятные сообщения.</summary>
    public async Task<XstsToken> AuthorizeAsync(string xboxToken, CancellationToken ct)
    {
        var request = AuthHttp.Json(HttpMethod.Post, XstsUrl, new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxToken } },
            RelyingParty = MinecraftRelyingParty,
            TokenType = "JWT",
        });

        var (status, body) = await AuthHttp.SendAsync(_http, request, ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Xbox Live");

        using var doc = AuthHttp.TryParse(body);
        var root = doc?.RootElement ?? default;

        if (AuthHttp.IsSuccess(status) && root.Str("Token") is { } token && ReadUserHash(root) is { } userHash)
            return new XstsToken(token, userHash);

        throw MapXstsError(root.Num("XErr"), (int)status);
    }

    private static string? ReadUserHash(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("DisplayClaims", out var claims) || claims.ValueKind != JsonValueKind.Object) return null;
        if (!claims.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array || xui.GetArrayLength() == 0) return null;
        return xui[0].Str("uhs");
    }

    /// <summary>Коды XErr из документации по схеме входа Minecraft (wiki.vg / minecraft.wiki).</summary>
    internal static AuthenticationException MapXstsError(long? xerr, int httpStatus) => xerr switch
    {
        2148916233 => new(AuthFailureReason.NoXboxAccount,
            "У этой учётной записи Microsoft нет профиля Xbox. Создайте его на minecraft.net или в приложении Xbox и повторите вход."),
        2148916235 => new(AuthFailureReason.XboxUnavailableInCountry,
            "Xbox Live недоступен в стране этой учётной записи, поэтому войти нельзя."),
        2148916236 or 2148916237 => new(AuthFailureReason.XboxAdultVerificationRequired,
            "Для этой учётной записи нужно пройти проверку возраста на сайте Xbox, после чего повторить вход."),
        2148916238 => new(AuthFailureReason.XboxChildAccount,
            "Это детская учётная запись. Родитель должен добавить её в семейную группу Microsoft и разрешить онлайн-игру."),
        2148916227 => new(AuthFailureReason.XboxBanned,
            "Эта учётная запись заблокирована в Xbox Live."),
        _ => new(AuthFailureReason.Unknown,
            $"Xbox Live не выдал разрешение (код {httpStatus}{(xerr is null ? "" : ", XErr " + xerr)})."),
    };
}
