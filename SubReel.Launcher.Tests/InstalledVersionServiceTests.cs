using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Infrastructure.Versions;

namespace SubReel.Launcher.Tests;

public sealed class InstalledVersionServiceTests
{
    private static InstalledVersionService Create(TempDir dir) =>
        new(new AppPaths(dir.Root, dir.Root, dir.Root), NullLogger<InstalledVersionService>.Instance);

    private static void AddVersion(TempDir dir, string id, string json, bool withJar = false)
    {
        var folder = Path.Combine(dir.Root, "versions", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), json);
        if (withJar) File.WriteAllText(Path.Combine(folder, id + ".jar"), "jar");
    }

    [Fact]
    public async Task Missing_versions_folder_is_reported()
    {
        using var dir = new TempDir();
        var result = await Create(dir).ScanAsync(dir.Root);

        Assert.False(result.DirectoryFound);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task Detects_vanilla_and_fabric_and_skips_broken_and_jarless_vanilla()
    {
        using var dir = new TempDir();
        AddVersion(dir, "1.21.1", """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main"}""", withJar: true);
        AddVersion(dir, "fabric-loader-0.16.5-1.21.1", """{"inheritsFrom":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient"}""");
        AddVersion(dir, "1.20.4", """{"id":"1.20.4"}"""); // без jar — не установлена
        AddVersion(dir, "broken", "{ not json");

        var result = await Create(dir).ScanAsync(dir.Root);

        Assert.True(result.DirectoryFound);
        Assert.Equal(2, result.Versions.Count);
        Assert.Equal(LoaderKind.Vanilla, result.Versions[0].Loader);
        var fabric = result.Versions[1];
        Assert.Equal(LoaderKind.Fabric, fabric.Loader);
        Assert.Equal("1.21.1", fabric.McVersion);
        Assert.Equal("0.16.5", fabric.LoaderVersion);
    }

    [Fact]
    public async Task Newer_minecraft_versions_are_listed_first_using_numeric_order()
    {
        using var dir = new TempDir();
        AddVersion(dir, "1.9", """{"id":"1.9"}""", withJar: true);
        AddVersion(dir, "1.10", """{"id":"1.10"}""", withJar: true);

        var result = await Create(dir).ScanAsync(dir.Root);

        Assert.Equal(new[] { "1.10", "1.9" }, result.Versions.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task Cancelled_scan_throws()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(dir).ScanAsync(dir.Root, cts.Token));
    }
}
