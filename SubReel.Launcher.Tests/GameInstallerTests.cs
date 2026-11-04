using System.Text;
using SubReel.Launcher.Minecraft.Downloads;
using SubReel.Launcher.Application.Downloads;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Minecraft.Installation;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Tests;

public sealed class GameFilePlannerTests
{
    [Fact]
    public async Task Core_plan_contains_only_files_needed_on_this_platform()
    {
        using var world = new FakeGameWorld();
        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId);

        var plan = world.Planner.PlanCore(version, world.Environment);
        var urls = plan.Select(r => r.Url).ToList();

        Assert.Equal(8, plan.Count);
        Assert.Contains("https://dl.test/client.jar", urls);
        Assert.Contains("https://dl.test/log.xml", urls);
        Assert.Contains("https://dl.test/index17.json", urls);
        Assert.Contains("https://dl.test/old/platform/1.0/platform-1.0-natives-windows.jar", urls);
        Assert.Contains("https://dl.test/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar", urls);
        Assert.DoesNotContain(urls, u => u.Contains("linux"));
        Assert.All(plan, r => Assert.NotNull(r.ExpectedSha1));
    }

    [Fact]
    public async Task Linux_plan_picks_linux_natives()
    {
        using var world = new FakeGameWorld();
        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId);

        var urls = world.Planner.PlanCore(version, new OsEnvironment("linux", "x86_64")).Select(r => r.Url).ToList();

        Assert.Contains("https://dl.test/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar", urls);
        Assert.Contains("https://dl.test/old/platform/1.0/platform-1.0-natives-linux.jar", urls);
        Assert.DoesNotContain(urls, u => u.Contains("windows"));
    }

    [Fact]
    public void Asset_plan_has_one_request_per_unique_hash()
    {
        using var world = new FakeGameWorld();
        var index = AssetIndexParser.Parse(world.ReadServerText("https://dl.test/index17.json"));

        var plan = world.Planner.PlanAssets(index);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, r => Assert.Equal(r.ExpectedSha1, Path.GetFileName(r.DestinationPath)));
        Assert.Contains(world.AssetUrl(world.AssetB), plan.Select(r => r.Url));
    }

    [Fact]
    public void Libraries_without_download_address_are_not_planned()
    {
        using var world = new FakeGameWorld();
        var library = new Library("net.minecraftforge:forge:1.0", new LibraryArtifact("net/minecraftforge/forge/1.0/forge-1.0.jar", new FileDownloadInfo("", null, null)),
            new Dictionary<string, string>(), new Dictionary<string, LibraryArtifact>(), Array.Empty<Rule>(), Array.Empty<string>());
        var version = VersionResolver.Resolve("v", new[] { new GameVersionMetadata { Id = "v", MainClass = "M", Libraries = new[] { library } } });

        Assert.Empty(world.Planner.PlanCore(version, world.Environment));
    }
}

public sealed class GameInstallerTests
{
    [Fact]
    public async Task Install_downloads_everything_into_shared_cache_and_reports_progress()
    {
        using var world = new FakeGameWorld();
        var progress = new ListProgress<InstallProgress>();

        var result = await world.Installer.InstallAsync(FakeGameWorld.VersionId, progress);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(FakeGameWorld.VersionId, result.Version!.Id);

        Assert.True(File.Exists(world.Layout.VersionJsonPath("1.21.1")));
        Assert.True(File.Exists(world.Layout.ClientJarPath("1.21.1")));
        Assert.True(File.Exists(world.Layout.LibraryPath("com/example/core/1.0/core-1.0.jar")));
        Assert.True(File.Exists(world.Layout.LibraryPath("old/platform/1.0/platform-1.0-natives-windows.jar")));
        Assert.True(File.Exists(world.Layout.LoggingConfigPath("client-1.12.xml")));
        Assert.True(File.Exists(world.Layout.AssetIndexPath("17")));
        Assert.Equal(world.AssetA, await File.ReadAllBytesAsync(world.Layout.AssetObjectPath(FakeGameWorld.Sha1(world.AssetA))));
        Assert.Equal(world.AssetB, await File.ReadAllBytesAsync(world.Layout.AssetObjectPath(FakeGameWorld.Sha1(world.AssetB))));
        Assert.False(File.Exists(world.Layout.LibraryPath("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar")));

        // список версий, version.json, 8 основных файлов и 2 уникальных ассета
        Assert.Equal(12, world.Requested.Count);

        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal(new[] { InstallStage.ResolvingMetadata, InstallStage.DownloadingFiles, InstallStage.DownloadingAssets, InstallStage.Completed }, stages);
        var assetReports = progress.Items.Where(p => p.Stage == InstallStage.DownloadingAssets).ToList();
        Assert.Equal(2, assetReports.Max(p => p.CompletedFiles));
        Assert.All(assetReports, p => Assert.Equal(2, p.TotalFiles));
    }

    [Fact]
    public async Task Repeated_install_downloads_nothing()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var before = world.Requested.Count;

        var second = await world.Installer.InstallAsync(FakeGameWorld.VersionId);

        Assert.True(second.Succeeded);
        Assert.Equal(before, world.Requested.Count);
    }

    [Fact]
    public async Task Install_repairs_a_corrupted_library()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var core = world.Layout.LibraryPath("com/example/core/1.0/core-1.0.jar");
        await File.WriteAllTextAsync(core, "garbage");
        var before = world.Requested.Count;

        var result = await world.Installer.InstallAsync(FakeGameWorld.VersionId);

        Assert.True(result.Succeeded);
        Assert.Equal(before + 1, world.Requested.Count);
        Assert.Equal("core-lib", await File.ReadAllTextAsync(core));
    }

    [Fact]
    public async Task Unknown_version_is_a_failed_result_not_an_exception()
    {
        using var world = new FakeGameWorld();

        var result = await world.Installer.InstallAsync("9.9.9");

        Assert.False(result.Succeeded);
        Assert.Contains("9.9.9", result.Error);
    }

    [Fact]
    public async Task Missing_asset_on_server_fails_the_install_with_details()
    {
        using var world = new FakeGameWorld();
        world.Break(world.AssetUrl(world.AssetA));

        var result = await world.Installer.InstallAsync(FakeGameWorld.VersionId);

        Assert.False(result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(world.AssetUrl(world.AssetA), result.Failures[0].Request.Url);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var world = new FakeGameWorld();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Installer.InstallAsync(FakeGameWorld.VersionId, ct: cts.Token));
    }

    [Fact]
    public async Task Virtual_asset_index_gets_named_copies()
    {
        using var world = new FakeGameWorld(virtualAssets: true);

        var result = await world.Installer.InstallAsync(FakeGameWorld.VersionId);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(world.AssetA, await File.ReadAllBytesAsync(world.Layout.VirtualAssetPath("17", "icons/icon.png")));
        Assert.Equal(world.AssetB, await File.ReadAllBytesAsync(world.Layout.VirtualAssetPath("17", "sounds/copy.ogg")));
    }

    [Fact]
    public async Task Verify_is_valid_after_install_and_makes_no_network_requests()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var before = world.Requested.Count;

        var verification = await world.Installer.VerifyAsync(FakeGameWorld.VersionId);

        Assert.True(verification.IsValid);
        Assert.Equal(before, world.Requested.Count);
    }

    private static GameInstaller WithMemory(FakeGameWorld world) => new(world.Metadata, world.Downloads, world.Planner, world.Layout, world.Environment,
        NullLogger<GameInstaller>.Instance, new VerifiedFilesCache(world.Layout, NullLogger<VerifiedFilesCache>.Instance));

    [Fact]
    public async Task Verify_with_memory_skips_hashing_of_unchanged_files_but_notices_changed_ones()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var installer = WithMemory(world);
        var lwjgl = world.Layout.LibraryPath("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");

        // Первая проверка полная: хэши сходятся, файл памяти записан.
        Assert.True((await installer.VerifyAsync(FakeGameWorld.VersionId)).IsValid);
        Assert.True(File.Exists(world.Layout.VerifiedFilesPath));

        // Содержимое подменили, размер и время изменения вернули: быстрая проверка этого не видит (хэш не считается).
        var stamp = File.GetLastWriteTimeUtc(lwjgl);
        await File.WriteAllBytesAsync(lwjgl, Encoding.UTF8.GetBytes("LWJGL-LIB"));
        File.SetLastWriteTimeUtc(lwjgl, stamp);
        Assert.True((await installer.VerifyAsync(FakeGameWorld.VersionId)).IsValid);

        // Время изменения другое: хэш считается заново, подмена найдена.
        File.SetLastWriteTimeUtc(lwjgl, stamp.AddMinutes(5));
        var verification = await installer.VerifyAsync(FakeGameWorld.VersionId);
        Assert.False(verification.IsValid);
        Assert.Contains(verification.Problems, p => p.Path == lwjgl && p.Reason == FileProblemReason.WrongHash);
    }

    [Fact]
    public async Task Download_of_an_existing_unchanged_file_does_not_rehash_it_but_a_changed_file_is_downloaded_again()
    {
        using var world = new FakeGameWorld();
        var cache = new VerifiedFilesCache(world.Layout, NullLogger<VerifiedFilesCache>.Instance);
        var downloads = new HttpDownloadManager(new StubHttpClientFactory(world.Handler),
            Options.Create(new DownloadOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero }), NullLogger<HttpDownloadManager>.Instance, cache);

        var hash = FakeGameWorld.Sha1(world.AssetA);
        var path = world.Layout.AssetObjectPath(hash);
        var request = new DownloadRequest(world.AssetUrl(world.AssetA), path, hash, world.AssetA.Length);

        Assert.Equal(DownloadStatus.Downloaded, (await downloads.DownloadAsync(request)).Status);

        // Тот же файл, что скачали: берётся из общей памяти, сеть не нужна.
        var requestsBefore = world.Requested.Count;
        Assert.Equal(DownloadStatus.AlreadyUpToDate, (await downloads.DownloadAsync(request)).Status);
        Assert.Equal(requestsBefore, world.Requested.Count);

        // Файл изменили (тот же размер, другое время): хэш считается заново, файл скачивается снова.
        await File.WriteAllBytesAsync(path, new byte[world.AssetA.Length]);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
        Assert.Equal(DownloadStatus.Downloaded, (await downloads.DownloadAsync(request)).Status);
        Assert.Equal(world.AssetA, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Verify_with_memory_still_reports_missing_and_wrong_size_files()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var installer = WithMemory(world);
        Assert.True((await installer.VerifyAsync(FakeGameWorld.VersionId)).IsValid);

        File.Delete(world.Layout.AssetObjectPath(FakeGameWorld.Sha1(world.AssetA)));
        var core = world.Layout.LibraryPath("com/example/core/1.0/core-1.0.jar");
        await File.WriteAllTextAsync(core, "x");

        var verification = await installer.VerifyAsync(FakeGameWorld.VersionId);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Problems, p => p.Path == core && p.Reason == FileProblemReason.WrongSize);
        Assert.Contains(verification.Problems, p => p.Reason == FileProblemReason.Missing);
    }

    [Fact]
    public async Task Verify_reports_missing_wrong_size_and_wrong_hash()
    {
        using var world = new FakeGameWorld();
        await world.Installer.InstallAsync(FakeGameWorld.VersionId);

        File.Delete(world.Layout.AssetObjectPath(FakeGameWorld.Sha1(world.AssetA)));
        var core = world.Layout.LibraryPath("com/example/core/1.0/core-1.0.jar");
        await File.WriteAllTextAsync(core, "x");
        var lwjgl = world.Layout.LibraryPath("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
        await File.WriteAllBytesAsync(lwjgl, Encoding.UTF8.GetBytes("LWJGL-LIB")); // тот же размер, другое содержимое

        var verification = await world.Installer.VerifyAsync(FakeGameWorld.VersionId);

        Assert.False(verification.IsValid);
        Assert.True(verification.MetadataAvailable);
        Assert.Equal(3, verification.Problems.Count);
        Assert.Contains(verification.Problems, p => p.Path == core && p.Reason == FileProblemReason.WrongSize);
        Assert.Contains(verification.Problems, p => p.Path == lwjgl && p.Reason == FileProblemReason.WrongHash);
        Assert.Contains(verification.Problems, p => p.Reason == FileProblemReason.Missing);
    }

    [Fact]
    public async Task Verify_of_not_installed_version_says_metadata_is_unavailable()
    {
        using var world = new FakeGameWorld();

        var verification = await world.Installer.VerifyAsync(FakeGameWorld.VersionId);

        Assert.False(verification.MetadataAvailable);
        Assert.False(verification.IsValid);
        Assert.Empty(world.Requested);
    }
}
