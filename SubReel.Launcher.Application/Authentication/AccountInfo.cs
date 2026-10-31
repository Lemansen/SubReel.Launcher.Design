using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Authentication;

/// <summary>Публичные сведения об аккаунте, без токенов — их безопасно показывать и логировать.</summary>
/// <param name="ProfileId">UUID профиля Minecraft без дефисов.</param>
/// <param name="SkinUrl">Адрес активного скина (для головы в интерфейсе), если известен.</param>
public sealed record AccountInfo(AccountProviderType Provider, string Name, string ProfileId, string? SkinUrl = null);
