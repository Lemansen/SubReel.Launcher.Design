namespace SubReel.Launcher.Infrastructure.Images;

/// <summary>
/// Держит папку кэша картинок в пределах лимита: когда файлов накопилось больше, удаляет самые старые (по времени записи), пока размер не уложится.
/// Недокачанные «.tmp» старше суток тоже удаляются. Вызывается один раз при запуске в фоне; удалённая картинка просто скачается снова.
/// </summary>
public static class ImageCachePruner
{
    public const long DefaultLimitBytes = 200L * 1024 * 1024;

    /// <summary>Возвращает, сколько байт освобождено. Любые ошибки диска пропускаются: чистка необязательная.</summary>
    public static long Prune(string directory, long limitBytes = DefaultLimitBytes, DateTime? nowUtc = null)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;

            var now = nowUtc ?? DateTime.UtcNow;
            var images = new List<FileInfo>();
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (now - file.LastWriteTimeUtc > TimeSpan.FromDays(1)) freed += TryDelete(file);
                }
                else if (file.Extension.Equals(".img", StringComparison.OrdinalIgnoreCase))
                {
                    images.Add(file);
                }
            }

            var total = images.Sum(f => f.Length);
            foreach (var file in images.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= limitBytes) break;

                var length = file.Length;
                var deleted = TryDelete(file);
                freed += deleted;
                if (deleted > 0) total -= length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // кэш недоступен — оставляем как есть
        }

        return freed;
    }

    private static long TryDelete(FileInfo file)
    {
        try
        {
            var length = file.Length;
            file.Delete();
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
