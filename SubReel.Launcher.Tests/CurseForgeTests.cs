using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Modpacks;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Security;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;
using SubReel.Launcher.Mods.CurseForge;

namespace SubReel.Launcher.Tests;

internal sealed class FakeCurseForgeKey : ICurseForgeKey
{
    public string? Key { get; set; } = "test-key";
    public bool IsConfigured => !string.IsNullOrEmpty(Key);
    public string? Get() => Key;
    public bool Set(string? key) { Key = key; return true; }
}

public sealed class CurseForgeTests
{
    private static readonly string Sha1 = new('a', 40);

    private static string CfFile(string name = "mod.jar", string? url = "https://edge.forgecdn.net/files/1/2/mod.jar", string? sha1 = null, int release = 1, string deps = "[]", int id = 77, int modId = 5, int length = 99) =>
        $$"""
        { "id": {{id}}, "modId": {{modId}}, "displayName": "Mod 1.0", "fileName": "{{name}}", "fileDate": "2026-05-01T10:00:00Z", "releaseType": {{release}}, "fileLength": {{length}},
          "downloadUrl": {{(url is null ? "null" : "\"" + url + "\"")}}, "hashes": [ { "value": "{{sha1 ?? Sha1}}", "algo": 1 }, { "value": "x", "algo": 2 } ], "dependencies": {{deps}} }
        """;

    private static CurseForgeCatalog Catalog(Func<HttpRequestMessage, HttpResponseMessage> respond, ICurseForgeKey? key = null) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri(CurseForgeCatalog.BaseAddress) }, key ?? new FakeCurseForgeKey(), NullLogger<CurseForgeCatalog>.Instance);

    // ───────────────────────── Разбор ответов ─────────────────────────

    [Fact]
    public void File_is_read_with_sha1_and_no_sha512()
    {
        var version = Assert.Single(CurseForgeParser.ParseFiles("{\"data\":[" + CfFile(release: 2, deps: """[ { "modId": 9, "relationType": 3 }, { "modId": 8, "relationType": 2 }, { "modId": 7, "relationType": 5 } ]""") + "]}"));

        Assert.Equal("77", version.Id);
        Assert.Equal("5", version.ProjectId);
        Assert.Equal(ModReleaseType.Beta, version.Type);
        Assert.Null(version.File.Sha512);
        Assert.Equal(new[] { ModDependencyKind.Required, ModDependencyKind.Optional, ModDependencyKind.Incompatible }, version.Dependencies.Select(d => d.Kind));
    }

    [Theory]
    [InlineData("mod.jar", null)] // автор запретил раздачу сторонним программам
    [InlineData("mod.jar", "http://edge.forgecdn.net/a.jar")]
    [InlineData("mod.jar", "https://evil.example.com/a.jar")]
    [InlineData("../x.jar", "https://edge.forgecdn.net/a.jar")]
    [InlineData("mod.exe", "https://edge.forgecdn.net/a.exe")]
    public void Unusable_files_are_skipped(string name, string? url) =>
        Assert.Empty(CurseForgeParser.ParseFiles("{\"data\":[" + CfFile(name, url) + "]}"));

    [Fact]
    public void Resource_pack_zip_is_accepted_as_a_pack_file()
    {
        var version = Assert.Single(CurseForgeParser.ParseFiles("{\"data\":[" + CfFile("pack.zip", "https://mediafilez.forgecdn.net/files/1/2/pack.zip") + "]}"));

        Assert.Equal("pack.zip", version.File.FileName);
    }

    [Fact]
    public void Search_reads_projects_and_total()
    {
        var json = """{ "data": [ { "id": 5, "slug": "jei", "name": "JEI", "summary": "Рецепты", "downloadCount": 1234.0, "authors": [ { "name": "mezz" } ] } ], "pagination": { "totalCount": 40 } }""";

        var result = CurseForgeParser.ParseSearch(json);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("5", hit.Id);
        Assert.Equal("mezz", hit.Author);
        Assert.Equal(40, result.TotalHits);
    }

    // ───────────────────────── Каталог ─────────────────────────

    [Fact]
    public async Task Search_sends_key_game_version_and_loader()
    {
        string? url = null;
        string? key = null;
        var catalog = Catalog(r =>
        {
            url = r.RequestUri!.ToString();
            key = r.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            return StubHandler.Ok("""{ "data": [] }""");
        });

        await catalog.SearchAsync(new ModSearchQuery("jei", "1.20.1", LoaderKind.Forge));

        Assert.Contains("gameId=432", url);
        Assert.Contains("classId=6", url);
        Assert.Contains("gameVersion=1.20.1", url);
        Assert.Contains("modLoaderType=1", url);
        Assert.Contains("searchFilter=jei", url);
        Assert.Equal("test-key", key);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent_and_the_user_is_told_where_to_enter_it()
    {
        var handler = new StubHandler(_ => StubHandler.Ok("{}"));
        var catalog = new CurseForgeCatalog(new HttpClient(handler) { BaseAddress = new Uri(CurseForgeCatalog.BaseAddress) }, new FakeCurseForgeKey { Key = null }, NullLogger<CurseForgeCatalog>.Instance);

        var ex = await Assert.ThrowsAsync<ModCatalogException>(() => catalog.SearchAsync(new ModSearchQuery("x", "1.20.1", LoaderKind.Fabric)));

        Assert.Contains("ключ API", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejected_key_is_explained()
    {
        var catalog = Catalog(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<ModCatalogException>(() => catalog.SearchAsync(new ModSearchQuery("x", "1.20.1", LoaderKind.Fabric)));

        Assert.Contains("не принял ключ", ex.Message);
    }

    // ───────────────────────── Ключ в настройках ─────────────────────────

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.Reverse().ToArray();
    }

    [Fact]
    public void Key_is_stored_encrypted_and_can_be_read_back_and_removed()
    {
        var settings = new FakeSettingsService();
        var store = new CurseForgeKeyStore(settings, new ReversingProtector(), NullLogger<CurseForgeKeyStore>.Instance);

        Assert.False(store.IsConfigured);
        Assert.True(store.Set("  $2a$10$secretkey  "));

        Assert.True(store.IsConfigured);
        Assert.Equal("$2a$10$secretkey", store.Get());
        Assert.DoesNotContain("secretkey", settings.Current.CurseForgeKeyProtected);
        Assert.Equal(1, settings.SaveCount);

        Assert.True(store.Set(""));
        Assert.False(store.IsConfigured);
        Assert.Null(store.Get());
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("new\nline")]
    public void Key_with_whitespace_is_refused(string key)
    {
        var settings = new FakeSettingsService();
        var store = new CurseForgeKeyStore(settings, new ReversingProtector(), NullLogger<CurseForgeKeyStore>.Instance);

        Assert.False(store.Set(key));
        Assert.False(store.IsConfigured);
    }

    // ───────────────────────── manifest.json ─────────────────────────

    private static string Manifest(string loaders = """[ { "id": "forge-47.2.0", "primary": true } ]""", string files = """[ { "projectID": 5, "fileID": 77, "required": true } ]""", string overrides = "overrides") =>
        $$"""{ "minecraft": { "version": "1.20.1", "modLoaders": {{loaders}} }, "manifestType": "minecraftModpack", "name": "Моя CF сборка", "overrides": "{{overrides}}", "files": {{files}} }""";

    [Fact]
    public void Manifest_gives_version_loader_and_required_files()
    {
        var manifest = CurseForgeManifestParser.Parse(Manifest(files: """[ { "projectID": 5, "fileID": 77, "required": true }, { "projectID": 6, "fileID": 78, "required": false }, { "projectID": 7, "fileID": 79 } ]"""));

        Assert.Equal("1.20.1", manifest.McVersion);
        Assert.Equal(LoaderKind.Forge, manifest.Loader);
        Assert.Equal("47.2.0", manifest.LoaderVersion);
        Assert.Equal(new long[] { 77, 79 }, manifest.Files.Select(f => f.FileId));
        Assert.Equal("overrides", manifest.Overrides);
    }

    [Theory]
    [InlineData("fabric-0.15.11", LoaderKind.Fabric, "0.15.11")]
    [InlineData("neoforge-21.1.172", LoaderKind.NeoForge, "21.1.172")]
    [InlineData("quilt-0.26.0", LoaderKind.Quilt, "0.26.0")]
    public void Other_loaders_are_recognized(string id, LoaderKind kind, string version)
    {
        var manifest = CurseForgeManifestParser.Parse(Manifest(loaders: $$"""[ { "id": "{{id}}", "primary": true } ]"""));

        Assert.Equal(kind, manifest.Loader);
        Assert.Equal(version, manifest.LoaderVersion);
    }

    [Fact]
    public void Pack_without_a_loader_is_vanilla()
    {
        var manifest = CurseForgeManifestParser.Parse(Manifest(loaders: "[]"));

        Assert.Equal(LoaderKind.Vanilla, manifest.Loader);
        Assert.Null(manifest.LoaderVersion);
    }

    [Theory]
    [InlineData("""[ { "id": "liteloader-1.0", "primary": true } ]""", "overrides")]
    [InlineData("""[ { "id": "forge", "primary": true } ]""", "overrides")]
    [InlineData("""[ { "id": "forge-47.2.0", "primary": true } ]""", "../outside")]
    public void Unsupported_or_unsafe_manifests_are_refused(string loaders, string overrides) =>
        Assert.Throws<ModpackException>(() => CurseForgeManifestParser.Parse(Manifest(loaders: loaders, overrides: overrides)));

    [Fact]
    public void Broken_manifest_is_refused()
    {
        Assert.Throws<ModpackException>(() => CurseForgeManifestParser.Parse("not json"));
        Assert.Throws<ModpackException>(() => CurseForgeManifestParser.Parse("""{ "manifestType": "minecraftModpack" }"""));
    }

    // ───────────────────────── Импорт .zip ─────────────────────────

    private sealed class FakeDownloads : IDownloadManager
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            if (!Files.TryGetValue(request.Url, out var data)) return Task.FromResult(new DownloadResult(request, DownloadStatus.Failed, 0, "404"));
            if (request.ExpectedSha1 is not null && !string.Equals(Convert.ToHexString(SHA1.HashData(data)), request.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new DownloadResult(request, DownloadStatus.Failed, data.Length, "sha1"));

            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            System.IO.File.WriteAllBytes(request.DestinationPath, data);
            return Task.FromResult(new DownloadResult(request, DownloadStatus.Downloaded, data.Length));
        }

        public async Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(IReadOnlyList<DownloadRequest> requests, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default)
        {
            var results = new List<DownloadResult>();
            foreach (var request in requests) results.Add(await DownloadAsync(request, null, ct));
            return results;
        }
    }

    private sealed class ImportEnv : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly byte[] _modBytes = Encoding.UTF8.GetBytes("pretend this is a mod jar");
        private const string ModUrl = "https://edge.forgecdn.net/files/1/2/mod.jar";

        public ImportEnv(bool fileAvailable = true, string? key = "test-key")
        {
            var paths = new AppPaths(_dir.Root, _dir.Root);
            var store = new JsonFileStore(NullLogger<JsonFileStore>.Instance);
            Repo = new JsonInstanceRepository(paths, store, NullLogger<JsonInstanceRepository>.Instance);
            Instances = new InstanceService(Repo, TimeProvider.System, NullLogger<InstanceService>.Instance);
            Downloads = new FakeDownloads();
            Downloads.Files[ModUrl] = _modBytes;

            var fileJson = CfFile(sha1: Convert.ToHexString(SHA1.HashData(_modBytes)).ToLowerInvariant(), length: _modBytes.Length);
            var catalog = Catalog(r => r.RequestUri!.AbsolutePath.EndsWith("/mods/files", StringComparison.Ordinal)
                ? StubHandler.Ok("{\"data\":[" + (fileAvailable ? fileJson : "") + "]}")
                : StubHandler.Ok("""{ "data": [ { "id": 5, "slug": "jei", "name": "Just Enough Items" } ] }"""), new FakeCurseForgeKey { Key = key });
            Importer = new CurseForgeImporter(Instances, Downloads, catalog, store, NullLogger<CurseForgeImporter>.Instance);
        }

        public JsonInstanceRepository Repo { get; }
        public InstanceService Instances { get; }
        public FakeDownloads Downloads { get; }
        public CurseForgeImporter Importer { get; }

        public string Pack(string manifest, params (string Name, string Text)[] entries)
        {
            var path = _dir.File("pack-" + Guid.NewGuid().ToString("N") + ".zip");
            using var stream = System.IO.File.Create(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, text) in new[] { ("manifest.json", manifest) }.Concat(entries))
            {
                using var writer = zip.CreateEntry(name).Open();
                writer.Write(Encoding.UTF8.GetBytes(text));
            }
            return path;
        }

        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public async Task Import_creates_the_build_downloads_mods_and_lays_out_overrides()
    {
        using var env = new ImportEnv();
        var pack = env.Pack(Manifest(), ("overrides/options.txt", "fov:90"), ("overrides/config/a.toml", "x"));

        var result = await env.Importer.ImportAsync(pack);

        Assert.True(result.Succeeded, result.Error);
        var build = result.Instance!;
        Assert.Equal("Моя CF сборка", build.Name);
        Assert.Equal(LoaderKind.Forge, build.Loader);
        Assert.Equal("47.2.0", build.LoaderVersion);

        var game = env.Repo.GetGameDirectory(build.Id);
        Assert.True(System.IO.File.Exists(Path.Combine(game, "mods", "mod.jar")));
        Assert.Equal("fov:90", System.IO.File.ReadAllText(Path.Combine(game, "options.txt")));

        // Мод записан в список модов сборки как мод CurseForge — окно «Моды» увидит его и умеет обновлять.
        var modsJson = System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(game.TrimEnd(Path.DirectorySeparatorChar))!, "mods.json"));
        Assert.Contains("CurseForge", modsJson);
        Assert.Contains("Just Enough Items", modsJson);
    }

    [Fact]
    public async Task Import_refuses_when_a_mod_cannot_be_downloaded_and_creates_nothing()
    {
        using var env = new ImportEnv(fileAvailable: false);

        var result = await env.Importer.ImportAsync(env.Pack(Manifest()));

        Assert.False(result.Succeeded);
        Assert.Contains("Just Enough Items", result.Error);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Import_without_a_key_explains_and_creates_nothing()
    {
        using var env = new ImportEnv(key: null);

        var result = await env.Importer.ImportAsync(env.Pack(Manifest()));

        Assert.False(result.Succeeded);
        Assert.Contains("ключ API", result.Error);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Import_rolls_back_on_a_download_failure()
    {
        using var env = new ImportEnv();
        env.Downloads.Files.Clear();

        var result = await env.Importer.ImportAsync(env.Pack(Manifest()));

        Assert.False(result.Succeeded);
        Assert.Empty(await env.Instances.ListAsync());
    }

    [Fact]
    public async Task Import_rejects_an_archive_that_is_not_a_curseforge_pack()
    {
        using var env = new ImportEnv();
        var path = env.Pack(Manifest());
        // подменяем содержимое: в архиве нет manifest.json
        using (var stream = System.IO.File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = zip.CreateEntry("readme.txt").Open();
            writer.Write(Encoding.UTF8.GetBytes("hi"));
        }

        var result = await env.Importer.ImportAsync(path);

        Assert.False(result.Succeeded);
        Assert.Contains("manifest.json", result.Error);
    }
}
