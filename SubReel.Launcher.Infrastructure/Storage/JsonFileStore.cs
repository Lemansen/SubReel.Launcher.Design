using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Infrastructure.Storage;

public enum StoreReadStatus
{
    /// <summary>Файл прочитан.</summary>
    Loaded,
    /// <summary>Файла нет (первый запуск).</summary>
    Missing,
    /// <summary>Основной файл повреждён или отсутствует, данные восстановлены из резервной копии (.bak).</summary>
    RecoveredFromBackup,
    /// <summary>Файл повреждён и восстановить его не из чего. Повреждённый файл сохранён рядом (.corrupt-*), не удалён.</summary>
    Corrupt,
    /// <summary>Файл существует, но прочитать его не удалось (занят другим процессом, нет доступа). Перезаписывать его нельзя.</summary>
    Unavailable,
}

public sealed record StoreReadResult<T>(T? Value, StoreReadStatus Status) where T : class;

/// <summary>
/// Надёжное хранение одного JSON-файла.
/// Запись: временный файл → сброс на диск → атомарная подмена с сохранением предыдущей версии в «.bak».
/// Чтение: повреждённый файл не удаляется и не затирается, а переименовывается в «.corrupt-метка»; данные берутся из «.bak».
/// </summary>
public sealed class JsonFileStore
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly ILogger<JsonFileStore> _logger;
    private readonly object _writeLock = new();

    public JsonFileStore(ILogger<JsonFileStore> logger) => _logger = logger;

    public static string BackupPathFor(string path) => path + ".bak";

    public StoreReadResult<T> Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        var backupPath = BackupPathFor(path);

        if (!File.Exists(path))
        {
            if (File.Exists(backupPath) && TryReadFile<T>(backupPath, options, out var orphan, out _) == ReadOutcome.Ok)
            {
                _logger.LogWarning("Файл {Path} отсутствует, данные восстановлены из резервной копии.", path);
                return new StoreReadResult<T>(orphan, StoreReadStatus.RecoveredFromBackup);
            }
            return new StoreReadResult<T>(null, StoreReadStatus.Missing);
        }

        var outcome = TryReadFile<T>(path, options, out var value, out var error);
        if (outcome == ReadOutcome.Ok) return new StoreReadResult<T>(value, StoreReadStatus.Loaded);

        if (outcome == ReadOutcome.Locked)
        {
            _logger.LogError(error, "Не удалось прочитать файл {Path}: он недоступен. Файл не будет перезаписан.", path);
            return new StoreReadResult<T>(null, StoreReadStatus.Unavailable);
        }

        _logger.LogError(error, "Файл {Path} повреждён.", path);
        QuarantineCorruptFile(path);

        if (File.Exists(backupPath))
        {
            if (TryReadFile<T>(backupPath, options, out var restored, out var backupError) == ReadOutcome.Ok)
            {
                _logger.LogWarning("Данные {Path} восстановлены из резервной копии.", path);
                return new StoreReadResult<T>(restored, StoreReadStatus.RecoveredFromBackup);
            }
            _logger.LogError(backupError, "Резервная копия {Path} тоже не читается.", backupPath);
        }

        return new StoreReadResult<T>(null, StoreReadStatus.Corrupt);
    }

    /// <summary>Атомарно записывает значение. false — запись не удалась (причина в журнале), прежний файл при этом не тронут.</summary>
    public bool Write<T>(string path, T value, JsonSerializerOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + ".tmp";

        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath)) File.Replace(tempPath, fullPath, BackupPathFor(fullPath));
                else File.Move(tempPath, fullPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Не удалось записать файл {Path}.", fullPath);
            return false;
        }
    }

    private enum ReadOutcome { Ok, Invalid, Locked }

    private static ReadOutcome TryReadFile<T>(string path, JsonSerializerOptions options, out T? value, out Exception? error) where T : class
    {
        value = null;
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var content = bytes.AsSpan();
            if (content.StartsWith(new ReadOnlySpan<byte>(Utf8Bom))) content = content[Utf8Bom.Length..];

            value = JsonSerializer.Deserialize<T>(content, options);
            if (value is null)
            {
                error = new JsonException("Файл не содержит данных.");
                return ReadOutcome.Invalid;
            }
            return ReadOutcome.Ok;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            error = ex;
            return ReadOutcome.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return ReadOutcome.Locked;
        }
    }

    private void QuarantineCorruptFile(string path)
    {
        var target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, target, overwrite: true);
            _logger.LogWarning("Повреждённый файл сохранён как {Target}.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось отложить повреждённый файл {Path}.", path);
        }
    }
}
