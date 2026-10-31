using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Tests;

public sealed class RuleEvaluatorTests
{
    private static readonly OsEnvironment Windows = new("windows", "x86_64", "10.0.22631");
    private static readonly OsEnvironment Linux = new("linux", "x86_64", "6.8");

    [Fact]
    public void No_rules_means_allowed() =>
        Assert.True(RuleEvaluator.IsAllowed(Array.Empty<Rule>(), Windows));

    [Fact]
    public void Allow_for_one_os_only()
    {
        var rules = new[] { new Rule(true, OsName: "windows") };
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, Linux));
    }

    [Fact]
    public void Allow_everything_except_one_os()
    {
        var rules = new[] { new Rule(true), new Rule(false, OsName: "osx") };
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new OsEnvironment("osx", "arm64")));
    }

    [Fact]
    public void Last_matching_rule_wins()
    {
        var rules = new[] { new Rule(false, OsName: "windows"), new Rule(true, OsName: "windows") };
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void Only_disallow_rule_that_does_not_match_leaves_library_disallowed()
    {
        // Список без подошедшего правила «allow» запрещает: так читает правила официальный лаунчер.
        Assert.False(RuleEvaluator.IsAllowed(new[] { new Rule(false, OsName: "osx") }, Windows));
    }

    [Fact]
    public void Arch_must_match()
    {
        var x86Only = new[] { new Rule(true, OsName: "windows", OsArch: "x86") };
        Assert.False(RuleEvaluator.IsAllowed(x86Only, Windows));
        Assert.True(RuleEvaluator.IsAllowed(x86Only, new OsEnvironment("windows", "x86")));
    }

    [Fact]
    public void Os_version_is_a_regex_and_unknown_version_does_not_match()
    {
        var rules = new[] { new Rule(true, OsName: "windows", OsVersionPattern: "^10\\.") };
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new OsEnvironment("windows", "x86_64", "6.1")));
        Assert.False(RuleEvaluator.IsAllowed(rules, new OsEnvironment("windows", "x86_64")));
        Assert.False(RuleEvaluator.IsAllowed(new[] { new Rule(true, OsVersionPattern: "([") }, Windows));
    }

    [Fact]
    public void Features_are_off_by_default()
    {
        var demo = new[] { new Rule(true, Features: new Dictionary<string, bool> { ["is_demo_user"] = true }) };
        Assert.False(RuleEvaluator.IsAllowed(demo, Windows));
        Assert.True(RuleEvaluator.IsAllowed(demo, Windows with { Features = new Dictionary<string, bool> { ["is_demo_user"] = true } }));
    }
}

public sealed class MavenCoordinateTests
{
    [Theory]
    [InlineData("net.fabricmc:fabric-loader:0.16.5", "net/fabricmc/fabric-loader/0.16.5/fabric-loader-0.16.5.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("a.b:c:1.0@zip", "a/b/c/1.0/c-1.0.zip")]
    public void Builds_repository_path(string name, string expected) =>
        Assert.Equal(expected, MavenCoordinate.ToRelativePath(name));

    [Theory]
    [InlineData("nonsense")]
    [InlineData("a:b")]
    [InlineData("a:b:c:d:e")]
    [InlineData("a::1.0")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.Throws<FormatException>(() => MavenCoordinate.ToRelativePath(name));

    [Theory]
    [InlineData("com.example:core:1.0", "com.example:core")]
    [InlineData("com.example:core:2.0", "com.example:core")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org.lwjgl:lwjgl:natives-windows")]
    public void Key_ignores_version(string name, string expected) =>
        Assert.Equal(expected, MavenCoordinate.KeyOf(name));
}

public sealed class VersionResolverTests
{
    private static Library Lib(string name) =>
        new(name, null, new Dictionary<string, string>(), new Dictionary<string, LibraryArtifact>(), Array.Empty<Rule>(), Array.Empty<string>());

    private static ArgumentValue Arg(string value) => new(new[] { value }, Array.Empty<Rule>());

    private static GameVersionMetadata Parent() => new()
    {
        Id = "1.21.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "17",
        AssetIndex = new AssetIndexInfo("17", new FileDownloadInfo("https://x/17.json", "a", 1)),
        ClientDownload = new FileDownloadInfo("https://x/client.jar", "b", 2),
        Libraries = new[] { Lib("com.example:core:1.0"), Lib("org.other:lib:1.0") },
        GameArguments = new[] { Arg("--username") },
        JvmArguments = new[] { Arg("-cp") },
        JavaMajorVersion = 21,
    };

    private static GameVersionMetadata Child() => new()
    {
        Id = "fabric-loader-0.16.5-1.21.1",
        Type = "release",
        InheritsFrom = "1.21.1",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = new[] { Lib("com.example:core:2.0"), Lib("net.fabricmc:fabric-loader:0.16.5") },
        GameArguments = new[] { Arg("--fabric") },
    };

    [Fact]
    public void Child_overrides_scalars_and_inherits_missing_ones()
    {
        var resolved = VersionResolver.Resolve("fabric-loader-0.16.5-1.21.1", new[] { Child(), Parent() });

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", resolved.MainClass);
        Assert.Equal("17", resolved.AssetsId);
        Assert.Equal("17", resolved.AssetIndex!.Id);
        Assert.Equal(21, resolved.JavaMajorVersion);
        Assert.Equal("1.21.1", resolved.ClientJarVersionId);
        Assert.Equal("https://x/client.jar", resolved.ClientDownload!.Url);
        Assert.Equal(new[] { "fabric-loader-0.16.5-1.21.1", "1.21.1" }, resolved.Chain);
    }

    [Fact]
    public void Child_library_replaces_parent_version_and_keeps_child_first()
    {
        var resolved = VersionResolver.Resolve("x", new[] { Child(), Parent() });

        Assert.Equal(
            new[] { "com.example:core:2.0", "net.fabricmc:fabric-loader:0.16.5", "org.other:lib:1.0" },
            resolved.Libraries.Select(l => l.Name));
    }

    [Fact]
    public void Parent_arguments_come_before_child_arguments()
    {
        var resolved = VersionResolver.Resolve("x", new[] { Child(), Parent() });

        Assert.Equal(new[] { "--username", "--fabric" }, resolved.GameArguments.SelectMany(a => a.Values));
        Assert.Equal(new[] { "-cp" }, resolved.JvmArguments.SelectMany(a => a.Values));
    }

    [Fact]
    public void Single_version_resolves_to_itself()
    {
        var resolved = VersionResolver.Resolve("1.21.1", new[] { Parent() });
        Assert.Equal("1.21.1", resolved.ClientJarVersionId);
        Assert.Equal(2, resolved.Libraries.Count);
    }

    [Fact]
    public void Missing_main_class_and_empty_chain_are_errors()
    {
        var noMain = new GameVersionMetadata { Id = "broken" };
        Assert.Throws<VersionMetadataException>(() => VersionResolver.Resolve("broken", new[] { noMain }));
        Assert.Throws<VersionMetadataException>(() => VersionResolver.Resolve("broken", Array.Empty<GameVersionMetadata>()));
    }

    [Fact]
    public void Assets_id_falls_back_to_index_id_then_legacy()
    {
        var withIndex = new GameVersionMetadata { Id = "a", MainClass = "M", AssetIndex = new AssetIndexInfo("idx", new FileDownloadInfo("u", null, null)) };
        var bare = new GameVersionMetadata { Id = "b", MainClass = "M" };

        Assert.Equal("idx", VersionResolver.Resolve("a", new[] { withIndex }).AssetsId);
        Assert.Equal("legacy", VersionResolver.Resolve("b", new[] { bare }).AssetsId);
    }
}
