using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Versions;

public sealed record ManifestEntry(string Id, string Type, string Url, string? Sha1);

/// <summary>
/// Список версий Mojang (version_manifest_v2) на диске. Свежий файл (моложе 10 минут) используется как есть, иначе файл обновляется;
/// если сети нет, берётся устаревший, а когда файла нет совсем — ошибка. Разбор — <see cref="Parse"/>.
/// </summary>
public sealed class VersionManifestSource
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly IDownloadManager _downloads;
    private readonly GameFileLayout _layout;
    private readonly TimeProvider _time;
    private readonly ILogger<VersionManifestSource> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public VersionManifestSource(IDownloadManager downloads, GameFileLayout layout, TimeProvider time, ILogger<VersionManifestSource> logger)
    {
        _downloads = downloads;
        _layout = layout;
        _time = time;
        _logger = logger;
    }

    /// <summary>null — такой версии в каталоге нет. <paramref name="allowNetwork"/> = false — только файл на диске.</summary>
    public async Task<ManifestEntry?> FindAsync(string versionId, bool allowNetwork, CancellationToken ct)
    {
        var path = _layout.ManifestPath;
        if (allowNetwork) await RefreshAsync(path, ct).ConfigureAwait(false);

        if (!File.Exists(path)) throw new VersionMetadataException("Список версий Mojang недоступен: нет сети и нет сохранённой копии.");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VersionMetadataException("Не удалось прочитать сохранённый список версий Mojang.", ex);
        }

        return Parse(json).GetValueOrDefault(versionId);
    }

    public static IReadOnlyDictionary<string, ManifestEntry> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new VersionMetadataException("В списке версий Mojang нет раздела versions.");

            var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            foreach (var item in versions.EnumerateArray())
            {
                var id = Text(item, "id");
                var url = Text(item, "url");
                if (id is null || url is null) continue;
                result.TryAdd(id, new ManifestEntry(id, Text(item, "type") ?? "", url, Text(item, "sha1")));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new VersionMetadataException("Список версий Mojang не является корректным JSON.", ex);
        }
    }

    private async Task RefreshAsync(string path, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) < MaxAge) return;

            var result = await _downloads.DownloadAsync(new DownloadRequest(ManifestUrl, path), null, ct).ConfigureAwait(false);
            if (!result.Succeeded) _logger.LogWarning("Не удалось обновить список версий Mojang: {Error}. Используется сохранённая копия, если она есть.", result.Error);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
