using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Minecraft.Installation;

namespace SubReel.Launcher.Minecraft.Downloads;

/// <summary>
/// Загрузка файлов по HTTP. Один общий лимит параллелизма на весь лаунчер; запись во временный файл «.part» с докачкой
/// через Range; проверка размера и SHA-1 до подмены итогового файла; повтор с растущей паузой; тайм-аут «зависшего» соединения.
/// </summary>
public sealed class HttpDownloadManager : IDownloadManager
{
    public const string HttpClientName = "downloads";
    private const int BufferSize = 81920;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpDownloadManager> _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _stallTimeout;
    private readonly SemaphoreSlim _gate;
    private readonly KeyedLock _fileLocks = new();
    private readonly int _maxConcurrent;
    private readonly VerifiedFilesCache? _verified;

    public HttpDownloadManager(IHttpClientFactory httpFactory, IOptions<DownloadOptions> options, ILogger<HttpDownloadManager> logger,
        VerifiedFilesCache? verifiedFiles = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;

        var value = options.Value;
        _maxAttempts = Math.Max(1, value.MaxAttempts);
        _retryDelay = value.RetryDelay < TimeSpan.Zero ? TimeSpan.Zero : value.RetryDelay;
        _stallTimeout = value.StallTimeout > TimeSpan.Zero ? value.StallTimeout : TimeSpan.FromSeconds(30);
        _maxConcurrent = Math.Max(1, value.MaxConcurrentDownloads);
        _gate = new SemaphoreSlim(_maxConcurrent);
        _verified = verifiedFiles;
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new DownloadResult(request, DownloadStatus.Failed, 0, "Адрес загрузки должен быть http или https.");
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
            return new DownloadResult(request, DownloadStatus.Failed, 0, "Не указан путь для сохранения файла.");

        var destination = Path.GetFullPath(request.DestinationPath);

        // Сначала блокировка файла, потом общий лимит: ожидающий дубликат не занимает слот параллелизма.
        using (await _fileLocks.AcquireAsync(destination, ct).ConfigureAwait(false))
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await RunAsync(request, uri, destination, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(IReadOnlyList<DownloadRequest> requests, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var count = requests.Count;
        var results = new DownloadResult[count];
        var received = new long[count];
        long totalReceived = 0;
        var completed = 0;
        long? total = requests.All(r => r.ExpectedSize is not null) ? requests.Sum(r => r.ExpectedSize!.Value) : null;

        void Report() => progress?.Report(new DownloadBatchProgress(Volatile.Read(ref completed), count, Interlocked.Read(ref totalReceived), total));

        void SetReceived(int index, long bytes)
        {
            var delta = bytes - Interlocked.Exchange(ref received[index], bytes);
            Interlocked.Add(ref totalReceived, delta);
        }

        // Не больше MaxConcurrentDownloads файлов одновременно: тысячи ассетов не превращаются в тысячи ожидающих задач.
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrent, CancellationToken = ct }, async (index, token) =>
        {
            var fileProgress = new InlineProgress<DownloadProgress>(p =>
            {
                SetReceived(index, p.BytesReceived);
                Report();
            });

            var result = await DownloadAsync(requests[index], fileProgress, token).ConfigureAwait(false);
            if (result.Status == DownloadStatus.AlreadyUpToDate && requests[index].ExpectedSize is { } size) SetReceived(index, size);

            results[index] = result;
            Interlocked.Increment(ref completed);
            Report();
        }).ConfigureAwait(false);

        return results;
    }

    private async Task<DownloadResult> RunAsync(DownloadRequest request, Uri uri, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var verified = _verified is null ? null : await _verified.GetAsync(ct).ConfigureAwait(false);
        if (await IsValidExistingFileAsync(destination, request, verified, ct).ConfigureAwait(false))
            return new DownloadResult(request, DownloadStatus.AlreadyUpToDate, 0);

        string? lastError = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                var bytes = await AttemptAsync(request, uri, destination, progress, ct).ConfigureAwait(false);
                if (verified is not null && !string.IsNullOrWhiteSpace(request.ExpectedSha1)) verified.Remember(destination, new FileInfo(destination)); // хэш сошёлся при скачивании
                return new DownloadResult(request, DownloadStatus.Downloaded, bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // .part остаётся на диске: при следующем запуске загрузка продолжится
            }
            catch (HttpRequestException ex) when (IsPermanent(ex))
            {
                _logger.LogWarning(ex, "Загрузка {Url} отклонена сервером ({Status}), повторов не будет.", request.Url, ex.StatusCode);
                return new DownloadResult(request, DownloadStatus.Failed, 0, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Нет доступа к {Path}.", destination);
                return new DownloadResult(request, DownloadStatus.Failed, 0, ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or DownloadVerificationException)
            {
                lastError = ex is OperationCanceledException ? $"Нет данных от сервера дольше {_stallTimeout.TotalSeconds:0} с." : ex.Message;
                _logger.LogWarning(ex, "Загрузка {Url} не удалась (попытка {Attempt} из {Max}).", request.Url, attempt, _maxAttempts);
            }

            if (attempt < _maxAttempts && _retryDelay > TimeSpan.Zero)
                await Task.Delay(_retryDelay * attempt, ct).ConfigureAwait(false);
        }

        return new DownloadResult(request, DownloadStatus.Failed, 0, lastError ?? "Загрузка не удалась.");
    }

    private async Task<long> AttemptAsync(DownloadRequest request, Uri uri, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";

        long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (request.ExpectedSize is { } expectedSize && offset > expectedSize)
        {
            File.Delete(partPath);
            offset = 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_stallTimeout);

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) message.Headers.Range = new RangeHeaderValue(offset, null);

        using var response = await _httpFactory.CreateClient(HttpClientName)
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partPath);
            throw new IOException("Сервер отклонил докачку, файл будет скачан заново.");
        }
        response.EnsureSuccessStatusCode();

        var append = false;
        if (offset > 0)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset)
                {
                    File.Delete(partPath);
                    throw new IOException("Сервер вернул не тот фрагмент файла, файл будет скачан заново.");
                }
                append = true;
            }
            else
            {
                offset = 0; // сервер игнорирует Range и отдаёт файл целиком
            }
        }

        var headers = response.Content.Headers;
        long? total = append
            ? headers.ContentRange?.Length ?? (headers.ContentLength is { } rest ? (long?)(rest + offset) : null)
            : headers.ContentLength;

        long receivedTotal = offset;
        progress?.Report(new DownloadProgress(receivedTotal, total));

        await using (var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
        await using (var file = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    linked.CancelAfter(_stallTimeout);
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    receivedTotal += read;
                    progress?.Report(new DownloadProgress(receivedTotal, total));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
        }

        if (total is { } expectedTotal && receivedTotal != expectedTotal)
            throw new IOException($"Соединение оборвалось: получено {receivedTotal} из {expectedTotal} байт.");

        await VerifyAsync(partPath, request, ct).ConfigureAwait(false);
        File.Move(partPath, destination, overwrite: true);
        return receivedTotal;
    }

    /// <summary>Проверяет размер и SHA-1; при несовпадении удаляет «.part», чтобы следующая попытка началась с нуля.</summary>
    private static async Task VerifyAsync(string path, DownloadRequest request, CancellationToken ct)
    {
        if (request.ExpectedSize is { } size)
        {
            var actual = new FileInfo(path).Length;
            if (actual != size)
            {
                File.Delete(path);
                throw new DownloadVerificationException($"Размер файла {actual} байт, ожидалось {size}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha1) && !await FileVerifier.Sha1MatchesAsync(path, request.ExpectedSha1, ct).ConfigureAwait(false))
        {
            File.Delete(path);
            throw new DownloadVerificationException("Контрольная сумма SHA-1 не совпала.");
        }
    }

    private static async Task<bool> IsValidExistingFileAsync(string path, DownloadRequest request, VerifiedFilesSnapshot? verified, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (request.ExpectedSize is { } size && info.Length != size) return false;
            if (!string.IsNullOrWhiteSpace(request.ExpectedSha1))
            {
                // Файл уже проверяли и он не менялся (например, общий ассет другой версии) — хэш второй раз не считаем.
                if (verified is not null && verified.IsUnchanged(path, info)) return true;

                var matches = await FileVerifier.Sha1MatchesAsync(path, request.ExpectedSha1, ct).ConfigureAwait(false);
                if (matches) verified?.Remember(path, info);
                return matches;
            }
            return request.ExpectedSize is not null || request.ReuseExisting;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // файл не прочитать — пробуем скачать заново
        }
    }

    /// <summary>4xx, кроме «таймаут» и «слишком много запросов», повтор не исправит.</summary>
    private static bool IsPermanent(HttpRequestException ex) =>
        ex.StatusCode is { } code && (int)code is >= 400 and < 500 && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private sealed class DownloadVerificationException : Exception
    {
        public DownloadVerificationException(string message) : base(message) { }
    }

    /// <summary>Вызывает обработчик сразу в потоке загрузки (в отличие от <see cref="Progress{T}"/>, не уходит в SynchronizationContext).</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public InlineProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
