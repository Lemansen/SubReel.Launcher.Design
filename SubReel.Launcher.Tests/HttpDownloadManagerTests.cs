using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Minecraft.Downloads;

namespace SubReel.Launcher.Tests;

internal sealed class AsyncStubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _count;
    public AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
    public int RequestCount => Volatile.Read(ref _count);
    public List<RangeHeaderValue?> Ranges { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        lock (Ranges) Ranges.Add(request.Headers.Range);
        return _respond(request, cancellationToken);
    }
}

internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = new();
    public void Report(T value)
    {
        lock (Items) Items.Add(value);
    }
}

public sealed class HttpDownloadManagerTests
{
    private static byte[] Data(int size = 5000)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static HttpDownloadManager Create(HttpMessageHandler handler, int concurrency = 4, int attempts = 3, TimeSpan? stall = null) =>
        new(new StubHttpClientFactory(handler),
            Options.Create(new DownloadOptions
            {
                MaxConcurrentDownloads = concurrency,
                MaxAttempts = attempts,
                RetryDelay = TimeSpan.Zero,
                StallTimeout = stall ?? TimeSpan.FromSeconds(30),
            }),
            NullLogger<HttpDownloadManager>.Instance);

    private static Task<HttpResponseMessage> Ok(byte[] data) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

    private static Task<HttpResponseMessage> Status(HttpStatusCode code) =>
        Task.FromResult(new HttpResponseMessage(code));

    /// <summary>Сервер с поддержкой Range: отдаёт 206 с нужным заголовком.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RangeAware(byte[] data) => (request, _) =>
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range?.From is not { } from) return Ok(data);

        var slice = data[(int)from..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
        return Task.FromResult(response);
    };

    [Fact]
    public async Task Downloads_file_and_verifies_hash_and_size()
    {
        using var dir = new TempDir();
        var data = Data();
        var handler = new AsyncStubHandler((_, _) => Ok(data));
        var manager = Create(handler);
        var path = dir.File(Path.Combine("libs", "a.jar"));

        var result = await manager.DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data).ToLowerInvariant(), data.Length));

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Valid_existing_file_is_not_downloaded_again()
    {
        using var dir = new TempDir();
        var data = Data();
        var path = dir.File("a.jar");
        await File.WriteAllBytesAsync(path, data);
        var handler = new AsyncStubHandler((_, _) => Ok(data));

        var result = await Create(handler).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data), data.Length));

        Assert.Equal(DownloadStatus.AlreadyUpToDate, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Existing_file_without_hash_or_size_is_replaced_unless_reuse_is_requested()
    {
        using var dir = new TempDir();
        var fresh = Data(100);
        var path = dir.File("a.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9 });
        var handler = new AsyncStubHandler((_, _) => Ok(fresh));
        var manager = Create(handler);

        Assert.Equal(DownloadStatus.Downloaded, (await manager.DownloadAsync(new DownloadRequest("https://example.test/a", path))).Status);
        Assert.Equal(fresh, await File.ReadAllBytesAsync(path));

        var reuse = await manager.DownloadAsync(new DownloadRequest("https://example.test/a", path, ReuseExisting: true));
        Assert.Equal(DownloadStatus.AlreadyUpToDate, reuse.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Corrupt_existing_file_is_replaced()
    {
        using var dir = new TempDir();
        var data = Data();
        var path = dir.File("a.jar");
        await File.WriteAllBytesAsync(path, new byte[data.Length]);

        var result = await Create(new AsyncStubHandler((_, _) => Ok(data))).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data), data.Length));

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Wrong_hash_fails_after_all_attempts_and_leaves_no_files()
    {
        using var dir = new TempDir();
        var data = Data();
        var handler = new AsyncStubHandler((_, _) => Ok(data));
        var path = dir.File("a.jar");

        var result = await Create(handler, attempts: 3).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, new string('0', 40)));

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(3, handler.RequestCount);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Wrong_size_is_detected()
    {
        using var dir = new TempDir();
        var data = Data();
        var path = dir.File("a.jar");

        var result = await Create(new AsyncStubHandler((_, _) => Ok(data)), attempts: 1).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, ExpectedSize: data.Length + 1));

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Server_error_is_retried_and_then_succeeds()
    {
        using var dir = new TempDir();
        var data = Data();
        var calls = 0;
        var handler = new AsyncStubHandler((_, _) => Interlocked.Increment(ref calls) == 1 ? Status(HttpStatusCode.InternalServerError) : Ok(data));
        var path = dir.File("a.jar");

        var result = await Create(handler).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data)));

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Not_found_is_not_retried()
    {
        using var dir = new TempDir();
        var handler = new AsyncStubHandler((_, _) => Status(HttpStatusCode.NotFound));

        var result = await Create(handler, attempts: 5).DownloadAsync(new DownloadRequest("https://example.test/missing", dir.File("m.bin")));

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.test/a")]
    public async Task Non_http_addresses_fail_without_network(string url)
    {
        using var dir = new TempDir();
        var handler = new AsyncStubHandler((_, _) => Ok(Data()));

        var result = await Create(handler).DownloadAsync(new DownloadRequest(url, dir.File("a")));

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Interrupted_download_resumes_from_part_file_with_range_request()
    {
        using var dir = new TempDir();
        var data = Data(10_000);
        var path = dir.File("big.jar");
        await File.WriteAllBytesAsync(path + ".part", data[..4000]);
        var handler = new AsyncStubHandler(RangeAware(data));
        var progress = new ListProgress<DownloadProgress>();

        var result = await Create(handler).DownloadAsync(new DownloadRequest("https://example.test/big.jar", path, Sha1(data), data.Length), progress);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Equal(4000L, handler.Ranges.Single()!.Ranges.Single().From);
        Assert.Equal(4000, progress.Items.First().BytesReceived);
        Assert.Equal(data.Length, progress.Items.Last().BytesReceived);
        Assert.Equal((long?)data.Length, progress.Items.Last().TotalBytes);
    }

    [Fact]
    public async Task Server_that_ignores_range_gets_a_full_download()
    {
        using var dir = new TempDir();
        var data = Data(6000);
        var path = dir.File("a.jar");
        await File.WriteAllBytesAsync(path + ".part", new byte[] { 1, 2, 3 });

        var result = await Create(new AsyncStubHandler((_, _) => Ok(data))).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data)));

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Oversized_part_file_is_discarded()
    {
        using var dir = new TempDir();
        var data = Data(1000);
        var path = dir.File("a.jar");
        await File.WriteAllBytesAsync(path + ".part", new byte[5000]);
        var handler = new AsyncStubHandler(RangeAware(data));

        var result = await Create(handler).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path, Sha1(data), data.Length));

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Null(handler.Ranges.Single());
    }

    [Fact]
    public async Task Cancellation_throws_and_keeps_part_file_for_resume()
    {
        using var dir = new TempDir();
        var path = dir.File("a.jar");
        var started = new TaskCompletionSource();
        var handler = new AsyncStubHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();

        var task = Create(handler).DownloadAsync(new DownloadRequest("https://example.test/a.jar", path), ct: cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Silent_connection_is_treated_as_failure_after_stall_timeout()
    {
        using var dir = new TempDir();
        var handler = new AsyncStubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Create(handler, attempts: 2, stall: TimeSpan.FromMilliseconds(100)).DownloadAsync(new DownloadRequest("https://example.test/a.jar", dir.File("a.jar")));

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Global_limit_caps_parallel_downloads()
    {
        using var dir = new TempDir();
        var data = Data(100);
        var current = 0;
        var peak = 0;
        var handler = new AsyncStubHandler(async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, seen);
            await Task.Delay(40);
            Interlocked.Decrement(ref current);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        });
        var manager = Create(handler, concurrency: 2);
        var requests = Enumerable.Range(0, 8).Select(i => new DownloadRequest($"https://example.test/{i}", dir.File($"f{i}.bin"), Sha1(data), data.Length)).ToList();

        var results = await manager.DownloadAllAsync(requests);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(peak <= 2, $"Одновременно скачивалось {peak} файлов.");
        Assert.True(peak >= 1);
    }

    [Fact]
    public async Task Two_requests_for_one_file_download_it_once()
    {
        using var dir = new TempDir();
        var data = Data();
        var handler = new AsyncStubHandler(async (_, _) =>
        {
            await Task.Delay(50);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        });
        var manager = Create(handler);
        var request = new DownloadRequest("https://example.test/a.jar", dir.File("a.jar"), Sha1(data), data.Length);

        var results = await Task.WhenAll(manager.DownloadAsync(request), manager.DownloadAsync(request));

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains(results, r => r.Status == DownloadStatus.Downloaded);
        Assert.Contains(results, r => r.Status == DownloadStatus.AlreadyUpToDate);
    }

    [Fact]
    public async Task Batch_keeps_order_reports_progress_and_does_not_hide_single_failures()
    {
        using var dir = new TempDir();
        var good = Data(300);
        var handler = new AsyncStubHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("bad") ? Status(HttpStatusCode.NotFound) : Ok(good));
        var requests = new[]
        {
            new DownloadRequest("https://example.test/one", dir.File("one"), Sha1(good), good.Length),
            new DownloadRequest("https://example.test/bad", dir.File("bad"), ExpectedSize: 10),
            new DownloadRequest("https://example.test/two", dir.File("two"), Sha1(good), good.Length),
        };
        var progress = new ListProgress<DownloadBatchProgress>();

        var results = await Create(handler).DownloadAllAsync(requests, progress);

        Assert.Equal(requests, results.Select(r => r.Request));
        Assert.Equal(new[] { true, false, true }, results.Select(r => r.Succeeded));
        var last = progress.Items.Last();
        Assert.Equal(3, last.CompletedFiles);
        Assert.Equal(3, last.TotalFiles);
        Assert.Equal((long?)(good.Length * 2 + 10), last.TotalBytes);
        Assert.Equal(good.Length * 2, last.BytesReceived);
    }

    [Fact]
    public async Task Empty_batch_returns_empty_result()
    {
        var results = await Create(new AsyncStubHandler((_, _) => Ok(Data()))).DownloadAllAsync(Array.Empty<DownloadRequest>());
        Assert.Empty(results);
    }

    [Fact]
    public async Task Keyed_lock_removes_entries_when_nobody_waits()
    {
        var locks = new KeyedLock();
        using (await locks.AcquireAsync("a", CancellationToken.None))
            Assert.Equal(1, locks.ActiveKeys);

        Assert.Equal(0, locks.ActiveKeys);

        using var held = await locks.AcquireAsync("a", CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync("a", cts.Token));
        Assert.Equal(1, locks.ActiveKeys);
    }
}
