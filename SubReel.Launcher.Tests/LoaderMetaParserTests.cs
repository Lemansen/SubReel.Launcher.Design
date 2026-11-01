using System.Text.Json;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Loaders;

namespace SubReel.Launcher.Tests;

public sealed class LoaderMetaParserTests
{
    internal static string FabricList(params (string Version, bool Stable)[] builds) =>
        JsonSerializer.Serialize(builds.Select(b => new { loader = new { version = b.Version, stable = b.Stable }, intermediary = new { version = "1.21.1" } }).ToArray());

    internal static string QuiltList(params string[] versions) =>
        JsonSerializer.Serialize(versions.Select(v => new { loader = new { version = v }, hashed = new { version = "1.21.1" } }).ToArray());

    [Fact]
    public void Fabric_builds_keep_server_order_and_stable_flag()
    {
        var builds = LoaderMetaParser.ParseBuilds(FabricList(("0.17.0", false), ("0.16.5", true)), LoaderKind.Fabric, "1.21.1");

        Assert.Equal(new[] { "0.17.0", "0.16.5" }, builds.Select(b => b.Version).ToArray());
        Assert.False(builds[0].Stable);
        Assert.True(builds[1].Stable);
        Assert.All(builds, b => Assert.Equal(("1.21.1", LoaderKind.Fabric), (b.McVersion, b.Kind)));
    }

    [Fact]
    public void Quilt_without_stable_flag_marks_suffixed_versions_as_beta()
    {
        var builds = LoaderMetaParser.ParseBuilds(QuiltList("0.27.0-beta.1", "0.26.0"), LoaderKind.Quilt, "1.21.1");

        Assert.False(builds[0].Stable);
        Assert.True(builds[1].Stable);
    }

    [Fact]
    public void Unsafe_missing_and_duplicate_versions_are_skipped()
    {
        var json = QuiltList("../evil", "", "0.26.0", "0.26.0", "a b");

        var builds = LoaderMetaParser.ParseBuilds(json, LoaderKind.Quilt, "1.21.1");

        Assert.Equal("0.26.0", Assert.Single(builds).Version);
    }

    [Fact]
    public void Empty_list_means_the_version_is_not_supported() =>
        Assert.Empty(LoaderMetaParser.ParseBuilds("[]", LoaderKind.Fabric, "1.2"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{ not json")]
    public void Unexpected_answers_are_catalog_errors(string json) =>
        Assert.Throws<LoaderCatalogException>(() => LoaderMetaParser.ParseBuilds(json, LoaderKind.Fabric, "1.21.1"));

    [Theory]
    [InlineData("0.16.5", true)]
    [InlineData("0.27.0-beta.1", true)]
    [InlineData("24w14a", true)]
    [InlineData("1.21.1-pre1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("-1", false)]
    public void Version_text_is_checked_before_it_reaches_urls_and_paths(string? text, bool expected) =>
        Assert.Equal(expected, LoaderMetaParser.IsSafeVersionText(text));

    [Fact]
    public void Too_long_version_text_is_unsafe() => Assert.False(LoaderMetaParser.IsSafeVersionText(new string('1', 65)));
}
