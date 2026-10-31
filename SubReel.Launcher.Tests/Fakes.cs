using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.Settings;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Tests;

internal sealed class FakeSettingsService : ISettingsService
{
    public LauncherSettings Current { get; } = new();
    public int SaveCount { get; private set; }
    public void Save() => SaveCount++;
}

internal sealed class FakeCatalogService : IVersionCatalogService
{
    public VersionCatalog CreateFallback() => VersionCatalog.CreateFallback();
    public Task<VersionCatalog> LoadAsync(CancellationToken ct = default) => Task.FromResult(VersionCatalog.CreateFallback());
}

internal sealed class FakeInstalledService : IInstalledVersionService
{
    public string DefaultGameDirectory => "game";
    public Func<Task<InstalledScanResult>> OnScan { get; set; } = () => Task.FromResult(new InstalledScanResult { GameDirectory = "game", DirectoryFound = true });
    public Task<InstalledScanResult> ScanAsync(CancellationToken ct = default) => OnScan();
}
