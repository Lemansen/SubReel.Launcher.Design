using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Tests;

public sealed class SessionManagerTests
{
    private sealed class Env
    {
        public FakeSettingsService Settings { get; } = new();
        public FakeTokenStore Tokens { get; } = new();
        public AuthTestClock Time { get; } = new();
        public AccountService Accounts { get; }
        public FakeAccountProvider Provider { get; }
        public SessionManager Manager { get; }

        public Env()
        {
            Accounts = new AccountService(Settings);
            Provider = new FakeAccountProvider(Tokens, Time);
            Manager = new SessionManager(Accounts, new IAccountProvider[] { Provider }, Tokens, Time, NullLogger<SessionManager>.Instance);
        }
    }

    [Fact]
    public async Task Offline_account_gets_offline_session_without_provider()
    {
        var env = new Env();
        var session = await env.Manager.GetSessionAsync(env.Accounts.Active);

        Assert.Equal(GameSession.OfflineUserType, session.UserType);
        Assert.Equal("0", session.AccessToken);
        Assert.Equal(AccountAuthState.NotRequired, env.Manager.GetState(env.Accounts.Active));
        Assert.Equal(0, env.Provider.RestoreCount);
    }

    [Fact]
    public void Offline_uuid_is_stable_version3_and_depends_on_name()
    {
        var a = OfflineIdentity.CreateProfileId("Steve");
        Assert.Equal(a, OfflineIdentity.CreateProfileId("Steve"));
        Assert.NotEqual(a, OfflineIdentity.CreateProfileId("Alex"));
        Assert.Equal(32, a.Length);
        Assert.Equal('3', a[12]);
        Assert.Contains(a[16], "89ab");
    }

    [Fact]
    public async Task Sign_in_adds_active_microsoft_account_and_caches_session()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        Assert.Equal(AccountProviderType.Microsoft, account.Provider);
        Assert.Equal(LauncherAccount.MicrosoftKind, account.Kind);
        Assert.Equal("Steve", env.Settings.Current.ActiveAccount);
        Assert.Equal(AccountAuthState.SignedIn, env.Manager.GetState(account));

        var session = await env.Manager.GetSessionAsync(account);
        Assert.Equal(GameSession.MicrosoftUserType, session.UserType);
        Assert.Equal(0, env.Provider.RestoreCount); // сессия взята из памяти
    }

    [Fact]
    public async Task Session_close_to_expiry_is_refreshed_once_under_concurrency()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        env.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1)); // осталось меньше запаса

        var sessions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => env.Manager.GetSessionAsync(account)));

        Assert.Equal(1, env.Provider.RestoreCount);
        Assert.All(sessions, s => Assert.Same(sessions[0], s));
    }

    [Fact]
    public async Task Restore_after_restart_uses_stored_tokens()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        // «Перезапуск»: новый менеджер с тем же хранилищем и теми же настройками.
        var restarted = new SessionManager(new AccountService(env.Settings), new IAccountProvider[] { env.Provider }, env.Tokens, env.Time, NullLogger<SessionManager>.Instance);
        Assert.Equal(AccountAuthState.Restorable, restarted.GetState(account));

        var session = await restarted.GetSessionAsync(account);
        Assert.Equal("Steve", session.Account.Name);
        Assert.Equal(1, env.Provider.RestoreCount);
    }

    [Fact]
    public async Task Rejected_refresh_token_deletes_tokens_and_asks_for_sign_in()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        env.Time.Advance(TimeSpan.FromHours(25));
        env.Provider.RestoreFailure = _ => new AuthenticationException(AuthFailureReason.SignInRequired, "истёк");

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Manager.GetSessionAsync(account));

        Assert.Equal(AuthFailureReason.SignInRequired, ex.Reason);
        Assert.False(env.Tokens.Exists(account.ProfileId!));
        Assert.Equal(AccountAuthState.SignInRequired, env.Manager.GetState(account));
    }

    [Fact]
    public async Task Network_failure_keeps_tokens()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        env.Time.Advance(TimeSpan.FromHours(25));
        env.Provider.RestoreFailure = _ => new AuthenticationException(AuthFailureReason.Network, "нет сети");

        await Assert.ThrowsAsync<AuthenticationException>(() => env.Manager.GetSessionAsync(account));

        Assert.True(env.Tokens.Exists(account.ProfileId!));
    }

    [Fact]
    public async Task Removing_account_deletes_its_tokens()
    {
        var env = new Env();
        var ms = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        env.Accounts.Activate("Gamer");

        Assert.True(env.Accounts.Remove(ms));

        Assert.False(env.Tokens.Exists(ms.ProfileId!));
    }

    [Fact]
    public async Task Sign_out_keeps_account_but_requires_new_sign_in()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        env.Manager.SignOut(account);

        Assert.Contains(account, env.Accounts.Accounts);
        Assert.Equal(AccountAuthState.SignInRequired, env.Manager.GetState(account));
    }

    [Fact]
    public async Task Several_accounts_switch_without_mixing_sessions()
    {
        var env = new Env();
        var first = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        env.Provider.NextName = "Alex_2";
        env.Provider.NextProfileId = "11111111111111111111111111111111";
        var second = await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        env.Accounts.Activate("Steve");
        Assert.Equal("Steve", (await env.Manager.GetSessionAsync(first)).Account.Name);
        env.Accounts.Activate("Alex_2");
        Assert.Equal("Alex_2", (await env.Manager.GetSessionAsync(second)).Account.Name);
        Assert.Equal(3, env.Settings.Current.Accounts.Count); // офлайн Gamer + два Microsoft
    }

    [Fact]
    public async Task Name_conflict_with_offline_account_cleans_up_tokens()
    {
        var env = new Env();
        env.Accounts.AddAndActivate("Steve"); // офлайн-аккаунт с тем же ником

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => env.Manager.SignInAsync(AccountProviderType.Microsoft));

        Assert.Equal(AuthFailureReason.NameConflict, ex.Reason);
        Assert.Empty(env.Tokens.Items);
        Assert.Single(env.Settings.Current.Accounts, a => a.Name == "Steve");
    }

    [Fact]
    public async Task Microsoft_nickname_change_updates_existing_account_and_keeps_cosmetics()
    {
        var env = new Env();
        var account = await env.Manager.SignInAsync(AccountProviderType.Microsoft);
        var cosmetics = account.Cosmetics;

        env.Provider.NextName = "Steve_New";
        var again = await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        Assert.Same(account, again);
        Assert.Equal("Steve_New", again.Name);
        Assert.Same(cosmetics, again.Cosmetics);
        Assert.Equal("Steve_New", env.Settings.Current.ActiveAccount);
    }

    [Fact]
    public async Task Microsoft_account_cannot_be_renamed_by_user()
    {
        var env = new Env();
        await env.Manager.SignInAsync(AccountProviderType.Microsoft);

        Assert.False(env.Accounts.RenameActive("Other_Name"));
    }

    [Fact]
    public void Session_and_credentials_do_not_leak_tokens_in_ToString()
    {
        var session = new GameSession(new AccountInfo(AccountProviderType.Microsoft, "Steve", "id"), "SECRET-TOKEN", "msa", null);
        var creds = new StoredCredentials { RefreshToken = "SECRET-REFRESH", MinecraftAccessToken = "SECRET-TOKEN" };

        Assert.DoesNotContain("SECRET", session.ToString());
        Assert.DoesNotContain("SECRET", creds.ToString());
    }

    [Fact]
    public async Task Missing_provider_gives_not_configured_error()
    {
        var settings = new FakeSettingsService();
        var manager = new SessionManager(new AccountService(settings), Array.Empty<IAccountProvider>(), new FakeTokenStore(), new AuthTestClock(), NullLogger<SessionManager>.Instance);

        Assert.False(manager.IsAvailable(AccountProviderType.Microsoft));
        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => manager.SignInAsync(AccountProviderType.Microsoft));
        Assert.Equal(AuthFailureReason.NotConfigured, ex.Reason);
    }
}
