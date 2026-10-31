using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Minecraft.Java;

namespace SubReel.Launcher.Tests;

internal static class JavaTestHomes
{
    public static string ExeName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>Папка «установленной Java»: bin/java и файл release. Запускать её не нужно — версию отдаёт release.</summary>
    public static string Create(string root, string name, string version = "21.0.4", string arch = "x86_64", string? vendor = "Eclipse Adoptium")
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", ExeName), "fake");
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nOS_ARCH=\"{arch}\"\n" + (vendor is null ? "" : $"IMPLEMENTOR=\"{vendor}\"\n"));
        return home;
    }
}

internal sealed class FixedHomesSource : IJavaHomeSource
{
    private readonly List<string> _homes;
    public FixedHomesSource(JavaSource source, params string[] homes)
    {
        Source = source;
        _homes = homes.ToList();
    }

    public JavaSource Source { get; }
    public void Add(string home) => _homes.Add(home);
    public IEnumerable<string> GetHomes() => _homes.ToList();
}

public sealed class JavaVersionReaderTests
{
    private static JavaVersionReader Reader() => new(NullLogger<JavaVersionReader>.Instance);

    [Fact]
    public void Parses_release_file()
    {
        var parsed = JavaVersionReader.ParseRelease("JAVA_VERSION=\"21.0.4\"\r\nOS_ARCH=\"x86_64\"\r\nIMPLEMENTOR=\"Eclipse Adoptium\"\r\n");

        Assert.Equal(21, parsed!.Value.Major);
        Assert.Equal("21.0.4", parsed.Value.Version);
        Assert.True(parsed.Value.Is64Bit);
        Assert.Equal("Eclipse Adoptium", parsed.Value.Vendor);
    }

    [Theory]
    [InlineData("x86", false)]
    [InlineData("i386", false)]
    [InlineData("amd64", true)]
    [InlineData("aarch64", true)]
    public void Release_architecture_decides_bitness(string arch, bool is64)
    {
        var parsed = JavaVersionReader.ParseRelease($"JAVA_VERSION=\"17.0.1\"\nOS_ARCH=\"{arch}\"");
        Assert.Equal(is64, parsed!.Value.Is64Bit);
    }

    [Fact]
    public void Old_style_version_in_release_gives_major_8()
    {
        var parsed = JavaVersionReader.ParseRelease("JAVA_VERSION=\"1.8.0_392\"");
        Assert.Equal(8, parsed!.Value.Major);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OS_ARCH=\"x86_64\"")]
    [InlineData("JAVA_VERSION=\"nonsense\"")]
    public void Release_without_a_usable_version_is_rejected(string text) =>
        Assert.Null(JavaVersionReader.ParseRelease(text));

    [Fact]
    public void Parses_java_version_output()
    {
        const string output = "openjdk version \"21.0.4\" 2024-07-16 LTS\nOpenJDK Runtime Environment Temurin-21.0.4+7 (build 21.0.4+7-LTS)\nOpenJDK 64-Bit Server VM Temurin-21.0.4+7 (build 21.0.4+7-LTS, mixed mode, sharing)";
        var parsed = JavaVersionReader.ParseVersionOutput(output);

        Assert.Equal(21, parsed!.Value.Major);
        Assert.Equal("21.0.4", parsed.Value.Version);
        Assert.True(parsed.Value.Is64Bit);
    }

    [Fact]
    public void Output_of_32_bit_java_8_is_recognised()
    {
        var parsed = JavaVersionReader.ParseVersionOutput("java version \"1.8.0_391\"\nJava HotSpot(TM) Client VM (build 25.391-b13, mixed mode)");
        Assert.Equal(8, parsed!.Value.Major);
        Assert.False(parsed.Value.Is64Bit);
    }

    [Fact]
    public void Unrelated_output_is_rejected() =>
        Assert.Null(JavaVersionReader.ParseVersionOutput("command not found"));

    [Fact]
    public async Task Reads_installation_from_release_without_starting_java()
    {
        using var dir = new TempDir();
        var home = JavaTestHomes.Create(dir.Root, "jdk-21");

        var installation = await Reader().ReadAsync(home, JavaSource.Registry, null, CancellationToken.None);

        Assert.NotNull(installation);
        Assert.Equal(21, installation.MajorVersion);
        Assert.Equal("21.0.4", installation.FullVersion);
        Assert.Equal(JavaSource.Registry, installation.Source);
        Assert.Equal("Eclipse Adoptium", installation.Vendor);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "bin", JavaTestHomes.ExeName)), installation.Executable);
    }

    [Fact]
    public async Task Folder_without_java_executable_is_not_a_java_home()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "empty"));

        Assert.Null(await Reader().ReadAsync(Path.Combine(dir.Root, "empty"), JavaSource.KnownFolder, null, CancellationToken.None));
    }

    [Fact]
    public async Task Broken_executable_without_release_file_is_ignored()
    {
        using var dir = new TempDir();
        var home = JavaTestHomes.Create(dir.Root, "broken");
        File.Delete(Path.Combine(home, "release"));

        Assert.Null(await Reader().ReadAsync(home, JavaSource.KnownFolder, null, CancellationToken.None));
    }
}

public sealed class JavaHomeSourceTests
{
    [Fact]
    public void Environment_source_returns_java_home_and_path_entries_with_java()
    {
        using var dir = new TempDir();
        var home = JavaTestHomes.Create(dir.Root, "from-path");
        var variables = new Dictionary<string, string?>
        {
            ["JAVA_HOME"] = "/custom/java",
            ["PATH"] = string.Join(Path.PathSeparator, Path.Combine(dir.Root, "nothing"), Path.Combine(home, "bin")),
        };
        var source = new JavaHomeEnvironmentSource(name => variables.GetValueOrDefault(name));

        var homes = source.GetHomes().ToList();

        Assert.Equal(new[] { "/custom/java", home }, homes);
    }

    [Fact]
    public void Environment_source_without_variables_returns_nothing() =>
        Assert.Empty(new JavaHomeEnvironmentSource(_ => null).GetHomes());

    [Fact]
    public void Known_folders_source_lists_children_of_existing_roots_only()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Root, "jvm");
        JavaTestHomes.Create(root, "temurin-21");
        JavaTestHomes.Create(root, "temurin-17");
        var source = new KnownFoldersJavaSource(new[] { root, Path.Combine(dir.Root, "missing") });

        var homes = source.GetHomes().ToList();

        Assert.Contains(Path.Combine(root, "temurin-21"), homes);
        Assert.Contains(Path.Combine(root, "temurin-17"), homes);
        Assert.DoesNotContain(homes, h => h.Contains("missing"));
    }

    [Fact]
    public void Official_launcher_source_follows_component_platform_layout()
    {
        using var dir = new TempDir();
        var runtime = Path.Combine(dir.Root, "runtime");
        var home = JavaTestHomes.Create(Path.Combine(runtime, "java-runtime-delta", "windows-x64"), "java-runtime-delta");
        var source = new OfficialLauncherJavaSource(runtime);

        Assert.Equal(new[] { home }, source.GetHomes());
        Assert.Empty(new OfficialLauncherJavaSource(Path.Combine(dir.Root, "none")).GetHomes());
    }

    [Fact]
    public void Registry_source_does_not_fail_on_any_platform() =>
        Assert.NotNull(new WindowsRegistryJavaSource().GetHomes().ToList());
}
