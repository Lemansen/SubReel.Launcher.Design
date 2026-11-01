using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Downloads;
using SubReel.Launcher.Minecraft.Installation;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Tests;

/// <summary>
/// Игровой «сервер Mojang» в памяти: список версий, version.json, клиент, библиотеки (в том числе нативные), конфигурация логирования,
/// индекс ассетов и сами ассеты. Настоящие менеджер загрузок, провайдер описаний и установщик работают с ним поверх временной папки.
/// </summary>
internal sealed class FakeGameWorld : IDisposable
{
    public const string VersionId = "1.21.1";
    private const string Host = "https://dl.test/";

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly List<string> _requested = new();

    public FakeGameWorld(bool virtualAssets = false)
    {
        Dir = new TempDir();
        Paths = new AppPaths(Dir.Root, Dir.Root);
        Layout = new GameFileLayout(Paths);

        BuildServer(virtualAssets);

        Handler = new AsyncStubHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            lock (_requested) _requested.Add(url);
            if (_missing.Contains(url) || !_files.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        });

        Downloads = new HttpDownloadManager(
            new StubHttpClientFactory(Handler),
            Options.Create(new DownloadOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero }),
            NullLogger<HttpDownloadManager>.Instance);
        Manifest = new VersionManifestSource(Downloads, Layout, TimeProvider.System, NullLogger<VersionManifestSource>.Instance);
        Metadata = new VersionMetadataProvider(Manifest, Downloads, Layout, NullLogger<VersionMetadataProvider>.Instance);
        Planner = new GameFilePlanner(Layout);
        Installer = new GameInstaller(Metadata, Downloads, Planner, Layout, Environment, NullLogger<GameInstaller>.Instance);
        Natives = new NativesExtractor(Layout, Environment, NullLogger<NativesExtractor>.Instance);
    }

    public TempDir Dir { get; }
    public AppPaths Paths { get; }
    public GameFileLayout Layout { get; }
    public AsyncStubHandler Handler { get; }
    public OsEnvironment Environment { get; } = new("windows", "x86_64", "10.0");
    public HttpDownloadManager Downloads { get; }
    public VersionManifestSource Manifest { get; }
    public VersionMetadataProvider Metadata { get; }
    public GameFilePlanner Planner { get; }
    public GameInstaller Installer { get; }
    public NativesExtractor Natives { get; }

    public byte[] AssetA { get; } = Encoding.UTF8.GetBytes("asset-a");
    public byte[] AssetB { get; } = Encoding.UTF8.GetBytes("asset-b-shared");

    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_requested) return _requested.ToList();
        }
    }

    public string AssetUrl(byte[] data)
    {
        var hash = Sha1(data);
        return $"{GameFilePlanner.AssetsBaseUrl}{hash[..2]}/{hash}";
    }

    public string ReadServerText(string url) => Encoding.UTF8.GetString(_files[url]);

    /// <summary>Сервер перестаёт отдавать этот адрес (404).</summary>
    public void Break(string url) => _missing.Add(url);

    public void Dispose() => Dir.Dispose();

    public static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    public static byte[] Jar(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private string Add(string url, byte[] data)
    {
        _files[url] = data;
        return url;
    }

    private static byte[] ToJson(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private object Artifact(string path, byte[] data) => new { path, sha1 = Sha1(data), size = data.Length, url = Add(Host + path, data) };

    private object Library(string name, string path, byte[] data, object[]? rules = null) =>
        rules is null
            ? (object)new { name, downloads = new { artifact = Artifact(path, data) } }
            : new { name, downloads = new { artifact = Artifact(path, data) }, rules };

    private void BuildServer(bool virtualAssets)
    {
        var client = Encoding.UTF8.GetBytes("client-jar");
        var core = Encoding.UTF8.GetBytes("core-lib");
        var lwjgl = Encoding.UTF8.GetBytes("lwjgl-lib");
        var lwjglWin = Jar(("lwjgl.dll", "win"));
        var lwjglLinux = Jar(("liblwjgl.so", "linux"));
        var oldMain = Encoding.UTF8.GetBytes("old-platform");
        var oldWin = Jar(("old.dll", "win-native"), ("META-INF/MANIFEST.MF", "x"), ("excluded/skip.txt", "x"), ("sub/dir/keep.txt", "k"));
        var oldLinux = Jar(("libold.so", "linux-native"));
        var logging = Encoding.UTF8.GetBytes("<Configuration/>");

        object[] windowsOnly = { new { action = "allow", os = new { name = "windows" } } };
        object[] linuxOnly = { new { action = "allow", os = new { name = "linux" } } };

        var index = ToJson(new
        {
            objects = new Dictionary<string, object>
            {
                ["icons/icon.png"] = new { hash = Sha1(AssetA), size = AssetA.Length },
                ["sounds/a.ogg"] = new { hash = Sha1(AssetB), size = AssetB.Length },
                ["sounds/copy.ogg"] = new { hash = Sha1(AssetB), size = AssetB.Length },
            },
            @virtual = virtualAssets,
        });
        Add(AssetUrl(AssetA), AssetA);
        Add(AssetUrl(AssetB), AssetB);

        var oldPlatform = new
        {
            name = "old:platform:1.0",
            downloads = new
            {
                artifact = Artifact("old/platform/1.0/platform-1.0.jar", oldMain),
                classifiers = new Dictionary<string, object>
                {
                    ["natives-windows"] = Artifact("old/platform/1.0/platform-1.0-natives-windows.jar", oldWin),
                    ["natives-linux"] = Artifact("old/platform/1.0/platform-1.0-natives-linux.jar", oldLinux),
                },
            },
            natives = new { windows = "natives-windows", linux = "natives-linux" },
            extract = new { exclude = new[] { "excluded/" } },
        };

        var version = new
        {
            id = VersionId,
            type = "release",
            mainClass = "net.minecraft.client.main.Main",
            assets = "17",
            assetIndex = new { id = "17", sha1 = Sha1(index), size = index.Length, url = Add(Host + "index17.json", index) },
            downloads = new { client = new { sha1 = Sha1(client), size = client.Length, url = Add(Host + "client.jar", client) } },
            javaVersion = new { component = "java-runtime-delta", majorVersion = 21 },
            logging = new
            {
                client = new
                {
                    argument = "-Dlog4j.configurationFile=${path}",
                    file = new { id = "client-1.12.xml", sha1 = Sha1(logging), size = logging.Length, url = Add(Host + "log.xml", logging) },
                },
            },
            arguments = new
            {
                game = new object[]
                {
                    "--username", "${auth_player_name}",
                    new { rules = new object[] { new { action = "allow", features = new Dictionary<string, bool> { ["is_demo_user"] = true } } }, value = "--demo" },
                },
                jvm = new object[] { new { rules = windowsOnly, value = "-XX:HeapDumpPath=dump" }, "-cp", "${classpath}" },
            },
            libraries = new object[]
            {
                Library("com.example:core:1.0", "com/example/core/1.0/core-1.0.jar", core),
                Library("org.lwjgl:lwjgl:3.3.3", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", lwjgl),
                Library("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar", lwjglWin, windowsOnly),
                Library("org.lwjgl:lwjgl:3.3.3:natives-linux", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar", lwjglLinux, linuxOnly),
                oldPlatform,
            },
        };

        var versionJson = ToJson(version);
        var versionUrl = Add(Host + "1.21.1.json", versionJson);
        var manifest = ToJson(new
        {
            latest = new { release = VersionId },
            versions = new[] { new { id = VersionId, type = "release", url = versionUrl, sha1 = Sha1(versionJson) } },
        });
        Add(VersionManifestSource.ManifestUrl, manifest);
    }
}
