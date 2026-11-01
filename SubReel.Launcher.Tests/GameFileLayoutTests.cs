using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Tests;

public sealed class GameFileLayoutTests
{
    private static GameFileLayout Create(TempDir dir) => new(new AppPaths(dir.Root, dir.Root));

    [Fact]
    public void Builds_expected_shared_cache_paths()
    {
        using var dir = new TempDir();
        var layout = Create(dir);
        var root = Path.Combine(dir.Root, "shared", "game");
        var hash = "ab" + new string('c', 38);

        Assert.Equal(Path.Combine(root, "versions", "1.21.1", "1.21.1.json"), layout.VersionJsonPath("1.21.1"));
        Assert.Equal(Path.Combine(root, "versions", "1.21.1", "1.21.1.jar"), layout.ClientJarPath("1.21.1"));
        Assert.Equal(Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl.jar"), layout.LibraryPath("org/lwjgl/lwjgl.jar"));
        Assert.Equal(Path.Combine(root, "assets", "indexes", "17.json"), layout.AssetIndexPath("17"));
        Assert.Equal(Path.Combine(root, "assets", "objects", "ab", hash), layout.AssetObjectPath(hash.ToUpperInvariant()));
        Assert.Equal(Path.Combine(root, "assets", "virtual", "legacy", "icons", "icon.png"), layout.VirtualAssetPath("legacy", "icons/icon.png"));
        Assert.Equal(Path.Combine(root, "assets", "log_configs", "client.xml"), layout.LoggingConfigPath("client.xml"));
    }

    [Theory]
    [InlineData("../../evil.jar")]
    [InlineData("a/../../evil.jar")]
    public void Library_path_cannot_escape_the_cache(string relative)
    {
        using var dir = new TempDir();
        Assert.Throws<VersionMetadataException>(() => Create(dir).LibraryPath(relative));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Version_ids_must_be_simple_names(string id)
    {
        using var dir = new TempDir();
        var layout = Create(dir);
        Assert.Throws<VersionMetadataException>(() => layout.VersionJsonPath(id));
        Assert.Throws<VersionMetadataException>(() => layout.ClientJarPath(id));
        Assert.Throws<VersionMetadataException>(() => layout.AssetIndexPath(id));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Asset_hash_must_be_sha1_hex(string hash)
    {
        using var dir = new TempDir();
        Assert.Throws<VersionMetadataException>(() => Create(dir).AssetObjectPath(hash));
    }
}
