using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Images;

/// <summary>
/// Кэш картинок в %LocalAppData%\SubReel\cache\images (имена файлов и расположение прежние — уже скачанное сохраняется).
/// Одну ссылку качают один раз одновременно; пока загрузка идёт, она лежит в словаре, после завершения запись удаляется,
/// поэтому словарь не растёт. Общий лимит одновременных загрузок — <see cref="MaxConcurrentDownloads"/>.
/// </summary>
public sealed class DiskImageCache : IImageCache, IDisposable
{
    public const string HttpClientName = "images";
    private const int MaxConcurrentDownloads = 6;
    private const int PrefetchParallelism = 2;

    private readonly IHttpClientFactory _httpFactory;
    private readonly AppPaths _paths;
    private readonly ILogger<DiskImageCache> _logger;
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly SemaphoreSlim _downloadGate = new(MaxConcurrentDownloads);
    private readonly CancellationTokenSource _shutdown = new();

    public DiskImageCache(IHttpClientFactory httpFactory, AppPaths paths, ILogger<DiskImageCache> logger)
    {
        _httpFactory = httpFactory;
        _paths = paths;
        _logger = logger;
    }

    public async Task<byte[]?> GetBytesAsync(string url, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var path = await GetPathAsync(url, maxAge).WaitAsync(ct).ConfigureAwait(false);
        if (path is null) return null;

        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать файл кэша {Path}.", path);
            return null;
        }
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(PrefetchParallelism);
        var tasks = urls.Distinct().Select(async url =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await GetPathAsync(url, null).WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void Invalidate(string url)
    {
        var path = PathFor(url);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось удалить повреждённый файл кэша {Path}.", path);
        }
    }

    /// <summary>
    /// Прерывает текущие загрузки. Источник отмены и семафор намеренно не освобождаются: незавершённые загрузки
    /// ещё обращаются к ним при выходе (Release / Token), а после Dispose это привело бы к ObjectDisposedException.
    /// Ни то, ни другое не держит неуправляемых ресурсов (нет таймеров и дескрипторов ожидания).
    /// </summary>
    public void Dispose() => _shutdown.Cancel();

    private Task<string?> GetPathAsync(string url, TimeSpan? maxAge)
    {
        // Завершённую загрузку заново не используем: файл мог измениться или быть удалён (Invalidate).
        if (_inFlight.TryGetValue(url, out var existing) && existing.IsCompleted)
            _inFlight.TryRemove(new KeyValuePair<string, Task<string?>>(url, existing));

        var task = _inFlight.GetOrAdd(url, u => DownloadAsync(u, maxAge));
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(new KeyValuePair<string, Task<string?>>(url, task)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private string PathFor(string url) =>
        Path.Combine(_paths.ImageCacheDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

    private async Task<string?> DownloadAsync(string url, TimeSpan? maxAge)
    {
        string path;
        bool exists;
        try
        {
            Directory.CreateDirectory(_paths.ImageCacheDirectory);
            path = PathFor(url);
            exists = File.Exists(path);
            if (exists && (maxAge is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge)) return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Кэш картинок недоступен ({Directory}).", _paths.ImageCacheDirectory);
            return null;
        }

        try
        {
            await _downloadGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return exists ? path : null; // лаунчер закрывается
        }

        try
        {
            // Две попытки: одна случайная ошибка сети не должна оставлять карточку без картинки.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var data = await _httpFactory.CreateClient(HttpClientName).GetByteArrayAsync(url, _shutdown.Token).ConfigureAwait(false);
                    var tmp = path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, data, _shutdown.Token).ConfigureAwait(false);
                    File.Move(tmp, path, overwrite: true);
                    return path;
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Не удалось скачать картинку {Url} (попытка {Attempt}).", url, attempt + 1);
                    if (attempt == 0) await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _downloadGate.Release();
        }

        return exists ? path : null; // сети нет — берём устаревший файл, если он есть
    }
}
