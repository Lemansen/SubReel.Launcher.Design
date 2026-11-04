using System.IO.Compression;
using System.Security.Cryptography;

namespace SubReel.Launcher.Infrastructure.Updates;

/// <summary>Проверка и распаковка архива обновления. Архив приходит из сети, поэтому пути и размеры проверяются до записи на диск.</summary>
public static class UpdatePackage
{
    private const int MaxEntries = 20_000;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>SHA-256 файла совпадает с ожидаемым (hex, регистр не важен).</summary>
    public static async Task<bool> VerifySha256Async(string path, string expectedHex, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Распаковывает архив в <paramref name="destination"/>. Внутри обязан быть <paramref name="exeName"/> — в корне или в единственной вложенной папке
    /// (так упаковывает «Отправить в сжатую папку»); вложенная папка срезается. Битый архив не должен убить установку, поэтому без exe — отказ.
    /// </summary>
    /// <exception cref="InvalidDataException">Архив не читается, без нужного exe или с небезопасными путями / слишком большой.</exception>
    public static async Task ExtractAsync(string zipPath, string destination, string exeName, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("В архиве обновления слишком много файлов.");

        var prefix = FindRoot(zip, exeName) ?? throw new InvalidDataException($"В архиве обновления нет файла {exeName}.");

        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            var relative = name[prefix.Length..];
            if (relative.Length == 0) continue;
            if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(part => part is ".." or "." or ""))
                throw new InvalidDataException($"В архиве обновления небезопасный путь: «{entry.FullName}».");

            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"В архиве обновления небезопасный путь: «{entry.FullName}».");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read; // считаем настоящие байты: размер в заголовке архива может лгать
                if (total > MaxExtractedBytes) throw new InvalidDataException("Содержимое архива обновления слишком большое.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Префикс корня внутри архива: "" (exe в корне) или "папка/" (exe в единственной вложенной папке). null — exe нет.</summary>
    internal static string? FindRoot(ZipArchive zip, string exeName)
    {
        string? best = null;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.Split('/');
            if (parts.Length > 2 || !string.Equals(parts[^1], exeName, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = parts.Length == 1 ? "" : parts[0] + "/";
            if (prefix.Length == 0) return prefix; // в корне — приоритетнее
            best ??= prefix;
        }
        return best;
    }
}
