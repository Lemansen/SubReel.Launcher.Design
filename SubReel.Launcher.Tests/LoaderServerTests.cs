using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Downloads;
using SubReel.Launcher.Minecraft.Installation;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Loaders;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Tests;

/// <summary>Мета-серверы Fabric и Quilt в памяти; настоящие каталог, установщик и менеджер загрузок работают с ними поверх временной папки.</summary>
internal sealed class FakeLoaderMeta : IDisposable
{
    public const string FabricBase = "https://meta.fabric.test/v2";
    public const string QuiltBase = "https://meta.quilt.test/v3";

    private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
    private readonly List<string> _requested = new();

    public FakeLoaderMeta()
    {
        Dir = new TempDir();
        Layout = new GameFileLayout(new AppPaths(Dir.Root, Dir.Root));
        Endpoints = new LoaderEndpoints(FabricBase, QuiltBase);
        Time = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };

        var handler = new AsyncStubHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            lock (_requested) _requested.Add(url);
            return Task.FromResult(_bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        Downloads = new HttpDownloadManager(
            new StubHttpClientFactory(handler),
            Options.Create(new DownloadOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero }),
            NullLogger<HttpDownloadManager>.Instance);
        Catalog = new LoaderCatalog(Downloads, Layout, Endpoints, Time, NullLogger<LoaderCatalog>.Instance);
        Installer = new LoaderInstaller(Catalog, Downloads, Layout, Endpoints, NullLogger<LoaderInstaller>.Instance);
    }

    public TempDir Dir { get; }
    public GameFileLayout Layout { get; }
    public LoaderEndpoints Endpoints { get; }
    public ManualTimeProvider Time { get; }
    public HttpDownloadManager Downloads { get; }
    public LoaderCatalog Catalog { get; }
    public LoaderInstaller Installer { get; }

    public void Serve(string url, string body) => _bodies[url] = body;

    public void Break(string url) => _bodies.Remove(url);

    public int Count(string url)
    {
        lock (_requested) return _requested.Count(r => r == url);
    }

    public int TotalRequests
    {
        get
        {
            lock (_requested) return _requested.Count;
        }
    }

    public static string Profile(string id, string inheritsFrom, string mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient") =>
        JsonSerializer.Serialize(new
        {
            id,
            inheritsFrom,
            type = "release",
            mainClass,
            arguments = new { game = Array.Empty<string>(), jvm = new[] { "-DFabricMcEmu= net.minecraft.client.main.Main " } },
            libraries = new object[] { new { name = "net.fabricmc:fabric-loader:0.16.5", url = "https://maven.fabricmc.net/" } },
        });

    public void Dispose() => Dir.Dispose();
}

public sealed class LoaderCatalogTests
{
    private const string ListUrl = FakeLoaderMeta.FabricBase + "/versions/loader/1.21.1";

    [Fact]
    public async Task List_is_downloaded_parsed_and_kept_on_disk()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(ListUrl, LoaderMetaParserTests.FabricList(("0.16.5", true)));

        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        Assert.Equal("0.16.5", Assert.Single(builds).Version);
        Assert.True(File.Exists(meta.Layout.LoaderBuildsPath(LoaderKind.Fabric, "1.21.1")));
    }

    [Fact]
    public async Task Fresh_copy_is_not_downloaded_again()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(ListUrl, LoaderMetaParserTests.FabricList(("0.16.5", true)));

        await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");
        await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        Assert.Equal(1, meta.Count(ListUrl));
    }

    [Fact]
    public async Task Stale_copy_is_refreshed_and_replaced()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(ListUrl, LoaderMetaParserTests.FabricList(("0.16.5", true)));
        await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        meta.Time.Now = DateTimeOffset.UtcNow.AddHours(2);
        meta.Serve(ListUrl, LoaderMetaParserTests.FabricList(("0.16.9", true), ("0.16.5", true)));
        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        Assert.Equal(2, meta.Count(ListUrl));
        Assert.Equal("0.16.9", builds[0].Version);
    }

    [Fact]
    public async Task Without_network_the_stale_copy_is_used()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(ListUrl, LoaderMetaParserTests.FabricList(("0.16.5", true)));
        await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        meta.Time.Now = DateTimeOffset.UtcNow.AddHours(2);
        meta.Break(ListUrl);
        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1");

        Assert.Equal("0.16.5", Assert.Single(builds).Version);
    }

    [Fact]
    public async Task Without_network_and_without_copy_it_is_a_catalog_error()
    {
        using var meta = new FakeLoaderMeta();

        await Assert.ThrowsAsync<LoaderCatalogException>(() => meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1"));
    }

    [Fact]
    public async Task Quilt_uses_its_own_server()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FakeLoaderMeta.QuiltBase + "/versions/loader/1.21.1", LoaderMetaParserTests.QuiltList("0.26.0"));

        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Quilt, "1.21.1");

        Assert.Equal((LoaderKind.Quilt, "0.26.0"), (builds[0].Kind, builds[0].Version));
    }

    [Fact]
    public async Task Loaders_that_cannot_be_installed_give_an_empty_list_without_requests()
    {
        using var meta = new FakeLoaderMeta();

        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Vanilla, "1.21.1");

        Assert.Empty(builds);
        Assert.Equal(0, meta.TotalRequests);
    }

    [Fact]
    public async Task Forge_builds_come_from_maven_metadata_and_are_kept_on_disk()
    {
        using var meta = new FakeLoaderMeta();
        var url = meta.Endpoints.ForgeBuildListUrl;
        meta.Serve(url, LoaderMavenParserTests.ForgeXml("1.21.1-52.0.0", "1.21.1-52.0.2", "1.20.1-47.2.0"));

        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.Forge, "1.21.1");
        await meta.Catalog.GetBuildsAsync(LoaderKind.Forge, "1.20.1");

        Assert.Equal(new[] { "52.0.2", "52.0.0" }, builds.Select(b => b.Version).ToArray());
        Assert.Equal(1, meta.Count(url)); // второй вызов взял свежую копию
    }

    [Fact]
    public async Task NeoForge_builds_come_from_maven_metadata()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(meta.Endpoints.NeoForgeBuildListUrl, LoaderMavenParserTests.ForgeXml("21.0.5", "21.1.172", "21.1.3"));

        var builds = await meta.Catalog.GetBuildsAsync(LoaderKind.NeoForge, "1.21.1");

        Assert.Equal(new[] { "21.1.172", "21.1.3" }, builds.Select(b => b.Version).ToArray());
    }

    [Fact]
    public async Task Maven_catalog_without_network_and_copy_is_a_catalog_error()
    {
        using var meta = new FakeLoaderMeta();

        await Assert.ThrowsAsync<LoaderCatalogException>(() => meta.Catalog.GetBuildsAsync(LoaderKind.NeoForge, "1.21.1"));
    }

    [Fact]
    public async Task Corrupt_maven_copy_is_removed()
    {
        using var meta = new FakeLoaderMeta();
        var path = meta.Layout.LoaderBuildsPath(LoaderKind.Forge, "maven-metadata");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "<not xml");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // свежая: сеть не нужна

        await Assert.ThrowsAsync<LoaderCatalogException>(() => meta.Catalog.GetBuildsAsync(LoaderKind.Forge, "1.21.1"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Unsafe_game_version_is_refused_before_any_request()
    {
        using var meta = new FakeLoaderMeta();

        await Assert.ThrowsAsync<LoaderCatalogException>(() => meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "../1"));
        Assert.Equal(0, meta.TotalRequests);
    }

    [Fact]
    public async Task Corrupt_copy_is_removed_so_the_next_call_downloads_again()
    {
        using var meta = new FakeLoaderMeta();
        var path = meta.Layout.LoaderBuildsPath(LoaderKind.Fabric, "1.21.1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ garbage");

        await Assert.ThrowsAsync<LoaderCatalogException>(() => meta.Catalog.GetBuildsAsync(LoaderKind.Fabric, "1.21.1"));
        Assert.False(File.Exists(path));
    }
}

public sealed class LoaderInstallerTests
{
    private const string Mc = "1.21.1";
    private const string FabricId = "fabric-loader-0.16.5-1.21.1";
    private const string FabricProfileUrl = FakeLoaderMeta.FabricBase + "/versions/loader/1.21.1/0.16.5/profile/json";
    private const string FabricListUrl = FakeLoaderMeta.FabricBase + "/versions/loader/1.21.1";

    [Fact]
    public async Task Pinned_version_is_downloaded_validated_and_placed_in_the_shared_cache()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, Mc));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(FabricId, result.VersionId);
        Assert.Equal("0.16.5", result.LoaderVersion);
        var path = meta.Layout.VersionJsonPath(FabricId);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".download"));
        Assert.Equal(0, meta.Count(FabricListUrl));
    }

    [Fact]
    public async Task Latest_stable_build_is_chosen_when_the_version_is_not_given()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricListUrl, LoaderMetaParserTests.FabricList(("0.17.0", false), ("0.16.5", true)));
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, Mc));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("0.16.5", result.LoaderVersion);
    }

    [Fact]
    public async Task First_build_is_used_when_none_is_stable()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricListUrl, LoaderMetaParserTests.FabricList(("0.17.0", false)));
        meta.Serve(FakeLoaderMeta.FabricBase + "/versions/loader/1.21.1/0.17.0/profile/json", FakeLoaderMeta.Profile("fabric-loader-0.17.0-1.21.1", Mc));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "  ");

        Assert.Equal("0.17.0", result.LoaderVersion);
    }

    [Fact]
    public async Task Installed_profile_is_not_downloaded_again()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, Mc));

        await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");
        var second = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.True(second.Succeeded);
        Assert.Equal(1, meta.Count(FabricProfileUrl));
    }

    [Fact]
    public async Task Broken_profile_on_disk_is_replaced()
    {
        using var meta = new FakeLoaderMeta();
        var path = meta.Layout.VersionJsonPath(FabricId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ garbage");
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, Mc));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("KnotClient", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Profile_based_on_another_game_version_is_rejected_and_not_kept()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, "1.20.1"));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(meta.Layout.VersionJsonPath(FabricId)));
        Assert.False(File.Exists(meta.Layout.VersionJsonPath(FabricId) + ".download"));
    }

    [Fact]
    public async Task Answer_that_is_not_a_profile_is_rejected()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricProfileUrl, "<html>oops</html>");

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(meta.Layout.VersionJsonPath(FabricId)));
    }

    [Fact]
    public async Task Server_error_is_reported_with_the_loader_and_versions()
    {
        using var meta = new FakeLoaderMeta();

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        Assert.False(result.Succeeded);
        Assert.Contains("Fabric 0.16.5", result.Error);
        Assert.Contains(Mc, result.Error);
    }

    [Fact]
    public async Task Game_version_unknown_to_the_loader_is_reported()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricListUrl, "[]");

        var result = await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, null);

        Assert.False(result.Succeeded);
        Assert.Contains("не поддерживает", result.Error);
    }

    [Fact]
    public async Task Quilt_profile_comes_from_the_quilt_server()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FakeLoaderMeta.QuiltBase + "/versions/loader/1.21.1/0.26.0/profile/json",
            FakeLoaderMeta.Profile("quilt-loader-0.26.0-1.21.1", Mc, "org.quiltmc.loader.impl.launch.knot.KnotClient"));

        var result = await meta.Installer.EnsureAsync(LoaderKind.Quilt, Mc, "0.26.0");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("quilt-loader-0.26.0-1.21.1", result.VersionId);
    }

    [Theory]
    [InlineData(LoaderKind.Vanilla, Mc, "47.2.0")]
    [InlineData(LoaderKind.Forge, Mc, "47.2.0")] // без Java Manager современный установщик не запускается, и скачивать его незачем
    [InlineData(LoaderKind.NeoForge, Mc, "21.1.172")]
    [InlineData(LoaderKind.Fabric, Mc, "../x")]
    [InlineData(LoaderKind.Fabric, "a/b", "0.16.5")]
    [InlineData(LoaderKind.Fabric, "", "0.16.5")]
    public async Task Unsupported_loader_or_unsafe_versions_fail_without_any_request(LoaderKind kind, string mc, string loader)
    {
        using var meta = new FakeLoaderMeta();

        var result = await meta.Installer.EnsureAsync(kind, mc, loader);

        Assert.False(result.Succeeded);
        Assert.Equal(0, meta.TotalRequests);
    }

    [Fact]
    public async Task Installed_fabric_profile_resolves_with_its_vanilla_parent_and_plans_loader_libraries()
    {
        using var meta = new FakeLoaderMeta();
        meta.Serve(FabricProfileUrl, FakeLoaderMeta.Profile(FabricId, Mc));
        await meta.Installer.EnsureAsync(LoaderKind.Fabric, Mc, "0.16.5");

        var parentPath = meta.Layout.VersionJsonPath(Mc);
        Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);
        await File.WriteAllTextAsync(parentPath, JsonSerializer.Serialize(new
        {
            id = Mc,
            mainClass = "net.minecraft.client.main.Main",
            assets = "17",
            downloads = new { client = new { url = "https://dl.test/client.jar", sha1 = new string('a', 40), size = 3 } },
            libraries = new object[]
            {
                new { name = "org.lwjgl:lwjgl:3.3.3", downloads = new { artifact = new { path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", url = "https://dl.test/lwjgl.jar", sha1 = new string('b', 40), size = 1 } } },
            },
        }));

        var manifest = new VersionManifestSource(meta.Downloads, meta.Layout, meta.Time, NullLogger<VersionManifestSource>.Instance);
        var provider = new VersionMetadataProvider(manifest, meta.Downloads, meta.Layout, NullLogger<VersionMetadataProvider>.Instance);
        var version = await provider.ResolveAsync(FabricId, allowNetwork: false);

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", version.MainClass);
        Assert.Equal(new[] { FabricId, Mc }, version.Chain.ToArray());
        Assert.Equal(Mc, version.ClientJarVersionId);

        var plan = new GameFilePlanner(meta.Layout).PlanCore(version, new OsEnvironment("windows", "x86_64", "10.0"));
        Assert.Contains(plan, r => r.Url == "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.5/fabric-loader-0.16.5.jar");
        Assert.Contains(plan, r => r.Url == "https://dl.test/lwjgl.jar");
        Assert.Contains(plan, r => r.Url == "https://dl.test/client.jar");
    }
}
