using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Tests;

public sealed class JavaVersionParserTests
{
    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("1.7.0_80", 7)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    [InlineData("9-ea", 9)]
    [InlineData("  21.0.4 ", 21)]
    public void Reads_major_version(string version, int expected) =>
        Assert.Equal(expected, JavaVersionParser.ParseMajor(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void Garbage_has_no_major_version(string? version) =>
        Assert.Null(JavaVersionParser.ParseMajor(version));

    [Theory]
    [InlineData("21.0.10", "21.0.9", 1)]
    [InlineData("21.0.9", "21.0.10", -1)]
    [InlineData("21.0", "21.0.0", 0)]
    [InlineData("1.8.0_402", "1.8.0_392", 1)]
    [InlineData("17.0.9", "21.0.1", -1)]
    public void Compares_numerically(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(JavaVersionParser.Compare(left, right)));
}

public sealed class JavaSelectorTests
{
    private static JavaInstallation Java(int major, JavaSource source = JavaSource.Registry, string? full = null, bool is64 = true, string name = "j") =>
        new($"/java/{name}", $"/java/{name}/bin/java", major, full ?? $"{major}.0.1", is64, source);

    private static readonly JavaRequirement Java21 = new(21, "java-runtime-delta");

    [Fact]
    public void Only_the_exact_major_version_is_acceptable()
    {
        var selection = JavaSelector.Select(Java21, new[] { Java(17), Java(22), Java(8) }, allow32Bit: false);

        Assert.Equal(JavaSelectionStatus.NeedsDownload, selection.Status);
        Assert.Null(selection.Installation);
    }

    [Fact]
    public void Missing_java_without_known_runtime_is_not_found()
    {
        var selection = JavaSelector.Select(new JavaRequirement(21, null), new[] { Java(17) }, allow32Bit: false);
        Assert.Equal(JavaSelectionStatus.NotFound, selection.Status);
    }

    [Fact]
    public void Prefers_manual_then_launcher_runtime_then_official_then_system()
    {
        var system = Java(21, JavaSource.Registry, name: "system");
        var official = Java(21, JavaSource.OfficialLauncher, name: "official");
        var managed = Java(21, JavaSource.Managed, name: "managed");
        var manual = Java(21, JavaSource.Manual, name: "manual");

        Assert.Equal("managed", Name(JavaSelector.Select(Java21, new[] { system, official, managed }, false)));
        Assert.Equal("official", Name(JavaSelector.Select(Java21, new[] { system, official }, false)));
        Assert.Equal("manual", Name(JavaSelector.Select(Java21, new[] { system, managed, manual }, false)));
    }

    [Fact]
    public void Prefers_64_bit_and_excludes_32_bit_on_64_bit_systems()
    {
        var x86 = Java(21, name: "x86", is64: false);
        var x64 = Java(21, name: "x64");

        Assert.Equal("x64", Name(JavaSelector.Select(Java21, new[] { x86, x64 }, allow32Bit: true)));
        Assert.Equal(JavaSelectionStatus.NeedsDownload, JavaSelector.Select(Java21, new[] { x86 }, allow32Bit: false).Status);
        Assert.Equal("x86", Name(JavaSelector.Select(Java21, new[] { x86 }, allow32Bit: true)));
    }

    [Fact]
    public void Newest_build_wins_among_equals()
    {
        var older = Java(21, full: "21.0.2", name: "old");
        var newer = Java(21, full: "21.0.10", name: "new");

        Assert.Equal("new", Name(JavaSelector.Select(Java21, new[] { older, newer }, false)));
    }

    [Fact]
    public void Requirement_from_version_defaults_to_legacy_java_8()
    {
        var legacy = VersionResolver.Resolve("1.8.9", new[] { new GameVersionMetadata { Id = "1.8.9", MainClass = "M" } });
        var modern = VersionResolver.Resolve("1.21.1", new[] { new GameVersionMetadata { Id = "1.21.1", MainClass = "M", JavaMajorVersion = 21, JavaComponent = "java-runtime-delta" } });
        var noComponent = VersionResolver.Resolve("x", new[] { new GameVersionMetadata { Id = "x", MainClass = "M", JavaMajorVersion = 17 } });

        Assert.Equal(new JavaRequirement(8, "jre-legacy"), JavaRequirement.FromVersion(legacy));
        Assert.Equal(new JavaRequirement(21, "java-runtime-delta"), JavaRequirement.FromVersion(modern));
        Assert.Equal(new JavaRequirement(17, null), JavaRequirement.FromVersion(noComponent));
    }

    private static string Name(JavaSelection selection) => Path.GetFileName(selection.Installation!.Home);
}
