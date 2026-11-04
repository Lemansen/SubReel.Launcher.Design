using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Mods.Modrinth;

namespace SubReel.Launcher.Tests;

public sealed class ModrinthParserTests
{
    private static readonly string Sha1 = new('a', 40);
    private static readonly string Sha512 = new('b', 128);

    internal static string FileJson(string name = "sodium.jar", string url = "https://cdn.modrinth.com/data/AANobbMI/versions/v1/sodium.jar", string? sha1 = null, string? sha512 = null, bool primary = true) =>
        $$"""{ "hashes": { "sha1": "{{sha1 ?? Sha1}}", "sha512": "{{sha512 ?? Sha512}}" }, "url": "{{url}}", "filename": "{{name}}", "primary": {{(primary ? "true" : "false")}}, "size": 1234 }""";

    internal static string VersionJson(string id, string date, string files, string deps = "[]", string type = "release", string project = "AANobbMI") =>
        $$"""{ "id": "{{id}}", "project_id": "{{project}}", "name": "Версия {{id}}", "version_number": "1.0-{{id}}", "version_type": "{{type}}", "date_published": "{{date}}", "dependencies": {{deps}}, "files": [{{files}}] }""";

    [Fact]
    public void Versions_are_sorted_newest_first_and_use_the_primary_file()
    {
        var json = "[" + string.Join(",",
            VersionJson("old", "2026-01-01T10:00:00Z", FileJson("a.jar", primary: true)),
            VersionJson("new", "2026-05-01T10:00:00Z", FileJson("extra.jar", primary: false) + "," + FileJson("main.jar", primary: true))) + "]";

        var versions = ModrinthParser.ParseVersions(json);

        Assert.Equal(new[] { "new", "old" }, versions.Select(v => v.Id));
        Assert.Equal("main.jar", versions[0].File.FileName);
        Assert.Equal(1234, versions[0].File.Size);
    }

    [Theory]
    [InlineData("../evil.jar", "https://cdn.modrinth.com/a.jar")]
    [InlineData("sub/evil.jar", "https://cdn.modrinth.com/a.jar")]
    [InlineData("evil.exe", "https://cdn.modrinth.com/a.jar")]
    [InlineData("ok.jar", "http://cdn.modrinth.com/a.jar")]
    [InlineData("ok.jar", "https://evil.example.com/a.jar")]
    public void Versions_with_an_untrusted_file_are_dropped(string name, string url)
    {
        var json = "[" + VersionJson("v1", "2026-01-01T10:00:00Z", FileJson(name, url)) + "]";

        Assert.Empty(ModrinthParser.ParseVersions(json));
    }

    [Fact]
    public void Version_with_broken_hashes_is_dropped_but_the_rest_survive()
    {
        var json = "[" + string.Join(",",
            VersionJson("bad", "2026-02-01T10:00:00Z", FileJson(sha1: "zz")),
            VersionJson("good", "2026-01-01T10:00:00Z", FileJson())) + "]";

        var versions = ModrinthParser.ParseVersions(json);

        Assert.Equal("good", Assert.Single(versions).Id);
    }

    [Fact]
    public void Dependencies_keep_their_kind_and_skip_unknown_or_empty_ones()
    {
        var deps = """
            [ { "project_id": "P1", "dependency_type": "required" },
              { "project_id": "P2", "dependency_type": "optional" },
              { "project_id": "P3", "dependency_type": "incompatible" },
              { "project_id": "P4", "dependency_type": "embedded" },
              { "version_id": "V9", "dependency_type": "required" },
              { "project_id": "P5", "dependency_type": "something-new" },
              { "dependency_type": "required" } ]
            """;

        var version = Assert.Single(ModrinthParser.ParseVersions("[" + VersionJson("v1", "2026-01-01T10:00:00Z", FileJson(), deps) + "]"));

        Assert.Equal(5, version.Dependencies.Count);
        Assert.Equal(ModDependencyKind.Required, version.Dependencies[0].Kind);
        Assert.Equal(ModDependencyKind.Incompatible, version.Dependencies[2].Kind);
        Assert.Null(version.Dependencies[4].ProjectId);
        Assert.Equal("V9", version.Dependencies[4].VersionId);
    }

    [Fact]
    public void Beta_and_alpha_types_are_recognized()
    {
        var json = "[" + VersionJson("b", "2026-01-01T10:00:00Z", FileJson(), type: "beta") + "," + VersionJson("a", "2026-01-02T10:00:00Z", FileJson(), type: "alpha") + "]";

        var versions = ModrinthParser.ParseVersions(json);

        Assert.Equal(ModReleaseType.Alpha, versions[0].Type);
        Assert.Equal(ModReleaseType.Beta, versions[1].Type);
    }

    [Fact]
    public void Search_reads_hits_and_total()
    {
        var json = """
            { "hits": [ { "project_id": "AANobbMI", "slug": "sodium", "title": "Sodium", "description": "Быстрый рендер", "author": "jellysquid3", "downloads": 100, "icon_url": "https://cdn.modrinth.com/i.png" },
                        { "project_id": "../bad", "title": "Bad" } ],
              "offset": 0, "limit": 20, "total_hits": 57 }
            """;

        var result = ModrinthParser.ParseSearch(json);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("sodium", hit.Slug);
        Assert.Equal("jellysquid3", hit.Author);
        Assert.Equal(57, result.TotalHits);
    }

    [Fact]
    public void Garbage_is_reported_as_a_catalog_error()
    {
        Assert.Throws<ModCatalogException>(() => ModrinthParser.ParseVersions("not json"));
        Assert.Throws<ModCatalogException>(() => ModrinthParser.ParseSearch("[]"));
        Assert.Throws<ModCatalogException>(() => ModrinthParser.ParseHashMap("[]"));
    }

    [Theory]
    [InlineData("sodium-fabric-0.5.jar", true)]
    [InlineData("Mod (1).JAR", true)]
    [InlineData("", false)]
    [InlineData(".hidden.jar", false)]
    [InlineData("a/b.jar", false)]
    [InlineData("a\\b.jar", false)]
    [InlineData("mod.zip", false)]
    public void File_names_must_be_plain_jar_names(string name, bool expected) =>
        Assert.Equal(expected, ModrinthParser.IsSafeFileName(name));
}

public sealed class ModrinthCatalogTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public List<(string Method, string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ModrinthCatalog Create(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(ModrinthCatalog.BaseAddress) }, NullLogger<ModrinthCatalog>.Instance);

    [Fact]
    public async Task Search_filters_by_game_version_and_loader()
    {
        var handler = new RecordingHandler(_ => Json("""{ "hits": [], "total_hits": 0 }"""));

        await Create(handler).SearchAsync(new ModSearchQuery("sodium", "1.21.1", LoaderKind.Quilt));

        var (method, url, _) = Assert.Single(handler.Requests);
        Assert.Equal("GET", method);
        Assert.StartsWith("https://api.modrinth.com/v2/search?", url);
        Assert.Contains("query=sodium", url);
        Assert.Contains("index=relevance", url);
        Assert.Contains("\"project_type:mod\"", url);
        Assert.Contains("\"versions:1.21.1\"", url);
        Assert.Contains("[\"categories:quilt\",\"categories:fabric\"]", url); // Quilt загружает и моды Fabric
    }

    [Fact]
    public async Task Empty_search_text_means_popular_mods()
    {
        var handler = new RecordingHandler(_ => Json("""{ "hits": [], "total_hits": 0 }"""));

        await Create(handler).SearchAsync(new ModSearchQuery("  ", "1.21.1", LoaderKind.Fabric));

        Assert.Contains("index=downloads", handler.Requests[0].Url);
        Assert.DoesNotContain("query=", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Vanilla_has_no_mods()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        var catalog = Create(handler);

        await Assert.ThrowsAsync<ModCatalogException>(() => catalog.SearchAsync(new ModSearchQuery("x", "1.21.1", LoaderKind.Vanilla)));
        await Assert.ThrowsAsync<ModCatalogException>(() => catalog.GetVersionsAsync("AANobbMI", "1.21.1", LoaderKind.Vanilla));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unsafe_ids_never_reach_the_network()
    {
        var handler = new RecordingHandler(_ => Json("[]"));

        await Assert.ThrowsAsync<ModCatalogException>(() => Create(handler).GetVersionsAsync("../../users/x", "1.21.1", LoaderKind.Fabric));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Versions_are_requested_for_the_loaders_and_game_version()
    {
        var handler = new RecordingHandler(_ => Json("[]"));

        await Create(handler).GetVersionsAsync("AANobbMI", "1.21.1", LoaderKind.Fabric);

        Assert.Contains("project/AANobbMI/version?loaders=[\"fabric\"]&game_versions=[\"1.21.1\"]", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Update_check_posts_hashes_loaders_and_game_version()
    {
        var sha = new string('c', 40);
        var handler = new RecordingHandler(_ => Json("{}"));

        await Create(handler).FindLatestForFilesAsync(new[] { sha.ToUpperInvariant(), "not-a-hash" }, "1.21.1", LoaderKind.NeoForge);

        var (method, url, body) = Assert.Single(handler.Requests);
        Assert.Equal("POST", method);
        Assert.EndsWith("version_files/update", url);
        Assert.Contains($"\"hashes\":[\"{sha}\"]", body);
        Assert.Contains("\"algorithm\":\"sha1\"", body);
        Assert.Contains("\"loaders\":[\"neoforge\"]", body);
        Assert.Contains("\"game_versions\":[\"1.21.1\"]", body);
    }

    [Fact]
    public async Task Hash_lookup_maps_hashes_to_versions()
    {
        var sha = new string('a', 40);
        var version = ModrinthParserTests.VersionJson("v1", "2026-01-01T10:00:00Z", ModrinthParserTests.FileJson());
        var handler = new RecordingHandler(_ => Json($$"""{ "{{sha}}": {{version}} }"""));

        var map = await Create(handler).IdentifyFilesAsync(new[] { sha });

        Assert.Equal("v1", map[sha].Id);
        Assert.EndsWith("version_files", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Empty_hash_list_makes_no_request()
    {
        var handler = new RecordingHandler(_ => Json("{}"));

        Assert.Empty(await Create(handler).IdentifyFilesAsync(Array.Empty<string>()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_become_catalog_errors()
    {
        var limited = Create(new RecordingHandler(_ => Json("{}", HttpStatusCode.TooManyRequests)));
        var broken = Create(new RecordingHandler(_ => Json("{}", HttpStatusCode.InternalServerError)));
        var query = new ModSearchQuery("x", "1.21.1", LoaderKind.Fabric);

        var ex = await Assert.ThrowsAsync<ModCatalogException>(() => limited.SearchAsync(query));
        Assert.Contains("слишком много запросов", ex.Message);
        await Assert.ThrowsAsync<ModCatalogException>(() => broken.SearchAsync(query));
    }

    [Fact]
    public async Task Network_failure_becomes_a_catalog_error_but_cancellation_stays_cancellation()
    {
        var offline = Create(new RecordingHandler(_ => throw new HttpRequestException("offline")));
        var query = new ModSearchQuery("x", "1.21.1", LoaderKind.Fabric);
        await Assert.ThrowsAsync<ModCatalogException>(() => offline.SearchAsync(query));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var catalog = Create(new RecordingHandler(_ => throw new TaskCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.SearchAsync(query, cts.Token));
    }

    [Fact]
    public async Task Missing_version_is_null_not_an_error()
    {
        var handler = new RecordingHandler(_ => Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await Create(handler).GetVersionAsync("gone"));
    }
}
