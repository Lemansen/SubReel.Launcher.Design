using System.Net;
using System.Text.Json;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Authentication.Http;

namespace SubReel.Launcher.Authentication.MinecraftServices;

internal sealed class MinecraftToken
{
    public MinecraftToken(string accessToken, DateTimeOffset expiresAt)
    {
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    public string AccessToken { get; }
    public DateTimeOffset ExpiresAt { get; }
    public override string ToString() => "MinecraftToken(<скрыто>)";
}

/// <param name="Id">UUID без дефисов, в нижнем регистре.</param>
internal sealed record MinecraftProfile(string Id, string Name, string? SkinUrl);

/// <summary>api.minecraftservices.com: вход через XSTS, проверка владения Java Edition, профиль.</summary>
internal sealed class MinecraftServicesClient
{
    private const string Api = "https://api.minecraftservices.com";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public MinecraftServicesClient(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public async Task<MinecraftToken> LoginAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        var request = AuthHttp.Json(HttpMethod.Post, $"{Api}/authentication/login_with_xbox", new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
        var (status, body) = await AuthHttp.SendAsync(_http, request, ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Minecraft Services");

        using var doc = AuthHttp.TryParse(body);
        var root = doc?.RootElement ?? default;
        if (AuthHttp.IsSuccess(status) && root.Str("access_token") is { } token)
            return new MinecraftToken(token, _time.GetUtcNow().AddSeconds(root.Num("expires_in") ?? 86400));

        throw MapFailure(status, body, "вход");
    }

    /// <summary>true — в покупках есть Minecraft Java Edition. У подписчиков Game Pass записей нет, но профиль у них есть: решает вызывающий.</summary>
    public async Task<bool> OwnsJavaEditionAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await AuthHttp.SendAsync(_http, AuthHttp.Bearer($"{Api}/entitlements/mcstore", accessToken), ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Minecraft Services");
        if (!AuthHttp.IsSuccess(status)) throw MapFailure(status, body, "проверка покупки");

        using var doc = AuthHttp.TryParse(body);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            if (item.Str("name") is "product_minecraft" or "game_minecraft") return true;
        }
        return false;
    }

    /// <returns>null — профиля нет (404): игра не куплена или ник ещё не выбран.</returns>
    public async Task<MinecraftProfile?> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await AuthHttp.SendAsync(_http, AuthHttp.Bearer($"{Api}/minecraft/profile", accessToken), ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Minecraft Services");
        if (status == HttpStatusCode.NotFound) return null;
        if (!AuthHttp.IsSuccess(status)) throw MapFailure(status, body, "загрузка профиля");

        using var doc = AuthHttp.TryParse(body);
        var root = doc?.RootElement ?? default;
        var id = root.Str("id");
        var name = root.Str("name");
        if (id is null || name is null)
            throw new AuthenticationException(AuthFailureReason.Unknown, "Minecraft Services вернули профиль в неожиданном виде.");

        return new MinecraftProfile(id.Replace("-", "").ToLowerInvariant(), name, FindActiveSkin(root));
    }

    private static string? FindActiveSkin(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("skins", out var skins) || skins.ValueKind != JsonValueKind.Array) return null;
        string? first = null;
        foreach (var skin in skins.EnumerateArray())
        {
            var url = skin.Str("url");
            if (url is null) continue;
            first ??= url;
            if (skin.Str("state") == "ACTIVE") return ToHttps(url);
        }
        return first is null ? null : ToHttps(first);
    }

    private static string ToHttps(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    private static AuthenticationException MapFailure(HttpStatusCode status, string body, string step)
    {
        // Для приложения, которое Mojang ещё не одобрил, сервис отвечает ошибкой «Invalid app registration».
        if (body.Contains("app registration", StringComparison.OrdinalIgnoreCase))
            return new AuthenticationException(AuthFailureReason.MinecraftServicesRejected,
                "Mojang ещё не одобрил это приложение (Client ID) для входа в Minecraft. Нужно подать заявку в Mojang и дождаться одобрения.");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new AuthenticationException(AuthFailureReason.MinecraftServicesRejected,
                $"Minecraft Services отклонили запрос ({step}, код {(int)status}). Повторите вход через Microsoft.");
        return new AuthenticationException(AuthFailureReason.Unknown, $"Minecraft Services вернули ошибку ({step}, код {(int)status}).");
    }
}
