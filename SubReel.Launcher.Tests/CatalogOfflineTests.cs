using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Infrastructure.Versions;

namespace SubReel.Launcher.Tests;

/// <summary>Каталог версий без сети: копия на диске, пометка «устарел» и обновление, когда сеть вернулась.</summary>
public sealed class CatalogOfflineTests
{
    private const string Manifest = """
        {"latest":{"release":"1.21.1","snapshot":"24w01a"},"versions":[
          {"id":"1.21.1","type":"release","releaseTime":"2024-08-08T12:00:00+00:00"},
          {"id":"1.21","type":"release","releaseTime":"2024-06-13T12:00:00+00:00"}]}
        """;

    private const string Notes = """{"entries":[{"version":"1.21","title":"x","shortText":"Tricky","image":{"url":"/img/a.jpg"}}]}""";

    /// <summary>Сеть, которую можно «включать» и «выключать».</summary>
    private sealed class SwitchableHandler : HttpMessageHandler
    {
        public bool Online { get; set; } = true;
        public bool NotesOnline { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Online) throw new HttpRequestException("нет сети");
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("version_manifest")) return Task.FromResult(Ok(Manifest));
            if (!NotesOnline) throw new HttpRequestException("патчноуты недоступны");
            return Task.FromResult(Ok(Notes));
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static MojangVersionCatalogService Create(TempDir dir, SwitchableHandler handler) =>
        new(new HttpClient(handler), NullLogger<MojangVersionCatalogService>.Instance, new AppPaths(dir.Root, dir.Root, dir.Root));

    [Fact]
    public async Task Successful_load_saves_copy_and_is_not_stale()
    {
        using var dir = new TempDir();
        var catalog = await Create(dir, new SwitchableHandler()).LoadAsync();

        Assert.False(catalog.IsOffline);
        Assert.False(catalog.IsStale);
        Assert.True(File.Exists(Path.Combine(dir.Root, "cache", "version_manifest.json")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "cache", "patch_notes.json")));
    }

    [Fact]
    public async Task Without_network_catalog_comes_from_saved_copy_with_dates_and_images()
    {
        using var dir = new TempDir();
        var handler = new SwitchableHandler();
        var service = Create(dir, handler);
        await service.LoadAsync();

        handler.Online = false;
        var catalog = await Create(dir, handler).LoadAsync(); // «перезапуск» без сети

        Assert.False(catalog.IsOffline); // даты и картинки настоящие
        Assert.True(catalog.IsStale);
        Assert.Equal("1.21.1", catalog.LatestRelease);
        Assert.Equal(new DateTimeOffset(2024, 8, 8, 12, 0, 0, TimeSpan.Zero), catalog.Find("1.21.1")!.ReleaseTime);
        Assert.NotNull(catalog.ImageFor("1.21"));
    }

    [Fact]
    public async Task Without_network_and_without_copy_catalog_is_builtin_offline()
    {
        using var dir = new TempDir();
        var catalog = await Create(dir, new SwitchableHandler { Online = false }).LoadAsync();

        Assert.True(catalog.IsOffline);
        Assert.False(catalog.IsStale);
    }

    [Fact]
    public async Task Corrupted_copy_falls_back_to_builtin_catalog()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "cache"));
        File.WriteAllText(Path.Combine(dir.Root, "cache", "version_manifest.json"), "{ это не json");

        var catalog = await Create(dir, new SwitchableHandler { Online = false }).LoadAsync();

        Assert.True(catalog.IsOffline);
    }

    [Fact]
    public async Task Saved_patch_notes_are_used_when_only_notes_are_unavailable()
    {
        using var dir = new TempDir();
        var handler = new SwitchableHandler();
        await Create(dir, handler).LoadAsync();

        handler.NotesOnline = false;
        var catalog = await Create(dir, handler).LoadAsync();

        Assert.False(catalog.IsStale); // список версий свежий
        Assert.NotNull(catalog.ImageFor("1.21")); // а картинки — из сохранённых патчноутов
        Assert.True(File.Exists(Path.Combine(dir.Root, "cache", "patch_notes.json")));
    }

    // ───────────── VersionStore: обновление после появления сети ─────────────

    private sealed class ScriptedCatalogService : IVersionCatalogService
    {
        public VersionCatalog Next { get; set; } = VersionCatalog.CreateFallback();
        public int LoadCount { get; private set; }
        public VersionCatalog CreateFallback() => VersionCatalog.CreateFallback();

        public Task<VersionCatalog> LoadAsync(CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult(Next);
        }
    }

    private static VersionCatalog Online() => MojangCatalogParser.BuildCatalog(Manifest, MojangCatalogParser.ParsePatchNotes(Notes));

    [Fact]
    public async Task Store_stays_offline_until_network_returns_and_then_swaps_catalog()
    {
        var settings = new FakeSettingsService();
        var catalogs = new ScriptedCatalogService();
        var store = new VersionStore(settings, catalogs, new FakeInstalledService(), NullLogger<VersionStore>.Instance);
        await store.InitializeAsync();
        var refreshed = 0;
        store.CatalogRefreshed += (_, _) => refreshed++;

        Assert.True(store.NeedsCatalogRefresh);
        Assert.False(await store.RefreshCatalogAsync()); // сети всё ещё нет
        Assert.True(store.Catalog.IsOffline);
        Assert.Equal(0, refreshed);

        catalogs.Next = Online();
        Assert.True(await store.RefreshCatalogAsync());

        Assert.False(store.Catalog.IsOffline);
        Assert.False(store.NeedsCatalogRefresh);
        Assert.Equal(1, refreshed);
        Assert.Equal("1.21.1", store.SelectedVersion); // первый запуск был без сети: теперь выбран настоящий последний релиз
        Assert.Equal("1.21.1", settings.Current.SelectedVersion);
    }

    [Fact]
    public async Task Refresh_does_not_touch_saved_selection()
    {
        var settings = new FakeSettingsService();
        settings.Current.SelectedVersion = "1.20.1";
        var catalogs = new ScriptedCatalogService();
        var store = new VersionStore(settings, catalogs, new FakeInstalledService(), NullLogger<VersionStore>.Instance);
        await store.InitializeAsync();

        catalogs.Next = Online();
        await store.RefreshCatalogAsync();

        Assert.Equal("1.20.1", store.SelectedVersion);
    }

    [Fact]
    public async Task Stale_copy_is_refreshed_but_stale_result_is_not_accepted()
    {
        var catalogs = new ScriptedCatalogService { Next = Online().AsStale() };
        var store = new VersionStore(new FakeSettingsService(), catalogs, new FakeInstalledService(), NullLogger<VersionStore>.Instance);
        await store.InitializeAsync();

        Assert.True(store.NeedsCatalogRefresh);
        Assert.False(await store.RefreshCatalogAsync()); // снова копия, а не свежие данные

        catalogs.Next = Online();
        Assert.True(await store.RefreshCatalogAsync());
        Assert.False(store.NeedsCatalogRefresh);
    }

    [Fact]
    public async Task Fresh_catalog_is_not_reloaded()
    {
        var catalogs = new ScriptedCatalogService { Next = Online() };
        var store = new VersionStore(new FakeSettingsService(), catalogs, new FakeInstalledService(), NullLogger<VersionStore>.Instance);
        await store.InitializeAsync();
        var loads = catalogs.LoadCount;

        Assert.False(await store.RefreshCatalogAsync());
        Assert.Equal(loads, catalogs.LoadCount);
    }
}
