using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Authentication;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Tests;

/// <summary>Весь путь входа проверяется на заглушках HTTP: Microsoft, Xbox Live, XSTS и Minecraft Services настоящие не вызываются.</summary>
public sealed class MicrosoftAccountProviderTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string ProfileId = "069a79f444e94726a5befca90e38aaf5";

    private const string DeviceCodeOk = """{"device_code":"DEVICE","user_code":"ABCD1234","verification_uri":"https://www.microsoft.com/link","expires_in":900,"interval":5}""";
    private const string TokenOk = """{"access_token":"MS_ACCESS","refresh_token":"MS_REFRESH","expires_in":3600}""";
    private const string Pending = """{"error":"authorization_pending"}""";
    private const string XblOk = """{"Token":"XBL_TOKEN","DisplayClaims":{"xui":[{"uhs":"UHS"}]}}""";
    private const string XstsOk = """{"Token":"XSTS_TOKEN","DisplayClaims":{"xui":[{"uhs":"UHS"}]}}""";
    private const string McOk = """{"access_token":"MC_TOKEN","expires_in":86400}""";
    private const string Entitled = """{"items":[{"name":"product_minecraft"},{"name":"game_minecraft"}]}""";
    private const string NoItems = """{"items":[]}""";
    private const string ProfileOkJson = """{"id":"069a79f4-44e9-4726-a5be-fca90e38aaf5","name":"Steve","skins":[{"id":"x","state":"ACTIVE","url":"http://textures.minecraft.net/texture/abc","variant":"CLASSIC"}]}""";

    private const string DeviceCode = "/devicecode";
    private const string OAuthToken = "oauth2/v2.0/token";
    private const string Xbl = "user.auth.xboxlive.com";
    private const string Xsts = "xsts.auth.xboxlive.com";
    private const string McLogin = "login_with_xbox";
    private const string McEntitlements = "entitlements/mcstore";
    private const string McProfile = "minecraft/profile";

    private sealed class ListProgress : IProgress<SignInProgress>
    {
        public List<SignInProgress> Items { get; } = new();
        public void Report(SignInProgress value) => Items.Add(value);
    }

    private sealed class Env
    {
        public ScriptedHandler Http { get; } = new();
        public FakeTokenStore Tokens { get; } = new();
        public AuthTestClock Time { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public MicrosoftAccountProvider Provider { get; }

        public Env(string clientId = ClientId) => Provider = Create(clientId);

        public MicrosoftAccountProvider Create(string clientId = ClientId) => new(
            new HttpClient(Http),
            Options.Create(new AuthenticationOptions { ClientId = clientId }),
            Tokens,
            Time,
            NullLogger<MicrosoftAccountProvider>.Instance,
            (interval, _) =>
            {
                Delays.Add(interval);
                Time.Advance(interval);
                return Task.CompletedTask;
            });

        /// <summary>Успешный вход: код устройства, опрос (один раз «ждём»), затем вся цепочка до профиля.</summary>
        public void QueueFullSignIn(string entitlements = Entitled, HttpStatusCode profileStatus = HttpStatusCode.OK, string profile = ProfileOkJson)
        {
            Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
            Http.Respond(OAuthToken, HttpStatusCode.BadRequest, Pending);
            Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
            QueueChain(entitlements, profileStatus, profile);
        }

        public void QueueChain(string? entitlements, HttpStatusCode profileStatus = HttpStatusCode.OK, string profile = ProfileOkJson)
        {
            Http.Respond(Xbl, HttpStatusCode.OK, XblOk);
            Http.Respond(Xsts, HttpStatusCode.OK, XstsOk);
            Http.Respond(McLogin, HttpStatusCode.OK, McOk);
            if (entitlements is not null) Http.Respond(McEntitlements, HttpStatusCode.OK, entitlements);
            Http.Respond(McProfile, profileStatus, profile);
        }

        public void Seed(string? minecraftToken, TimeSpan? lifetime, string refresh = "OLD_REFRESH") =>
            Tokens.Items[ProfileId] = new StoredCredentials
            {
                RefreshToken = refresh,
                MinecraftAccessToken = minecraftToken,
                MinecraftTokenExpiresAt = lifetime is null ? null : Time.GetUtcNow() + lifetime,
                Name = "Steve",
                SkinUrl = "https://textures.minecraft.net/texture/abc",
            };
    }

    // ---------- Вход ----------

    [Fact]
    public async Task Full_sign_in_runs_the_whole_chain_and_stores_tokens()
    {
        var env = new Env();
        env.QueueFullSignIn();
        var progress = new ListProgress();

        var session = await env.Provider.SignInAsync(progress);

        Assert.Equal("Steve", session.Account.Name);
        Assert.Equal(ProfileId, session.Account.ProfileId);
        Assert.Equal("https://textures.minecraft.net/texture/abc", session.Account.SkinUrl);
        Assert.Equal("MC_TOKEN", session.AccessToken);
        Assert.Equal(GameSession.MicrosoftUserType, session.UserType);
        Assert.Equal(env.Time.GetUtcNow().AddSeconds(86400), session.ExpiresAt);

        var stored = env.Tokens.Items[ProfileId];
        Assert.Equal("MS_REFRESH", stored.RefreshToken);
        Assert.Equal("MC_TOKEN", stored.MinecraftAccessToken);
        Assert.Equal("Steve", stored.Name);

        var requests = env.Http.Requests;
        Assert.Contains("client_id=" + ClientId, requests[0].Body);
        Assert.Contains("XboxLive.signin", requests[0].Body);
        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", requests[0].Url);
        Assert.Contains("urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", requests[1].Body);
        Assert.Contains("device_code=DEVICE", requests[1].Body);
        Assert.Contains("\"RpsTicket\":\"d=MS_ACCESS\"", env.Http.Requests.Single(r => r.Url.Contains(Xbl)).Body);
        Assert.Contains("rp://api.minecraftservices.com/", env.Http.Requests.Single(r => r.Url.Contains(Xsts)).Body);
        Assert.Contains("XBL3.0 x=UHS;XSTS_TOKEN", env.Http.Requests.Single(r => r.Url.Contains(McLogin)).Body);
        Assert.Equal("Bearer MC_TOKEN", env.Http.Requests.Single(r => r.Url.Contains(McEntitlements)).Authorization);
        Assert.Equal("Bearer MC_TOKEN", env.Http.Requests.Single(r => r.Url.Contains(McProfile)).Authorization);

        var stages = progress.Items.Select(p => p.Stage).ToList();
        Assert.Equal(new[]
        {
            SignInStage.RequestingCode, SignInStage.WaitingForUser, SignInStage.SigningInXbox, SignInStage.SigningInXsts,
            SignInStage.SigningInMinecraft, SignInStage.CheckingOwnership, SignInStage.LoadingProfile, SignInStage.Done,
        }, stages);
        var prompt = progress.Items[1].Prompt!;
        Assert.Equal("ABCD1234", prompt.UserCode);
        Assert.Equal("https://www.microsoft.com/link", prompt.VerificationUri);
    }

    [Fact]
    public async Task Polling_keeps_interval_and_slow_down_increases_it()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, Pending);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, """{"error":"slow_down"}""");
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
        env.QueueChain(Entitled);

        await env.Provider.SignInAsync(null);

        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, env.Delays);
    }

    [Fact]
    public async Task Declined_sign_in_reports_access_denied()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, """{"error":"authorization_declined"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.AccessDenied, ex.Reason);
        Assert.Empty(env.Tokens.Items);
    }

    [Fact]
    public async Task Code_that_is_not_entered_in_time_expires()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, """{"device_code":"D","user_code":"U","expires_in":12,"interval":5}""");
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, Pending);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, Pending);

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.CodeExpired, ex.Reason);
    }

    [Fact]
    public async Task Expired_token_error_from_microsoft_is_code_expired()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, """{"error":"expired_token"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.CodeExpired, ex.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public async Task Missing_or_malformed_client_id_is_not_configured_and_makes_no_requests(string clientId)
    {
        var env = new Env(clientId);

        var signIn = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));
        var restore = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal(AuthFailureReason.NotConfigured, signIn.Reason);
        Assert.Equal(AuthFailureReason.NotConfigured, restore.Reason);
        Assert.Empty(env.Http.Requests);
    }

    [Fact]
    public async Task Public_client_flows_disabled_is_reported_as_misconfiguration()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"AADSTS7000218"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.NotConfigured, ex.Reason);
        Assert.Contains("Allow public client flows", ex.Message);
    }

    [Fact]
    public async Task Network_error_and_timeout_are_reported_as_network()
    {
        var env = new Env();
        env.Http.Fail(DeviceCode, new HttpRequestException("boom"));
        env.Http.Fail(DeviceCode, new TaskCanceledException());

        var first = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));
        var second = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.Network, first.Reason);
        Assert.Equal(AuthFailureReason.Network, second.Reason);
    }

    [Fact]
    public async Task Cancellation_by_user_is_not_turned_into_an_error()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Provider.SignInAsync(null, cts.Token));
    }

    // ---------- Xbox / XSTS ----------

    [Theory]
    [InlineData(2148916233L, AuthFailureReason.NoXboxAccount)]
    [InlineData(2148916235L, AuthFailureReason.XboxUnavailableInCountry)]
    [InlineData(2148916236L, AuthFailureReason.XboxAdultVerificationRequired)]
    [InlineData(2148916237L, AuthFailureReason.XboxAdultVerificationRequired)]
    [InlineData(2148916238L, AuthFailureReason.XboxChildAccount)]
    [InlineData(2148916227L, AuthFailureReason.XboxBanned)]
    [InlineData(1L, AuthFailureReason.Unknown)]
    public async Task Xsts_errors_become_readable_failures(long xerr, AuthFailureReason expected)
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
        env.Http.Respond(Xbl, HttpStatusCode.OK, XblOk);
        env.Http.Respond(Xsts, HttpStatusCode.Unauthorized, $$"""{"Identity":"0","XErr":{{xerr}},"Message":"","Redirect":"https://example.test"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(expected, ex.Reason);
        Assert.Empty(env.Tokens.Items);
    }

    [Fact]
    public async Task Xbox_service_outage_is_network_failure()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
        env.Http.Respond(Xbl, HttpStatusCode.ServiceUnavailable, "");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.Network, ex.Reason);
    }

    // ---------- Minecraft Services ----------

    [Fact]
    public async Task Unapproved_app_registration_is_reported_and_tokens_do_not_leak_into_message()
    {
        var env = new Env();
        env.Http.Respond(DeviceCode, HttpStatusCode.OK, DeviceCodeOk);
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
        env.Http.Respond(Xbl, HttpStatusCode.OK, XblOk);
        env.Http.Respond(Xsts, HttpStatusCode.OK, XstsOk);
        env.Http.Respond(McLogin, HttpStatusCode.Forbidden,
            """{"path":"/authentication/login_with_xbox","errorType":"FORBIDDEN","error":"FORBIDDEN","errorMessage":"Invalid app registration, see https://aka.ms/AppRegInfo for more information"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.MinecraftServicesRejected, ex.Reason);
        Assert.Contains("Mojang", ex.Message);
        foreach (var secret in new[] { "MS_ACCESS", "XBL_TOKEN", "XSTS_TOKEN", "MS_REFRESH" })
            Assert.DoesNotContain(secret, ex.ToString());
        Assert.Empty(env.Tokens.Items);
    }

    [Fact]
    public async Task Game_pass_account_without_purchases_but_with_profile_is_accepted()
    {
        var env = new Env();
        env.QueueFullSignIn(entitlements: NoItems);

        var session = await env.Provider.SignInAsync(null);

        Assert.Equal("Steve", session.Account.Name);
    }

    [Fact]
    public async Task No_purchases_and_no_profile_means_no_license()
    {
        var env = new Env();
        env.QueueFullSignIn(NoItems, HttpStatusCode.NotFound, """{"path":"/minecraft/profile","errorType":"NOT_FOUND"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.NoMinecraftLicense, ex.Reason);
        Assert.Empty(env.Tokens.Items);
    }

    [Fact]
    public async Task Purchased_game_without_profile_asks_to_create_one()
    {
        var env = new Env();
        env.QueueFullSignIn(Entitled, HttpStatusCode.NotFound, """{"errorType":"NOT_FOUND"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.SignInAsync(null));

        Assert.Equal(AuthFailureReason.NoMinecraftProfile, ex.Reason);
    }

    // ---------- Восстановление и обновление ----------

    [Fact]
    public async Task Restore_with_valid_minecraft_token_makes_no_requests()
    {
        var env = new Env();
        env.Seed("MC_STORED", TimeSpan.FromHours(2));

        var session = await env.Provider.RestoreAsync(ProfileId);

        Assert.Equal("MC_STORED", session.AccessToken);
        Assert.Equal("Steve", session.Account.Name);
        Assert.Empty(env.Http.Requests);
    }

    [Fact]
    public async Task Restore_with_expiring_token_refreshes_and_rotates_refresh_token_without_ownership_check()
    {
        var env = new Env();
        env.Seed("MC_STORED", TimeSpan.FromMinutes(1));
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, """{"access_token":"MS_ACCESS2","refresh_token":"NEW_REFRESH","expires_in":3600}""");
        env.QueueChain(entitlements: null);

        var session = await env.Provider.RestoreAsync(ProfileId);

        Assert.Equal("MC_TOKEN", session.AccessToken);
        var refresh = env.Http.Requests[0];
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=OLD_REFRESH", refresh.Body);
        Assert.Equal("NEW_REFRESH", env.Tokens.Items[ProfileId].RefreshToken);
        Assert.Equal("MC_TOKEN", env.Tokens.Items[ProfileId].MinecraftAccessToken);
        Assert.Equal(0, env.Http.Count(McEntitlements));
    }

    [Fact]
    public async Task Rejected_refresh_token_requires_new_sign_in()
    {
        var env = new Env();
        env.Seed(null, null);
        env.Http.Respond(OAuthToken, HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal(AuthFailureReason.SignInRequired, ex.Reason);
    }

    [Fact]
    public async Task Network_failure_during_refresh_keeps_stored_tokens()
    {
        var env = new Env();
        env.Seed(null, null);
        env.Http.Fail(OAuthToken, new HttpRequestException("offline"));

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal(AuthFailureReason.Network, ex.Reason);
        Assert.Equal("OLD_REFRESH", env.Tokens.Items[ProfileId].RefreshToken);
    }

    [Fact]
    public async Task New_refresh_token_is_kept_even_if_later_step_fails()
    {
        var env = new Env();
        env.Seed(null, null);
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, """{"access_token":"MS_ACCESS2","refresh_token":"NEW_REFRESH","expires_in":3600}""");
        env.Http.Respond(Xbl, HttpStatusCode.ServiceUnavailable, "");

        await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal("NEW_REFRESH", env.Tokens.Items[ProfileId].RefreshToken);
    }

    [Fact]
    public async Task Restore_for_other_profile_is_rejected()
    {
        var env = new Env();
        env.Seed(null, null);
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, TokenOk);
        env.QueueChain(null, profile: """{"id":"ffffffffffffffffffffffffffffffff","name":"Other"}""");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal(AuthFailureReason.SignInRequired, ex.Reason);
    }

    [Fact]
    public async Task Restore_without_stored_tokens_requires_sign_in()
    {
        var env = new Env();

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Provider.RestoreAsync(ProfileId));

        Assert.Equal(AuthFailureReason.SignInRequired, ex.Reason);
        Assert.Empty(env.Http.Requests);
    }

    // ---------- Вместе с SessionManager ----------

    [Fact]
    public async Task Sign_in_then_restart_restores_session_from_stored_tokens_through_session_manager()
    {
        var env = new Env();
        var settings = new FakeSettingsService();
        env.QueueFullSignIn();

        var manager = new SessionManager(new AccountService(settings), new IAccountProvider[] { env.Provider }, env.Tokens, env.Time, NullLogger<SessionManager>.Instance);
        var account = await manager.SignInAsync(AccountProviderType.Microsoft);
        Assert.Equal(ProfileId, account.ProfileId);
        Assert.DoesNotContain("MC_TOKEN", System.Text.Json.JsonSerializer.Serialize(settings.Current)); // токенов в настройках нет

        // Сутки спустя, новое приложение: токен Minecraft истёк, живёт только refresh-токен.
        env.Time.Advance(TimeSpan.FromHours(25));
        env.Http.Respond(OAuthToken, HttpStatusCode.OK, """{"access_token":"MS_ACCESS2","refresh_token":"MS_REFRESH2","expires_in":3600}""");
        env.QueueChain(entitlements: null);
        var restarted = new SessionManager(new AccountService(settings), new IAccountProvider[] { env.Create() }, env.Tokens, env.Time, NullLogger<SessionManager>.Instance);

        Assert.Equal(AccountAuthState.Restorable, restarted.GetState(account));
        var session = await restarted.GetSessionAsync(account);

        Assert.Equal("Steve", session.Account.Name);
        Assert.Equal("MS_REFRESH2", env.Tokens.Items[ProfileId].RefreshToken);
        Assert.Equal(AccountAuthState.SignedIn, restarted.GetState(account));
    }

    [Fact]
    public void Authentication_services_register_and_validate_in_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService, FakeSettingsService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<ITokenStore, FakeTokenStore>();
        services.AddOptions<AuthenticationOptions>();
        services.AddSubReelAuthentication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<SessionManager>());
        Assert.IsType<MicrosoftAccountProvider>(Assert.Single(provider.GetServices<IAccountProvider>()));
    }
}
