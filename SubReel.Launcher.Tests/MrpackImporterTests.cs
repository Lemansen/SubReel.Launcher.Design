using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;
using SubReel.Launcher.Minecraft.Modpacks;

namespace SubReel.Launcher.Tests;

public sealed class MrpackImporterTests
{
    private sealed class FakeDownloads : IDownloadManager
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool Cancel { get; set; }

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            if (Cancel) throw new OperationCanceledException();
            if (!Files.TryGetValue(request.Url, out var data)) return Task.FromResult(new DownloadResult(request, DownloadStatus.Failed, 0, "404"));
            if (request.ExpectedSha1 is not null && !string.Equals(Convert.ToHexString(SHA1.HashData(data)), request.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new DownloadResult(request, DownloadStatus.Failed, data.Length, "sha1"));

            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            File.WriteAllBytes(request.DestinationPath, data);
            return Task.FromResult(new DownloadResult(request, DownloadStatus.Downloaded, data.Length));
        }

        public async Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(IReadOnlyList<DownloadRequest> requests, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default)
        {
            var results = new List<DownloadResult>();
            foreach (var request in requests) results.Add(await DownloadAsync(request, null, ct));
            return results;
        }
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir _dir = new();

        public Env()
        {
            var paths = new AppPaths(_dir.Root, _dir.Root);
            Repo = new JsonInstanceRepository(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<JsonInstanceRepository>.Instance);
            Instances = new InstanceService(Repo, TimeProvider.System, NullLogger<InstanceService>.Instance);
            Downloads = new FakeDownloads();
            Importer = new MrpackImporter(Instances, Downloads, NullLogger<MrpackImporter>.Instance);
        }

        public JsonInstanceRepository Repo { get; }
        public InstanceService Instances { get; }
        public FakeDownloads Downloads { get; }
        public MrpackImporter Importer { get; }

        public string Pack(string name, Action<ZipArchive> fill)
        {
            var path = _dir.File(name);
            using var stream = File.Create(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            fill(zip);
            return path;
        }

        public void Dispose() => _dir.Dispose();
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    private static void Add(ZipArchive zip, string name, string text) => Add(zip, name, Encoding.UTF8.GetBytes(text));

    private static string FileJson(string path, string url, byte[] data, string? sha512 = null) =>
        MrpackParserTests.FileJson(path, url, Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(), sha512 ?? Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant());

    private static readonly byte[] ModBytes = Encoding.UTF8.GetBytes("pretend this is a mod jar");
    private const string ModUrl = "https://cdn.modrinth.com/data/AAA/versions/1/mod.jar";

    private static string Pack(params string[] files) => MrpackParserTests.PackJson(string.Join(",", files));

    [Fact]
    public async Task Imports_files_overrides_and_creates_a_build_with_the_packs_version_and_loader()
    {
        using var env = new Env();
        env.Downloads.Files[ModUrl] = ModBytes;
        var pack = env.Pack("good.mrpack", zip =>
        {
            Add(zip, "modrinth.index.json", Pack(FileJson("mods/mod.jar", ModUrl, ModBytes)));
            Add(zip, "overrides/options.txt", "fov:90");
            Add(zip, "overrides/config/a.toml", "common");
            Add(zip, "client-overrides/config/a.toml", "client"); // побеждает общий
            Add(zip, "overrides/config/", Array.Empty<byte>());    // запись папки пропускается
        });

        var result = await env.Importer.ImportMrpackAsync(pack);

        Assert.True(result.Succeeded, result.Error);
        var build = result.Instance!;
        Assert.Equal("Моя сборка", build.Name);
        Assert.Equal("1.20.1", build.McVersion);
        Assert.Equal(LoaderKind.Fabric, build.Loader);
        Assert.Equal("0.15.0", build.LoaderVersion);

        var game = env.Repo.GetGameDirectory(build.Id);
        Assert.Equal(ModBytes, File.ReadAllBytes(Path.Combine(game, "mods", "mod.jar")));
        Assert.Equal("fov:90", File.ReadAllText(Path.Combine(game, "options.txt")));
        Assert.Equal("client", File.ReadAllText(Path.Combine(game, "config", "a.toml")));
        Assert.Single(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Second_import_of_the_same_pack_gets_a_numbered_name()
    {
        using var env = new Env();
        var pack = env.Pack("p.mrpack", zip => Add(zip, "modrinth.index.json", Pack()));

        var first = await env.Importer.ImportMrpackAsync(pack);
        var second = await env.Importer.ImportMrpackAsync(pack);

        Assert.Equal("Моя сборка", first.Instance!.Name);
        Assert.Equal("Моя сборка (2)", second.Instance!.Name);
    }

    [Fact]
    public async Task Sha512_mismatch_fails_and_removes_the_unfinished_build()
    {
        using var env = new Env();
        env.Downloads.Files[ModUrl] = ModBytes;
        var pack = env.Pack("bad.mrpack", zip => Add(zip, "modrinth.index.json", Pack(FileJson("mods/mod.jar", ModUrl, ModBytes, sha512: new string('b', 128)))));

        var result = await env.Importer.ImportMrpackAsync(pack);

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-512", result.Error);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Failed_download_fails_and_removes_the_unfinished_build()
    {
        using var env = new Env(); // в «сети» нет ни одного файла
        var pack = env.Pack("nonet.mrpack", zip => Add(zip, "modrinth.index.json", Pack(FileJson("mods/mod.jar", ModUrl, ModBytes))));

        var result = await env.Importer.ImportMrpackAsync(pack);

        Assert.False(result.Succeeded);
        Assert.Contains("mod.jar", result.Error);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Cancellation_removes_the_unfinished_build_and_rethrows()
    {
        using var env = new Env();
        env.Downloads.Cancel = true;
        var pack = env.Pack("c.mrpack", zip => Add(zip, "modrinth.index.json", Pack(FileJson("mods/mod.jar", ModUrl, ModBytes))));

        await Assert.ThrowsAsync<OperationCanceledException>(() => env.Importer.ImportMrpackAsync(pack));

        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Override_that_escapes_the_game_folder_is_rejected()
    {
        using var env = new Env();
        var pack = env.Pack("slip.mrpack", zip =>
        {
            Add(zip, "modrinth.index.json", Pack());
            Add(zip, "overrides/../../evil.txt", "boom");
        });

        var result = await env.Importer.ImportMrpackAsync(pack);

        Assert.False(result.Succeeded);
        Assert.Empty(await env.Instances.ListAsync());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(pack)!, "evil.txt")));
    }

    [Fact]
    public async Task Archive_without_index_and_non_archive_are_reported_not_thrown()
    {
        using var env = new Env();
        var noIndex = env.Pack("empty.mrpack", zip => Add(zip, "readme.txt", "hi"));
        var notZip = Path.Combine(Path.GetDirectoryName(noIndex)!, "text.mrpack");
        File.WriteAllText(notZip, "это не архив");

        var a = await env.Importer.ImportMrpackAsync(noIndex);
        var b = await env.Importer.ImportMrpackAsync(notZip);

        Assert.False(a.Succeeded);
        Assert.False(b.Succeeded);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Missing_file_is_reported()
    {
        using var env = new Env();

        var result = await env.Importer.ImportMrpackAsync(Path.Combine(Path.GetTempPath(), "net-takogo-" + Guid.NewGuid().ToString("N") + ".mrpack"));

        Assert.False(result.Succeeded);
    }
}
