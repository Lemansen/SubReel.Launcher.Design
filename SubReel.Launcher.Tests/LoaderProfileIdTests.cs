using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Tests;

public sealed class LoaderProfileIdTests
{
    [Theory]
    [InlineData(LoaderKind.Fabric, "0.16.5", "1.21.1", "fabric-loader-0.16.5-1.21.1")]
    [InlineData(LoaderKind.Quilt, "0.27.0-beta.1", "1.21.1", "quilt-loader-0.27.0-beta.1-1.21.1")]
    public void Profile_id_follows_the_meta_server_naming(LoaderKind kind, string loader, string mc, string expected)
    {
        Assert.Equal(expected, LoaderInfo.ProfileId(kind, mc, loader));
        Assert.Equal(expected, new LoaderBuild(kind, mc, loader, true).ProfileId);
    }

    [Theory]
    [InlineData(LoaderKind.Forge)]
    [InlineData(LoaderKind.NeoForge)]
    [InlineData(LoaderKind.Vanilla)]
    public void Profile_id_is_not_built_for_loaders_with_an_installer(LoaderKind kind) =>
        Assert.Throws<NotSupportedException>(() => LoaderInfo.ProfileId(kind, "1.21.1", "1.0"));

    [Theory]
    [InlineData(LoaderKind.Fabric, "0.16.5", "1.21.1")]
    [InlineData(LoaderKind.Fabric, "0.15.11", "24w14a")]
    [InlineData(LoaderKind.Fabric, "0.16.5", "1.21.1-pre1")]
    [InlineData(LoaderKind.Quilt, "0.27.0-beta.1", "1.21.1")]
    [InlineData(LoaderKind.Quilt, "0.26.0", "26.1")]
    public void Parsing_gives_back_what_was_built(LoaderKind kind, string loader, string mc)
    {
        var parsed = LoaderInfo.TryParseProfileId(LoaderInfo.ProfileId(kind, mc, loader));

        Assert.Equal(new LoaderProfileRef(kind, loader, mc), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.21.1")]
    [InlineData("forge-47.2.0")]
    [InlineData("fabric-loader-")]
    [InlineData("fabric-loader-0.16.5")]
    public void Other_names_are_not_profiles(string? id) => Assert.Null(LoaderInfo.TryParseProfileId(id));

    [Theory]
    [InlineData(LoaderKind.Fabric, true)]
    [InlineData(LoaderKind.Quilt, true)]
    [InlineData(LoaderKind.Forge, false)]
    [InlineData(LoaderKind.NeoForge, false)]
    [InlineData(LoaderKind.Vanilla, false)]
    [InlineData(LoaderKind.Other, false)]
    public void Only_fabric_and_quilt_can_be_installed(LoaderKind kind, bool expected) =>
        Assert.Equal(expected, LoaderInfo.CanInstall(kind));
}
