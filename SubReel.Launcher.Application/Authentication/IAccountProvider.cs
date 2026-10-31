using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Authentication;

/// <summary>
/// Источник онлайн-аккаунтов (сейчас Microsoft). Офлайн-аккаунтам провайдер не нужен: их сессию создаёт <see cref="OfflineIdentity"/>.
/// Провайдер сам сохраняет и обновляет токены в <see cref="ITokenStore"/>; наружу отдаёт только готовую сессию.
/// </summary>
public interface IAccountProvider
{
    AccountProviderType Type { get; }

    /// <summary>Полный вход по коду устройства. Отмена — <see cref="OperationCanceledException"/>, остальные сбои — <see cref="AuthenticationException"/>.</summary>
    Task<ISession> SignInAsync(IProgress<SignInProgress>? progress, CancellationToken ct = default);

    /// <summary>Тихое восстановление по сохранённым токенам (без участия пользователя). Нет данных или они не действуют — <see cref="AuthFailureReason.SignInRequired"/>.</summary>
    Task<ISession> RestoreAsync(string profileId, CancellationToken ct = default);
}
