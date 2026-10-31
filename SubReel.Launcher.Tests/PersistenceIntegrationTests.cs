using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Settings;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

/// <summary>
/// Сквозные сценарии «пользователь что-то сделал → лаунчер перезапущен → всё на месте». Настоящие SettingsService и JsonFileStore
/// работают на временной папке; сеть и папка игры подменены, поэтому тесты не зависят от интернета и от установленного Minecraft.
/// «Перезапуск» — это новые экземпляры сервисов поверх тех же файлов.
/// </summary>
public sealed class PersistenceIntegrationTests
{
    private static SettingsService OpenSettings(AppPaths paths) =>
        new(paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SettingsService>.Instance);

    private static VersionStore OpenStore(SettingsService settings, FakeInstalledService? installed = null) =>
        new(settings, new FakeCatalogService(), installed ?? new FakeInstalledService(), NullLogger<VersionStore>.Instance);

    [Fact]
    public async Task First_run_then_pick_version_then_restart_keeps_selection_and_recent_list()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);

        // Первый запуск: файла нет, версия не выбрана.
        var first = OpenStore(OpenSettings(paths));
        await first.InitializeAsync();
        Assert.False(File.Exists(paths.SettingsFile));

        first.Select("1.20.1");
        first.Select("1.21");

        // Перезапуск.
        var secondSettings = OpenSettings(paths);
        var second = OpenStore(secondSettings);
        await second.InitializeAsync();

        Assert.Equal("1.21", second.SelectedVersion);
        Assert.Equal(new[] { "1.21", "1.20.1" }, secondSettings.Current.RecentVersions);
    }

    [Fact]
    public async Task Selected_loader_install_survives_restart()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        var fabric = new InstalledVersion { Id = "fabric-loader-0.16.0-1.21.1", McVersion = "1.21.1", Loader = LoaderKind.Fabric };
        FakeInstalledService Installed() => new()
        {
            OnScan = () => Task.FromResult(new InstalledScanResult { GameDirectory = "game", DirectoryFound = true, Versions = new[] { fabric } }),
        };

        var first = OpenStore(OpenSettings(paths), Installed());
        await first.RefreshInstalled();
        first.Select("1.21.1", fabric);

        var second = OpenStore(OpenSettings(paths), Installed());
        await second.InitializeAsync();
        await second.ScanTask;

        Assert.Equal("1.21.1", second.SelectedVersion);
        Assert.Equal(fabric.Id, second.SelectedInstall?.Id);
    }

    [Fact]
    public void Accounts_and_cosmetics_survive_restart()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);

        var accounts = new AccountService(OpenSettings(paths));
        Assert.True(accounts.AddAndActivate("Alex_7"));
        accounts.Active.Cosmetics.NickMode = ColorMode.Solid;
        accounts.Active.Cosmetics.NickColorA = "#FFD166";
        accounts.SaveChanges();

        var restarted = new AccountService(OpenSettings(paths));

        Assert.Equal("Alex_7", restarted.Active.Name);
        Assert.Equal(2, restarted.Accounts.Count);
        Assert.Equal(ColorMode.Solid, restarted.Active.Cosmetics.NickMode);
        Assert.Equal("#FFD166", restarted.Active.Cosmetics.NickColorA);
    }

    [Fact]
    public void Saving_after_a_corrupt_file_never_destroys_the_original()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, dir.Root, dir.Root);
        const string garbage = "{ \"Accounts\": [ { \"Name\": \"Steve_01\" ";
        File.WriteAllText(paths.SettingsFile, garbage);

        // Запуск с повреждённым файлом, затем обычное действие пользователя, которое вызывает Save().
        var accounts = new AccountService(OpenSettings(paths));
        Assert.True(accounts.AddAndActivate("Nova_1"));

        // Новые настройки записаны, а исходный повреждённый файл лежит рядом нетронутым.
        var kept = Directory.GetFiles(dir.Root, "design-settings.json.corrupt-*");
        Assert.Single(kept);
        Assert.Equal(garbage, File.ReadAllText(kept[0]));
        Assert.Equal("Nova_1", new AccountService(OpenSettings(paths)).Active.Name);
    }
}
