using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.News;
using SubReel.Launcher.Infrastructure.News;

namespace SubReel.Launcher.Tests;

public sealed class MojangNewsSourceTests
{
    private const string Json = """
        { "entries": [
            { "id": "a", "title": "Minecraft 1.21.1", "version": "1.21.1", "date": "2026-08-01T10:00:00+00:00",
              "shortText": "  Исправления ошибок  ", "image": { "url": "/images/a.jpg" } },
            { "id": "b", "title": "", "version": "1.20.4", "date": "2026-09-01T10:00:00+00:00" },
            { "id": "c", "title": "", "version": "" }
        ] }
        """;

    private static MojangNewsSource Create(StubHandler handler)
    {
        var store = new VersionStore(new FakeSettingsService(), new FakeCatalogService(), new FakeInstalledService(), NullLogger<VersionStore>.Instance);
        return new MojangNewsSource(new HttpClient(handler), store, NullLogger<MojangNewsSource>.Instance);
    }

    [Fact]
    public async Task Parses_entries_makes_urls_absolute_and_sorts_newest_first()
    {
        var items = await Create(new StubHandler(_ => StubHandler.Ok(Json))).LoadAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal("Minecraft 1.20.4", items[0].Title);
        Assert.Equal("Исправления ошибок", items[1].Summary);
        Assert.Equal("https://launchercontent.mojang.com/images/a.jpg", items[1].ImageUrl);
        Assert.All(items, i => Assert.Equal(NewsCategory.Minecraft, i.Category));
    }

    [Fact]
    public async Task Falls_back_to_second_url_when_first_fails()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/v2/")
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            : StubHandler.Ok(Json));

        var items = await Create(handler).LoadAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Throws_when_every_url_fails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Valid_empty_answer_is_not_a_failure()
    {
        var items = await Create(new StubHandler(_ => StubHandler.Ok("""{ "entries": [] }"""))).LoadAsync(CancellationToken.None);

        Assert.Empty(items);
    }
}
