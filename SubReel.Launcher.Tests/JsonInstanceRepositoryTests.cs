using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;

namespace SubReel.Launcher.Tests;

public sealed class JsonInstanceRepositoryTests
{
    private static (JsonInstanceRepository Repo, AppPaths Paths) Create(TempDir dir)
    {
        var paths = new AppPaths(dir.Root, dir.Root);
        return (new JsonInstanceRepository(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<JsonInstanceRepository>.Instance), paths);
    }

    private static Instance Draft(string name = "My Pack") => new() { Name = name, McVersion = "1.21.1", Loader = LoaderKind.Fabric, LoaderVersion = "0.16.5" };

    [Fact]
    public async Task Create_makes_folder_with_game_directory_and_roundtrips_all_fields()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var draft = Draft();
        draft.MinMemoryMb = 1024;
        draft.MaxMemoryMb = 4096;
        draft.JvmArguments = "-XX:+UseG1GC";
        draft.JavaPath = @"C:\Java\bin\java.exe";

        var created = await repo.CreateAsync(draft);

        Assert.Equal("My-Pack", created.Id);
        Assert.True(Directory.Exists(Path.Combine(paths.InstancesDirectory, "My-Pack", "minecraft")));
        Assert.Equal(Path.Combine(paths.InstancesDirectory, "My-Pack", "minecraft"), repo.GetGameDirectory("My-Pack"));

        var loaded = Assert.Single(await repo.ListAsync());
        Assert.Equal("My Pack", loaded.Name);
        Assert.Equal("1.21.1", loaded.McVersion);
        Assert.Equal(LoaderKind.Fabric, loaded.Loader);
        Assert.Equal("0.16.5", loaded.LoaderVersion);
        Assert.Equal(1024, loaded.MinMemoryMb);
        Assert.Equal(4096, loaded.MaxMemoryMb);
        Assert.Equal("-XX:+UseG1GC", loaded.JvmArguments);
        Assert.Equal(@"C:\Java\bin\java.exe", loaded.JavaPath);
    }

    [Fact]
    public async Task Same_folder_name_gets_numeric_suffix_and_does_not_overwrite()
    {
        using var dir = new TempDir();
        var (repo, _) = Create(dir);

        var first = await repo.CreateAsync(Draft("Pack"));
        var second = await repo.CreateAsync(Draft("Pack"));

        Assert.Equal("Pack", first.Id);
        Assert.Equal("Pack-2", second.Id);
        Assert.Equal(2, (await repo.ListAsync()).Count);
    }

    [Fact]
    public async Task Concurrent_creates_get_distinct_folders()
    {
        using var dir = new TempDir();
        var (repo, _) = Create(dir);

        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.CreateAsync(Draft("Same"))));

        Assert.Equal(8, created.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Update_persists_changes_and_keeps_previous_version_as_backup()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var created = await repo.CreateAsync(Draft());

        created.Name = "Renamed";
        created.LastPlayedUtc = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(await repo.UpdateAsync(created));

        var loaded = await repo.GetAsync(created.Id);
        Assert.Equal("Renamed", loaded!.Name);
        Assert.Equal(created.Id, loaded.Id); // папка не переименовывается
        Assert.Equal(created.LastPlayedUtc, loaded.LastPlayedUtc);
        Assert.True(File.Exists(Path.Combine(paths.InstancesDirectory, created.Id, "instance.json.bak")));
    }

    [Fact]
    public async Task Update_of_unknown_instance_returns_false()
    {
        using var dir = new TempDir();
        var (repo, _) = Create(dir);
        var ghost = Draft();
        ghost.Id = "ghost";

        Assert.False(await repo.UpdateAsync(ghost));
        Assert.False(Directory.Exists(Path.Combine(dir.Root, "profiles", "ghost")));
    }

    [Fact]
    public async Task Old_file_without_schema_version_and_unknown_loader_is_still_readable()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var folder = Path.Combine(paths.InstancesDirectory, "legacy");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "instance.json"), "{\"name\":\"Legacy\",\"mcVersion\":\"1.12.2\",\"loader\":\"SomethingNew\"}");

        var loaded = Assert.Single(await repo.ListAsync());
        Assert.Equal("legacy", loaded.Id);
        Assert.Equal("Legacy", loaded.Name);
        Assert.Equal(LoaderKind.Other, loaded.Loader);
    }

    [Fact]
    public async Task File_from_newer_launcher_is_skipped_and_never_overwritten()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var folder = Path.Combine(paths.InstancesDirectory, "future");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "instance.json");
        const string content = "{\"schemaVersion\":99,\"name\":\"Future\",\"mcVersion\":\"2.0\"}";
        await File.WriteAllTextAsync(file, content);

        Assert.Empty(await repo.ListAsync());
        Assert.False(await repo.UpdateAsync(new Instance { Id = "future", Name = "X", McVersion = "1.0" }));
        Assert.Equal(content, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Corrupt_description_is_quarantined_not_deleted_and_other_instances_still_load()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        await repo.CreateAsync(Draft("Good"));
        var badFolder = Path.Combine(paths.InstancesDirectory, "bad");
        Directory.CreateDirectory(badFolder);
        await File.WriteAllTextAsync(Path.Combine(badFolder, "instance.json"), "{ not json");

        var list = await repo.ListAsync();

        Assert.Equal("Good", Assert.Single(list).Name);
        Assert.Contains(Directory.GetFiles(badFolder), f => Path.GetFileName(f).StartsWith("instance.json.corrupt-"));
    }

    [Fact]
    public async Task Folders_without_description_are_not_instances()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        Directory.CreateDirectory(Path.Combine(paths.InstancesDirectory, "random"));

        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Delete_removes_the_whole_folder_including_user_files()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var created = await repo.CreateAsync(Draft());
        var world = Path.Combine(repo.GetGameDirectory(created.Id), "saves", "World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "x");

        Assert.True(await repo.DeleteAsync(created.Id));

        Assert.False(Directory.Exists(Path.Combine(paths.InstancesDirectory, created.Id)));
        Assert.Null(await repo.GetAsync(created.Id));
    }

    [Fact]
    public async Task Delete_refuses_folders_that_are_not_instances()
    {
        using var dir = new TempDir();
        var (repo, paths) = Create(dir);
        var other = Path.Combine(paths.InstancesDirectory, "mine");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "notes.txt"), "keep");

        Assert.False(await repo.DeleteAsync("mine"));
        Assert.True(File.Exists(Path.Combine(other, "notes.txt")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("..\\x")]
    [InlineData("")]
    public async Task Unsafe_ids_never_leave_the_instances_folder(string id)
    {
        using var dir = new TempDir();
        var (repo, _) = Create(dir);
        await File.WriteAllTextAsync(dir.File("instance.json"), "{\"name\":\"X\",\"mcVersion\":\"1\"}");

        Assert.Null(await repo.GetAsync(id));
        Assert.False(await repo.DeleteAsync(id));
        Assert.Throws<ArgumentException>(() => repo.GetGameDirectory(id));
        Assert.True(File.Exists(dir.File("instance.json")));
    }
}
