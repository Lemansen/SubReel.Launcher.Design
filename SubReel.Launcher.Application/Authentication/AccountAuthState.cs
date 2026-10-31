namespace SubReel.Launcher.Application.Authentication;

public enum AccountAuthState
{
    /// <summary>Офлайн-аккаунт: вход не нужен.</summary>
    NotRequired,
    /// <summary>Сессия получена и ещё действует.</summary>
    SignedIn,
    /// <summary>Токены сохранены, сессия поднимется при запуске (тихо, без участия пользователя).</summary>
    Restorable,
    /// <summary>Токенов нет или они не действуют: нужно войти заново.</summary>
    SignInRequired,
}
