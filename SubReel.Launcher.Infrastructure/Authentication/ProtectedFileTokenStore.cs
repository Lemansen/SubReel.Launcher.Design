using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Authentication;

/// <summary>
/// Хранилище токенов: по одному зашифрованному файлу <c>%LocalAppData%\SubReel\auth\&lt;uuid&gt;.bin</c> на аккаунт.
/// Содержимое шифруется <see cref="ISecretProtector"/> (DPAPI), поэтому файл бесполезен вне профиля пользователя Windows.
/// Повреждённый, обрезанный или «чужой» файл читается как «нет данных» (нужен повторный вход), а не как ошибка.
/// В журнал попадают только UUID профиля и тип сбоя — никогда содержимое.
/// </summary>
public sealed class ProtectedFileTokenStore : ITokenStore
{
    // UUID профиля — единственное, что попадает в имя файла, поэтому проверяем его строго (никаких «..\»).
    private static readonly Regex ProfileIdRegex = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly ILogger<ProtectedFileTokenStore> _logger;
    private readonly object _gate = new();

    public ProtectedFileTokenStore(AppPaths paths, ISecretProtector protector, ILogger<ProtectedFileTokenStore> logger)
    {
        _directory = paths.AuthDirectory;
        _protector = protector;
        _logger = logger;
    }

    public StoredCredentials? Load(string profileId)
    {
        if (!IsValidProfileId(profileId)) return null;
        var path = PathFor(profileId);

        lock (_gate)
        {
            byte[]? plain = null;
            try
            {
                if (!File.Exists(path)) return null;
                plain = _protector.Unprotect(File.ReadAllBytes(path));
                var credentials = JsonSerializer.Deserialize<StoredCredentials>(plain);
                return credentials is { RefreshToken.Length: > 0 } ? credentials : null;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Сообщение исключения не пишем: оно не содержит токенов, но тип сбоя достаточен для диагностики.
                _logger.LogWarning("Токены аккаунта {ProfileId} не прочитаны ({Reason}); потребуется повторный вход.", profileId, ex.GetType().Name);
                return null;
            }
            finally
            {
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public void Save(string profileId, StoredCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!IsValidProfileId(profileId)) throw new ArgumentException("Некорректный UUID профиля.", nameof(profileId));

        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = _protector.Protect(plain);
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = PathFor(profileId);
                var temp = path + ".tmp";
                // Пишем во временный файл и подменяем: обрыв записи не оставит наполовину записанный файл вместо рабочего.
                File.WriteAllBytes(temp, encrypted);
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool Exists(string profileId) => IsValidProfileId(profileId) && File.Exists(PathFor(profileId));

    public void Delete(string profileId)
    {
        if (!IsValidProfileId(profileId)) return;
        lock (_gate)
        {
            var path = PathFor(profileId);
            // File.Delete не бросает, если файла нет; а вот занятый файл — ошибка, которую нельзя скрывать
            // (токен должен реально исчезнуть), поэтому исключение уходит вызывающему.
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private static bool IsValidProfileId(string? profileId) => profileId is not null && ProfileIdRegex.IsMatch(profileId);

    private string PathFor(string profileId) => Path.Combine(_directory, profileId.ToLowerInvariant() + ".bin");
}
