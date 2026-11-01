using System.Text.Json;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

public sealed class DataLocationServiceTests
{
    // Раскладка как у настоящего пользователя: кэш лежал прямо в корне по умолчанию, настройки — в отдельном «роуминговом» корне.
    private static string DefaultRoot(TempDir dir) => dir.File("default");
    private static string LegacyRoaming(TempDir dir) => dir.File("roaming");

    private static DataLocationService Start(TempDir dir) =>
        DataLocationService.Start(DefaultRoot(dir), LegacyRoaming(dir), DefaultRoot(dir));

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ───────────────────────── Запуск без выбора ─────────────────────────

    [Fact]
    public void Fresh_start_uses_the_default_root_with_nothing_pending()
    {
        using var dir = new TempDir();

        var location = Start(dir);

        Assert.Equal(DefaultRoot(dir), location.CurrentRoot);
        Assert.Equal(location.DefaultRoot, location.CurrentRoot);
        Assert.False(location.IsCustom);
        Assert.Null(location.PendingRoot);
        Assert.False(location.FellBackToDefault);
        Assert.Empty(location.StartupNotes);
    }

    [Fact]
    public void Old_layout_is_moved_into_the_three_zones_without_losing_files()
    {
        using var dir = new TempDir();
        var roaming = LegacyRoaming(dir);
        var local = DefaultRoot(dir);
        Write(Path.Combine(roaming, "design-settings.json"), "settings");
        Write(Path.Combine(roaming, "design-settings.json.bak"), "backup");
        Write(Path.Combine(roaming, "avatars", "steve.png"), "png");
        Write(Path.Combine(roaming, "instances", "pack", "instance.json"), "instance");
        Write(Path.Combine(local, "cache", "news.json"), "news");
        Write(Path.Combine(local, "logs", "launcher-1.log"), "log");
        Write(Path.Combine(local, "auth", "p.bin"), "token");
        Write(Path.Combine(local, "game", "versions", "1.21.1", "1.21.1.json"), "version");
        Write(Path.Combine(local, "runtimes", "java-runtime-delta", "bin", "java.exe"), "java");

        var location = Start(dir);
        var paths = new AppPaths(location.CurrentRoot);

        Assert.Equal("settings", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("backup", File.ReadAllText(paths.SettingsFile + ".bak"));
        Assert.Equal("png", File.ReadAllText(Path.Combine(paths.AvatarsDirectory, "steve.png")));
        Assert.Equal("instance", File.ReadAllText(Path.Combine(paths.InstancesDirectory, "pack", "instance.json")));
        Assert.Equal("news", File.ReadAllText(paths.NewsCacheFile));
        Assert.Equal("log", File.ReadAllText(Path.Combine(paths.LogsDirectory, "launcher-1.log")));
        Assert.Equal("token", File.ReadAllText(Path.Combine(paths.AuthDirectory, "p.bin")));
        Assert.Equal("version", File.ReadAllText(Path.Combine(paths.GameDataDirectory, "versions", "1.21.1", "1.21.1.json")));
        Assert.Equal("java", File.ReadAllText(Path.Combine(paths.RuntimesDirectory, "java-runtime-delta", "bin", "java.exe")));

        // Из старых мест всё ушло.
        Assert.False(File.Exists(Path.Combine(roaming, "design-settings.json")));
        Assert.False(Directory.Exists(Path.Combine(roaming, "instances")));
        Assert.False(Directory.Exists(Path.Combine(local, "game")));
        Assert.False(Directory.Exists(Path.Combine(local, "cache")));
    }

    [Fact]
    public void Second_start_does_not_repeat_the_migration()
    {
        using var dir = new TempDir();
        Write(Path.Combine(LegacyRoaming(dir), "design-settings.json"), "settings");
        Start(dir);

        var again = Start(dir);

        Assert.Empty(again.StartupNotes);
        Assert.Equal("settings", File.ReadAllText(new AppPaths(again.CurrentRoot).SettingsFile));
    }

    [Fact]
    public void Migration_never_overwrites_files_already_in_the_new_layout()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(DefaultRoot(dir));
        Write(paths.SettingsFile, "new settings");
        Write(Path.Combine(LegacyRoaming(dir), "design-settings.json"), "old settings");

        Start(dir);

        Assert.Equal("new settings", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(LegacyRoaming(dir), "design-settings.json")));
    }

    // ───────────────────────── Выбор папки ─────────────────────────

    [Fact]
    public void Choosing_a_folder_applies_only_after_restart_and_moves_all_zones()
    {
        using var dir = new TempDir();
        var oldPaths = new AppPaths(DefaultRoot(dir));
        Write(oldPaths.SettingsFile, "settings");
        Write(Path.Combine(oldPaths.GameDataDirectory, "versions", "v.json"), "version");
        Write(Path.Combine(oldPaths.InstancesDirectory, "pack", "instance.json"), "instance");
        var target = dir.File("disk-d");

        var first = Start(dir);
        var change = first.Change(target);

        Assert.True(change.Succeeded);
        Assert.Equal(target, change.Root);
        Assert.Equal(target, first.PendingRoot);
        Assert.Equal(DefaultRoot(dir), first.CurrentRoot); // в этом сеансе ничего не переехало
        Assert.True(File.Exists(oldPaths.SettingsFile));

        var restarted = Start(dir);
        var newPaths = new AppPaths(restarted.CurrentRoot);

        Assert.Equal(target, restarted.CurrentRoot);
        Assert.True(restarted.IsCustom);
        Assert.Null(restarted.PendingRoot);
        Assert.Equal("settings", File.ReadAllText(newPaths.SettingsFile));
        Assert.Equal("version", File.ReadAllText(Path.Combine(newPaths.GameDataDirectory, "versions", "v.json")));
        Assert.Equal("instance", File.ReadAllText(Path.Combine(newPaths.InstancesDirectory, "pack", "instance.json")));
        Assert.False(Directory.Exists(oldPaths.LauncherDirectory));
        Assert.False(Directory.Exists(oldPaths.SharedDirectory));
        Assert.False(Directory.Exists(oldPaths.InstancesDirectory));

        // Перенос выполняется один раз.
        Assert.Empty(Start(dir).StartupNotes);
    }

    [Fact]
    public void Foreign_non_empty_folder_gets_a_subfolder_so_launcher_data_is_not_mixed_with_other_files()
    {
        using var dir = new TempDir();
        var foreign = dir.File("documents");
        Write(Path.Combine(foreign, "report.docx"));
        var location = Start(dir);

        var change = location.Change(foreign);

        Assert.True(change.Succeeded);
        Assert.Equal(Path.Combine(foreign, "SubReel"), change.Root);
        Assert.Equal(Path.Combine(foreign, "SubReel"), location.PendingRoot);
    }

    [Fact]
    public void Existing_launcher_folder_is_used_as_is()
    {
        using var dir = new TempDir();
        var existing = dir.File("old-install");
        Directory.CreateDirectory(Path.Combine(existing, AppPaths.LauncherZone));
        var location = Start(dir);

        var change = location.Change(existing);

        Assert.Equal(existing, change.Root);
    }

    [Fact]
    public void Choosing_the_current_folder_cancels_a_pending_change()
    {
        using var dir = new TempDir();
        var location = Start(dir);
        location.Change(dir.File("disk-d"));
        Assert.NotNull(location.PendingRoot);

        var back = location.Change(DefaultRoot(dir));

        Assert.True(back.Succeeded);
        Assert.Null(location.PendingRoot);
        Assert.Equal(DefaultRoot(dir), Start(dir).CurrentRoot);
    }

    [Fact]
    public void Reset_to_default_moves_data_back_after_restart()
    {
        using var dir = new TempDir();
        var custom = dir.File("disk-d");
        Start(dir).Change(custom);
        var inCustom = Start(dir);
        Write(new AppPaths(inCustom.CurrentRoot).SettingsFile, "settings");

        var reset = inCustom.ResetToDefault();
        Assert.True(reset.Succeeded);
        Assert.Equal(DefaultRoot(dir), inCustom.PendingRoot);

        var back = Start(dir);

        Assert.Equal(DefaultRoot(dir), back.CurrentRoot);
        Assert.False(back.IsCustom);
        Assert.Equal("settings", File.ReadAllText(new AppPaths(back.CurrentRoot).SettingsFile));
        Assert.False(Directory.Exists(Path.Combine(custom, AppPaths.LauncherZone)));
    }

    [Fact]
    public void Invalid_choices_are_rejected_and_change_nothing()
    {
        using var dir = new TempDir();
        var location = Start(dir);
        Directory.CreateDirectory(dir.File(AppPaths.LauncherZone)); // родитель по умолчанию выглядит как корень данных

        Assert.Equal(DataLocationError.NotAbsolute, location.Change(Path.Combine("relative", "folder")).Error);
        Assert.Equal(DataLocationError.NotAbsolute, location.Change("  ").Error);
        Assert.Equal(DataLocationError.NestedInCurrent, location.Change(Path.Combine(DefaultRoot(dir), "inner")).Error);
        Assert.Equal(DataLocationError.ContainsCurrent, location.Change(dir.Root).Error);
        Assert.Null(location.PendingRoot);
    }

    [Fact]
    public void Folder_that_cannot_be_created_is_rejected()
    {
        using var dir = new TempDir();
        var blocker = dir.File("file.txt");
        Write(blocker);
        var location = Start(dir);

        var change = location.Change(Path.Combine(blocker, "SubReel")); // «внутри файла» создать нельзя

        Assert.False(change.Succeeded);
        Assert.Equal(DataLocationError.NotWritable, change.Error);
        Assert.Null(location.PendingRoot);
    }

    // ───────────────────────── Сбои ─────────────────────────

    [Fact]
    public void Unavailable_chosen_folder_falls_back_to_default_for_the_session_and_keeps_the_choice()
    {
        using var dir = new TempDir();
        var blocker = dir.File("file.txt");
        Write(blocker);
        var unreachable = Path.Combine(blocker, "SubReel");
        Directory.CreateDirectory(DefaultRoot(dir));
        File.WriteAllText(Path.Combine(DefaultRoot(dir), "location.json"), JsonSerializer.Serialize(new { Root = unreachable }));

        var location = Start(dir);

        Assert.True(location.FellBackToDefault);
        Assert.Equal(DefaultRoot(dir), location.CurrentRoot);
        Assert.Equal(unreachable, location.PendingRoot); // выбор не потерян: подключим диск — заработает
        Assert.NotEmpty(location.StartupNotes);
    }

    [Fact]
    public void Corrupt_pointer_file_starts_with_default_root()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(DefaultRoot(dir));
        File.WriteAllText(Path.Combine(DefaultRoot(dir), "location.json"), "{ не json");

        var location = Start(dir);

        Assert.Equal(DefaultRoot(dir), location.CurrentRoot);
        Assert.NotEmpty(location.StartupNotes);
    }

    [Fact]
    public void Non_empty_target_zone_is_left_alone_and_the_old_data_stays_in_place()
    {
        using var dir = new TempDir();
        var oldPaths = new AppPaths(DefaultRoot(dir));
        Write(oldPaths.SettingsFile, "old");
        var target = dir.File("disk-d");
        var targetPaths = new AppPaths(target);
        Write(targetPaths.SettingsFile, "already here");
        Start(dir).Change(target);

        var restarted = Start(dir);

        Assert.Equal(target, restarted.CurrentRoot);
        Assert.Equal("already here", File.ReadAllText(targetPaths.SettingsFile));
        Assert.Equal("old", File.ReadAllText(oldPaths.SettingsFile));
    }
}

public sealed class StorageMoverTests
{
    [Fact]
    public void Directory_is_moved_with_all_contents()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File(Path.Combine("src", "sub")));
        File.WriteAllText(dir.File(Path.Combine("src", "sub", "a.txt")), "a");

        var moved = StorageMover.MoveDirectory(dir.File("src"), dir.File(Path.Combine("out", "dst")), out var problem);

        Assert.True(moved);
        Assert.Null(problem);
        Assert.Equal("a", File.ReadAllText(dir.File(Path.Combine("out", "dst", "sub", "a.txt"))));
        Assert.False(Directory.Exists(dir.File("src")));
    }

    [Fact]
    public void Existing_destination_is_never_overwritten()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("src"));
        File.WriteAllText(dir.File(Path.Combine("src", "a.txt")), "source");
        Directory.CreateDirectory(dir.File("dst"));
        File.WriteAllText(dir.File(Path.Combine("dst", "a.txt")), "destination");

        var moved = StorageMover.MoveDirectory(dir.File("src"), dir.File("dst"), out var problem);

        Assert.False(moved);
        Assert.NotNull(problem);
        Assert.Equal("source", File.ReadAllText(dir.File(Path.Combine("src", "a.txt"))));
        Assert.Equal("destination", File.ReadAllText(dir.File(Path.Combine("dst", "a.txt"))));
    }

    [Fact]
    public void Missing_source_is_not_an_error()
    {
        using var dir = new TempDir();

        Assert.True(StorageMover.MoveDirectory(dir.File("none"), dir.File("dst"), out var problem));
        Assert.Null(problem);
        Assert.True(StorageMover.MoveFile(dir.File("none.txt"), dir.File("dst.txt"), out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void File_is_moved_and_existing_file_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.txt"), "a");
        File.WriteAllText(dir.File("b.txt"), "b");

        Assert.True(StorageMover.MoveFile(dir.File("a.txt"), dir.File(Path.Combine("sub", "a.txt")), out _));
        Assert.Equal("a", File.ReadAllText(dir.File(Path.Combine("sub", "a.txt"))));

        File.WriteAllText(dir.File("c.txt"), "c");
        Assert.False(StorageMover.MoveFile(dir.File("c.txt"), dir.File("b.txt"), out var problem));
        Assert.NotNull(problem);
        Assert.Equal("b", File.ReadAllText(dir.File("b.txt")));
        Assert.True(File.Exists(dir.File("c.txt")));
    }
}
