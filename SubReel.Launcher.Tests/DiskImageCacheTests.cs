using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Infrastructure.Images;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

public sealed class DiskImageCacheTests
{
    private static readonly byte[] Png = { 1, 2, 3, 4 };

    private static DiskImageCache Create(TempDir dir, StubHandler handler) =>
        new(new StubHttpClientFactory(handler), new AppPaths(dir.Root, dir.Root), NullLogger<DiskImageCache>.Instance);

    [Fact]
    public async Task Downloads_once_then_serves_from_disk()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => StubHandler.Ok(Png));
        using var cache = Create(dir, handler);

        var first = await cache.GetBytesAsync("https://example.test/a.png");
        var second = await cache.GetBytesAsync("https://example.test/a.png");

        Assert.Equal(Png, first);
        Assert.Equal(Png, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Concurrent_requests_for_same_url_share_one_download()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => StubHandler.Ok(Png));
        using var cache = Create(dir, handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetBytesAsync("https://example.test/b.png")));

        Assert.All(results, r => Assert.Equal(Png, r));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Offline_without_cache_returns_null()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var cache = Create(dir, handler);

        Assert.Null(await cache.GetBytesAsync("https://example.test/c.png"));
    }

    [Fact]
    public async Task Offline_with_stale_file_returns_stale_bytes()
    {
        using var dir = new TempDir();
        var online = true;
        var handler = new StubHandler(_ => online ? StubHandler.Ok(Png) : throw new HttpRequestException("offline"));
        using var cache = Create(dir, handler);
        await cache.GetBytesAsync("https://example.test/d.png");
        online = false;

        var bytes = await cache.GetBytesAsync("https://example.test/d.png", TimeSpan.Zero);

        Assert.Equal(Png, bytes);
    }

    [Fact]
    public async Task Invalidate_forces_new_download()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => StubHandler.Ok(Png));
        using var cache = Create(dir, handler);
        await cache.GetBytesAsync("https://example.test/e.png");

        cache.Invalidate("https://example.test/e.png");
        await cache.GetBytesAsync("https://example.test/e.png");

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Prefetch_fills_cache_for_all_urls()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => StubHandler.Ok(Png));
        using var cache = Create(dir, handler);
        var urls = Enumerable.Range(0, 5).Select(i => $"https://example.test/{i}.png").ToList();

        await cache.PrefetchAsync(urls);
        await cache.GetBytesAsync(urls[3]);

        Assert.Equal(5, handler.Requests.Count);
    }
}
