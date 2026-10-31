using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Authentication.MicrosoftOAuth;
using SubReel.Launcher.Authentication.MinecraftServices;
using SubReel.Launcher.Authentication.XboxLive;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Authentication;

/// <summary>
/// Вход в Minecraft через аккаунт Microsoft: код устройства → Microsoft → Xbox Live → XSTS → Minecraft Services → профиль.
/// В хранилище попадают только refresh-токен Microsoft и токен Minecraft; токены Xbox и XSTS живут лишь внутри одного входа.
/// В журнал и в тексты ошибок токены не попадают.
/// </summary>
public sealed class MicrosoftAccountProvider : IAccountProvider
{
    private readonly AuthenticationOptions _options;
    private readonly ITokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger<MicrosoftAccountProvider> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly MicrosoftOAuthClient _oauth;
    private readonly XboxLiveClient _xbox;
    private readonly MinecraftServicesClient _minecraft;

    public MicrosoftAccountProvider(HttpClient http, IOptions<AuthenticationOptions> options, ITokenStore tokens, TimeProvider time, ILogger<MicrosoftAccountProvider> logger)
        : this(http, options, tokens, time, logger, null)
    {
    }

    /// <param name="delay">Пауза между опросами кода устройства. Тесты подставляют свою, чтобы не ждать по-настоящему.</param>
    internal MicrosoftAccountProvider(HttpClient http, IOptions<AuthenticationOptions> options, ITokenStore tokens, TimeProvider time, ILogger<MicrosoftAccountProvider> logger,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _options = options.Value;
        _tokens = tokens;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((interval, ct) => Task.Delay(interval, time, ct));
        _oauth = new MicrosoftOAuthClient(http, _options.ClientId, _options.Tenant);
        _xbox = new XboxLiveClient(http);
        _minecraft = new MinecraftServicesClient(http, time);
    }

    public AccountProviderType Type => AccountProviderType.Microsoft;

    public async Task<ISession> SignInAsync(IProgress<SignInProgress>? progress, CancellationToken ct = default)
    {
        EnsureConfigured();

        progress?.Report(new SignInProgress(SignInStage.RequestingCode));
        var code = await _oauth.RequestDeviceCodeAsync(_time, ct).ConfigureAwait(false);
        progress?.Report(new SignInProgress(SignInStage.WaitingForUser, new DeviceCodePrompt(code.UserCode, code.VerificationUri, code.ExpiresAt)));

        var microsoft = await _oauth.PollForTokenAsync(code, _time, _delay, ct).ConfigureAwait(false);
        var chain = await RunChainAsync(microsoft.AccessToken, progress, checkOwnership: true, ct).ConfigureAwait(false);

        SaveCredentials(chain.Profile.Id, new StoredCredentials
        {
            RefreshToken = microsoft.RefreshToken ?? "",
            MinecraftAccessToken = chain.Token.AccessToken,
            MinecraftTokenExpiresAt = chain.Token.ExpiresAt,
            Name = chain.Profile.Name,
            SkinUrl = chain.Profile.SkinUrl,
        });

        progress?.Report(new SignInProgress(SignInStage.Done));
        return CreateSession(chain.Profile.Id, chain.Profile.Name, chain.Profile.SkinUrl, chain.Token);
    }

    public async Task<ISession> RestoreAsync(string profileId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var stored = _tokens.Load(profileId)
            ?? throw new AuthenticationException(AuthFailureReason.SignInRequired, "Для этого аккаунта нужно войти через Microsoft.");

        // Токен Minecraft ещё действует с запасом — сеть не нужна.
        if (!string.IsNullOrEmpty(stored.MinecraftAccessToken) && stored.MinecraftTokenExpiresAt is { } expires
            && !string.IsNullOrEmpty(stored.Name) && expires - _time.GetUtcNow() > SessionManager.RefreshMargin)
        {
            return CreateSession(profileId, stored.Name, stored.SkinUrl, new MinecraftToken(stored.MinecraftAccessToken, expires));
        }

        if (string.IsNullOrEmpty(stored.RefreshToken))
            throw new AuthenticationException(AuthFailureReason.SignInRequired, "Сессия Microsoft устарела. Войдите в аккаунт заново.");

        var microsoft = await _oauth.RefreshAsync(stored.RefreshToken, ct).ConfigureAwait(false);

        // Microsoft выдаёт новый refresh-токен: сохраняем сразу, чтобы сбой на следующих шагах не потерял его.
        stored.RefreshToken = microsoft.RefreshToken ?? stored.RefreshToken;
        SaveCredentials(profileId, stored);

        var chain = await RunChainAsync(microsoft.AccessToken, progress: null, checkOwnership: false, ct).ConfigureAwait(false);
        if (chain.Profile.Id != profileId)
            throw new AuthenticationException(AuthFailureReason.SignInRequired, "Сохранённый вход относится к другому профилю Minecraft. Войдите в аккаунт заново.");

        SaveCredentials(profileId, new StoredCredentials
        {
            RefreshToken = stored.RefreshToken,
            MinecraftAccessToken = chain.Token.AccessToken,
            MinecraftTokenExpiresAt = chain.Token.ExpiresAt,
            Name = chain.Profile.Name,
            SkinUrl = chain.Profile.SkinUrl,
        });

        _logger.LogInformation("Сессия аккаунта {Name} обновлена.", chain.Profile.Name);
        return CreateSession(profileId, chain.Profile.Name, chain.Profile.SkinUrl, chain.Token);
    }

    private async Task<(MinecraftToken Token, MinecraftProfile Profile)> RunChainAsync(string microsoftAccessToken, IProgress<SignInProgress>? progress, bool checkOwnership, CancellationToken ct)
    {
        progress?.Report(new SignInProgress(SignInStage.SigningInXbox));
        var xbl = await _xbox.AuthenticateAsync(microsoftAccessToken, ct).ConfigureAwait(false);

        progress?.Report(new SignInProgress(SignInStage.SigningInXsts));
        var xsts = await _xbox.AuthorizeAsync(xbl, ct).ConfigureAwait(false);

        progress?.Report(new SignInProgress(SignInStage.SigningInMinecraft));
        var token = await _minecraft.LoginAsync(xsts.UserHash, xsts.Token, ct).ConfigureAwait(false);

        var owns = false;
        if (checkOwnership)
        {
            progress?.Report(new SignInProgress(SignInStage.CheckingOwnership));
            owns = await _minecraft.OwnsJavaEditionAsync(token.AccessToken, ct).ConfigureAwait(false);
        }

        progress?.Report(new SignInProgress(SignInStage.LoadingProfile));
        var profile = await _minecraft.GetProfileAsync(token.AccessToken, ct).ConfigureAwait(false);

        // Подписчики Game Pass не видят покупки, но профиль у них есть — этого достаточно. Нет профиля — игры нет или ник не выбран.
        if (profile is null)
        {
            throw owns
                ? new AuthenticationException(AuthFailureReason.NoMinecraftProfile,
                    "Minecraft на аккаунте есть, но профиль ещё не создан. Запустите официальный лаунчер Minecraft, выберите ник и повторите вход.")
                : new AuthenticationException(AuthFailureReason.NoMinecraftLicense,
                    "На этой учётной записи Microsoft нет Minecraft Java Edition. Купите игру на minecraft.net (или подключите Game Pass и войдите в официальный лаунчер один раз) и повторите вход.");
        }

        return (token, profile);
    }

    private static ISession CreateSession(string profileId, string name, string? skinUrl, MinecraftToken token) =>
        new GameSession(new AccountInfo(AccountProviderType.Microsoft, name, profileId, skinUrl), token.AccessToken, GameSession.MicrosoftUserType, token.ExpiresAt);

    private void SaveCredentials(string profileId, StoredCredentials credentials)
    {
        try
        {
            _tokens.Save(profileId, credentials);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось сохранить токены в защищённое хранилище.");
            throw new AuthenticationException(AuthFailureReason.Unknown, "Не удалось сохранить вход в защищённое хранилище Windows.", ex);
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new AuthenticationException(AuthFailureReason.NotConfigured,
                "Вход через Microsoft не настроен: не указан Client ID (appsettings.json, раздел Authentication, поле ClientId).");
        if (!Guid.TryParse(_options.ClientId, out _))
            throw new AuthenticationException(AuthFailureReason.NotConfigured,
                "Client ID для входа через Microsoft указан неверно: ожидается идентификатор вида xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.");
    }
}
