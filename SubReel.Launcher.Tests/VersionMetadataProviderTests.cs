using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Tests;

public sealed class VersionMetadataProviderTests
{
    private static void WriteVersion(FakeGameWorld world, string id, string json)
    {
        var path = world.Layout.VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public async Task Downloads_description_by_manifest_and_verifies_it()
    {
        using var world = new FakeGameWorld();

        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId);

        Assert.Equal("net.minecraft.client.main.Main", version.MainClass);
        Assert.Equal(21, version.JavaMajorVersion);
        Assert.Equal(new[] { "1.21.1" }, version.Chain);
        Assert.Equal(2, world.Requested.Count);
    }

    [Fact]
    public async Task Local_description_is_used_without_network()
    {
        using var world = new FakeGameWorld();
        await world.Metadata.ResolveAsync(FakeGameWorld.VersionId);
        var before = world.Requested.Count;

        var again = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId, allowNetwork: false);

        Assert.Equal("1.21.1", again.Id);
        Assert.Equal(before, world.Requested.Count);
    }

    [Fact]
    public async Task Offline_resolve_of_missing_version_fails_without_requests()
    {
        using var world = new FakeGameWorld();

        var ex = await Assert.ThrowsAsync<VersionMetadataException>(() => world.Metadata.ResolveAsync(FakeGameWorld.VersionId, allowNetwork: false));

        Assert.Contains("не установлена", ex.Message);
        Assert.Empty(world.Requested);
    }

    [Fact]
    public async Task Unknown_version_throws_not_found()
    {
        using var world = new FakeGameWorld();
        var ex = await Assert.ThrowsAsync<VersionNotFoundException>(() => world.Metadata.ResolveAsync("0.0.1"));
        Assert.Equal("0.0.1", ex.VersionId);
    }

    [Fact]
    public async Task Corrupt_local_description_is_downloaded_again()
    {
        using var world = new FakeGameWorld();
        WriteVersion(world, FakeGameWorld.VersionId, "{ broken");

        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId);

        Assert.Equal("net.minecraft.client.main.Main", version.MainClass);
    }

    [Fact]
    public async Task Corrupt_local_description_offline_is_an_error()
    {
        using var world = new FakeGameWorld();
        WriteVersion(world, FakeGameWorld.VersionId, "{ broken");

        await Assert.ThrowsAsync<VersionMetadataException>(() => world.Metadata.ResolveAsync(FakeGameWorld.VersionId, allowNetwork: false));
    }

    [Fact]
    public async Task Loader_version_inherits_from_vanilla_installed_earlier()
    {
        using var world = new FakeGameWorld();
        WriteVersion(world, "fabric-1.21.1", """
            {"id":"fabric-1.21.1","inheritsFrom":"1.21.1","mainClass":"net.fabricmc.KnotClient",
             "libraries":[{"name":"net.fabricmc:fabric-loader:0.16.5","url":"https://maven.fabricmc.net/"}],
             "arguments":{"game":["--fabric"]}}
            """);

        var version = await world.Metadata.ResolveAsync("fabric-1.21.1");

        Assert.Equal("net.fabricmc.KnotClient", version.MainClass);
        Assert.Equal(new[] { "fabric-1.21.1", "1.21.1" }, version.Chain);
        Assert.Equal("1.21.1", version.ClientJarVersionId);
        Assert.Equal("17", version.AssetsId);
        Assert.Equal("net.fabricmc:fabric-loader:0.16.5", version.Libraries[0].Name);
        Assert.Equal(6, version.Libraries.Count);
    }

    [Fact]
    public async Task Cyclic_inheritance_is_detected()
    {
        using var world = new FakeGameWorld();
        WriteVersion(world, "a", """{"id":"a","inheritsFrom":"b","mainClass":"M"}""");
        WriteVersion(world, "b", """{"id":"b","inheritsFrom":"a","mainClass":"M"}""");

        var ex = await Assert.ThrowsAsync<VersionMetadataException>(() => world.Metadata.ResolveAsync("a"));
        Assert.Contains("зациклена", ex.Message);
    }

    [Fact]
    public async Task Too_long_inheritance_chain_is_rejected()
    {
        using var world = new FakeGameWorld();
        for (var i = 0; i < 10; i++)
            WriteVersion(world, $"v{i}", $"{{\"id\":\"v{i}\",\"inheritsFrom\":\"v{i + 1}\",\"mainClass\":\"M\"}}");

        await Assert.ThrowsAsync<VersionMetadataException>(() => world.Metadata.ResolveAsync("v0"));
    }
}

public sealed class VersionManifestSourceTests
{
    [Fact]
    public async Task Fresh_manifest_on_disk_is_not_downloaded_again()
    {
        using var world = new FakeGameWorld();
        await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);
        var before = world.Requested.Count;

        var entry = await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);

        Assert.Equal("1.21.1", entry!.Id);
        Assert.Equal(before, world.Requested.Count);
    }

    [Fact]
    public async Task Stale_manifest_is_refreshed()
    {
        using var world = new FakeGameWorld();
        await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);
        File.SetLastWriteTimeUtc(world.Layout.ManifestPath, DateTime.UtcNow.AddHours(-2));
        var before = world.Requested.Count;

        await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);

        Assert.Equal(before + 1, world.Requested.Count);
    }

    [Fact]
    public async Task Stale_copy_is_used_when_refresh_fails()
    {
        using var world = new FakeGameWorld();
        await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);
        File.SetLastWriteTimeUtc(world.Layout.ManifestPath, DateTime.UtcNow.AddHours(-2));
        world.Break(VersionManifestSource.ManifestUrl);

        var entry = await world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None);

        Assert.Equal("1.21.1", entry!.Id);
    }

    [Fact]
    public async Task No_network_and_no_copy_is_an_error()
    {
        using var world = new FakeGameWorld();
        world.Break(VersionManifestSource.ManifestUrl);

        await Assert.ThrowsAsync<VersionMetadataException>(() => world.Manifest.FindAsync(FakeGameWorld.VersionId, allowNetwork: true, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_id_returns_null()
    {
        using var world = new FakeGameWorld();
        Assert.Null(await world.Manifest.FindAsync("0.0.1", allowNetwork: true, CancellationToken.None));
    }
}
