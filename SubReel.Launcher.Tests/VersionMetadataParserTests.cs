using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Tests;

public sealed class VersionMetadataParserTests
{
    [Fact]
    public void Modern_version_is_parsed_completely()
    {
        using var world = new FakeGameWorld();
        var metadata = ParseWorldVersion(world);

        Assert.Equal("1.21.1", metadata.Id);
        Assert.Equal("release", metadata.Type);
        Assert.Equal("net.minecraft.client.main.Main", metadata.MainClass);
        Assert.Equal("17", metadata.Assets);
        Assert.Equal("17", metadata.AssetIndex!.Id);
        Assert.Equal("https://dl.test/client.jar", metadata.ClientDownload!.Url);
        Assert.Equal(21, metadata.JavaMajorVersion);
        Assert.Equal("java-runtime-delta", metadata.JavaComponent);
        Assert.Equal("client-1.12.xml", metadata.Logging!.FileId);
        Assert.Equal(5, metadata.Libraries.Count);

        var demo = Assert.Single(metadata.GameArguments, a => a.Rules.Count > 0);
        Assert.Equal("--demo", Assert.Single(demo.Values));
        Assert.True(demo.Rules[0].Features!["is_demo_user"]);

        var windowsOnly = Assert.Single(metadata.JvmArguments, a => a.Rules.Count > 0);
        Assert.Equal("windows", windowsOnly.Rules[0].OsName);
    }

    [Fact]
    public void Old_style_natives_and_extract_rules_are_parsed()
    {
        using var world = new FakeGameWorld();
        var old = ParseWorldVersion(world).Libraries.Single(l => l.Name == "old:platform:1.0");

        Assert.Equal("natives-windows", old.NativeClassifiers["windows"]);
        Assert.Equal(2, old.Classifiers.Count);
        Assert.Equal(new[] { "excluded/" }, old.ExtractExclude);
        Assert.NotNull(old.Artifact);
    }

    [Fact]
    public void Legacy_arguments_string_is_kept()
    {
        var metadata = VersionMetadataParser.Parse("""{"id":"1.8.9","mainClass":"net.minecraft.client.main.Main","minecraftArguments":"--username ${auth_player_name} --version ${version_name}","libraries":[]}""");
        Assert.Equal("--username ${auth_player_name} --version ${version_name}", metadata.LegacyGameArguments);
        Assert.Empty(metadata.GameArguments);
    }

    [Fact]
    public void Library_without_downloads_gets_maven_artifact_from_its_repository()
    {
        var metadata = VersionMetadataParser.Parse("""
            {"id":"fabric","inheritsFrom":"1.21.1","libraries":[
              {"name":"net.fabricmc:fabric-loader:0.16.5","url":"https://maven.fabricmc.net/","sha1":"abc","size":42},
              {"name":"org.ow2.asm:asm:9.7","url":"https://maven.fabricmc.net"},
              {"name":"com.example:plain:1.0"}]}
            """);

        var loader = metadata.Libraries[0].Artifact!;
        Assert.Equal("net/fabricmc/fabric-loader/0.16.5/fabric-loader-0.16.5.jar", loader.Path);
        Assert.Equal("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.5/fabric-loader-0.16.5.jar", loader.Download.Url);
        Assert.Equal("abc", loader.Download.Sha1);
        Assert.Equal(42, loader.Download.Size);
        Assert.Equal("https://maven.fabricmc.net/org/ow2/asm/asm/9.7/asm-9.7.jar", metadata.Libraries[1].Artifact!.Download.Url);
        Assert.StartsWith(VersionMetadataParser.DefaultLibrariesBaseUrl, metadata.Libraries[2].Artifact!.Download.Url);
        Assert.Null(metadata.MainClass);
        Assert.Equal("1.21.1", metadata.InheritsFrom);
    }

    [Fact]
    public void Library_with_downloads_but_no_artifact_has_none()
    {
        var metadata = VersionMetadataParser.Parse("""
            {"id":"x","mainClass":"M","libraries":[{"name":"a:b:1","downloads":{"classifiers":{"natives-linux":{"path":"a/b/1/b-1-natives-linux.jar","url":"https://x/y.jar"}}}}]}
            """);
        Assert.Null(metadata.Libraries[0].Artifact);
        Assert.Single(metadata.Libraries[0].Classifiers);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"mainClass":"M"}""")]
    [InlineData("""{"id":"x","libraries":[{"downloads":{}}]}""")]
    [InlineData("""{"id":"x","libraries":[{"name":"a:b:1","downloads":{"artifact":{"url":"https://x"}}}]}""")]
    [InlineData("""{"id":"x","libraries":[{"name":"a:b:1","rules":[{"action":"maybe"}],"downloads":{}}]}""")]
    [InlineData("""{"id":"x","assetIndex":{"url":"https://x"}}""")]
    [InlineData("""{"id":"x","libraries":[{"name":"broken-name"}]}""")]
    public void Malformed_descriptions_throw_metadata_exception(string json) =>
        Assert.Throws<VersionMetadataException>(() => VersionMetadataParser.Parse(json));

    private static GameVersionMetadata ParseWorldVersion(FakeGameWorld world) =>
        VersionMetadataParser.Parse(world.ReadServerText("https://dl.test/1.21.1.json"));
}

public sealed class AssetIndexParserTests
{
    private static readonly string Hash = new('a', 40);

    [Fact]
    public void Parses_objects_and_flags()
    {
        var index = AssetIndexParser.Parse("""{"objects":{"a/b.png":{"hash":"HASH","size":10}},"virtual":true,"map_to_resources":false}""".Replace("HASH", Hash.ToUpperInvariant()));

        Assert.Equal(new AssetObject(Hash, 10), index.Objects["a/b.png"]);
        Assert.True(index.IsVirtual);
        Assert.False(index.MapToResources);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("""{"objects":{"x":{"hash":"short","size":1}}}""")]
    [InlineData("""{"objects":{"x":{"size":1}}}""")]
    [InlineData("""{"objects":{"x":{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}""")]
    public void Malformed_index_throws(string json) =>
        Assert.Throws<VersionMetadataException>(() => AssetIndexParser.Parse(json));
}

public sealed class VersionManifestParserTests
{
    [Fact]
    public void Parses_entries_and_skips_incomplete_ones()
    {
        var entries = VersionManifestSource.Parse("""
            {"versions":[
              {"id":"1.21.1","type":"release","url":"https://x/1.json","sha1":"abc"},
              {"id":"broken"},
              {"id":"1.21.1","type":"snapshot","url":"https://x/dup.json"}]}
            """);

        var entry = Assert.Single(entries).Value;
        Assert.Equal("1.21.1", entry.Id);
        Assert.Equal("https://x/1.json", entry.Url);
        Assert.Equal("abc", entry.Sha1);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{}")]
    public void Malformed_manifest_throws(string json) =>
        Assert.Throws<VersionMetadataException>(() => VersionManifestSource.Parse(json));
}
