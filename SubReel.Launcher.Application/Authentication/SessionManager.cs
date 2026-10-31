using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// Единая точка получения игровых сессий. Офлайн-сессию создаёт сам, онлайн — через <see cref="IAccountProvider"/>.
/// Обновление токена идёт под замком аккаунта, чтобы два запуска подряд не обновляли его одновременно.
/// Удаление аккаунта (откуда бы оно ни вызывалось) стирает его токены из хранилища.
/// В журнал попадают только ники и причины сбоев — никогда токены.
/// </summary>
public sealed class SessionManager : IDisposable
{
    /// <summary>За сколько до истечения токен считается устаревшим.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly AccountService _accounts;
    private readonly IReadOnlyList<IAccountProvider> _providers;
    private readonly ITokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionManager> _logger;
    private readonly ConcurrentDictionary<string, ISession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SessionManager(AccountService accounts, IEnumerable<IAccountProvider> providers, ITokenStore tokens, TimeProvider time, ILogger<SessionManager> logger)
    {
        _accounts = accounts;
        _providers = providers.ToList();
        _tokens = tokens;
        _time = time;
        _logger = logger;
        _accounts.Removed += OnAccountRemoved;
    }

    /// <summary>Есть ли провайдер для этого типа аккаунта (Microsoft может быть не подключён).</summary>
    public bool IsAvailable(AccountProviderType type) => type == AccountProviderType.Offline || _providers.Any(p => p.Type == type);

    public AccountAuthState GetState(LauncherAccount account)
    {
        if (account.Provider == AccountProviderType.Offline) return AccountAuthState.NotRequired;
        if (string.IsNullOrEmpty(account.ProfileId)) return AccountAuthState.SignInRequired;
        if (_sessions.TryGetValue(account.ProfileId, out var cached) && !cached.IsExpiringSoon(_time.GetUtcNow(), RefreshMargin))
            return AccountAuthState.SignedIn;
        return _tokens.Exists(account.ProfileId) ? AccountAuthState.Restorable : AccountAuthState.SignInRequired;
    }

    /// <summary>Готовая сессия для запуска игры. Для Microsoft при необходимости тихо обновляет токены.</summary>
    public async Task<ISession> GetSessionAsync(LauncherAccount account, CancellationToken ct = default)
    {
        if (account.Provider == AccountProviderType.Offline) return OfflineIdentity.CreateSession(account.Name);

        var profileId = account.ProfileId;
        if (string.IsNullOrEmpty(profileId))
            throw new AuthenticationException(AuthFailureReason.SignInRequired, "Для этого аккаунта нужно войти через Microsoft.");

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(profileId, out var cached) && !cached.IsExpiringSoon(_time.GetUtcNow(), RefreshMargin))
                return cached;

            var provider = FindProvider(account.Provider);
            try
            {
                var session = await provider.RestoreAsync(profileId, ct).ConfigureAwait(false);
                _sessions[profileId] = session;
                return session;
            }
            catch (AuthenticationException ex) when (ex.Reason == AuthFailureReason.SignInRequired)
            {
                // Refresh-токен больше не действует: хранить его незачем.
                _sessions.TryRemove(profileId, out _);
                _tokens.Delete(profileId);
                _logger.LogInformation("Для аккаунта {Name} нужен повторный вход.", account.Name);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Вход (или повторный вход) через онлайн-провайдера. Аккаунт добавляется или обновляется и становится активным.</summary>
    public async Task<LauncherAccount> SignInAsync(AccountProviderType type, IProgress<SignInProgress>? progress = null, CancellationToken ct = default)
    {
        var provider = FindProvider(type);
        var session = await provider.SignInAsync(progress, ct).ConfigureAwait(false);
        try
        {
            var account = _accounts.AddOrUpdateMicrosoft(session.Account.ProfileId, session.Account.Name);
            _sessions[session.Account.ProfileId] = session;
            _logger.LogInformation("Выполнен вход в аккаунт {Name}.", session.Account.Name);
            return account;
        }
        catch
        {
            // Аккаунт не добавлен (например, конфликт ника): токены не оставляем.
            _sessions.TryRemove(session.Account.ProfileId, out _);
            _tokens.Delete(session.Account.ProfileId);
            throw;
        }
    }

    /// <summary>Выход: токены удаляются, аккаунт остаётся в списке и требует нового входа.</summary>
    public void SignOut(LauncherAccount account)
    {
        if (account.Provider == AccountProviderType.Offline || string.IsNullOrEmpty(account.ProfileId)) return;
        _sessions.TryRemove(account.ProfileId, out _);
        _tokens.Delete(account.ProfileId);
        _logger.LogInformation("Выполнен выход из аккаунта {Name}.", account.Name);
    }

    public void Dispose()
    {
        _accounts.Removed -= OnAccountRemoved;
        foreach (var gate in _locks.Values) gate.Dispose();
        _locks.Clear();
    }

    private IAccountProvider FindProvider(AccountProviderType type) =>
        _providers.FirstOrDefault(p => p.Type == type)
        ?? throw new AuthenticationException(AuthFailureReason.NotConfigured, "Вход через Microsoft недоступен в этой сборке лаунчера.");

    private void OnAccountRemoved(LauncherAccount account)
    {
        if (account.Provider == AccountProviderType.Offline || string.IsNullOrEmpty(account.ProfileId)) return;
        // Аккаунт удалён — стираем и сессию, и токены. Сбой хранилища не должен ломать удаление.
        try
        {
            _sessions.TryRemove(account.ProfileId, out _);
            _tokens.Delete(account.ProfileId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить токены удалённого аккаунта {Name}.", account.Name);
        }
    }
}
