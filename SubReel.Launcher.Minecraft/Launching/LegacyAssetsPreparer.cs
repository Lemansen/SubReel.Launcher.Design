using Microsoft.Extensions.Logging;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Minecraft.Launching;

/// <summary>
/// Ассеты старых версий. Индекс с <c>virtual</c> требует копию файлов под исходными именами (<c>assets/virtual/&lt;индекс&gt;</c>),
/// индекс с <c>map_to_resources</c> — копию в папку <c>resources</c> самой игры. Копируются только недостающие или другого размера файлы,
/// поэтому повторный запуск почти ничего не делает. Для современных версий (обычный индекс) ничего не происходит.
/// </summary>
public sealed class LegacyAssetsPreparer
{
    private readonly GameFileLayout _layout;
    private readonly ILogger<LegacyAssetsPreparer> _logger;

    public LegacyAssetsPreparer(GameFileLayout layout, ILogger<LegacyAssetsPreparer> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    /// <summary>Возвращает значение для <c>${game_assets}</c>: папка копии для virtual-индекса, иначе корень ассетов.</summary>
    public Task<string> PrepareAsync(ResolvedVersion version, string gameDirectory, CancellationToken ct = default) =>
        Task.Run(() => Prepare(version, gameDirectory, ct), ct);

    private string Prepare(ResolvedVersion version, string gameDirectory, CancellationToken ct)
    {
        if (version.AssetIndex is not { } indexInfo) return _layout.AssetsRoot;

        var indexPath = _layout.AssetIndexPath(indexInfo.Id);
        if (!File.Exists(indexPath)) throw new FileNotFoundException($"Индекс ассетов «{indexInfo.Id}» не установлен.", indexPath);

        var index = AssetIndexParser.Parse(File.ReadAllText(indexPath));
        var gameAssets = _layout.AssetsRoot;

        if (index.IsVirtual)
        {
            gameAssets = _layout.VirtualAssetsDirectory(indexInfo.Id);
            var copied = CopyAll(index, name => Combine(gameAssets, name), ct);
            if (copied > 0) _logger.LogInformation("Ассеты {Index}: скопировано в virtual {Count} файлов.", indexInfo.Id, copied);
        }

        if (index.MapToResources)
        {
            var resources = Path.Combine(gameDirectory, "resources");
            var copied = CopyAll(index, name => Combine(resources, name), ct);
            if (copied > 0) _logger.LogInformation("Ассеты {Index}: скопировано в resources {Count} файлов.", indexInfo.Id, copied);
        }

        return gameAssets;
    }

    private int CopyAll(AssetIndex index, Func<string, string> destinationOf, CancellationToken ct)
    {
        var copied = 0;
        foreach (var (name, asset) in index.Objects)
        {
            ct.ThrowIfCancellationRequested();

            var source = _layout.AssetObjectPath(asset.Hash);
            var destination = destinationOf(name);
            if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>Имя ресурса приходит из сети: выход за целевую папку («..») — ошибка, а не запись в чужое место.</summary>
    private static string Combine(string root, string name)
    {
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new VersionMetadataException($"Имя ассета «{name}» выходит за пределы папки.");
        return full;
    }
}
