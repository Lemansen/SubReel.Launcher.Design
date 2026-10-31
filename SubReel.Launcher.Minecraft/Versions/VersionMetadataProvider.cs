using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Versions;

/// <summary>
/// Находит version.json на диске или скачивает его по каталогу Mojang (с проверкой SHA-1), разбирает и склеивает цепочку inheritsFrom.
/// Версии загрузчиков (Fabric, Forge …) появляются на диске после их установки и находятся так же, как ванильные.
/// </summary>
public sealed class VersionMetadataProvider : IVersionMetadataProvider
{
    private const int MaxChainLength = 8;

    private readonly VersionManifestSource _manifest;
    private readonly IDownloadManager _downloads;
    private readonly GameFileLayout _layout;
    private readonly ILogger<VersionMetadataProvider> _logger;

    public VersionMetadataProvider(VersionManifestSource manifest, IDownloadManager downloads, GameFileLayout layout, ILogger<VersionMetadataProvider> logger)
    {
        _manifest = manifest;
        _downloads = downloads;
        _layout = layout;
        _logger = logger;
    }

    public async Task<ResolvedVersion> ResolveAsync(string versionId, bool allowNetwork = true, CancellationToken ct = default)
    {
        var chain = new List<GameVersionMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = versionId;

        while (true)
        {
            if (!seen.Add(current)) throw new VersionMetadataException($"Цепочка inheritsFrom версии «{versionId}» зациклена на «{current}».");
            if (chain.Count >= MaxChainLength) throw new VersionMetadataException($"Цепочка inheritsFrom версии «{versionId}» длиннее {MaxChainLength} звеньев.");

            var metadata = await LoadAsync(current, allowNetwork, ct).ConfigureAwait(false);
            chain.Add(metadata);

            if (string.IsNullOrWhiteSpace(metadata.InheritsFrom)) break;
            current = metadata.InheritsFrom;
        }

        return VersionResolver.Resolve(versionId, chain);
    }

    private async Task<GameVersionMetadata> LoadAsync(string id, bool allowNetwork, CancellationToken ct)
    {
        var path = _layout.VersionJsonPath(id);

        if (File.Exists(path))
        {
            try
            {
                return VersionMetadataParser.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (allowNetwork && (ex is VersionMetadataException or IOException))
            {
                _logger.LogWarning(ex, "Описание версии {Id} на диске не читается, скачиваем заново.", id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new VersionMetadataException($"Не удалось прочитать описание версии «{id}».", ex);
            }
        }

        if (!allowNetwork) throw new VersionMetadataException($"Версия «{id}» не установлена.");

        var entry = await _manifest.FindAsync(id, allowNetwork: true, ct).ConfigureAwait(false) ?? throw new VersionNotFoundException(id);
        var result = await _downloads.DownloadAsync(new DownloadRequest(entry.Url, path, entry.Sha1), null, ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new VersionMetadataException($"Не удалось скачать описание версии «{id}»: {result.Error}");

        try
        {
            return VersionMetadataParser.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VersionMetadataException($"Не удалось прочитать описание версии «{id}».", ex);
        }
    }
}
