using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Поиск установленных версий в папке игры.</summary>
public interface IInstalledVersionService
{
    string DefaultGameDirectory { get; }

    Task<InstalledScanResult> ScanAsync(CancellationToken ct = default);
}
