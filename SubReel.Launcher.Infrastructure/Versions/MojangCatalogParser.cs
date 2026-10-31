using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Infrastructure.Versions;

/// <summary>Картинка и краткое описание обновления из патчноутов Mojang.</summary>
public sealed record PatchNote(string? ImageUrl, string? Summary);

/// <summary>Разбор ответов серверов Mojang: version_manifest_v2 и javaPatchNotes. Только разбор, без сети.</summary>
public static class MojangCatalogParser
{
    public const string ContentBase = "https://launchercontent.mojang.com";

    private static readonly Regex TrailingVersion = new(@"(\d+\.\d+(?:\.\d+)?)\s*$", RegexOptions.Compiled);

    /// <summary>version id → картинка и краткое описание.</summary>
    public static Dictionary<string, PatchNote> ParsePatchNotes(string json)
    {
        var result = new Dictionary<string, PatchNote>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in entries.EnumerateArray())
        {
            var version = GetString(entry, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                // Запасной вариант: версия в заголовке («Minecraft Java Edition 1.21.4»).
                var m = TrailingVersion.Match(GetString(entry, "title") ?? "");
                version = m.Success ? m.Groups[1].Value : null;
            }
            if (version is null) continue;

            string? image = null;
            if (entry.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                image = GetString(img, "url");
            var summary = GetString(entry, "shortText");
            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(summary)) continue;

            result.TryAdd(version, new PatchNote(
                string.IsNullOrWhiteSpace(image) ? null : Absolute(image),
                string.IsNullOrWhiteSpace(summary) ? null : summary.Trim()));
        }
        return result;
    }

    public static VersionCatalog BuildCatalog(string manifestJson, IReadOnlyDictionary<string, PatchNote> notes)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var releases = new List<McVersion>();
        foreach (var item in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (GetString(item, "type") != "release") continue;
            var id = GetString(item, "id");
            if (id is null || !VersionCatalog.IsReleaseId(id)) continue;

            DateTimeOffset.TryParse(GetString(item, "releaseTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
            notes.TryGetValue(id, out var note);
            releases.Add(new McVersion { Id = id, ReleaseTime = time, ImageUrl = note?.ImageUrl, Summary = note?.Summary });
        }

        var latest = doc.RootElement.TryGetProperty("latest", out var latestEl) ? GetString(latestEl, "release") : null;
        return VersionCatalog.Create(releases, latest);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Absolute(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url
        : ContentBase + (url.StartsWith('/') ? url : "/" + url);
}
