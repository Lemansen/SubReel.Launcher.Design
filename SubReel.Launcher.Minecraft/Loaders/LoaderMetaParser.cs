using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Minecraft.Loaders;

/// <summary>Разбор ответа мета-сервера со списком сборок загрузчика. Только разбор, без диска и сети.</summary>
public static class LoaderMetaParser
{
    private static readonly Regex SafeVersion = new(@"^[0-9A-Za-z][0-9A-Za-z._+\-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Версия игры или загрузчика годится для адреса и имени папки: буквы, цифры и «. _ + -», до 64 знаков.
    /// Версии приходят из сети и из файла сборки, поэтому в адрес и путь они попадают только после этой проверки.
    /// </summary>
    public static bool IsSafeVersionText(string? text) => !string.IsNullOrEmpty(text) && SafeVersion.IsMatch(text);

    /// <summary>
    /// Ответ <c>versions/loader/&lt;игра&gt;</c>: массив объектов, у каждого поле <c>loader</c> с <c>version</c> (и <c>stable</c> у Fabric).
    /// Порядок сохраняется (мета-серверы отдают новые первыми). Нет поля <c>stable</c> (Quilt) — бета узнаётся по суффиксу после «-».
    /// Записи без версии и с недопустимой версией пропускаются; повторы убираются.
    /// </summary>
    public static IReadOnlyList<LoaderBuild> ParseBuilds(string json, LoaderKind kind, string mcVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new LoaderCatalogException("Ответ мета-сервера загрузчика не является списком.");

            var builds = new List<LoaderBuild>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object) continue;

                var version = loader.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                if (!IsSafeVersionText(version) || !seen.Add(version!)) continue;

                var hasFlag = loader.TryGetProperty("stable", out var stableElement) && (stableElement.ValueKind is JsonValueKind.True or JsonValueKind.False);
                var stable = hasFlag ? stableElement.GetBoolean() : !version!.Contains('-');
                builds.Add(new LoaderBuild(kind, mcVersion, version!, stable));
            }
            return builds;
        }
        catch (JsonException ex)
        {
            throw new LoaderCatalogException("Ответ мета-сервера загрузчика не является корректным JSON.", ex);
        }
    }

    /// <summary>NeoForge publishes versions through Maven metadata; the game version is encoded in the first numeric components.</summary>
    public static IReadOnlyList<LoaderBuild> ParseNeoForgeBuilds(string xml, string mcVersion)
    {
        if (!IsSafeVersionText(mcVersion)) return Array.Empty<LoaderBuild>();
        var prefix = (mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion[2..] : mcVersion) + ".";
        try
        {
            var versions = XDocument.Parse(xml).Descendants("version")
                .Select(e => e.Value.Trim())
                .Where(v => IsSafeVersionText(v) && v.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Select(v => new LoaderBuild(LoaderKind.NeoForge, mcVersion, v, !v.Contains('-', StringComparison.Ordinal)))
                .OrderByDescending(b => b.Stable)
                .ThenByDescending(b => b.Version, MavenVersionComparer.Instance)
                .ToList();
            return versions;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new LoaderCatalogException("Ответ Maven NeoForged не является корректным XML.", ex);
        }
    }

    /// <summary>Forge Maven versions use the combined form &lt;minecraft&gt;-&lt;forge&gt;; the installer route depends on the game version.</summary>
    public static IReadOnlyList<LoaderBuild> ParseForgeBuilds(string xml, string mcVersion)
    {
        if (!IsSafeVersionText(mcVersion)) return Array.Empty<LoaderBuild>();
        var prefix = mcVersion + "-";
        try
        {
            return XDocument.Parse(xml).Descendants("version")
                .Select(e => e.Value.Trim())
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal) && IsSafeVersionText(v[prefix.Length..]))
                .Select(v => v[prefix.Length..])
                .Distinct(StringComparer.Ordinal)
                .Select(v => new LoaderBuild(LoaderKind.Forge, mcVersion, v, !v.Contains('-', StringComparison.Ordinal)))
                .OrderByDescending(b => b.Stable)
                .ThenByDescending(b => b.Version, MavenVersionComparer.Instance)
                .ToList();
        }
        catch (System.Xml.XmlException ex)
        {
            throw new LoaderCatalogException("Ответ Maven Forge не является корректным XML.", ex);
        }
    }

    public static bool IsForgeModernVersion(string version)
    {
        var text = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version;
        return Version.TryParse(text, out var parsed) && (parsed.Major > 13 || parsed.Major == 13);
    }

    private sealed class MavenVersionComparer : IComparer<string>
    {
        public static MavenVersionComparer Instance { get; } = new();
        public int Compare(string? x, string? y)
        {
            var left = (x ?? "").Split('-', 2)[0].Split('.');
            var right = (y ?? "").Split('-', 2)[0].Split('.');
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var a = i < left.Length && int.TryParse(left[i], out var av) ? av : -1;
                var b = i < right.Length && int.TryParse(right[i], out var bv) ? bv : -1;
                var cmp = a.CompareTo(b);
                if (cmp != 0) return cmp;
            }
            return string.Compare(x, y, StringComparison.Ordinal);
        }
    }
}
