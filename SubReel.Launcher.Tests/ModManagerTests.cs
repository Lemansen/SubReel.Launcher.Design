using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;
using SubReel.Launcher.Mods;

namespace SubReel.Launcher.Tests;

public sealed class ModManagerTests
{
    private sealed class FakeCatalog : IModCatalog
    {
        public ModSource Source => ModSource.Modrinth;

        public Dictionary<string, List<ModVersion>> Versions { get; } = new();
        public Dictionary<string, string> Titles { get; } = new();
        public Dictionary<string, ModVersion> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Offline { get; set; }
        public int VersionRequests { get; private set; }

        private void ThrowIfOffline()
        {
            if (Offline) throw new ModCatalogException("нет сети");
        }

        public Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ModSearchResult(Array.Empty<ModProject>(), 0));

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string mcVersion, LoaderKind loader, CancellationToken ct = default)
        {
            ThrowIfOffline();
            VersionRequests++;
            return Task.FromResult<IReadOnlyList<ModVersion>>(Versions.TryGetValue(projectId, out var list) ? list.ToList() : new List<ModVersion>());
        }

        public Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
        {
            ThrowIfOffline();
            return Task.FromResult(Versions.Values.SelectMany(v => v).FirstOrDefault(v => v.Id == versionId));
        }

        public Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyList<string> projectIds, CancellationToken ct = default)
        {
            ThrowIfOffline();
            IReadOnlyList<ModProject> projects = projectIds.Where(Titles.ContainsKey).Select(id => new ModProject(id, id, Titles[id], "", "", 0, null)).ToList();
            return Task.FromResult(projects);
        }

        public Task<IReadOnlyDictionary<string, ModVersion>> IdentifyFilesAsync(IReadOnlyList<string> sha1Hashes, CancellationToken ct = default)
        {
            ThrowIfOffline();
            var all = Versions.Values.SelectMany(v => v).ToList();
            IReadOnlyDictionary<string, ModVersion> map = sha1Hashes
                .Select(h => (Hash: h, Version: all.FirstOrDefault(v => v.File.Sha1 == h)))
                .Where(p => p.Version is not null)
                .ToDictionary(p => p.Hash, p => p.Version!, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(map);
        }

        public Task<IReadOnlyDictionary<string, ModVersion>> FindLatestForFilesAsync(IReadOnlyList<string> sha1Hashes, string mcVersion, LoaderKind loader, CancellationToken ct = default)
        {
            ThrowIfOffline();
            IReadOnlyDictionary<string, ModVersion> map = sha1Hashes.Where(Latest.ContainsKey).ToDictionary(h => h, h => Latest[h], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(map);
        }
    }

    private sealed class FakeDownloads : IDownloadManager
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool Cancel { get; set; }
        public int Calls { get; private set; }

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
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
            var store = new JsonFileStore(NullLogger<JsonFileStore>.Instance);
            Repo = new JsonInstanceRepository(paths, store, NullLogger<JsonInstanceRepository>.Instance);
            Instances = new InstanceService(Repo, TimeProvider.System, NullLogger<InstanceService>.Instance);
            Catalog = new FakeCatalog();
            Downloads = new FakeDownloads();
            Manager = new ModManager(new IModCatalog[] { Catalog }, Downloads, Instances, store, NullLogger<ModManager>.Instance);
        }

        public JsonInstanceRepository Repo { get; }
        public InstanceService Instances { get; }
        public FakeCatalog Catalog { get; }
        public FakeDownloads Downloads { get; }
        public ModManager Manager { get; }

        public async Task<Instance> NewInstanceAsync(string name = "Сборка", LoaderKind loader = LoaderKind.Fabric)
        {
            var result = await Instances.CreateAsync(name, "1.21.1", loader, loader == LoaderKind.Vanilla ? null : "0.16.0");
            return result.Instance!;
        }

        public string ModsDir(Instance instance) => Path.Combine(Repo.GetGameDirectory(instance.Id), "mods");

        public string[] ModFiles(Instance instance) =>
            Directory.Exists(ModsDir(instance)) ? Directory.GetFiles(ModsDir(instance)).Select(f => Path.GetFileName(f)).OrderBy(n => n).ToArray() : Array.Empty<string>();

        /// <summary>Заводит мод в «каталоге» и «на CDN»: возвращает версию с настоящими хэшами содержимого.</summary>
        public ModVersion AddMod(string project, string title, string versionId, string file, string published, IReadOnlyList<ModDependency>? deps = null, string content = "")
        {
            var bytes = Encoding.UTF8.GetBytes(content.Length > 0 ? content : $"jar:{project}:{versionId}");
            var url = $"https://cdn.modrinth.com/data/{project}/versions/{versionId}/{file}";
            Downloads.Files[url] = bytes;

            var version = new ModVersion(versionId, project, versionId, "1.0." + versionId, ModReleaseType.Release, DateTime.Parse(published, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                deps ?? Array.Empty<ModDependency>(),
                new ModFile(file, url, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(), bytes.Length));

            if (!Catalog.Versions.TryGetValue(project, out var list)) Catalog.Versions[project] = list = new List<ModVersion>();
            list.Add(version);
            Catalog.Titles[project] = title;
            return version;
        }

        public void Dispose() => _dir.Dispose();
    }

    private static ModDependency Required(string project) => new(project, null, ModDependencyKind.Required);

    // ───────────────────────── План ─────────────────────────

    [Fact]
    public async Task Plan_includes_required_dependencies_but_not_optional_or_embedded_ones()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("lib", "Lib", "l1", "lib.jar", "2026-01-01");
        env.AddMod("opt", "Optional", "o1", "opt.jar", "2026-01-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[]
        {
            Required("fabricapi"),
            new ModDependency("opt", null, ModDependencyKind.Optional),
            new ModDependency("lib", null, ModDependencyKind.Embedded),
        });

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.True(plan.CanInstall, plan.Error);
        Assert.Equal(new[] { "main", "fabricapi" }, plan.Items.Select(i => i.Version.ProjectId));
        Assert.Equal(ModInstallReason.UserChoice, plan.Items[0].Reason);
        Assert.Equal(ModInstallReason.Dependency, plan.Items[1].Reason);
        Assert.Equal("main", plan.Items[1].RequiredByProjectId);
        Assert.Equal("Fabric API", plan.Items[1].Project.Title);
    }

    [Fact]
    public async Task Plan_follows_dependencies_of_dependencies_and_survives_cycles()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("a", "A", "a1", "a.jar", "2026-01-01", new[] { Required("b") });
        env.AddMod("b", "B", "b1", "b.jar", "2026-01-01", new[] { Required("c") });
        env.AddMod("c", "C", "c1", "c.jar", "2026-01-01", new[] { Required("a") }); // цикл

        var plan = await env.Manager.PlanInstallAsync(instance, "a");

        Assert.True(plan.CanInstall, plan.Error);
        Assert.Equal(new[] { "a", "b", "c" }, plan.Items.Select(i => i.Version.ProjectId));
    }

    [Fact]
    public async Task Plan_pins_a_dependency_to_the_version_the_mod_asks_for()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("lib", "Lib", "l-old", "lib-old.jar", "2026-01-01");
        env.AddMod("lib", "Lib", "l-new", "lib-new.jar", "2026-03-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { new ModDependency("lib", "l-old", ModDependencyKind.Required) });

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.Equal("l-old", plan.Items.Single(i => i.Version.ProjectId == "lib").Version.Id);
    }

    [Fact]
    public async Task Plan_prefers_the_newest_release_over_a_newer_beta()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m-release", "main-1.jar", "2026-01-01");
        var beta = env.AddMod("main", "Main", "m-beta", "main-2.jar", "2026-06-01");
        env.Catalog.Versions["main"][1] = beta with { Type = ModReleaseType.Beta };

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.Equal("m-release", plan.Items[0].Version.Id);
    }

    [Fact]
    public async Task Plan_skips_dependencies_that_are_already_installed()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("one", "One", "o1", "one.jar", "2026-01-01", new[] { Required("fabricapi") });
        env.AddMod("two", "Two", "t1", "two.jar", "2026-01-01", new[] { Required("fabricapi") });
        await InstallAsync(env, instance, "one");

        var plan = await env.Manager.PlanInstallAsync(instance, "two");

        Assert.Equal(new[] { "two" }, plan.Items.Select(i => i.Version.ProjectId));
    }

    [Fact]
    public async Task Plan_reports_an_installed_mod_that_the_new_one_is_incompatible_with()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("old", "Старый мод", "o1", "old.jar", "2026-01-01");
        env.AddMod("new", "Новый мод", "n1", "new.jar", "2026-01-01", new[] { new ModDependency("old", null, ModDependencyKind.Incompatible) });
        await InstallAsync(env, instance, "old");

        var plan = await env.Manager.PlanInstallAsync(instance, "new");

        Assert.False(plan.CanInstall);
        Assert.Equal(new[] { "Старый мод" }, plan.Conflicts);

        var result = await env.Manager.InstallAsync(instance, plan);
        Assert.False(result.Succeeded);
        Assert.Contains("Старый мод", result.Error);
    }

    [Fact]
    public async Task Plan_explains_when_no_version_fits_the_build()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.Catalog.Titles["lonely"] = "Одинокий мод";

        var plan = await env.Manager.PlanInstallAsync(instance, "lonely");

        Assert.False(plan.CanInstall);
        Assert.Contains("Одинокий мод", plan.Error);
        Assert.Contains("1.21.1", plan.Error);
    }

    [Fact]
    public async Task Plan_explains_when_a_required_dependency_has_no_version()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.Catalog.Titles["lib"] = "Библиотека";
        env.AddMod("main", "Main", "m1", "main.jar", "2026-01-01", new[] { Required("lib") });

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.False(plan.CanInstall);
        Assert.Contains("Библиотека", plan.Error);
    }

    [Fact]
    public async Task Vanilla_build_cannot_have_mods()
    {
        using var env = new Env();
        var vanilla = await env.NewInstanceAsync("Ваниль", LoaderKind.Vanilla);
        env.AddMod("main", "Main", "m1", "main.jar", "2026-01-01");

        var plan = await env.Manager.PlanInstallAsync(vanilla, "main");

        Assert.False(plan.CanInstall);
        Assert.Contains("загрузчик", plan.Error);
        Assert.Equal(0, env.Catalog.VersionRequests);
    }

    [Fact]
    public async Task Catalog_outage_is_reported_in_the_plan_not_thrown()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.Catalog.Offline = true;

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.Equal("нет сети", plan.Error);
    }

    // ───────────────────────── Установка ─────────────────────────

    private static async Task<ModInstallResult> InstallAsync(Env env, Instance instance, string projectId)
    {
        var plan = await env.Manager.PlanInstallAsync(instance, projectId);
        Assert.True(plan.CanInstall, plan.Error);
        var result = await env.Manager.InstallAsync(instance, plan);
        Assert.True(result.Succeeded, result.Error);
        return result;
    }

    [Fact]
    public async Task Install_downloads_files_and_remembers_why_each_mod_is_there()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { Required("fabricapi") });

        var result = await InstallAsync(env, instance, "main");

        Assert.Equal(new[] { "fabric-api.jar", "main.jar" }, env.ModFiles(instance));
        Assert.Equal(2, result.Installed.Count);

        var listing = await env.Manager.ListAsync(instance);
        Assert.Equal(new[] { "Fabric API", "Main" }, listing.Mods.Select(m => m.Title));
        var api = listing.Mods.Single(m => m.ProjectId == "fabricapi");
        Assert.Equal(ModInstallReason.Dependency, api.Reason);
        Assert.Equal(new[] { "main" }, api.RequiredBy);
        Assert.Equal(ModInstallReason.UserChoice, listing.Mods.Single(m => m.ProjectId == "main").Reason);
        Assert.Empty(listing.Unrecognized);
    }

    [Fact]
    public async Task Installed_list_survives_a_restart()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        await InstallAsync(env, instance, "main");

        var again = await env.Manager.ListAsync(instance);

        Assert.Equal("Main", Assert.Single(again.Mods).Title);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(env.ModsDir(instance))!.TrimEnd(Path.DirectorySeparatorChar), "..", "mods.json")));
    }

    [Fact]
    public async Task Installing_twice_is_refused()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        await InstallAsync(env, instance, "main");

        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        Assert.False(plan.CanInstall);
        Assert.Contains("уже установлен", plan.Error);
    }

    [Fact]
    public async Task Failed_download_leaves_no_files_and_no_list_entries()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        var main = env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { Required("fabricapi") });
        env.Downloads.Files.Remove(main.File.Url); // главный файл недоступен, зависимость скачается

        var plan = await env.Manager.PlanInstallAsync(instance, "main");
        var result = await env.Manager.InstallAsync(instance, plan);

        Assert.False(result.Succeeded);
        Assert.Empty(env.ModFiles(instance));
        Assert.Empty((await env.Manager.ListAsync(instance)).Mods);
    }

    [Fact]
    public async Task Sha512_mismatch_rejects_the_mod()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var version = env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        // SHA-1 совпадает (его проверит менеджер загрузок), а SHA-512 из каталога — подделка
        env.Catalog.Versions["main"][0] = version with { File = version.File with { Sha512 = new string('0', 128) } };

        var plan = await env.Manager.PlanInstallAsync(instance, "main");
        var result = await env.Manager.InstallAsync(instance, plan);

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-512", result.Error);
        Assert.Empty(env.ModFiles(instance));
        Assert.Empty((await env.Manager.ListAsync(instance)).Mods);
    }

    [Fact]
    public async Task Cancelled_install_rethrows_and_cleans_up()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        var plan = await env.Manager.PlanInstallAsync(instance, "main");
        env.Downloads.Cancel = true;

        await Assert.ThrowsAsync<OperationCanceledException>(() => env.Manager.InstallAsync(instance, plan));

        Assert.Empty(env.ModFiles(instance));
        Assert.Empty((await env.Manager.ListAsync(instance)).Mods);
    }

    [Fact]
    public async Task A_foreign_file_with_the_same_name_is_never_overwritten()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        Directory.CreateDirectory(env.ModsDir(instance));
        File.WriteAllText(Path.Combine(env.ModsDir(instance), "main.jar"), "my own build");
        var plan = await env.Manager.PlanInstallAsync(instance, "main");

        var result = await env.Manager.InstallAsync(instance, plan);

        Assert.False(result.Succeeded);
        Assert.Contains("main.jar", result.Error);
        Assert.Equal("my own build", File.ReadAllText(Path.Combine(env.ModsDir(instance), "main.jar")));
        Assert.Equal(0, env.Downloads.Calls);
    }

    [Fact]
    public async Task Concurrent_installs_into_one_build_are_serialized_and_both_land()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("one", "One", "o1", "one.jar", "2026-01-01");
        env.AddMod("two", "Two", "t1", "two.jar", "2026-01-01");
        var planOne = await env.Manager.PlanInstallAsync(instance, "one");
        var planTwo = await env.Manager.PlanInstallAsync(instance, "two");

        var results = await Task.WhenAll(env.Manager.InstallAsync(instance, planOne), env.Manager.InstallAsync(instance, planTwo));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        Assert.Equal(2, (await env.Manager.ListAsync(instance)).Mods.Count);
    }

    // ───────────────────────── Список ─────────────────────────

    [Fact]
    public async Task List_adopts_known_files_by_hash_and_leaves_unknown_ones_alone()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var known = env.AddMod("sodium", "Sodium", "s1", "sodium.jar", "2026-01-01");
        Directory.CreateDirectory(env.ModsDir(instance));
        File.WriteAllBytes(Path.Combine(env.ModsDir(instance), "sodium.jar"), env.Downloads.Files[known.File.Url]);
        File.WriteAllText(Path.Combine(env.ModsDir(instance), "my-private-mod.jar"), "unknown");

        var listing = await env.Manager.ListAsync(instance);

        var mod = Assert.Single(listing.Mods);
        Assert.Equal("Sodium", mod.Title);
        Assert.Equal(ModInstallReason.UserChoice, mod.Reason);
        Assert.Equal(new[] { "my-private-mod.jar" }, listing.Unrecognized);
        Assert.Equal("unknown", File.ReadAllText(Path.Combine(env.ModsDir(instance), "my-private-mod.jar")));
    }

    [Fact]
    public async Task List_works_offline_and_keeps_files_unrecognized()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        Directory.CreateDirectory(env.ModsDir(instance));
        File.WriteAllText(Path.Combine(env.ModsDir(instance), "some.jar"), "x");
        env.Catalog.Offline = true;

        var listing = await env.Manager.ListAsync(instance);

        Assert.Empty(listing.Mods);
        Assert.Equal(new[] { "some.jar" }, listing.Unrecognized);
    }

    [Fact]
    public async Task List_forgets_mods_whose_files_were_deleted_by_hand()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        await InstallAsync(env, instance, "main");
        File.Delete(Path.Combine(env.ModsDir(instance), "main.jar"));

        var listing = await env.Manager.ListAsync(instance);

        Assert.Empty(listing.Mods);
    }

    // ───────────────────────── Обновление ─────────────────────────

    [Fact]
    public async Task Update_replaces_the_file_and_keeps_the_reason()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var v1 = env.AddMod("main", "Main", "m1", "main-1.jar", "2026-01-01");
        await InstallAsync(env, instance, "main");
        var v2 = env.AddMod("main", "Main", "m2", "main-2.jar", "2026-03-01");
        env.Catalog.Latest[v1.File.Sha1] = v2;

        var offers = await env.Manager.CheckUpdatesAsync(instance);
        Assert.Equal("m2", Assert.Single(offers).Latest.Id);

        var result = await env.Manager.UpdateAsync(instance, "main");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { "main-2.jar" }, env.ModFiles(instance));
        var mod = Assert.Single((await env.Manager.ListAsync(instance)).Mods);
        Assert.Equal("m2", mod.VersionId);
        Assert.Equal(ModInstallReason.UserChoice, mod.Reason);
        Assert.Empty(await env.Manager.CheckUpdatesAsync(instance)); // после обновления предложений нет (каталог знает только хэш старого файла)
    }

    [Fact]
    public async Task Update_installs_new_required_dependencies_of_the_new_version()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var v1 = env.AddMod("main", "Main", "m1", "main-1.jar", "2026-01-01");
        await InstallAsync(env, instance, "main");
        env.AddMod("lib", "Lib", "l1", "lib.jar", "2026-01-01");
        var v2 = env.AddMod("main", "Main", "m2", "main-2.jar", "2026-03-01", new[] { Required("lib") });
        env.Catalog.Latest[v1.File.Sha1] = v2;

        var result = await env.Manager.UpdateAsync(instance, "main");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { "lib.jar", "main-2.jar" }, env.ModFiles(instance));
    }

    [Fact]
    public async Task Failed_update_keeps_the_old_version_working()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var v1 = env.AddMod("main", "Main", "m1", "main-1.jar", "2026-01-01");
        await InstallAsync(env, instance, "main");
        var v2 = env.AddMod("main", "Main", "m2", "main-2.jar", "2026-03-01");
        env.Downloads.Files.Remove(v2.File.Url);
        env.Catalog.Latest[v1.File.Sha1] = v2;

        var result = await env.Manager.UpdateAsync(instance, "main");

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "main-1.jar" }, env.ModFiles(instance));
        Assert.Equal("m1", Assert.Single((await env.Manager.ListAsync(instance)).Mods).VersionId);
    }

    [Fact]
    public async Task No_update_is_offered_for_the_same_or_an_older_version()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        var v1 = env.AddMod("main", "Main", "m1", "main-1.jar", "2026-03-01");
        await InstallAsync(env, instance, "main");
        var older = env.AddMod("main", "Main", "m0", "main-0.jar", "2026-01-01");

        env.Catalog.Latest[v1.File.Sha1] = v1;
        Assert.Empty(await env.Manager.CheckUpdatesAsync(instance));

        env.Catalog.Latest[v1.File.Sha1] = older;
        Assert.Empty(await env.Manager.CheckUpdatesAsync(instance));

        var result = await env.Manager.UpdateAsync(instance, "main");
        Assert.False(result.Succeeded);
        Assert.Contains("нет обновления", result.Error);
    }

    [Fact]
    public async Task Update_check_reports_a_catalog_outage()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        await InstallAsync(env, instance, "main");
        env.Catalog.Offline = true;

        await Assert.ThrowsAsync<ModCatalogException>(() => env.Manager.CheckUpdatesAsync(instance));
    }

    // ───────────────────────── Удаление ─────────────────────────

    [Fact]
    public async Task Remove_deletes_the_file_and_the_list_entry()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01");
        await InstallAsync(env, instance, "main");

        var result = await env.Manager.RemoveAsync(instance, "main");

        Assert.True(result.Removed, result.Error);
        Assert.Empty(env.ModFiles(instance));
        Assert.Empty((await env.Manager.ListAsync(instance)).Mods);
    }

    [Fact]
    public async Task Removing_a_mod_that_others_need_is_blocked_until_forced()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { Required("fabricapi") });
        await InstallAsync(env, instance, "main");

        var blocked = await env.Manager.RemoveAsync(instance, "fabricapi");
        Assert.False(blocked.Removed);
        Assert.Equal(new[] { "Main" }, blocked.Blocked);
        Assert.Equal(2, env.ModFiles(instance).Length);

        var forced = await env.Manager.RemoveAsync(instance, "fabricapi", force: true);
        Assert.True(forced.Removed, forced.Error);
        Assert.Equal(new[] { "main.jar" }, env.ModFiles(instance));
    }

    [Fact]
    public async Task Removing_a_mod_reports_dependencies_nobody_needs_anymore()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { Required("fabricapi") });
        await InstallAsync(env, instance, "main");

        var kept = await env.Manager.RemoveAsync(instance, "main");

        Assert.True(kept.Removed, kept.Error);
        Assert.Equal("Fabric API", Assert.Single(kept.Orphans).Title);
        Assert.Equal(new[] { "fabric-api.jar" }, env.ModFiles(instance)); // по умолчанию решает пользователь
        Assert.Equal("Fabric API", Assert.Single((await env.Manager.ListAsync(instance)).Mods).Title);

        var cleaned = await env.Manager.RemoveAsync(instance, "fabricapi");
        Assert.True(cleaned.Removed);
        Assert.Empty(env.ModFiles(instance));
    }

    [Fact]
    public async Task Orphans_can_be_removed_together_with_the_mod()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("main", "Main", "m1", "main.jar", "2026-02-01", new[] { Required("fabricapi") });
        await InstallAsync(env, instance, "main");

        var result = await env.Manager.RemoveAsync(instance, "main", removeOrphans: true);

        Assert.True(result.Removed, result.Error);
        Assert.Empty(env.ModFiles(instance));
        Assert.Empty((await env.Manager.ListAsync(instance)).Mods);
    }

    [Fact]
    public async Task A_dependency_still_needed_by_another_mod_is_not_an_orphan()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();
        env.AddMod("fabricapi", "Fabric API", "fa1", "fabric-api.jar", "2026-01-01");
        env.AddMod("one", "One", "o1", "one.jar", "2026-01-01", new[] { Required("fabricapi") });
        env.AddMod("two", "Two", "t1", "two.jar", "2026-01-01", new[] { Required("fabricapi") });
        await InstallAsync(env, instance, "one");
        await InstallAsync(env, instance, "two");

        var result = await env.Manager.RemoveAsync(instance, "one", removeOrphans: true);

        Assert.Empty(result.Orphans);
        Assert.Equal(new[] { "fabric-api.jar", "two.jar" }, env.ModFiles(instance));
    }

    [Fact]
    public async Task Removing_an_unknown_mod_is_an_error_not_a_crash()
    {
        using var env = new Env();
        var instance = await env.NewInstanceAsync();

        var result = await env.Manager.RemoveAsync(instance, "ghost");

        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
    }
}
