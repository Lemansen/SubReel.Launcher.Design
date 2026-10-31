using System.Security.Cryptography;
using System.Text;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Application.Authentication;

/// <summary>Сессия офлайн-аккаунта. UUID считается так же, как в самой игре: UUID версии 3 от строки «OfflinePlayer:ник».</summary>
public static class OfflineIdentity
{
    public static string CreateProfileId(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // версия 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // вариант RFC 4122
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static ISession CreateSession(string name) =>
        new GameSession(
            new AccountInfo(AccountProviderType.Offline, name, CreateProfileId(name)),
            GameSession.OfflineAccessToken,
            GameSession.OfflineUserType,
            expiresAt: null);
}
