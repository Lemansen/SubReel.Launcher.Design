namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// Готовая игровая сессия: всё, что нужно запуску игры (этап 9) — ник, UUID, токен и тип пользователя.
/// Живёт только в памяти. Токен нельзя записывать в настройки и журнал: реализации скрывают его в <c>ToString</c>.
/// </summary>
public interface ISession
{
    AccountInfo Account { get; }

    /// <summary>Значение для аргумента <c>--accessToken</c>. У офлайн-сессии — «0».</summary>
    string AccessToken { get; }

    /// <summary>Значение для <c>--userType</c>: «msa» для Microsoft, «legacy» для офлайн.</summary>
    string UserType { get; }

    /// <summary>Когда токен перестанет действовать. null — не истекает (офлайн).</summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>true — токен истёк или истечёт в течение <paramref name="margin"/>, сессию пора обновить.</summary>
    bool IsExpiringSoon(DateTimeOffset now, TimeSpan margin);
}
