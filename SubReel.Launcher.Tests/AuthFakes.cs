using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Tests;

internal sealed class FakeTokenStore : ITokenStore
{
    public Dictionary<string, StoredCredentials> Items { get; } = new();
    public StoredCredentials? Load(string profileId) => Items.GetValueOrDefault(profileId);
    public void Save(string profileId, StoredCredentials credentials) => Items[profileId] = credentials;
    public bool Exists(string profileId) => Items.ContainsKey(profileId);
    public void Delete(string profileId) => Items.Remove(profileId);
}

/// <summary>Провайдер-заглушка: сам кладёт токены в хранилище, как это делает настоящий.</summary>
internal sealed class FakeAccountProvider : IAccountProvider
{
    private readonly ITokenStore _store;
    private readonly TimeProvider _time;

    public FakeAccountProvider(ITokenStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public AccountProviderType Type => AccountProviderType.Microsoft;
    public string NextName { get; set; } = "Steve";
    public string NextProfileId { get; set; } = "069a79f444e94726a5befca90e38aaf5";
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(24);
    public int SignInCount { get; private set; }
    public int RestoreCount { get; private set; }
    public Func<string, Exception?>? RestoreFailure { get; set; }

    public Task<ISession> SignInAsync(IProgress<SignInProgress>? progress, CancellationToken ct = default)
    {
        SignInCount++;
        progress?.Report(new SignInProgress(SignInStage.Done));
        return Task.FromResult(Issue(NextProfileId, NextName));
    }

    public Task<ISession> RestoreAsync(string profileId, CancellationToken ct = default)
    {
        RestoreCount++;
        if (RestoreFailure?.Invoke(profileId) is { } failure) throw failure;
        var stored = _store.Load(profileId) ?? throw new AuthenticationException(AuthFailureReason.SignInRequired, "нет данных");
        return Task.FromResult(Issue(profileId, stored.Name ?? NextName));
    }

    private ISession Issue(string profileId, string name)
    {
        var expires = _time.GetUtcNow() + TokenLifetime;
        var token = "mc-token-" + (SignInCount + RestoreCount);
        _store.Save(profileId, new StoredCredentials { RefreshToken = "refresh", MinecraftAccessToken = token, MinecraftTokenExpiresAt = expires, Name = name });
        return new GameSession(new AccountInfo(AccountProviderType.Microsoft, name, profileId), token, GameSession.MicrosoftUserType, expires);
    }
}

/// <summary>Часы, которые двигаются вручную.</summary>
internal sealed class AuthTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
