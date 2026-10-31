using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Versions;

/// <summary>
/// Ищет установленные версии в папке игры (<c>%AppData%\.minecraft\versions</c>). Читает только JSON-описания версий,
/// ничего не скачивает и не меняет. Работает в фоновом потоке и поддерживает отмену.
/// </summary>
public sealed class InstalledVersionService : IInstalledVersionService
{
    private readonly AppPaths _paths;
    private readonly ILogger<InstalledVersionService> _logger;

    public InstalledVersionService(AppPaths paths, ILogger<InstalledVersionService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string DefaultGameDirectory => _paths.DefaultGameDirectory;

    public Task<InstalledScanResult> ScanAsync(CancellationToken ct = default) => ScanAsync(DefaultGameDirectory, ct);

    public Task<InstalledScanResult> ScanAsync(string gameDirectory, CancellationToken ct = default) =>
        Task.Run(() => Scan(gameDirectory, ct), ct);

    private InstalledScanResult Scan(string gameDir, CancellationToken ct)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            _logger.LogInformation("Папка версий {Path} не найдена.", versionsDir);
            return new InstalledScanResult { GameDirectory = gameDir, DirectoryFound = false };
        }

        var found = new List<InstalledVersion>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue; // недокачанная или чужая папка
                try
                {
                    if (InstalledVersionParser.Parse(id, dir, json) is { } version) found.Add(version);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // повреждённая версия не должна скрывать остальные
                    _logger.LogWarning(ex, "Версия {VersionId} пропущена: не удалось прочитать {Path}.", id, json);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // папку не удалось дочитать — показываем то, что нашли
            _logger.LogWarning(ex, "Папка {Path} прочитана не полностью.", versionsDir);
        }

        found.Sort(InstalledVersionParser.Compare);
        _logger.LogInformation("Найдено установленных версий: {Count}.", found.Count);
        return new InstalledScanResult { Versions = found, GameDirectory = gameDir, DirectoryFound = true };
    }
}
