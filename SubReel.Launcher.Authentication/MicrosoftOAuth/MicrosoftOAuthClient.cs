using System.Net;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Authentication.Http;

namespace SubReel.Launcher.Authentication.MicrosoftOAuth;

internal sealed record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval);

/// <summary>Токены Microsoft. Класс, а не record: автоматический <c>ToString</c> у record показал бы токены.</summary>
internal sealed class OAuthTokens
{
    public OAuthTokens(string accessToken, string? refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    public string AccessToken { get; }
    public string? RefreshToken { get; }
    public override string ToString() => "OAuthTokens(<скрыто>)";
}

/// <summary>Microsoft identity platform v2.0, вход по коду устройства (публичный клиент, без секрета).</summary>
internal sealed class MicrosoftOAuthClient
{
    public const string Scope = "XboxLive.signin offline_access";
    private const string DefaultVerificationUri = "https://www.microsoft.com/link";
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _authority;

    public MicrosoftOAuthClient(HttpClient http, string clientId, string tenant)
    {
        _http = http;
        _clientId = clientId;
        _authority = $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(tenant) ? "consumers" : tenant)}/oauth2/v2.0";
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(TimeProvider time, CancellationToken ct)
    {
        var (status, body) = await AuthHttp.SendAsync(_http,
            AuthHttp.Form($"{_authority}/devicecode", ("client_id", _clientId), ("scope", Scope)), ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Вход Microsoft");

        using var doc = AuthHttp.TryParse(body);
        var root = doc?.RootElement ?? default;
        var deviceCode = root.Str("device_code");
        var userCode = root.Str("user_code");

        if (!AuthHttp.IsSuccess(status) || deviceCode is null || userCode is null)
        {
            var error = root.Str("error");
            if (error is "invalid_client" or "unauthorized_client")
                throw new AuthenticationException(AuthFailureReason.NotConfigured,
                    $"Приложение Microsoft настроено неверно (код {error}). Проверьте Client ID и включите «Allow public client flows» в Authentication.");
            throw new AuthenticationException(AuthFailureReason.Unknown, $"Не удалось получить код входа Microsoft (код {(int)status}{(error is null ? "" : ", " + error)}).");
        }

        var expiresIn = root.Num("expires_in") ?? 900;
        var interval = root.Num("interval") ?? 5;
        return new DeviceCodeInfo(
            deviceCode,
            userCode,
            root.Str("verification_uri") ?? DefaultVerificationUri,
            time.GetUtcNow().AddSeconds(expiresIn),
            TimeSpan.FromSeconds(Math.Max(0, interval)));
    }

    /// <summary>Ждёт, пока пользователь введёт код. Между опросами выдерживает интервал Microsoft (и увеличивает его по просьбе «slow_down»).</summary>
    public async Task<OAuthTokens> PollForTokenAsync(DeviceCodeInfo code, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var interval = code.Interval;
        while (true)
        {
            await delay(interval, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= code.ExpiresAt)
                throw new AuthenticationException(AuthFailureReason.CodeExpired, "Код входа истёк. Начните вход заново.");

            var (status, body) = await AuthHttp.SendAsync(_http,
                AuthHttp.Form($"{_authority}/token",
                    ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    ("client_id", _clientId),
                    ("device_code", code.DeviceCode)), ct).ConfigureAwait(false);
            AuthHttp.ThrowIfTemporary(status, "Вход Microsoft");

            using var doc = AuthHttp.TryParse(body);
            var root = doc?.RootElement ?? default;

            if (AuthHttp.IsSuccess(status) && root.Str("access_token") is { } accessToken)
                return new OAuthTokens(accessToken, root.Str("refresh_token"));

            switch (root.Str("error"))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += SlowDownStep;
                    break;
                case "authorization_declined":
                    throw new AuthenticationException(AuthFailureReason.AccessDenied, "Вход отклонён на странице Microsoft.");
                case "expired_token":
                case "bad_verification_code":
                    throw new AuthenticationException(AuthFailureReason.CodeExpired, "Код входа истёк. Начните вход заново.");
                case var other:
                    throw new AuthenticationException(AuthFailureReason.Unknown, $"Microsoft отклонил вход (код {(int)status}{(other is null ? "" : ", " + other)}).");
            }
        }
    }

    /// <summary>Обновляет токен по refresh-токену. «invalid_grant» — токен отозван или истёк: нужен новый вход.</summary>
    public async Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var (status, body) = await AuthHttp.SendAsync(_http,
            AuthHttp.Form($"{_authority}/token",
                ("grant_type", "refresh_token"),
                ("client_id", _clientId),
                ("refresh_token", refreshToken),
                ("scope", Scope)), ct).ConfigureAwait(false);
        AuthHttp.ThrowIfTemporary(status, "Вход Microsoft");

        using var doc = AuthHttp.TryParse(body);
        var root = doc?.RootElement ?? default;

        if (AuthHttp.IsSuccess(status) && root.Str("access_token") is { } accessToken)
            return new OAuthTokens(accessToken, root.Str("refresh_token") ?? refreshToken);

        var error = root.Str("error");
        if (error is "invalid_grant" or "interaction_required")
            throw new AuthenticationException(AuthFailureReason.SignInRequired, "Сессия Microsoft устарела. Войдите в аккаунт заново.");
        throw new AuthenticationException(AuthFailureReason.Unknown, $"Не удалось обновить вход Microsoft (код {(int)status}{(error is null ? "" : ", " + error)}).");
    }
}
