using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Tests;

public sealed class VersionStoreTests
{
    private static VersionStore Create(FakeSettingsService settings, FakeInstalledService? installed = null) =>
        new(settings, new FakeCatalogService(), installed ?? new FakeInstalledService(), NullLogger<VersionStore>.Instance);

    [Fact]
    public async Task First_run_selects_latest_release_but_offline_catalog_is_not_remembered()
    {
        var settings = new FakeSettingsService();
        var store = Create(settings);

        await store.InitializeAsync();

        Assert.True(store.CatalogLoaded);
        Assert.Equal(store.Catalog.LatestRelease, store.SelectedVersion);
        if (store.Catalog.IsOffline) Assert.Null(settings.Current.SelectedVersion);
    }

    [Fact]
    public async Task Saved_selection_survives_initialization()
    {
        var settings = new FakeSettingsService();
        settings.Current.SelectedVersion = "1.20.1";
        var store = Create(settings);

        await store.InitializeAsync();

        Assert.Equal("1.20.1", store.SelectedVersion);
    }

    [Fact]
    public void Select_keeps_recent_list_unique_newest_first_and_capped_at_six()
    {
        var settings = new FakeSettingsService();
        var store = Create(settings);

        foreach (var id in new[] { "1.16", "1.17", "1.18", "1.19", "1.20", "1.21", "1.22" }) store.Select(id);
        store.Select("1.19");

        Assert.Equal(6, settings.Current.RecentVersions.Count);
        Assert.Equal("1.19", settings.Current.RecentVersions[0]);
        Assert.Equal(settings.Current.RecentVersions.Distinct().Count(), settings.Current.RecentVersions.Count);
        Assert.Equal("1.19", settings.Current.SelectedVersion);
        Assert.True(settings.SaveCount > 0);
    }

    [Fact]
    public void Select_raises_changed_and_selected_events()
    {
        var store = Create(new FakeSettingsService());
        int changed = 0, selected = 0;
        store.Changed += (_, _) => changed++;
        store.VersionSelected += (_, _) => selected++;

        store.Select("1.21.1");

        Assert.Equal(1, changed);
        Assert.Equal(1, selected);
    }

    [Fact]
    public async Task Scan_failure_is_contained_and_reported_as_directory_not_found()
    {
        var installed = new FakeInstalledService { OnScan = () => throw new IOException("disk error") };
        var store = Create(new FakeSettingsService(), installed);

        await store.RefreshInstalled();

        Assert.True(store.InstalledScanned);
        Assert.False(store.GameDirectoryFound);
        Assert.Empty(store.Installed);
    }

    [Fact]
    public async Task Scan_resolves_selected_vanilla_install()
    {
        var installed = new FakeInstalledService
        {
            OnScan = () => Task.FromResult(new InstalledScanResult
            {
                GameDirectory = "game",
                DirectoryFound = true,
                Versions = new[] { new InstalledVersion { Id = "1.21.1", McVersion = "1.21.1", Loader = LoaderKind.Vanilla } },
            }),
        };
        var store = Create(new FakeSettingsService(), installed);
        store.Select("1.21.1");

        await store.RefreshInstalled();

        Assert.Equal("1.21.1", store.SelectedInstall?.Id);
        Assert.Equal("1.21.1", store.VersionLabel);
    }

    [Fact]
    public async Task Cancelled_scan_does_not_touch_state()
    {
        var installed = new FakeInstalledService { OnScan = () => throw new OperationCanceledException() };
        var store = Create(new FakeSettingsService(), installed);

        await store.RefreshInstalled();

        Assert.False(store.InstalledScanned);
    }
}
