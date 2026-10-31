using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Settings;
using SubReel.Launcher.Infrastructure.Settings;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

public sealed class SettingsServiceTests
{
    private static SettingsService Create(TempDir dir, out AppPaths paths)
    {
        paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        return new SettingsService(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SettingsService>.Instance);
    }

    private const string LegacyJson = """
        {
          "SelectedVersion": "1.20.1",
          "RecentVersions": ["1.20.1", "1.21"],
          "Accounts": [
            { "Name": "Steve_01", "Kind": "Офлайн", "Cosmetics": { "NickMode": "Solid", "NickColorA": "#FFD166", "Avatar": "Initials" } }
          ],
          "ActiveAccount": "Steve_01"
        }
        """;

    [Fact]
    public void Missing_file_gives_defaults_and_creates_nothing()
    {
        using var dir = new TempDir();
        var service = Create(dir, out var paths);

        Assert.Equal("Gamer", service.Current.ActiveAccount);
        Assert.Equal(LauncherSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void Legacy_file_without_schema_version_keeps_all_user_data_and_is_migrated()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        File.WriteAllText(paths.SettingsFile, LegacyJson);

        var service = Create(dir, out _);

        var account = Assert.Single(service.Current.Accounts);
        Assert.Equal("Steve_01", account.Name);
        Assert.Equal(ColorMode.Solid, account.Cosmetics.NickMode);
        Assert.Equal("#FFD166", account.Cosmetics.NickColorA);
        Assert.Equal(AvatarKind.Initials, account.Cosmetics.Avatar);
        Assert.Equal("1.20.1", service.Current.SelectedVersion);
        Assert.Equal(new[] { "1.20.1", "1.21" }, service.Current.RecentVersions);

        Assert.Equal(LauncherSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.Contains("SchemaVersion", File.ReadAllText(paths.SettingsFile));
        // Исходный файл сохранён как резервная копия.
        var backup = File.ReadAllText(JsonFileStore.BackupPathFor(paths.SettingsFile));
        Assert.DoesNotContain("SchemaVersion", backup);
        Assert.Contains("Steve_01", backup);
    }

    [Fact]
    public void Saved_settings_roundtrip()
    {
        using var dir = new TempDir();
        var first = Create(dir, out _);
        first.Current.SelectedVersion = "1.21.1";
        first.Current.Accounts.Add(new LauncherAccount { Name = "Alex_2", Kind = "Офлайн" });
        first.Save();

        var second = Create(dir, out _);

        Assert.Equal("1.21.1", second.Current.SelectedVersion);
        Assert.Equal(2, second.Current.Accounts.Count);
    }

    [Fact]
    public void Corrupt_file_starts_with_defaults_and_keeps_original_aside()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        File.WriteAllText(paths.SettingsFile, "{oops");

        var service = Create(dir, out _);
        service.Save();

        Assert.Equal("Gamer", service.Current.ActiveAccount);
        var aside = Assert.Single(Directory.GetFiles(dir.Root, "design-settings.json.corrupt-*"));
        Assert.Equal("{oops", File.ReadAllText(aside));
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void Settings_from_newer_launcher_are_never_overwritten()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        const string newer = """{"SchemaVersion": 99, "SelectedVersion": "x", "FutureField": 1}""";
        File.WriteAllText(paths.SettingsFile, newer);

        var service = Create(dir, out _);
        service.Current.SelectedVersion = "changed";
        service.Save();

        Assert.Equal(newer, File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Migrator_repairs_empty_accounts_and_unknown_active_account()
    {
        var settings = new LauncherSettings { SchemaVersion = 0, ActiveAccount = "ghost", Accounts = new List<LauncherAccount>() };

        var changed = LauncherSettingsMigrator.Migrate(settings);

        Assert.True(changed);
        Assert.Single(settings.Accounts);
        Assert.Equal(settings.Accounts[0].Name, settings.ActiveAccount);
        Assert.False(LauncherSettingsMigrator.Migrate(settings));
    }
}
