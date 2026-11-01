using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Loaders;

namespace SubReel.Launcher.Tests;

public sealed class LoaderMavenParserTests
{
    internal static string ForgeXml(params string[] versions) =>
        "<?xml version=\"1.0\"?><metadata><versioning><versions>" + string.Concat(versions.Select(v => $"<version>{v}</version>")) + "</versions></versioning></metadata>";

    [Theory]
    [InlineData("1.21.1", 21, 1)]
    [InlineData("1.21", 21, 0)]
    [InlineData("1.20", 20, 0)]
    [InlineData("1.7.10", 7, 10)]
    [InlineData("1.21.1-pre1", 21, 1)]
    [InlineData("26.1", 26, 1)]
    public void Release_is_parsed(string mc, int major, int minor)
    {
        Assert.True(LoaderMetaParser.TryParseRelease(mc, out var gotMajor, out var gotMinor));
        Assert.Equal((major, minor), (gotMajor, gotMinor));
    }

    [Theory]
    [InlineData("24w14a")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    public void Snapshots_and_garbage_are_not_releases(string? mc) => Assert.False(LoaderMetaParser.TryParseRelease(mc, out _, out _));

    [Theory]
    [InlineData("1.12.2", false)]
    [InlineData("1.7.10", false)]
    [InlineData("1.13", true)]
    [InlineData("1.16.5", true)]
    [InlineData("1.19", true)]
    [InlineData("1.20", true)]
    [InlineData("1.20.1", true)]
    [InlineData("1.21.1", true)]
    [InlineData("26.1", true)]
    public void Forge_installer_kind_depends_on_the_game_version(string mc, bool modern) => Assert.Equal(modern, LoaderMetaParser.IsForgeModernVersion(mc));

    [Fact]
    public void NeoForge_matches_only_its_own_game_version()
    {
        var xml = ForgeXml("20.4.237", "21.0.167", "21.1.1", "21.1.172", "21.1.200-beta", "21.11.5");

        var builds = LoaderMetaParser.ParseNeoForgeBuilds(xml, "1.21.1");

        Assert.Equal(new[] { "21.1.172", "21.1.1", "21.1.200-beta" }, builds.Select(b => b.Version).ToArray());
        Assert.False(builds[2].Stable);
    }

    [Fact]
    public void NeoForge_for_1_21_does_not_take_1_21_1_builds()
    {
        var builds = LoaderMetaParser.ParseNeoForgeBuilds(ForgeXml("21.0.167", "21.1.1"), "1.21");

        Assert.Equal("21.0.167", Assert.Single(builds).Version);
    }

    [Fact]
    public void NeoForge_has_nothing_for_snapshots_and_old_games() =>
        Assert.Empty(LoaderMetaParser.ParseNeoForgeBuilds(ForgeXml("21.1.1"), "24w14a"));

    [Fact]
    public void Forge_versions_are_cut_to_the_loader_part_and_sorted_numerically()
    {
        var xml = ForgeXml("1.20.1-47.2.0", "1.20.1-47.10.0", "1.20.1-47.3.0", "1.21.1-52.0.0");

        var builds = LoaderMetaParser.ParseForgeBuilds(xml, "1.20.1");

        Assert.Equal(new[] { "47.10.0", "47.3.0", "47.2.0" }, builds.Select(b => b.Version).ToArray());
        Assert.All(builds, b => Assert.Equal(LoaderKind.Forge, b.Kind));
    }

    [Theory]
    [InlineData("1.7.10", false)]
    [InlineData("1.12.1", false)]
    [InlineData("1.12.2", true)]
    [InlineData("1.13", true)]
    [InlineData("1.21.1", true)]
    public void Old_forge_games_are_not_offered(string mc, bool supported)
    {
        var xml = ForgeXml($"{mc}-10.0.0");

        Assert.Equal(supported, LoaderMetaParser.IsForgeSupportedGame(mc));
        Assert.Equal(supported, LoaderMetaParser.ParseForgeBuilds(xml, mc).Count == 1);
    }

    [Fact]
    public void Broken_xml_is_a_catalog_error()
    {
        Assert.Throws<LoaderCatalogException>(() => LoaderMetaParser.ParseForgeBuilds("<not xml", "1.20.1"));
        Assert.Throws<LoaderCatalogException>(() => LoaderMetaParser.ParseNeoForgeBuilds("<not xml", "1.21.1"));
    }

    [Theory]
    [InlineData("1.21.1-forge-52.0.0", LoaderKind.Forge, "52.0.0", "1.21.1")]
    [InlineData("1.12.2-forge-14.23.5.2860", LoaderKind.Forge, "14.23.5.2860", "1.12.2")]
    [InlineData("neoforge-21.1.172", LoaderKind.NeoForge, "21.1.172", null)]
    public void Forge_and_neoforge_profile_names_are_parsed(string id, LoaderKind kind, string loader, string? mc) =>
        Assert.Equal(new LoaderProfileRef(kind, loader, mc), LoaderInfo.TryParseProfileId(id));

    [Fact]
    public void Profile_ids_of_installed_loaders_round_trip()
    {
        Assert.Equal(new LoaderProfileRef(LoaderKind.Forge, "52.0.0", "1.21.1"), LoaderInfo.TryParseProfileId(LoaderInfo.ProfileId(LoaderKind.Forge, "1.21.1", "52.0.0")));
        Assert.Equal(LoaderKind.NeoForge, LoaderInfo.TryParseProfileId(LoaderInfo.ProfileId(LoaderKind.NeoForge, "1.21.1", "21.1.172"))!.Kind);
    }
}
