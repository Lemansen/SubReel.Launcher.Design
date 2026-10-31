using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Downloads;
using SubReel.Launcher.Minecraft.Java;

namespace SubReel.Launcher.Tests;

/// <summary>Сервер рантаймов Mojang в памяти (список, описание файлов, три файла) и настоящий Java Manager поверх временной папки.</summary>
internal sealed class FakeJavaWorld : IDisposable
{
    public const string IndexUrl = "https://java.test/all.json";
    public const string Component = "java-runtime-delta";

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly List<string> _requested = new();

    public FakeJavaWorld(string releaseVersion = "21.0.3", string? evilPath = null)
    {
        Dir = new TempDir();
        Paths = new AppPaths(Dir.Root, Dir.Root, Dir.Root);
        BuildServer(releaseVersion, evilPath);

        Handler = new AsyncStubHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            lock (_requested) _requested.Add(url);
            if (_missing.Contains(url) || !_files.TryGetValue(url, out var body)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        });

        var downloads = new HttpDownloadManager(new StubHttpClientFactory(Handler),
            Options.Create(new DownloadOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero }), NullLogger<HttpDownloadManager>.Instance);
        var reader = new JavaVersionReader(NullLogger<JavaVersionReader>.Instance);
        Installer = new ManagedJavaInstaller(downloads, reader, new JsonFileStore(NullLogger<JsonFileStore>.Instance), Paths, Environment,
            Options.Create(new JavaOptions { RuntimeIndexUrl = IndexUrl }), NullLogger<ManagedJavaInstaller>.Instance);

        SystemJava = new FixedHomesSource(JavaSource.Registry);
        Manager = new JavaManager(new IJavaHomeSource[] { SystemJava }, reader, Installer, Environment, NullLogger<JavaManager>.Instance);
        Reader = reader;
    }

    public TempDir Dir { get; }
    public AppPaths Paths { get; }
    public AsyncStubHandler Handler { get; }
    public OsEnvironment Environment { get; } = new("windows", "x86_64");
    public ManagedJavaInstaller Installer { get; }
    public FixedHomesSource SystemJava { get; }
    public JavaManager Manager { get; }
    public JavaVersionReader Reader { get; }

    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_requested) return _requested.ToList();
        }
    }

    public string SystemRoot => Path.Combine(Dir.Root, "system-java");

    public void Break(string url) => _missing.Add(url);

    public void Dispose() => Dir.Dispose();

    private string Add(string url, byte[] data)
    {
        _files[url] = data;
        return url;
    }

    private object Raw(string url, byte[] data) => new { raw = new { sha1 = FakeGameWorld.Sha1(data), size = data.Length, url = Add(url, data) } };

    private void BuildServer(string releaseVersion, string? evilPath)
    {
        var javaBytes = Encoding.UTF8.GetBytes("fake-java");
        var release = Encoding.UTF8.GetBytes($"JAVA_VERSION=\"{releaseVersion}\"\nOS_ARCH=\"x86_64\"\nIMPLEMENTOR=\"Fake\"\n");
        var modules = Encoding.UTF8.GetBytes("modules");

        var files = new Dictionary<string, object>
        {
            ["bin"] = new { type = "directory" },
            ["lib"] = new { type = "directory" },
            ["bin/" + JavaTestHomes.ExeName] = new { type = "file", executable = true, downloads = Raw("https://java.test/files/java", javaBytes) },
            ["release"] = new { type = "file", executable = false, downloads = Raw("https://java.test/files/release", release) },
            ["lib/modules"] = new { type = "file", executable = false, downloads = Raw("https://java.test/files/modules", modules) },
        };
        if (evilPath is not null) files[evilPath] = new { type = "file", executable = false, downloads = Raw("https://java.test/files/evil", modules) };

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { files });
        var index = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["windows-x64"] = new Dictionary<string, object>
            {
                [Component] = new[]
                {
                    new
                    {
                        manifest = new { sha1 = FakeGameWorld.Sha1(manifest), size = manifest.Length, url = Add("https://java.test/delta.manifest.json", manifest) },
                        version = new { name = releaseVersion },
                    },
                },
            },
        });
        Add(IndexUrl, index);
    }
}

public sealed class JavaManagerTests
{
    private static readonly JavaRequirement Java21 = new(21, FakeJavaWorld.Component);

    [Fact]
    public async Task Downloads_runtime_when_no_suitable_java_exists_and_reuses_it_afterwards()
    {
        using var world = new FakeJavaWorld();

        var first = await world.Manager.EnsureAsync(Java21);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(first.Downloaded);
        Assert.Equal(JavaSource.Managed, first.Installation!.Source);
        Assert.Equal(21, first.Installation.MajorVersion);
        Assert.Equal(FakeJavaWorld.Component, first.Installation.Component);
        var home = world.Installer.HomeFor(FakeJavaWorld.Component);
        Assert.True(File.Exists(Path.Combine(home, "lib", "modules")));
        Assert.True(File.Exists(Path.Combine(home, ManagedJavaInstaller.MarkerFileName)));

        var before = world.Requested.Count;
        var second = await world.Manager.EnsureAsync(Java21);

        Assert.True(second.Succeeded);
        Assert.False(second.Downloaded);
        Assert.Equal(before, world.Requested.Count);
    }

    [Fact]
    public async Task Uses_installed_system_java_without_any_download()
    {
        using var world = new FakeJavaWorld();
        world.SystemJava.Add(JavaTestHomes.Create(world.SystemRoot, "jdk-21", "21.0.2"));

        var result = await world.Manager.EnsureAsync(Java21);

        Assert.True(result.Succeeded);
        Assert.False(result.Downloaded);
        Assert.Equal(JavaSource.Registry, result.Installation!.Source);
        Assert.Empty(world.Requested);
    }

    [Fact]
    public async Task Wrong_major_version_of_system_java_is_not_used()
    {
        using var world = new FakeJavaWorld();
        world.SystemJava.Add(JavaTestHomes.Create(world.SystemRoot, "jdk-17", "17.0.9"));

        var selection = await world.Manager.SelectAsync(Java21);

        Assert.Equal(JavaSelectionStatus.NeedsDownload, selection.Status);
    }

    [Fact]
    public async Task Installations_are_cached_until_refresh_is_requested()
    {
        using var world = new FakeJavaWorld();
        Assert.Empty(await world.Manager.GetInstallationsAsync());

        world.SystemJava.Add(JavaTestHomes.Create(world.SystemRoot, "jdk-21"));

        Assert.Empty(await world.Manager.GetInstallationsAsync());
        Assert.Single(await world.Manager.GetInstallationsAsync(refresh: true));
    }

    [Fact]
    public async Task Same_home_from_two_sources_is_listed_once()
    {
        using var world = new FakeJavaWorld();
        var home = JavaTestHomes.Create(world.SystemRoot, "jdk-21");
        world.SystemJava.Add(home);
        world.SystemJava.Add(home + Path.DirectorySeparatorChar);

        Assert.Single(await world.Manager.GetInstallationsAsync());
    }

    [Fact]
    public async Task Concurrent_requests_install_the_runtime_only_once()
    {
        using var world = new FakeJavaWorld();

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => world.Manager.EnsureAsync(Java21)));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        Assert.Equal(1, results.Count(r => r.Downloaded));
        Assert.Equal(5, world.Requested.Count); // список, описание, три файла
    }

    [Fact]
    public async Task Runtime_with_unexpected_java_version_is_rejected()
    {
        using var world = new FakeJavaWorld(releaseVersion: "17.0.9");

        var result = await world.Manager.EnsureAsync(Java21);

        Assert.False(result.Succeeded);
        Assert.Contains("17", result.Error);
    }

    [Fact]
    public async Task Unknown_component_is_a_failed_result()
    {
        using var world = new FakeJavaWorld();

        var result = await world.Manager.EnsureAsync(new JavaRequirement(99, "java-runtime-omega"));

        Assert.False(result.Succeeded);
        Assert.Contains("java-runtime-omega", result.Error);
    }

    [Fact]
    public async Task Requirement_without_runtime_and_without_java_explains_what_to_do()
    {
        using var world = new FakeJavaWorld();

        var result = await world.Manager.EnsureAsync(new JavaRequirement(21, null));

        Assert.False(result.Succeeded);
        Assert.Equal(JavaSelectionStatus.NotFound, result.Status);
        Assert.Empty(world.Requested);
    }

    [Fact]
    public async Task Failed_download_leaves_no_completion_marker_and_no_listed_runtime()
    {
        using var world = new FakeJavaWorld();
        world.Break("https://java.test/files/modules");

        var result = await world.Manager.EnsureAsync(Java21);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(world.Installer.HomeFor(FakeJavaWorld.Component), ManagedJavaInstaller.MarkerFileName)));
        Assert.Empty(await world.Manager.GetInstallationsAsync(refresh: true));
    }

    [Fact]
    public async Task Unmarked_runtime_folder_is_not_treated_as_installed()
    {
        using var world = new FakeJavaWorld();
        JavaTestHomes.Create(world.Paths.RuntimesDirectory, "half-installed");

        Assert.Empty(await world.Manager.GetInstallationsAsync());
    }

    [Fact]
    public async Task Manifest_path_traversal_is_rejected_and_nothing_is_written_outside()
    {
        using var world = new FakeJavaWorld(evilPath: "../../evil.txt");

        var result = await world.Manager.EnsureAsync(Java21);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(world.Dir.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(world.Dir.Root, "..", "evil.txt")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void Runtime_component_must_be_a_simple_folder_name(string component)
    {
        using var world = new FakeJavaWorld();
        Assert.Throws<JavaRuntimeException>(() => world.Installer.HomeFor(component));
    }

    [Fact]
    public async Task Explicit_path_beats_the_selection_rule_and_flags_version_mismatch()
    {
        using var world = new FakeJavaWorld();
        var home = JavaTestHomes.Create(world.SystemRoot, "jdk-17", "17.0.9");

        var byExecutable = await world.Manager.SelectAsync(Java21, Path.Combine(home, "bin", JavaTestHomes.ExeName));
        var byFolder = await world.Manager.SelectAsync(Java21, home);

        Assert.Equal(JavaSelectionStatus.ExplicitPathUsed, byExecutable.Status);
        Assert.True(byExecutable.VersionMismatch);
        Assert.Equal(JavaSource.Manual, byExecutable.Installation!.Source);
        Assert.Equal(17, byFolder.Installation!.MajorVersion);
        Assert.Empty(world.Requested);
    }

    [Fact]
    public async Task Invalid_explicit_path_is_reported_not_silently_replaced()
    {
        using var world = new FakeJavaWorld();
        world.SystemJava.Add(JavaTestHomes.Create(world.SystemRoot, "jdk-21"));

        var selection = await world.Manager.SelectAsync(Java21, Path.Combine(world.Dir.Root, "missing", "java.exe"));
        var ensure = await world.Manager.EnsureAsync(Java21, "   not a path   ");

        Assert.Equal(JavaSelectionStatus.ExplicitPathInvalid, selection.Status);
        Assert.False(ensure.Succeeded);
        Assert.Equal(JavaSelectionStatus.ExplicitPathInvalid, ensure.Status);
    }

    [Fact]
    public async Task Probe_rejects_arbitrary_files_and_empty_input()
    {
        using var world = new FakeJavaWorld();
        var notJava = Path.Combine(world.Dir.Root, "notepad.exe");
        await File.WriteAllTextAsync(notJava, "x");

        Assert.Null(await world.Manager.ProbeAsync(notJava));
        Assert.Null(await world.Manager.ProbeAsync(""));
        Assert.Null(await world.Manager.ProbeAsync("  "));
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var world = new FakeJavaWorld();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Manager.EnsureAsync(Java21, ct: cts.Token));
    }
}
