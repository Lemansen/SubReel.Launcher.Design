using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Launching;

namespace SubReel.Launcher.Tests;

public sealed class LegacyAssetsPreparerTests
{
    private static ResolvedVersion Version(string indexId) => new()
    {
        Id = "legacy-test",
        Chain = new[] { "legacy-test" },
        MainClass = "net.minecraft.client.Minecraft",
        AssetsId = indexId,
        AssetIndex = new AssetIndexInfo(indexId, new FileDownloadInfo("https://dl.test/index.json", null, null)),
        ClientJarVersionId = "legacy-test",
        Libraries = Array.Empty<Library>(),
        JvmArguments = Array.Empty<ArgumentValue>(),
        GameArguments = Array.Empty<ArgumentValue>(),
    };

    private static LegacyAssetsPreparer Preparer(FakeGameWorld world) => new(world.Layout, NullLogger<LegacyAssetsPreparer>.Instance);

    private static void WriteIndex(FakeGameWorld world, string indexId, string json)
    {
        var path = world.Layout.AssetIndexPath(indexId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static void PlaceObject(FakeGameWorld world, byte[] data)
    {
        var path = world.Layout.AssetObjectPath(FakeGameWorld.Sha1(data));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static string IndexJson(string assetName, byte[] data, string flags) =>
        $"{{\"objects\":{{\"{assetName}\":{{\"hash\":\"{FakeGameWorld.Sha1(data)}\",\"size\":{data.Length}}}}}{flags}}}";

    [Fact]
    public async Task Virtual_index_is_copied_under_original_names_and_restored_when_deleted()
    {
        using var world = new FakeGameWorld(virtualAssets: true);
        Assert.True((await world.Installer.InstallAsync(FakeGameWorld.VersionId)).Succeeded);
        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId, allowNetwork: false);
        var virtualDirectory = world.Layout.VirtualAssetsDirectory("17");
        Directory.Delete(virtualDirectory, recursive: true);

        var gameAssets = await Preparer(world).PrepareAsync(version, world.Dir.File("game"));

        Assert.Equal(virtualDirectory, gameAssets);
        Assert.Equal(world.AssetA, await File.ReadAllBytesAsync(Path.Combine(virtualDirectory, "icons", "icon.png")));
        Assert.Equal(world.AssetB, await File.ReadAllBytesAsync(Path.Combine(virtualDirectory, "sounds", "copy.ogg")));
    }

    [Fact]
    public async Task Regular_index_changes_nothing_and_returns_the_assets_root()
    {
        using var world = new FakeGameWorld();
        Assert.True((await world.Installer.InstallAsync(FakeGameWorld.VersionId)).Succeeded);
        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId, allowNetwork: false);
        var gameDirectory = world.Dir.File("game");

        var gameAssets = await Preparer(world).PrepareAsync(version, gameDirectory);

        Assert.Equal(world.Layout.AssetsRoot, gameAssets);
        Assert.False(Directory.Exists(world.Layout.VirtualAssetsDirectory("17")));
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "resources")));
    }

    [Fact]
    public async Task Map_to_resources_copies_assets_into_the_game_directory()
    {
        using var world = new FakeGameWorld();
        WriteIndex(world, "pre-1.6", IndexJson("icons/icon.png", world.AssetA, ",\"map_to_resources\":true"));
        PlaceObject(world, world.AssetA);
        var gameDirectory = world.Dir.File("game");

        var gameAssets = await Preparer(world).PrepareAsync(Version("pre-1.6"), gameDirectory);

        Assert.Equal(world.Layout.AssetsRoot, gameAssets);
        Assert.Equal(world.AssetA, await File.ReadAllBytesAsync(Path.Combine(gameDirectory, "resources", "icons", "icon.png")));
    }

    [Fact]
    public async Task Asset_name_that_escapes_the_target_folder_is_rejected()
    {
        using var world = new FakeGameWorld();
        WriteIndex(world, "evil", IndexJson("../escaped.txt", world.AssetA, ",\"map_to_resources\":true"));
        PlaceObject(world, world.AssetA);
        var gameDirectory = world.Dir.File("game");

        await Assert.ThrowsAsync<VersionMetadataException>(() => Preparer(world).PrepareAsync(Version("evil"), gameDirectory));

        Assert.False(File.Exists(Path.Combine(gameDirectory, "escaped.txt")));
    }

    [Fact]
    public async Task Missing_index_file_is_reported()
    {
        using var world = new FakeGameWorld();

        await Assert.ThrowsAsync<FileNotFoundException>(() => Preparer(world).PrepareAsync(Version("absent"), world.Dir.File("game")));
    }
}
