using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SubReel.Launcher.Application.Abstractions;

namespace SubReel.Launcher.Infrastructure.Security;

/// <summary>
/// DPAPI в области текущего пользователя Windows. Дополнительная «соль» (entropy) привязывает данные к лаунчеру:
/// другая программа того же пользователя не расшифрует их простым вызовом ProtectedData.Unprotect.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.Tokens.v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}
