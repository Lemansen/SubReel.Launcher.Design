using System.Security.Cryptography;

namespace SubReel.Launcher.Minecraft.Installation;

internal enum FileCheck { Ok, Missing, WrongSize, WrongHash }

/// <summary>Проверка файла по размеру и SHA-1. Общая для загрузчика и проверки целостности.</summary>
internal static class FileVerifier
{
    private const int BufferSize = 81920;

    /// <summary>Если не задано ни размера, ни хэша, достаточно, чтобы файл существовал.</summary>
    public static async Task<FileCheck> CheckAsync(string path, string? sha1, long? size, CancellationToken ct)
    {
        if (!File.Exists(path)) return FileCheck.Missing;
        if (size is { } expectedSize && new FileInfo(path).Length != expectedSize) return FileCheck.WrongSize;
        if (!string.IsNullOrWhiteSpace(sha1) && !await Sha1MatchesAsync(path, sha1, ct).ConfigureAwait(false)) return FileCheck.WrongHash;
        return FileCheck.Ok;
    }

    public static async Task<bool> Sha1MatchesAsync(string path, string expectedSha1, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
