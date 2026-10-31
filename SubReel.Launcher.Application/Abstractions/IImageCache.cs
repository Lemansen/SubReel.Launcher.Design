namespace SubReel.Launcher.Application.Abstractions;

/// <summary>
/// Дисковый кэш картинок из сети. Отдаёт байты файла: декодирование — задача слоя представления.
/// Одну ссылку одновременно качают один раз; число одновременных загрузок ограничено.
/// </summary>
public interface IImageCache
{
    /// <param name="maxAge">Как долго файл в кэше считается свежим (null — навсегда; для картинок обновлений, которые не меняются).</param>
    /// <returns>Байты картинки или null, если её не удалось получить (сети нет и в кэше пусто).</returns>
    Task<byte[]?> GetBytesAsync(string url, TimeSpan? maxAge = null, CancellationToken ct = default);

    /// <summary>Тихо докачивает картинки в кэш, чтобы потом они показывались без ожидания сети.</summary>
    Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default);

    /// <summary>Удаляет файл из кэша (например, если он не декодируется), чтобы при следующем запросе скачать заново.</summary>
    void Invalidate(string url);
}
