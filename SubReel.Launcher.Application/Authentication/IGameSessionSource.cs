using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// Откуда запуск игры берёт готовую сессию. Реализует <see cref="SessionManager"/>; отдельный контракт нужен, чтобы
/// игровое ядро не зависело от входа через Microsoft и запуск можно было проверить без сети.
/// </summary>
public interface IGameSessionSource
{
    /// <summary>Для Microsoft при необходимости тихо обновляет токены; бросает <see cref="AuthenticationException"/>.</summary>
    Task<ISession> GetSessionAsync(LauncherAccount account, CancellationToken ct = default);
}
