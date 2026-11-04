using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Mods;

namespace SubReel.Launcher.Infrastructure.Security;

/// <summary>Ключ API CurseForge в файле настроек, зашифрованный <see cref="ISecretProtector"/> (DPAPI). В журнал ключ не попадает.</summary>
public sealed class CurseForgeKeyStore : ICurseForgeKey
{
    private const int MaxKeyLength = 200;

    private readonly ISettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly ILogger<CurseForgeKeyStore> _logger;

    public CurseForgeKeyStore(ISettingsService settings, ISecretProtector protector, ILogger<CurseForgeKeyStore> logger)
    {
        _settings = settings;
        _protector = protector;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_settings.Current.CurseForgeKeyProtected);

    public string? Get()
    {
        var stored = _settings.Current.CurseForgeKeyProtected;
        if (string.IsNullOrEmpty(stored)) return null;

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(stored)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Ключ CurseForge не расшифровывается: его нужно ввести заново.");
            return null;
        }
    }

    public bool Set(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _settings.Current.CurseForgeKeyProtected = null;
            _settings.Save();
            return true;
        }

        if (trimmed.Length > MaxKeyLength || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;

        try
        {
            _settings.Current.CurseForgeKeyProtected = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(trimmed)));
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            _logger.LogError(ex, "Не удалось зашифровать ключ CurseForge.");
            return false;
        }

        _settings.Save();
        return true;
    }
}
