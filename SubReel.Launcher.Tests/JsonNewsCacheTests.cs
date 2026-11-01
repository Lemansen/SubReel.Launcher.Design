using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.News;
using SubReel.Launcher.Infrastructure.News;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

public sealed class JsonNewsCacheTests
{
    private static JsonNewsCache Create(TempDir dir, out AppPaths paths)
    {
        paths = new AppPaths(dir.Root, dir.Root);
        return new JsonNewsCache(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<JsonNewsCache>.Instance);
    }

    [Fact]
    public async Task Missing_cache_reads_as_null()
    {
        using var dir = new TempDir();
        Assert.Null(await Create(dir, out _).ReadAsync());
    }

    [Fact]
    public async Task Write_then_read_round_trips_items_and_time()
    {
        using var dir = new TempDir();
        var cache = Create(dir, out _);
        var at = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);
        var item = new NewsItem { Id = "mc:1", Title = "Заголовок", Category = NewsCategory.Minecraft, Source = "mojang", PublishedAt = at };

        await cache.WriteAsync(new NewsCacheSnapshot(at, new[] { item }));
        var read = await cache.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(at, read!.FetchedAt);
        Assert.Equal("Заголовок", read.Items.Single().Title);
        Assert.Equal(NewsCategory.Minecraft, read.Items.Single().Category);
    }

    [Fact]
    public async Task Corrupt_cache_reads_as_null_and_is_not_deleted()
    {
        using var dir = new TempDir();
        var cache = Create(dir, out var paths);
        Directory.CreateDirectory(paths.CacheDirectory);
        await File.WriteAllTextAsync(paths.NewsCacheFile, "{ not json");

        Assert.Null(await cache.ReadAsync());
        Assert.True(Directory.GetFiles(paths.CacheDirectory).Length > 0);
    }
}
