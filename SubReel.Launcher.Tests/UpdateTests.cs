using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Updates;
using SubReel.Launcher.Domain.Settings;
using SubReel.Launcher.Infrastructure.Settings;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Infrastructure.Updates;

namespace SubReel.Launcher.Tests;

public sealed class UpdateRulesTests
{
    private static readonly Uri Manifest = new("https://updates.example.com/manifest.json");
    private static readonly string Sha = new('a', 64);

    private static string Json(string version = "1.2.0", string url = "https://updates.example.com/SubReel-1.2.0.zip", string? sha = null, string size = "1000", string extra = "") =>
        $$"""{ "version": "{{version}}", "url": "{{url}}", "sha256": "{{sha ?? Sha}}", "sizeBytes": {{size}}{{extra}} }""";

    private static LauncherUpdateInfo? Parse(string json, string current = "1.0.0") =>
        UpdateRules.ParseManifest(json, Manifest, Version.Parse(current), new[] { "github.com" });

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0-beta.1", "1.2.0")]
    [InlineData("1.2.0+abc123", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void Versions_are_normalized(string text, string expected) => Assert.Equal(Version.Parse(expected), UpdateRules.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    public void Broken_versions_are_rejected(string? text) => Assert.Null(UpdateRules.ParseVersion(text));

    [Fact]
    public void Newer_version_is_returned_with_all_fields()
    {
        var info = Parse(Json(extra: """, "mandatory": true, "releaseNotesRu": "Новое" """));

        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
        Assert.Equal(Sha, info.Sha256);
        Assert.Equal(1000, info.SizeBytes);
        Assert.True(info.Mandatory);
        Assert.Equal("Новое", info.ReleaseNotes);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    public void Same_or_older_version_means_no_update(string current) => Assert.Null(Parse(Json(), current));

    [Fact]
    public void Archive_may_live_on_an_allowed_extra_host() =>
        Assert.NotNull(Parse(Json(url: "https://github.com/SBUnivers/SubReel.Launcher/releases/download/v1.2.0/a.zip")));

    [Theory]
    [InlineData("http://updates.example.com/a.zip")]
    [InlineData("https://evil.example.org/a.zip")]
    [InlineData("ftp://updates.example.com/a.zip")]
    [InlineData("not a url")]
    public void Unsafe_archive_addresses_are_refused(string url) =>
        Assert.Throws<UpdateManifestException>(() => Parse(Json(url: url)));

    [Theory]
    [InlineData("abc")]
    [InlineData("zz" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Bad_hash_is_refused(string sha) => Assert.Throws<UpdateManifestException>(() => Parse(Json(sha: sha)));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("99999999999")]
    public void Bad_size_is_refused(string size) => Assert.Throws<UpdateManifestException>(() => Parse(Json(size: size)));

    [Fact]
    public void Broken_manifest_is_refused()
    {
        Assert.Throws<UpdateManifestException>(() => Parse("not json"));
        Assert.Throws<UpdateManifestException>(() => Parse("[]"));
        Assert.Throws<UpdateManifestException>(() => Parse(Json(version: "soon")));
    }

    // ───────────────────────── Баннер и «Позже» ─────────────────────────

    private static LauncherUpdateInfo Info(string version = "1.2.0", bool mandatory = false) =>
        new(Version.Parse(version), "https://updates.example.com/a.zip", Sha, 1000, mandatory, null);

    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Banner_is_hidden_without_update() => Assert.False(UpdateRules.IsBannerVisible(null, null, null, Now));

    [Fact]
    public void Banner_is_visible_when_nothing_is_postponed() => Assert.True(UpdateRules.IsBannerVisible(Info(), null, null, Now));

    [Fact]
    public void Postponed_version_stays_hidden_until_the_snooze_ends()
    {
        var until = Now.AddDays(3);

        Assert.False(UpdateRules.IsBannerVisible(Info("1.2.0"), "1.2.0", until, Now));
        Assert.False(UpdateRules.IsBannerVisible(Info("1.2.0"), "1.2.0", until, Now.AddDays(2)));
        Assert.True(UpdateRules.IsBannerVisible(Info("1.2.0"), "1.2.0", until, until));
    }

    [Fact]
    public void Newer_version_brings_the_banner_back_immediately() =>
        Assert.True(UpdateRules.IsBannerVisible(Info("1.3.0"), "1.2.0", Now.AddDays(3), Now));

    [Fact]
    public void Mandatory_update_cannot_be_postponed() =>
        Assert.True(UpdateRules.IsBannerVisible(Info(mandatory: true), "1.2.0", Now.AddDays(3), Now));
}

public sealed class SettingsV2MigrationTests
{
    [Fact]
    public void V1_settings_get_update_fields_and_keep_everything_else()
    {
        var settings = new LauncherSettings { SchemaVersion = 1, SelectedVersion = "1.21.1", ActiveAccount = "Gamer" };

        var changed = LauncherSettingsMigrator.Migrate(settings);

        Assert.True(changed);
        Assert.Equal(2, settings.SchemaVersion);
        Assert.Null(settings.PostponedUpdateVersion);
        Assert.Null(settings.UpdateSnoozedUntilUtc);
        Assert.Equal("1.21.1", settings.SelectedVersion);
        Assert.False(LauncherSettingsMigrator.Migrate(settings));
    }

    [Fact]
    public void Update_fields_survive_a_round_trip()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root);
        var first = new SettingsService(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SettingsService>.Instance);
        first.Current.PostponedUpdateVersion = "1.2.0";
        first.Current.UpdateSnoozedUntilUtc = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        first.Save();

        var second = new SettingsService(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SettingsService>.Instance);

        Assert.Equal("1.2.0", second.Current.PostponedUpdateVersion);
        Assert.Equal(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), second.Current.UpdateSnoozedUntilUtc);
    }
}

public sealed class LauncherUpdateServiceTests
{
    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeVersion : IAppVersion
    {
        public Version Current { get; set; } = new(1, 0, 0);
    }

    private sealed class NoDownloads : IDownloadManager
    {
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(IReadOnlyList<DownloadRequest> requests, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir _dir = new();
        public FakeSettingsService Settings { get; } = new();
        public FixedTime Time { get; } = new();
        public FakeVersion Version { get; } = new();
        public StubHandler Handler { get; }
        public LauncherUpdateService Service { get; }

        public Env(Func<HttpRequestMessage, HttpResponseMessage> respond, string? manifestUrl = "https://updates.example.com/manifest.json")
        {
            Handler = new StubHandler(respond);
            var options = Options.Create(new UpdateOptions { ManifestUrl = manifestUrl });
            Service = new LauncherUpdateService(new StubHttpClientFactory(Handler), Settings, options, Version, new NoDownloads(),
                new AppPaths(_dir.Root, _dir.Root), Time, NullLogger<LauncherUpdateService>.Instance);
        }

        public void Dispose() => _dir.Dispose();
    }

    private static string Manifest(string version = "1.2.0") =>
        $$"""{ "version": "{{version}}", "url": "https://updates.example.com/a.zip", "sha256": "{{new string('b', 64)}}", "sizeBytes": 5000, "releaseNotesRu": "Что нового" }""";

    [Fact]
    public async Task Without_a_manifest_url_nothing_is_requested()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest()), manifestUrl: "");

        await env.Service.CheckAsync();

        Assert.False(env.Service.IsConfigured);
        Assert.Empty(env.Handler.Requests);
        Assert.Null(env.Service.Available);
    }

    [Fact]
    public async Task Plain_http_manifest_is_not_used()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest()), manifestUrl: "http://updates.example.com/manifest.json");

        await env.Service.CheckAsync();

        Assert.False(env.Service.IsConfigured);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task Newer_version_is_found_and_the_banner_is_visible()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest()));
        var changes = 0;
        env.Service.StateChanged += (_, _) => changes++;

        await env.Service.CheckAsync();

        Assert.Equal(new Version(1, 2, 0), env.Service.Available!.Version);
        Assert.True(env.Service.IsBannerVisible);
        Assert.Null(env.Service.LastError);
        Assert.NotNull(env.Service.LastCheckedUtc);
        Assert.False(env.Service.IsChecking);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Same_version_means_no_update()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest("1.0.0")));

        await env.Service.CheckAsync();

        Assert.Null(env.Service.Available);
        Assert.False(env.Service.IsBannerVisible);
        Assert.Null(env.Service.LastError);
    }

    [Fact]
    public async Task Server_failure_is_reported_but_keeps_the_previous_result()
    {
        var fail = false;
        using var env = new Env(_ => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : StubHandler.Ok(Manifest()));
        await env.Service.CheckAsync();

        fail = true;
        await env.Service.CheckAsync();

        Assert.NotNull(env.Service.LastError);
        Assert.NotNull(env.Service.Available);
    }

    [Fact]
    public async Task Broken_manifest_is_reported_without_throwing()
    {
        using var env = new Env(_ => StubHandler.Ok("not json"));

        await env.Service.CheckAsync();

        Assert.Null(env.Service.Available);
        Assert.Contains("повреждён", env.Service.LastError);
    }

    [Fact]
    public async Task Postpone_hides_the_banner_saves_the_choice_and_expires()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest()));
        await env.Service.CheckAsync();

        env.Service.Postpone();

        Assert.False(env.Service.IsBannerVisible);
        Assert.NotNull(env.Service.Available); // страница «О лаунчере» по-прежнему видит версию
        Assert.Equal("1.2.0", env.Settings.Current.PostponedUpdateVersion);
        Assert.Equal(env.Time.Now.UtcDateTime.AddDays(3), env.Settings.Current.UpdateSnoozedUntilUtc);
        Assert.Equal(1, env.Settings.SaveCount);

        env.Time.Now = env.Time.Now.AddDays(3);
        Assert.True(env.Service.IsBannerVisible);
    }

    [Fact]
    public async Task Mandatory_update_ignores_postpone()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest().Replace("\"sizeBytes\"", "\"mandatory\": true, \"sizeBytes\"")));
        await env.Service.CheckAsync();

        env.Service.Postpone();

        Assert.True(env.Service.IsBannerVisible);
        Assert.Null(env.Settings.Current.PostponedUpdateVersion);
        Assert.Equal(0, env.Settings.SaveCount);
    }

    [Fact]
    public async Task Apply_without_an_update_fails_politely()
    {
        using var env = new Env(_ => StubHandler.Ok(Manifest("1.0.0")));
        await env.Service.CheckAsync();

        var result = await env.Service.ApplyAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }
}

public sealed class UpdatePackageTests
{
    private static string MakeZip(TempDir dir, params (string Name, string Text)[] entries)
    {
        var path = dir.File("update-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = zip.CreateEntry(name).Open();
            writer.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    [Fact]
    public async Task Sha256_is_verified_case_insensitively()
    {
        using var dir = new TempDir();
        var path = dir.File("a.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));

        Assert.True(await UpdatePackage.VerifySha256Async(path, hash));
        Assert.True(await UpdatePackage.VerifySha256Async(path, hash.ToLowerInvariant()));
        Assert.False(await UpdatePackage.VerifySha256Async(path, new string('0', 64)));
    }

    [Fact]
    public async Task Files_next_to_the_exe_are_extracted()
    {
        using var dir = new TempDir();
        var zip = MakeZip(dir, ("Launcher.exe", "exe"), ("lib/a.dll", "dll"), ("Updater/SubReel.Updater.exe", "u"));

        await UpdatePackage.ExtractAsync(zip, dir.File("out"), "Launcher.exe");

        Assert.Equal("exe", File.ReadAllText(dir.File("out/Launcher.exe")));
        Assert.Equal("dll", File.ReadAllText(dir.File("out/lib/a.dll")));
        Assert.True(File.Exists(dir.File("out/Updater/SubReel.Updater.exe")));
    }

    [Fact]
    public async Task Single_top_folder_is_stripped()
    {
        using var dir = new TempDir();
        var zip = MakeZip(dir, ("SubReel-1.2.0/Launcher.exe", "exe"), ("SubReel-1.2.0/lib/a.dll", "dll"), ("readme-outside.txt", "x"));

        await UpdatePackage.ExtractAsync(zip, dir.File("out"), "launcher.EXE");

        Assert.True(File.Exists(dir.File("out/Launcher.exe")));
        Assert.True(File.Exists(dir.File("out/lib/a.dll")));
        Assert.False(File.Exists(dir.File("out/readme-outside.txt")));
    }

    [Fact]
    public async Task Archive_without_the_exe_is_refused()
    {
        using var dir = new TempDir();
        var zip = MakeZip(dir, ("lib/a.dll", "dll"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.ExtractAsync(zip, dir.File("out"), "Launcher.exe"));

        Assert.Contains("Launcher.exe", ex.Message);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("C:/evil.dll")]
    public async Task Path_traversal_is_refused(string name)
    {
        using var dir = new TempDir();
        var zip = MakeZip(dir, ("Launcher.exe", "exe"), (name, "x"));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.ExtractAsync(zip, dir.File("out"), "Launcher.exe"));

        Assert.False(File.Exists(dir.File("evil.dll")));
    }
}
