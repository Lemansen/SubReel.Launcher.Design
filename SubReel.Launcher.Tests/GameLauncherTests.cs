using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Launching;

namespace SubReel.Launcher.Tests;

public sealed class GameLauncherTests
{
    private sealed class Env : IDisposable
    {
        public FakeGameWorld World { get; }
        public FakeJavaManager Java { get; } = new();
        public FakeSessionSource Sessions { get; } = new();
        public RecordingStarter Starter { get; } = new();
        public GameLauncher Launcher { get; }
        public string GameDirectory { get; }

        public Env(bool virtualAssets = false)
        {
            World = new FakeGameWorld(virtualAssets);
            GameDirectory = World.Dir.File(Path.Combine("instances", "main", "minecraft"));
            Launcher = new GameLauncher(
                World.Metadata, World.Installer, Java, Sessions, World.Natives,
                new ClasspathBuilder(World.Layout, World.Environment),
                new LegacyAssetsPreparer(World.Layout, NullLogger<LegacyAssetsPreparer>.Instance),
                World.Layout, World.Environment, Starter, NullLogger<GameLauncher>.Instance);
        }

        public LaunchRequest Request(LoaderKind loader = LoaderKind.Vanilla, int? min = null, int? max = null, string? jvm = null, string? javaPath = null) =>
            new(FakeGameWorld.VersionId, GameDirectory, new LauncherAccount { Name = "Steve" }, loader, min, max, jvm, javaPath);

        public void Dispose() => World.Dispose();
    }

    private static string Next(IReadOnlyList<string> arguments, string flag) => arguments[arguments.ToList().IndexOf(flag) + 1];

    [Fact]
    public async Task Missing_files_are_installed_and_game_is_started_with_full_command()
    {
        using var env = new Env();
        var progress = new ListProgress<LaunchProgress>();

        var result = await env.Launcher.LaunchAsync(env.Request(), progress);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(LaunchFailure.None, result.Failure);

        var spec = Assert.Single(env.Starter.Specs);
        var args = spec.Arguments;
        Assert.Equal(@"C:\java21\bin\java.exe", spec.Executable);
        Assert.Equal(env.GameDirectory, spec.WorkingDirectory);
        Assert.Equal(Path.Combine(env.GameDirectory, "logs", "subreel-game-output.log"), spec.LogFilePath);
        Assert.Contains("Steve", spec.LogHeader);

        Assert.Equal(21, env.Java.LastRequirement!.MajorVersion);
        Assert.Equal("-Xmx2048M", args[0]);
        Assert.Contains("net.minecraft.client.main.Main", args);
        Assert.Equal("Steve", Next(args, "--username"));
        Assert.DoesNotContain("--demo", args);
        Assert.Contains($"-Djava.library.path={Path.Combine(env.GameDirectory, "natives")}", args);
        Assert.Contains($"-Dlog4j.configurationFile={env.World.Layout.LoggingConfigPath("client-1.12.xml")}", args);

        var classpath = Next(args, "-cp").Split(';');
        Assert.Contains(classpath, p => p.EndsWith("core-1.0.jar", StringComparison.Ordinal));
        Assert.Equal(env.World.Layout.ClientJarPath(FakeGameWorld.VersionId), classpath[^1]);
        Assert.DoesNotContain(classpath, p => p.Contains("natives-linux", StringComparison.Ordinal));
        Assert.DoesNotContain(classpath, p => p.Contains("platform-1.0-natives", StringComparison.Ordinal));

        Assert.True(File.Exists(Path.Combine(env.GameDirectory, "natives", "old.dll")));

        LaunchProgress[] reports;
        lock (progress.Items) reports = progress.Items.ToArray();
        Assert.Equal(LaunchStage.SigningIn, reports[0].Stage);
        Assert.Equal(new LaunchProgress(LaunchStage.Starting, 1), reports[^1]);
        Assert.All(reports, r => Assert.InRange(r.Fraction, 0, 1));
    }

    [Fact]
    public async Task Memory_javaPath_and_user_jvm_arguments_come_from_the_request()
    {
        using var env = new Env();

        var result = await env.Launcher.LaunchAsync(env.Request(min: 4096, max: 1024, jvm: "-Dfoo=bar -Xss2M", javaPath: @"D:\jdk\bin\java.exe"));

        Assert.True(result.Succeeded, result.Error);
        var list = env.Starter.Specs[0].Arguments.ToList();
        Assert.Equal(@"D:\jdk\bin\java.exe", env.Java.LastExplicitPath);
        // минимум больше максимума: JVM такое не запустит, поэтому минимум сводится к максимуму
        Assert.Equal(new[] { "-Xms1024M", "-Xmx1024M" }, list.Take(2));
        Assert.True(list.IndexOf("-cp") < list.IndexOf("-Dfoo=bar"));
        Assert.True(list.IndexOf("-Xss2M") < list.IndexOf("net.minecraft.client.main.Main"));
    }

    [Fact]
    public async Task Stale_native_files_are_removed_before_extraction()
    {
        using var env = new Env();
        var natives = Path.Combine(env.GameDirectory, "natives");
        Directory.CreateDirectory(natives);
        await File.WriteAllTextAsync(Path.Combine(natives, "stale.dll"), "old");

        var result = await env.Launcher.LaunchAsync(env.Request());

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(Path.Combine(natives, "stale.dll")));
        Assert.True(File.Exists(Path.Combine(natives, "old.dll")));
    }

    [Fact]
    public async Task Installed_version_starts_again_without_touching_the_network()
    {
        using var env = new Env();
        var first = await env.Launcher.LaunchAsync(env.Request());
        Assert.True(first.Succeeded, first.Error);
        var requestsAfterInstall = env.World.Requested.Count;
        env.Starter.Last!.Exit(0);

        var second = await env.Launcher.LaunchAsync(env.Request());

        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(2, env.Starter.Specs.Count);
        Assert.Equal(requestsAfterInstall, env.World.Requested.Count);
    }

    [Fact]
    public async Task Same_game_directory_cannot_be_started_twice_until_the_game_exits()
    {
        using var env = new Env();
        var first = await env.Launcher.LaunchAsync(env.Request());
        Assert.True(first.Succeeded, first.Error);
        Assert.Same(first.Process, env.Launcher.FindRunning(env.GameDirectory));

        var second = await env.Launcher.LaunchAsync(env.Request());

        Assert.False(second.Succeeded);
        Assert.Equal(LaunchFailure.AlreadyRunning, second.Failure);
        Assert.Single(env.Starter.Specs);

        env.Starter.Last!.Exit(1);
        Assert.Null(env.Launcher.FindRunning(env.GameDirectory));
        Assert.Equal(GameStatus.Crashed, first.Process!.Status);

        var third = await env.Launcher.LaunchAsync(env.Request());
        Assert.True(third.Succeeded, third.Error);
    }

    [Fact]
    public async Task Failed_download_is_reported_and_does_not_block_the_next_attempt()
    {
        using var env = new Env();
        env.World.Break("https://dl.test/client.jar");

        var first = await env.Launcher.LaunchAsync(env.Request());
        var second = await env.Launcher.LaunchAsync(env.Request());

        Assert.Equal(LaunchFailure.FilesNotReady, first.Failure);
        Assert.False(string.IsNullOrWhiteSpace(first.Error));
        Assert.Equal(LaunchFailure.FilesNotReady, second.Failure); // не AlreadyRunning: резерв папки снят
        Assert.Empty(env.Starter.Specs);
        Assert.Null(env.Launcher.FindRunning(env.GameDirectory));
    }

    [Fact]
    public async Task Missing_java_is_reported_with_its_message()
    {
        using var env = new Env();
        env.Java.Result = new JavaEnsureResult(null, JavaSelectionStatus.NotFound, false, false, "Нет Java 21.");

        var result = await env.Launcher.LaunchAsync(env.Request());

        Assert.Equal(LaunchFailure.JavaUnavailable, result.Failure);
        Assert.Equal("Нет Java 21.", result.Error);
        Assert.Empty(env.Starter.Specs);
    }

    [Theory]
    [InlineData(AuthFailureReason.SignInRequired, LaunchFailure.SignInRequired)]
    [InlineData(AuthFailureReason.Network, LaunchFailure.SignInFailed)]
    public async Task Session_failure_stops_the_launch_before_anything_is_downloaded(AuthFailureReason reason, LaunchFailure expected)
    {
        using var env = new Env();
        env.Sessions.Failure = new AuthenticationException(reason, "Нужен вход.");

        var result = await env.Launcher.LaunchAsync(env.Request());

        Assert.Equal(expected, result.Failure);
        Assert.Equal("Нужен вход.", result.Error);
        Assert.Empty(env.World.Requested);
        Assert.Empty(env.Starter.Specs);
    }

    [Theory]
    [InlineData(LoaderKind.Forge)]
    [InlineData(LoaderKind.NeoForge)]
    [InlineData(LoaderKind.Other)]
    public async Task Loaders_without_an_installer_are_not_launched_as_vanilla(LoaderKind loader)
    {
        using var env = new Env();

        var result = await env.Launcher.LaunchAsync(env.Request(loader: loader));

        Assert.Equal(LaunchFailure.UnsupportedLoader, result.Failure);
        Assert.Equal(0, env.Sessions.Calls);
        Assert.Empty(env.Starter.Specs);
    }

    [Theory]
    [InlineData(LoaderKind.Fabric)]
    [InlineData(LoaderKind.Quilt)]
    public async Task Fabric_and_quilt_start_like_any_installed_version(LoaderKind loader)
    {
        using var env = new Env();

        var result = await env.Launcher.LaunchAsync(env.Request(loader: loader));

        Assert.True(result.Succeeded, result.Error);
        Assert.Single(env.Starter.Specs);
    }

    [Fact]
    public async Task Process_start_failure_is_reported_and_releases_the_directory()
    {
        using var env = new Env();
        env.Starter.Failure = new Win32Exception("Файл не найден.");

        var failed = await env.Launcher.LaunchAsync(env.Request());

        Assert.Equal(LaunchFailure.StartFailed, failed.Failure);
        Assert.Contains("Файл не найден.", failed.Error);
        Assert.Null(env.Launcher.FindRunning(env.GameDirectory));

        env.Starter.Failure = null;
        var retry = await env.Launcher.LaunchAsync(env.Request());
        Assert.True(retry.Succeeded, retry.Error);
    }

    [Fact]
    public async Task Cancellation_before_start_throws_and_releases_the_directory()
    {
        using var env = new Env();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Launcher.LaunchAsync(env.Request(), ct: cts.Token));

        var retry = await env.Launcher.LaunchAsync(env.Request());
        Assert.True(retry.Succeeded, retry.Error);
    }

    [Fact]
    public async Task Virtual_assets_version_gets_game_assets_copy_before_start()
    {
        using var env = new Env(virtualAssets: true);

        var result = await env.Launcher.LaunchAsync(env.Request());

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(env.World.Layout.VirtualAssetPath("17", "icons/icon.png")));
    }
}
