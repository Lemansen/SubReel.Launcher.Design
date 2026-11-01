using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Tests;

public sealed class LaunchCommandBuilderTests
{
    private static readonly OsEnvironment Windows = new("windows", "x86_64", "10.0.22631");
    private static readonly OsEnvironment Linux = new("linux", "x86_64", "6.8");

    private static ArgumentValue Arg(params string[] values) => new(values, Array.Empty<Rule>());

    private static ArgumentValue Arg(Rule rule, params string[] values) => new(values, new[] { rule });

    private static ResolvedVersion Version(
        IReadOnlyList<ArgumentValue>? jvm = null,
        IReadOnlyList<ArgumentValue>? game = null,
        string? legacy = null,
        LoggingConfigInfo? logging = null) => new()
    {
        Id = "1.21.1",
        Chain = new[] { "1.21.1" },
        MainClass = "net.minecraft.client.main.Main",
        AssetsId = "17",
        ClientJarVersionId = "1.21.1",
        Libraries = Array.Empty<Library>(),
        JvmArguments = jvm ?? Array.Empty<ArgumentValue>(),
        GameArguments = game ?? Array.Empty<ArgumentValue>(),
        LegacyGameArguments = legacy,
        Logging = logging,
        Type = "release",
    };

    private static LaunchParameters Parameters(Action<LaunchParametersBuilder>? change = null)
    {
        var b = new LaunchParametersBuilder();
        change?.Invoke(b);
        return new LaunchParameters
        {
            PlayerName = b.PlayerName,
            PlayerUuid = "069a79f444e94726a5befca90e38aaf5",
            AccessToken = b.AccessToken,
            UserType = "msa",
            GameDirectory = b.GameDirectory,
            AssetsRoot = @"C:\game\assets",
            GameAssetsDirectory = @"C:\game\assets\virtual\legacy",
            NativesDirectory = @"C:\mc\natives",
            LibraryDirectory = @"C:\game\libraries",
            Classpath = new[] { @"C:\game\libraries\a.jar", @"C:\game\versions\1.21.1\1.21.1.jar" },
            LoggingConfigPath = b.LoggingConfigPath,
            MinMemoryMb = b.Min,
            MaxMemoryMb = b.Max,
            ExtraJvmArguments = b.Extra,
            WindowWidth = b.Width,
            WindowHeight = b.Height,
        };
    }

    private sealed class LaunchParametersBuilder
    {
        public string PlayerName { get; set; } = "Steve";
        public string AccessToken { get; set; } = "secret-token-123456";
        public string GameDirectory { get; set; } = @"C:\mc";
        public string? LoggingConfigPath { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; } = 2048;
        public IReadOnlyList<string> Extra { get; set; } = Array.Empty<string>();
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    private static readonly ArgumentValue[] ModernJvm =
    {
        Arg("-Djava.library.path=${natives_directory}"),
        Arg("-cp", "${classpath}"),
    };

    private static readonly ArgumentValue[] ModernGame =
    {
        Arg("--username", "${auth_player_name}"),
        Arg("--version", "${version_name}"),
        Arg("--gameDir", "${game_directory}"),
        Arg("--assetsDir", "${assets_root}"),
        Arg("--assetIndex", "${assets_index_name}"),
        Arg("--uuid", "${auth_uuid}"),
        Arg("--accessToken", "${auth_access_token}"),
        Arg("--userType", "${user_type}"),
        Arg("--versionType", "${version_type}"),
    };

    [Fact]
    public void Modern_version_gets_memory_jvm_arguments_main_class_and_game_arguments_in_order()
    {
        var command = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(), Windows);

        Assert.Equal(new[]
        {
            "-Xmx2048M",
            @"-Djava.library.path=C:\mc\natives",
            "-cp", @"C:\game\libraries\a.jar;C:\game\versions\1.21.1\1.21.1.jar",
            "net.minecraft.client.main.Main",
            "--username", "Steve",
            "--version", "1.21.1",
            "--gameDir", @"C:\mc",
            "--assetsDir", @"C:\game\assets",
            "--assetIndex", "17",
            "--uuid", "069a79f444e94726a5befca90e38aaf5",
            "--accessToken", "secret-token-123456",
            "--userType", "msa",
            "--versionType", "release",
        }, command.Arguments);
        Assert.Empty(command.UnresolvedPlaceholders);
    }

    [Fact]
    public void Classpath_separator_depends_on_platform()
    {
        var onLinux = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(), Linux);
        var classpath = onLinux.Arguments[onLinux.Arguments.ToList().IndexOf("-cp") + 1];

        Assert.Contains(":", classpath);
        Assert.DoesNotContain(";", classpath);
    }

    [Fact]
    public void Memory_flags_are_added_only_when_set()
    {
        var both = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(b => { b.Min = 512; b.Max = 4096; }), Windows);
        var none = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(b => b.Max = null), Windows);

        Assert.Equal(new[] { "-Xms512M", "-Xmx4096M" }, both.Arguments.Take(2));
        Assert.DoesNotContain(none.Arguments, a => a.StartsWith("-Xm", StringComparison.Ordinal));
    }

    [Fact]
    public void Jvm_arguments_follow_os_rules()
    {
        var jvm = new[]
        {
            Arg(new Rule(true, OsName: "windows"), "-XX:HeapDumpPath=dump"),
            Arg(new Rule(true, OsName: "osx"), "-XstartOnFirstThread"),
            Arg("-Djava.library.path=${natives_directory}"),
            Arg("-cp", "${classpath}"),
        };

        var onWindows = LaunchCommandBuilder.Build(Version(jvm, ModernGame), Parameters(), Windows);
        var onLinux = LaunchCommandBuilder.Build(Version(jvm, ModernGame), Parameters(), Linux);

        Assert.Contains("-XX:HeapDumpPath=dump", onWindows.Arguments);
        Assert.DoesNotContain("-XstartOnFirstThread", onWindows.Arguments);
        Assert.DoesNotContain("-XX:HeapDumpPath=dump", onLinux.Arguments);
    }

    [Fact]
    public void Demo_arguments_are_skipped_and_resolution_only_when_requested()
    {
        var game = ModernGame.Concat(new[]
        {
            Arg(new Rule(true, Features: new Dictionary<string, bool> { ["is_demo_user"] = true }), "--demo"),
            Arg(new Rule(true, Features: new Dictionary<string, bool> { ["has_custom_resolution"] = true }), "--width", "${resolution_width}", "--height", "${resolution_height}"),
        }).ToList();

        var plain = LaunchCommandBuilder.Build(Version(ModernJvm, game), Parameters(), Windows);
        var sized = LaunchCommandBuilder.Build(Version(ModernJvm, game), Parameters(b => { b.Width = 854; b.Height = 480; }), Windows);

        Assert.DoesNotContain("--demo", plain.Arguments);
        Assert.DoesNotContain("--width", plain.Arguments);
        Assert.DoesNotContain("--demo", sized.Arguments);
        var list = sized.Arguments.ToList();
        Assert.Equal("854", list[list.IndexOf("--width") + 1]);
        Assert.Equal("480", list[list.IndexOf("--height") + 1]);
    }

    [Fact]
    public void Legacy_version_without_jvm_arguments_gets_defaults_and_split_game_string()
    {
        var version = Version(legacy: "--username ${auth_player_name} --session ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}");

        var command = LaunchCommandBuilder.Build(version, Parameters(b => b.GameDirectory = @"C:\My Games\mc"), Windows);
        var list = command.Arguments.ToList();

        Assert.Contains(@"-Djava.library.path=C:\mc\natives", list);
        Assert.Contains("-cp", list);
        Assert.Contains("-Dminecraft.launcher.brand=SubReel", list);
        Assert.Equal("Steve", list[list.IndexOf("--username") + 1]);
        Assert.Equal("token:secret-token-123456:069a79f444e94726a5befca90e38aaf5", list[list.IndexOf("--session") + 1]);
        // путь с пробелом остаётся одним аргументом: делится шаблон, а не подставленное значение
        Assert.Equal(@"C:\My Games\mc", list[list.IndexOf("--gameDir") + 1]);
        Assert.Equal(@"C:\game\assets\virtual\legacy", list[list.IndexOf("--assetsDir") + 1]);
    }

    [Fact]
    public void Missing_classpath_and_natives_path_are_added_when_version_lacks_them()
    {
        var jvm = new[] { Arg("-Dminecraft.launcher.brand=${launcher_name}") };

        var command = LaunchCommandBuilder.Build(Version(jvm, ModernGame), Parameters(), Windows);
        var list = command.Arguments.ToList();

        Assert.Contains(@"-Djava.library.path=C:\mc\natives", list);
        Assert.Equal(@"C:\game\libraries\a.jar;C:\game\versions\1.21.1\1.21.1.jar", list[list.IndexOf("-cp") + 1]);
        Assert.True(list.IndexOf("-cp") < list.IndexOf("net.minecraft.client.main.Main"));
    }

    [Fact]
    public void Substituted_values_are_not_expanded_again()
    {
        var command = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(b => b.PlayerName = "${auth_access_token}"), Windows);
        var list = command.Arguments.ToList();

        Assert.Equal("${auth_access_token}", list[list.IndexOf("--username") + 1]);
    }

    [Fact]
    public void Unknown_placeholder_is_left_in_place_and_reported_once()
    {
        var game = new[] { Arg("--flag", "${no_such_value}"), Arg("--again", "${no_such_value}") };

        var command = LaunchCommandBuilder.Build(Version(ModernJvm, game), Parameters(), Windows);

        Assert.Contains("${no_such_value}", command.Arguments);
        Assert.Equal(new[] { "no_such_value" }, command.UnresolvedPlaceholders);
    }

    [Fact]
    public void Logging_argument_goes_after_version_jvm_arguments_and_user_arguments_before_main_class_without_substitution()
    {
        var logging = new LoggingConfigInfo("-Dlog4j.configurationFile=${path}", "client.xml", new FileDownloadInfo("https://x/log.xml", null, null));

        var command = LaunchCommandBuilder.Build(
            Version(ModernJvm, ModernGame, logging: logging),
            Parameters(b =>
            {
                b.LoggingConfigPath = @"C:\game\assets\log_configs\client.xml";
                b.Extra = new[] { "-Xss2M", "-Dpath=${classpath}" };
            }),
            Windows);
        var list = command.Arguments.ToList();

        var cp = list.IndexOf("-cp");
        var log = list.IndexOf(@"-Dlog4j.configurationFile=C:\game\assets\log_configs\client.xml");
        var user = list.IndexOf("-Xss2M");
        var main = list.IndexOf("net.minecraft.client.main.Main");

        Assert.True(cp >= 0 && cp < log && log < user && user < main);
        Assert.Contains("-Dpath=${classpath}", list); // аргументы пользователя подставляются как есть
        Assert.Empty(command.UnresolvedPlaceholders);
    }

    [Fact]
    public void Logging_argument_is_skipped_without_config_path()
    {
        var logging = new LoggingConfigInfo("-Dlog4j.configurationFile=${path}", "client.xml", new FileDownloadInfo("https://x/log.xml", null, null));

        var command = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame, logging: logging), Parameters(), Windows);

        Assert.DoesNotContain(command.Arguments, a => a.Contains("log4j", StringComparison.Ordinal));
    }

    [Fact]
    public void Display_string_hides_long_token_and_quotes_arguments_with_spaces()
    {
        var command = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(b => b.GameDirectory = @"C:\My Games\mc"), Windows);

        var text = command.ToDisplayString("secret-token-123456");

        Assert.DoesNotContain("secret-token-123456", text);
        Assert.Contains("<скрыт>", text);
        Assert.Contains("\"C:\\My Games\\mc\"", text);
    }

    [Fact]
    public void Display_string_keeps_short_offline_token_readable()
    {
        var command = LaunchCommandBuilder.Build(Version(ModernJvm, ModernGame), Parameters(b => b.AccessToken = "0"), Windows);

        var text = command.ToDisplayString("0");

        Assert.DoesNotContain("<скрыт>", text);
        Assert.Contains("--accessToken 0", text);
    }
}

public sealed class CommandLineTokenizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_gives_no_arguments(string? text) => Assert.Empty(CommandLineTokenizer.Split(text));

    [Fact]
    public void Splits_on_any_whitespace_and_ignores_extra_spaces() =>
        Assert.Equal(new[] { "-Xss2M", "-XX:+UseG1GC", "-Dfoo=bar" }, CommandLineTokenizer.Split("  -Xss2M \t -XX:+UseG1GC\n-Dfoo=bar  "));

    [Fact]
    public void Quoted_part_stays_whole_and_loses_quotes() =>
        Assert.Equal(new[] { "-Dname=a b", "-Xss2M" }, CommandLineTokenizer.Split("-Dname=\"a b\" -Xss2M"));

    [Fact]
    public void Unclosed_quote_takes_the_rest_of_the_text() =>
        Assert.Equal(new[] { "-Dname=a b c" }, CommandLineTokenizer.Split("-Dname=\"a b c"));
}
