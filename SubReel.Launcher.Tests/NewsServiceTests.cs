using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Tests;

public sealed class NewsServiceTests
{
    private static NewsItem Item(string id, string source, int day) => new()
    {
        Id = id, Title = id, Source = source, Category = NewsCategory.Minecraft,
        PublishedAt = new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero),
    };

    private static NewsService Create(FakeNewsCache cache, ManualTimeProvider time, params INewsSource[] sources) =>
        new(sources, cache, time, NullLogger<NewsService>.Instance);

    [Fact]
    public async Task No_configured_source_reports_not_configured_and_touches_nothing()
    {
        var source = new FakeNewsSource("subreel", NewsCategory.SubReel) { IsConfigured = false };
        var cache = new FakeNewsCache();
        var service = Create(cache, new ManualTimeProvider(), source);

        var result = await service.LoadAsync(force: false);

        Assert.False(result.AnySourceConfigured);
        Assert.Equal(0, source.Calls);
        Assert.False(service.HasSourceFor(NewsCategory.SubReel));
    }

    [Fact]
    public async Task Fresh_cache_is_returned_without_network()
    {
        var time = new ManualTimeProvider();
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft);
        var cache = new FakeNewsCache { Snapshot = new NewsCacheSnapshot(time.Now.AddMinutes(-5), new[] { Item("a", "mojang", 1) }) };

        var result = await Create(cache, time, source).LoadAsync(force: false);

        Assert.True(result.FromCache);
        Assert.Equal(0, source.Calls);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Force_ignores_fresh_cache_and_writes_new_snapshot()
    {
        var time = new ManualTimeProvider();
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft);
        source.Items.Add(Item("new", "mojang", 5));
        var cache = new FakeNewsCache { Snapshot = new NewsCacheSnapshot(time.Now.AddMinutes(-1), new[] { Item("old", "mojang", 1) }) };

        var result = await Create(cache, time, source).LoadAsync(force: true);

        Assert.False(result.FromCache);
        Assert.Equal("new", result.Items[0].Id);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(time.Now, cache.Snapshot!.FetchedAt);
    }

    [Fact]
    public async Task Stale_cache_triggers_refresh()
    {
        var time = new ManualTimeProvider();
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft);
        source.Items.Add(Item("new", "mojang", 5));
        var cache = new FakeNewsCache { Snapshot = new NewsCacheSnapshot(time.Now.AddMinutes(-21), new[] { Item("old", "mojang", 1) }) };

        await Create(cache, time, source).LoadAsync(force: false);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task All_sources_failed_falls_back_to_saved_news()
    {
        var time = new ManualTimeProvider();
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft) { Failure = new HttpRequestException("offline") };
        var cache = new FakeNewsCache { Snapshot = new NewsCacheSnapshot(time.Now.AddHours(-3), new[] { Item("old", "mojang", 1) }) };

        var result = await Create(cache, time, source).LoadAsync(force: true);

        Assert.True(result.FromCache);
        Assert.False(result.Failed);
        Assert.Equal(new[] { "mojang" }, result.FailedSources);
        Assert.Equal(0, cache.Writes);
    }

    [Fact]
    public async Task All_sources_failed_without_cache_reports_failure()
    {
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft) { Failure = new HttpRequestException("offline") };

        var result = await Create(new FakeNewsCache(), new ManualTimeProvider(), source).LoadAsync(force: true);

        Assert.True(result.Failed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Partial_failure_keeps_old_items_of_failed_source_and_sorts_newest_first()
    {
        var time = new ManualTimeProvider();
        var ok = new FakeNewsSource("mojang", NewsCategory.Minecraft);
        ok.Items.Add(Item("fresh", "mojang", 20));
        var bad = new FakeNewsSource("subreel", NewsCategory.SubReel) { Failure = new HttpRequestException("down") };
        var cache = new FakeNewsCache
        {
            Snapshot = new NewsCacheSnapshot(time.Now.AddDays(-1), new[] { Item("kept", "subreel", 10), Item("stale-mojang", "mojang", 2) }),
        };

        var result = await Create(cache, time, ok, bad).LoadAsync(force: true);

        Assert.Equal(new[] { "fresh", "kept" }, result.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "subreel" }, result.FailedSources);
        Assert.Equal(1, cache.Writes);
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_source_failure()
    {
        var source = new FakeNewsSource("mojang", NewsCategory.Minecraft) { Failure = new OperationCanceledException() };
        var service = Create(new FakeNewsCache(), new ManualTimeProvider(), source);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.LoadAsync(force: true));
    }
}
