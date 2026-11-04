using System.Globalization;
using System.Text.Json;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Mods.Modrinth;

namespace SubReel.Launcher.Mods.CurseForge;

/// <summary>
/// Разбор ответов CurseForge API v1 (обёртка <c>{"data": ...}</c>). Файл без адреса скачивания — автор запретил раздачу сторонним программам:
/// такие файлы пропускаются, обходных адресов лаунчер не строит. Хэш у CurseForge только SHA-1 (SHA-512 нет).
/// </summary>
internal static class CurseForgeParser
{
    public const int GameIdMinecraft = 432;
    public const int ClassIdMods = 6;

    private static readonly string[] TrustedHosts = { "edge.forgecdn.net", "mediafilez.forgecdn.net" };

    public static int? LoaderType(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null,
    };

    /// <summary>Quilt загружает и моды Fabric.</summary>
    public static IReadOnlyList<int> LoaderTypes(LoaderKind kind) =>
        kind == LoaderKind.Quilt ? new[] { 5, 4 } : LoaderType(kind) is { } t ? new[] { t } : Array.Empty<int>();

    private static JsonElement Data(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }).RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) return data;
        }
        catch (JsonException ex)
        {
            throw new ModCatalogException("Ответ CurseForge не читается. Попробуй ещё раз позже.", ex);
        }
        throw new ModCatalogException("Ответ CurseForge не читается. Попробуй ещё раз позже.");
    }

    public static ModSearchResult ParseSearch(string json)
    {
        var data = Data(json);
        var hits = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(ReadProject).OfType<ModProject>().ToList() : new List<ModProject>();
        var total = hits.Count;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pagination", out var p) && p.TryGetProperty("totalCount", out var t) && t.TryGetInt32(out var n)) total = Math.Max(n, hits.Count);
        }
        catch (JsonException)
        {
            // totalCount — украшение; список уже разобран
        }
        return new ModSearchResult(hits, total);
    }

    public static IReadOnlyList<ModProject> ParseProjects(string json)
    {
        var data = Data(json);
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(ReadProject).OfType<ModProject>().ToList() : new List<ModProject>();
    }

    public static IReadOnlyList<ModVersion> ParseFiles(string json)
    {
        var data = Data(json);
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<ModVersion>();
        return data.EnumerateArray().Select(ReadFile).OfType<ModVersion>().OrderByDescending(v => v.PublishedUtc).ToList();
    }

    private static ModProject? ReadProject(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || id <= 0) return null;
        var author = e.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 ? Str(a[0], "name") : null;
        string? icon = e.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object ? Str(logo, "thumbnailUrl") : null;
        long downloads = e.TryGetProperty("downloadCount", out var d) && d.TryGetDouble(out var dc) ? (long)dc : 0;
        var idText = id.ToString(CultureInfo.InvariantCulture);
        return new ModProject(idText, Str(e, "slug") ?? idText, Str(e, "name") ?? idText, Str(e, "summary") ?? "", author ?? "", downloads, icon);
    }

    private static ModVersion? ReadFile(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) return null;
        if (!e.TryGetProperty("modId", out var modEl) || !modEl.TryGetInt64(out var modId) || modId <= 0) return null;

        var name = Str(e, "fileName");
        var url = Str(e, "downloadUrl");
        if (!IsSafePackFileName(name) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;

        string? sha1 = null;
        if (e.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
            foreach (var h in hashes.EnumerateArray())
                if (h.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var al) && al == 1) sha1 = Str(h, "value")?.ToLowerInvariant();
        if (sha1 is not { Length: 40 } || !sha1.All(Uri.IsHexDigit)) return null;

        var size = e.TryGetProperty("fileLength", out var len) && len.TryGetInt64(out var l) ? l : 0;
        if (size <= 0) return null;

        var type = e.TryGetProperty("releaseType", out var rt) && rt.TryGetInt32(out var r) ? r switch { 2 => ModReleaseType.Beta, 3 => ModReleaseType.Alpha, _ => ModReleaseType.Release } : ModReleaseType.Release;
        var date = DateTime.TryParse(Str(e, "fileDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

        var deps = new List<ModDependency>();
        if (e.TryGetProperty("dependencies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var dep in arr.EnumerateArray())
            {
                if (!dep.TryGetProperty("modId", out var m) || !m.TryGetInt64(out var depId) || depId <= 0) continue;
                var kind = dep.TryGetProperty("relationType", out var rel) && rel.TryGetInt32(out var rk)
                    ? rk switch { 3 => ModDependencyKind.Required, 2 => ModDependencyKind.Optional, 5 => ModDependencyKind.Incompatible, 1 => ModDependencyKind.Embedded, _ => (ModDependencyKind?)null }
                    : null;
                if (kind is not null) deps.Add(new ModDependency(depId.ToString(CultureInfo.InvariantCulture), null, kind.Value));
            }

        var fileId = id.ToString(CultureInfo.InvariantCulture);
        return new ModVersion(fileId, modId.ToString(CultureInfo.InvariantCulture), Str(e, "displayName") ?? name!, Str(e, "displayName") ?? fileId, type, date, deps,
            new ModFile(name!, url!, sha1, null, size));
    }

    /// <summary>Файл мода (.jar) или ресурспака в сборке (.zip). Остальное — не принимаем.</summary>
    public static bool IsSafePackFileName(string? name) =>
        ModrinthParser.IsSafeFileName(name)
        || (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && ModrinthParser.IsSafeFileName(name[..^4] + ".jar"));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
