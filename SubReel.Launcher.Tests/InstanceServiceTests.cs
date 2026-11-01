using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft.Instances;

namespace SubReel.Launcher.Tests;

public sealed class InstanceServiceTests
{
    private static (InstanceService Service, ManualTimeProvider Time, JsonInstanceRepository Repo) Create(TempDir dir)
    {
        var paths = new AppPaths(dir.Root, dir.Root);
        var repo = new JsonInstanceRepository(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<JsonInstanceRepository>.Instance);
        var time = new ManualTimeProvider();
        return (new InstanceService(repo, time, NullLogger<InstanceService>.Instance), time, repo);
    }

    [Fact]
    public async Task Create_trims_input_sets_creation_time_and_ignores_loader_version_for_vanilla()
    {
        using var dir = new TempDir();
        var (service, time, _) = Create(dir);

        var result = await service.CreateAsync("  Survival  ", " 1.21.1 ", LoaderKind.Vanilla, "ignored");

        Assert.True(result.Succeeded);
        Assert.Equal("Survival", result.Instance!.Name);
        Assert.Equal("1.21.1", result.Instance.McVersion);
        Assert.Null(result.Instance.LoaderVersion);
        Assert.Equal(time.Now.UtcDateTime, result.Instance.CreatedUtc);
    }

    [Fact]
    public async Task Create_rejects_invalid_and_duplicate_names_without_touching_disk()
    {
        using var dir = new TempDir();
        var (service, _, repo) = Create(dir);
        await service.CreateAsync("Pack", "1.21.1");

        Assert.Equal(InstanceValidationError.NameEmpty, (await service.CreateAsync("  ", "1.21.1")).Error);
        Assert.Equal(InstanceValidationError.NameDuplicate, (await service.CreateAsync("PACK", "1.20.1")).Error);
        Assert.Equal(InstanceValidationError.McVersionEmpty, (await service.CreateAsync("Other", "")).Error);
        Assert.Single(await repo.ListAsync());
    }

    [Fact]
    public async Task Rename_changes_title_but_not_folder_and_rejects_taken_name()
    {
        using var dir = new TempDir();
        var (service, _, repo) = Create(dir);
        var a = (await service.CreateAsync("Alpha", "1.21.1")).Instance!;
        await service.CreateAsync("Beta", "1.21.1");

        Assert.True((await service.RenameAsync(a.Id, "Gamma")).Succeeded);
        Assert.Equal("Gamma", (await repo.GetAsync(a.Id))!.Name);

        var conflict = await service.RenameAsync(a.Id, "beta");
        Assert.False(conflict.Succeeded);
        Assert.Equal(InstanceValidationError.NameDuplicate, conflict.Error);
        Assert.Equal("Gamma", (await repo.GetAsync(a.Id))!.Name);
    }

    [Fact]
    public async Task Launch_settings_are_saved_and_invalid_memory_is_not()
    {
        using var dir = new TempDir();
        var (service, _, repo) = Create(dir);
        var id = (await service.CreateAsync("Pack", "1.21.1")).Instance!.Id;

        var ok = await service.UpdateLaunchSettingsAsync(id, 1024, 4096, " -Xss2M ", "  ");
        Assert.True(ok.Succeeded);
        var saved = (await repo.GetAsync(id))!;
        Assert.Equal(4096, saved.MaxMemoryMb);
        Assert.Equal("-Xss2M", saved.JvmArguments);
        Assert.Null(saved.JavaPath);

        var bad = await service.UpdateLaunchSettingsAsync(id, 8192, 2048, "", null);
        Assert.Equal(InstanceValidationError.MemoryInvalid, bad.Error);
        Assert.Equal(4096, (await repo.GetAsync(id))!.MaxMemoryMb);
    }

    [Fact]
    public async Task MarkPlayed_stores_current_time()
    {
        using var dir = new TempDir();
        var (service, time, repo) = Create(dir);
        var id = (await service.CreateAsync("Pack", "1.21.1")).Instance!.Id;

        await service.MarkPlayedAsync(id);

        Assert.Equal(time.Now.UtcDateTime, (await repo.GetAsync(id))!.LastPlayedUtc);
    }

    [Fact]
    public async Task Changing_unknown_instance_reports_storage_failure_and_delete_works()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir);

        Assert.True((await service.RenameAsync("nope", "X")).StorageFailed);

        var id = (await service.CreateAsync("Pack", "1.21.1")).Instance!.Id;
        Assert.True(await service.DeleteAsync(id));
        Assert.Empty(await service.ListAsync());
    }
}
