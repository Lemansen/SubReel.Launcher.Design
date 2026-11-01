using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;

namespace SubReel.Launcher.Tests;

public sealed class GamePlayServiceTests
{
    private sealed class RecordingLauncher : IGameLauncher
    {
        public List<LaunchRequest> Requests { get; } = new();
        public LaunchResult Result { get; set; } = LaunchResult.Success(new FakeGameProcess("game"));

        public Task<LaunchResult> LaunchAsync(LaunchRequest request, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public IGameProcess? FindRunning(string gameDirectory) => null;
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir _dir = new();

        public Env()
        {
            var paths = new AppPaths(_dir.Root, _dir.Root);
            Repo = new JsonInstanceRepository(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<JsonInstanceRepository>.Instance);
            Time = new ManualTimeProvider();
            Instances = new InstanceService(Repo, Time, NullLogger<InstanceService>.Instance);
            Launcher = new RecordingLauncher();
            Loaders = new FakeLoaderInstaller();
            Service = new GamePlayService(Instances, Launcher, Loaders, NullLogger<GamePlayService>.Instance);
        }

        public JsonInstanceRepository Repo { get; }
        public ManualTimeProvider Time { get; }
        public InstanceService Instances { get; }
        public RecordingLauncher Launcher { get; }
        public FakeLoaderInstaller Loaders { get; }
        public GamePlayService Service { get; }

        public static PlayRequest Request(string version = "1.21.1", LoaderKind loader = LoaderKind.Vanilla, string? loaderVersion = null) =>
            new(version, loader, new LauncherAccount { Name = "Steve" }, loaderVersion);

        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public async Task First_play_creates_a_build_for_the_version_and_launches_in_its_game_folder()
    {
        using var env = new Env();

        var result = await env.Service.PlayAsync(Env.Request());

        Assert.True(result.Launch.Succeeded);
        Assert.NotNull(result.Instance);
        Assert.Equal("Minecraft 1.21.1", result.Instance!.Name);
        Assert.Equal("1.21.1", result.Instance.McVersion);

        var launch = Assert.Single(env.Launcher.Requests);
        Assert.Equal("1.21.1", launch.VersionId);
        Assert.Equal(env.Repo.GetGameDirectory(result.Instance.Id), launch.GameDirectory);
        Assert.Equal("Steve", launch.Account.Name);
    }

    [Fact]
    public async Task Second_play_of_the_same_version_reuses_the_build()
    {
        using var env = new Env();

        var first = await env.Service.PlayAsync(Env.Request());
        var second = await env.Service.PlayAsync(Env.Request());

        Assert.Equal(first.Instance!.Id, second.Instance!.Id);
        Assert.Single(await env.Instances.ListAsync());
        Assert.Equal(2, env.Launcher.Requests.Count);
        Assert.Equal(env.Launcher.Requests[0].GameDirectory, env.Launcher.Requests[1].GameDirectory);
    }

    [Fact]
    public async Task Different_versions_get_separate_builds_with_separate_game_folders()
    {
        using var env = new Env();

        var a = await env.Service.PlayAsync(Env.Request("1.21.1"));
        var b = await env.Service.PlayAsync(Env.Request("1.20.4"));

        Assert.NotEqual(a.Instance!.Id, b.Instance!.Id);
        Assert.NotEqual(env.Launcher.Requests[0].GameDirectory, env.Launcher.Requests[1].GameDirectory);
        Assert.Equal(2, (await env.Instances.ListAsync()).Count);
    }

    [Fact]
    public async Task Successful_launch_marks_the_time_of_last_play()
    {
        using var env = new Env();
        env.Time.Now = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);

        var result = await env.Service.PlayAsync(Env.Request());

        var saved = await env.Instances.GetAsync(result.Instance!.Id);
        Assert.Equal(env.Time.Now.UtcDateTime, saved!.LastPlayedUtc);
    }

    [Fact]
    public async Task Failed_launch_returns_the_failure_and_does_not_mark_the_build_as_played()
    {
        using var env = new Env();
        env.Launcher.Result = LaunchResult.Fail(LaunchFailure.JavaUnavailable, "Нет Java.");

        var result = await env.Service.PlayAsync(Env.Request());

        Assert.False(result.Launch.Succeeded);
        Assert.Equal(LaunchFailure.JavaUnavailable, result.Launch.Failure);
        Assert.Equal("Нет Java.", result.Launch.Error);
        var saved = await env.Instances.GetAsync(result.Instance!.Id);
        Assert.Null(saved!.LastPlayedUtc);
    }

    [Theory]
    [InlineData(LoaderKind.Other)]
    public async Task Loader_without_an_installer_is_refused_without_creating_a_build(LoaderKind loader)
    {
        using var env = new Env();

        var result = await env.Service.PlayAsync(Env.Request(loader: loader));

        Assert.Equal(LaunchFailure.UnsupportedLoader, result.Launch.Failure);
        Assert.Null(result.Instance);
        Assert.Empty(env.Launcher.Requests);
        Assert.Empty(env.Loaders.Calls);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Fabric_play_prepares_the_profile_and_launches_it_in_its_own_build()
    {
        using var env = new Env();

        var result = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.5"));

        Assert.True(result.Launch.Succeeded);
        Assert.Equal((LoaderKind.Fabric, "1.21.1", (string?)"0.16.5"), Assert.Single(env.Loaders.Calls));
        Assert.Equal("Minecraft 1.21.1 Fabric", result.Instance!.Name);
        Assert.Equal(LoaderKind.Fabric, result.Instance.Loader);
        Assert.Equal("0.16.5", result.Instance.LoaderVersion);

        var launch = Assert.Single(env.Launcher.Requests);
        Assert.Equal("fabric-loader-0.16.5-1.21.1", launch.VersionId);
        Assert.Equal(LoaderKind.Fabric, launch.Loader);
        Assert.Equal(env.Repo.GetGameDirectory(result.Instance.Id), launch.GameDirectory);
    }

    [Fact]
    public async Task Missing_loader_version_means_latest_and_is_pinned_in_the_build()
    {
        using var env = new Env();
        env.Loaders.LatestVersion = "0.17.2";

        var result = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Quilt));

        Assert.Equal((LoaderKind.Quilt, "1.21.1", (string?)null), Assert.Single(env.Loaders.Calls));
        Assert.Equal("0.17.2", result.Instance!.LoaderVersion);
        Assert.Equal("quilt-loader-0.17.2-1.21.1", env.Launcher.Requests.Single().VersionId);
        Assert.Equal("Minecraft 1.21.1 Quilt", result.Instance.Name);
    }

    [Fact]
    public async Task Vanilla_and_fabric_of_the_same_version_are_separate_builds()
    {
        using var env = new Env();

        var vanilla = await env.Service.PlayAsync(Env.Request("1.21.1"));
        var fabric = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.5"));

        Assert.NotEqual(vanilla.Instance!.Id, fabric.Instance!.Id);
        Assert.NotEqual(env.Launcher.Requests[0].GameDirectory, env.Launcher.Requests[1].GameDirectory);
        Assert.Equal("1.21.1", env.Launcher.Requests[0].VersionId);
        Assert.Equal(2, (await env.Instances.ListAsync()).Count);
    }

    [Fact]
    public async Task Newer_loader_version_updates_the_same_build_instead_of_creating_another()
    {
        using var env = new Env();

        var first = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.5"));
        var second = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.9"));

        Assert.Equal(first.Instance!.Id, second.Instance!.Id);
        Assert.Single(await env.Instances.ListAsync());
        Assert.Equal("0.16.9", (await env.Instances.GetAsync(first.Instance.Id))!.LoaderVersion);
        Assert.Equal("fabric-loader-0.16.9-1.21.1", env.Launcher.Requests[1].VersionId);
        Assert.Equal(env.Launcher.Requests[0].GameDirectory, env.Launcher.Requests[1].GameDirectory);
    }

    [Fact]
    public async Task Loader_failure_is_reported_without_creating_a_build_or_launching()
    {
        using var env = new Env();
        env.Loaders.Failure = "Нет сети.";

        var result = await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.5"));

        Assert.Equal(LaunchFailure.LoaderNotReady, result.Launch.Failure);
        Assert.Equal("Нет сети.", result.Launch.Error);
        Assert.Null(result.Instance);
        Assert.Empty(env.Launcher.Requests);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Loader_preparation_is_reported_before_the_launch_starts()
    {
        using var env = new Env();
        var progress = new ListProgress<LaunchProgress>();

        await env.Service.PlayAsync(Env.Request("1.21.1", LoaderKind.Fabric, "0.16.5"), progress);

        LaunchProgress first;
        lock (progress.Items) first = progress.Items[0];
        Assert.Equal(LaunchStage.PreparingLoader, first.Stage);
    }

    [Fact]
    public async Task Vanilla_play_does_not_touch_the_loader_installer()
    {
        using var env = new Env();

        await env.Service.PlayAsync(Env.Request());

        Assert.Empty(env.Loaders.Calls);
    }

    [Fact]
    public async Task Name_taken_by_a_build_of_another_version_gets_a_numbered_name()
    {
        using var env = new Env();
        // Пользователь назвал свою сборку 1.20.4 так же, как называется автоматическая сборка для 1.21.1.
        await env.Instances.CreateAsync("Minecraft 1.21.1", "1.20.4");

        var result = await env.Service.PlayAsync(Env.Request("1.21.1"));

        Assert.True(result.Launch.Succeeded);
        Assert.Equal("Minecraft 1.21.1 (2)", result.Instance!.Name);
        Assert.Equal("1.21.1", env.Launcher.Requests.Single().VersionId);
    }

    [Fact]
    public async Task Launcher_settings_of_the_build_are_passed_to_the_launch()
    {
        using var env = new Env();
        var first = await env.Service.PlayAsync(Env.Request());
        await env.Instances.UpdateLaunchSettingsAsync(first.Instance!.Id, 1024, 4096, "-Dfoo=bar", null);

        await env.Service.PlayAsync(Env.Request());

        var launch = env.Launcher.Requests.Last();
        Assert.Equal(1024, launch.MinMemoryMb);
        Assert.Equal(4096, launch.MaxMemoryMb);
        Assert.Equal("-Dfoo=bar", launch.JvmArguments);
    }

    [Fact]
    public async Task Blank_version_is_rejected()
    {
        using var env = new Env();

        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.PlayAsync(Env.Request("  ")));
    }

    // ───────────────────────── 11.2: сборка, выбранная вручную ─────────────────────────

    private static PlayRequest ForBuild(string id) => new("", LoaderKind.Vanilla, new LauncherAccount { Name = "Steve" }, null, id);

    [Fact]
    public async Task Chosen_build_is_launched_in_its_own_folder_with_its_own_version_even_if_the_request_says_otherwise()
    {
        using var env = new Env();
        var build = (await env.Instances.CreateAsync("Моя сборка", "1.20.4")).Instance!;

        var result = await env.Service.PlayAsync(new PlayRequest("1.21.1", LoaderKind.Vanilla, new LauncherAccount { Name = "Steve" }, null, build.Id));

        Assert.True(result.Launch.Succeeded);
        Assert.Equal(build.Id, result.Instance!.Id);
        var launch = Assert.Single(env.Launcher.Requests);
        Assert.Equal("1.20.4", launch.VersionId);
        Assert.Equal(env.Repo.GetGameDirectory(build.Id), launch.GameDirectory);
        Assert.Single(await env.Instances.ListAsync()); // автосборка «Minecraft 1.21.1» не создавалась
    }

    [Fact]
    public async Task Chosen_build_with_a_loader_prepares_that_loader_version_and_launches_its_profile()
    {
        using var env = new Env();
        var build = (await env.Instances.CreateAsync("Фабрика", "1.21.1", LoaderKind.Fabric, "0.15.0")).Instance!;

        var result = await env.Service.PlayAsync(ForBuild(build.Id));

        Assert.True(result.Launch.Succeeded);
        var call = Assert.Single(env.Loaders.Calls);
        Assert.Equal((LoaderKind.Fabric, "1.21.1", "0.15.0"), call);
        Assert.Equal(LoaderInfo.ProfileId(LoaderKind.Fabric, "1.21.1", "0.15.0"), env.Launcher.Requests.Single().VersionId);
    }

    [Fact]
    public async Task Chosen_build_without_a_loader_version_remembers_the_one_that_was_installed()
    {
        using var env = new Env();
        env.Loaders.LatestVersion = "0.16.9";
        var build = (await env.Instances.CreateAsync("Фабрика", "1.21.1", LoaderKind.Fabric)).Instance!;

        await env.Service.PlayAsync(ForBuild(build.Id));

        Assert.Equal("0.16.9", (await env.Instances.GetAsync(build.Id))!.LoaderVersion);
    }

    [Fact]
    public async Task Chosen_build_marks_the_time_of_last_play()
    {
        using var env = new Env();
        env.Time.Now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var build = (await env.Instances.CreateAsync("Моя сборка", "1.21.1")).Instance!;

        await env.Service.PlayAsync(ForBuild(build.Id));

        Assert.Equal(env.Time.Now.UtcDateTime, (await env.Instances.GetAsync(build.Id))!.LastPlayedUtc);
    }

    [Fact]
    public async Task Missing_chosen_build_fails_without_launching_or_creating_anything()
    {
        using var env = new Env();

        var result = await env.Service.PlayAsync(ForBuild("net-takoy"));

        Assert.False(result.Launch.Succeeded);
        Assert.Equal(LaunchFailure.InstanceUnavailable, result.Launch.Failure);
        Assert.Null(result.Instance);
        Assert.Empty(env.Launcher.Requests);
        Assert.Empty(await env.Instances.ListAsync());
    }
}
