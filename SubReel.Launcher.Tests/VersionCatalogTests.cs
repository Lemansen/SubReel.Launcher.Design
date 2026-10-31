using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Versions;

namespace SubReel.Launcher.Tests;

public sealed class VersionCatalogTests
{
    private static McVersion Release(string id, int daysAgo, string? image = null) =>
        new() { Id = id, ReleaseTime = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-daysAgo), ImageUrl = image };

    [Fact]
    public void Fallback_is_offline_and_groups_versions_into_lines()
    {
        var catalog = VersionCatalog.CreateFallback();

        Assert.True(catalog.IsOffline);
        Assert.Equal("1.21.4", catalog.LatestRelease);
        Assert.Equal("1.21", catalog.LineOf("1.21.4")?.Key);
    }

    [Fact]
    public void Unknown_latest_release_falls_back_to_newest()
    {
        var catalog = VersionCatalog.Create(new[] { Release("1.20", 100), Release("1.21", 10) }, "9.9");

        Assert.Equal("1.21", catalog.LatestRelease);
    }

    [Fact]
    public void Old_lines_without_images_are_merged_into_one_legacy_card()
    {
        var catalog = VersionCatalog.Create(new[]
        {
            Release("1.21", 10, image: "https://x/img.jpg"),
            Release("1.2", 1000), Release("1.1", 1100), Release("1.0", 1200),
        }, "1.21");

        Assert.Equal(2, catalog.Groups.Count);
        Assert.True(catalog.Groups[^1].IsLegacy);
        Assert.Equal(3, catalog.Groups[^1].Lines!.Count);
        Assert.Equal(4, catalog.Lines.Count);
    }

    [Fact]
    public void Parser_builds_catalog_from_manifest_and_patch_notes()
    {
        const string manifest = """
            {"latest":{"release":"1.21.1","snapshot":"24w01a"},"versions":[
              {"id":"1.21.1","type":"release","releaseTime":"2024-08-08T12:00:00+00:00"},
              {"id":"24w01a","type":"snapshot","releaseTime":"2024-01-01T00:00:00+00:00"},
              {"id":"1.21","type":"release","releaseTime":"2024-06-13T12:00:00+00:00"}]}
            """;
        const string notes = """{"entries":[{"version":"1.21","title":"x","shortText":" Tricky ","image":{"url":"/img/a.jpg"}}]}""";

        var catalog = MojangCatalogParser.BuildCatalog(manifest, MojangCatalogParser.ParsePatchNotes(notes));

        Assert.Equal(2, catalog.Releases.Count);
        Assert.Equal("1.21.1", catalog.LatestRelease);
        Assert.Equal("https://launchercontent.mojang.com/img/a.jpg", catalog.ImageFor("1.21.1"));
        Assert.Equal("Tricky", catalog.SummaryFor("1.21"));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("нет сети");
    }

    [Fact]
    public async Task Network_failure_returns_offline_fallback()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var service = new MojangVersionCatalogService(http, NullLogger<MojangVersionCatalogService>.Instance);

        var catalog = await service.LoadAsync();

        Assert.True(catalog.IsOffline);
    }

    [Fact]
    public async Task Cancellation_is_propagated_not_swallowed()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var service = new MojangVersionCatalogService(http, NullLogger<MojangVersionCatalogService>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(cts.Token));
    }
}
