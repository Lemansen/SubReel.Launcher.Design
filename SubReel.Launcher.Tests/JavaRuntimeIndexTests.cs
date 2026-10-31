using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Minecraft.Java;

namespace SubReel.Launcher.Tests;

public sealed class JavaRuntimeIndexTests
{
    [Theory]
    [InlineData("windows", "x86_64", "windows-x64")]
    [InlineData("windows", "x86", "windows-x86")]
    [InlineData("windows", "arm64", "windows-arm64")]
    [InlineData("linux", "x86_64", "linux")]
    [InlineData("linux", "x86", "linux-i386")]
    [InlineData("osx", "x86_64", "mac-os")]
    [InlineData("osx", "arm64", "mac-os-arm64")]
    public void Maps_platform_to_mojang_key(string os, string arch, string expected) =>
        Assert.Equal(expected, JavaRuntimeIndex.PlatformKey(new OsEnvironment(os, arch)));

    [Fact]
    public void Linux_arm_has_no_mojang_runtime() =>
        Assert.Null(JavaRuntimeIndex.PlatformKey(new OsEnvironment("linux", "arm64")));

    private const string Index = """
        {"windows-x64":{
           "java-runtime-delta":[{"availability":{"group":1,"progress":100},"manifest":{"sha1":"abc","size":10,"url":"https://x/delta.json"},"version":{"name":"21.0.3","released":"2024"}}],
           "jre-legacy":[]},
         "linux":{"jre-legacy":[{"manifest":{"sha1":"def","size":5,"url":"https://x/legacy.json"},"version":{"name":"8.0.452"}}]}}
        """;

    [Fact]
    public void Finds_component_for_platform()
    {
        var reference = JavaRuntimeIndex.FindComponent(Index, "windows-x64", "java-runtime-delta");

        Assert.Equal(new RuntimeManifestRef("https://x/delta.json", "abc", 10, "21.0.3"), reference);
    }

    [Theory]
    [InlineData("windows-x64", "jre-legacy")]
    [InlineData("windows-x64", "unknown")]
    [InlineData("mac-os", "java-runtime-delta")]
    public void Missing_component_or_platform_returns_null(string platform, string component) =>
        Assert.Null(JavaRuntimeIndex.FindComponent(Index, platform, component));

    [Theory]
    [InlineData("nope")]
    [InlineData("[]")]
    public void Malformed_index_throws(string json) =>
        Assert.Throws<JavaRuntimeException>(() => JavaRuntimeIndex.FindComponent(json, "linux", "jre-legacy"));

    [Fact]
    public void Parses_manifest_entries()
    {
        var entries = JavaRuntimeIndex.ParseManifest("""
            {"files":{
              "bin":{"type":"directory"},
              "bin/java":{"type":"file","executable":true,"downloads":{"raw":{"sha1":"aa","size":3,"url":"https://x/java"},"lzma":{"sha1":"bb","size":2,"url":"https://x/java.lzma"}}},
              "release":{"type":"file","downloads":{"raw":{"sha1":"cc","size":4,"url":"https://x/release"}}},
              "lib/link":{"type":"link","target":"../other"}}}
            """);

        Assert.Equal(4, entries.Count);
        Assert.Equal(RuntimeFileKind.Directory, entries[0].Kind);
        Assert.True(entries[1].Executable);
        Assert.Equal("https://x/java", entries[1].Raw!.Url);
        Assert.False(entries[2].Executable);
        Assert.Equal("../other", entries[3].LinkTarget);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("""{"files":{"a":{"type":"file"}}}""")]
    [InlineData("""{"files":{"a":{"type":"file","downloads":{"raw":{"sha1":"x"}}}}}""")]
    [InlineData("""{"files":{"a":{"type":"link"}}}""")]
    [InlineData("""{"files":{"a":{"type":"strange"}}}""")]
    public void Malformed_manifest_throws(string json) =>
        Assert.Throws<JavaRuntimeException>(() => JavaRuntimeIndex.ParseManifest(json));
}
