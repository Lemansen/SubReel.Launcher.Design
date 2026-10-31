namespace SubReel.Launcher.Application.Authentication;

public enum AuthFailureReason
{
    Unknown,
    /// <summary>Не задан Client ID приложения Microsoft (раздел Authentication:ClientId).</summary>
    NotConfigured,
    Cancelled,
    CodeExpired,
    AccessDenied,
    Network,
    /// <summary>Нет сохранённого входа или refresh-токен больше не действует: нужно войти заново.</summary>
    SignInRequired,
    NoXboxAccount,
    XboxUnavailableInCountry,
    XboxAdultVerificationRequired,
    XboxChildAccount,
    XboxBanned,
    /// <summary>Minecraft Services отклонили приложение (Client ID не одобрен Mojang) или запрос.</summary>
    MinecraftServicesRejected,
    NoMinecraftLicense,
    NoMinecraftProfile,
    /// <summary>В лаунчере уже есть другой аккаунт с таким ником.</summary>
    NameConflict,
}

/// <summary>Ошибка входа. <see cref="Exception.Message"/> — готовый понятный текст для пользователя, без токенов.</summary>
public sealed class AuthenticationException : Exception
{
    public AuthenticationException(AuthFailureReason reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;

    public AuthFailureReason Reason { get; }
}
