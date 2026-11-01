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
        // 1.21 → «21.0.», 1.21.1 → «21.1.», 26.1 → «26.1.»: без нуля игра 1.21 подхватила бы сборки 1.21.1 и новее.
        if (!TryParseRelease(mcVersion, out var major, out var minor)) return Array.Empty<LoaderBuild>();
        var prefix = $"{major}.{minor}.";
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
        if (!IsSafeVersionText(mcVersion) || !IsForgeSupportedGame(mcVersion)) return Array.Empty<LoaderBuild>();
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

    /// <summary>
    /// Разбор номера релиза игры: <c>1.20.4</c> → (20, 4), <c>1.20</c> → (20, 0), <c>26.1</c> → (26, 1).
    /// Снапшоты вроде <c>24w14a</c> не разбираются (false). Суффикс после «-» (<c>1.21.1-pre1</c>) отбрасывается.
    /// </summary>
    public static bool TryParseRelease(string? mcVersion, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrEmpty(mcVersion)) return false;

        var parts = mcVersion.Split('-', 2)[0].Split('.');
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var first)) return false;

        if (first == 1)
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major)) return false;
            if (parts.Length > 2) int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minor);
            return true;
        }

        major = first;
        if (parts.Length > 1) int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minor);
        return true;
    }

    /// <summary>
    /// Forge, который лаунчер умеет ставить: Minecraft 1.12.2 и новее. У более старых профиль без <c>inheritsFrom</c>,
    /// а библиотеки лежат вне <c>maven/</c> установщика. Неразбираемые версии (снапшоты) считаются подходящими.
    /// </summary>
    public static bool IsForgeSupportedGame(string mcVersion) =>
        !TryParseRelease(mcVersion, out var major, out var minor) || major > 12 || major == 12 && minor >= 2;

    /// <summary>
    /// Forge для этой <b>версии игры</b> ставится современным установщиком (1.13 и новее, включая год-формат 26.x и неразбираемые снапшоты).
    /// Раньше здесь был <c>Version.TryParse</c>: на «1.20» (одно число после «1.») он давал false, и Forge 1.13–1.20 уходил в ветку старого Forge.
    /// </summary>
    public static bool IsForgeModernVersion(string mcVersion) => !TryParseRelease(mcVersion, out var major, out _) || major >= 13;

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
