namespace SubReel.Launcher.Application.Abstractions;

/// <summary>
/// Шифрование небольших секретов «для текущего пользователя ОС» (на Windows — DPAPI).
/// Зашифрованное одним пользователем не расшифровывается другим пользователем и на другом компьютере:
/// <see cref="Unprotect"/> в этом случае бросает <see cref="System.Security.Cryptography.CryptographicException"/>.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] protectedData);
}
