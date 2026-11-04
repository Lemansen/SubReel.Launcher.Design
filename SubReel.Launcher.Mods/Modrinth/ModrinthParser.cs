using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubReel.Launcher.Application.Mods;

namespace SubReel.Launcher.Mods.Modrinth;

/// <summary>
/// Разбор ответов Modrinth API v2. Ответы приходят из сети, поэтому ничему не доверяем: имя файла должно быть простым именем .jar,
/// адрес — https на узле Modrinth, хэши — настоящими hex-строками. Версия с негодным файлом пропускается, а не роняет весь список.
/// </summary>
internal static class ModrinthParser
{
    /// <summary>Файлы модов лежат только на CDN Modrinth.</summary>
    public const string CdnHost = "cdn.modrinth.com";

    private const int MaxFileNameLength = 150;

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Идентификатор проекта или версии (или slug) безопасен для подстановки в адрес.</summary>
    public static bool IsSafeId(string? id) => id is not null && IdPattern.IsMatch(id);

    private static readonly Regex VersionTextPattern = new(@"^[A-Za-z0-9._\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Версия игры (1.21.1, 24w14a, 1.21-pre1) безопасна для подстановки в запрос.</summary>
    public static bool IsSafeVersionText(string? text) => text is not null && VersionTextPattern.IsMatch(text);

    public static JsonDocument Open(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new ModCatalogException("Ответ Modrinth не читается. Попробуй ещё раз позже.", ex);
        }
    }

    public static ModSearchResult ParseSearch(string json)
    {
        using var document = Open(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ModCatalogException("Ответ Modrinth не читается. Попробуй ещё раз позже.");

        var hits = new List<ModProject>();
        if (root.TryGetProperty("hits", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var project = ReadSearchHit(element);
                if (project is not null) hits.Add(project);
            }
        }

        var total = root.TryGetProperty("total_hits", out var totalElement) && totalElement.TryGetInt32(out var t) ? t : hits.Count;
        return new ModSearchResult(hits, Math.Max(total, hits.Count));
    }

    public static IReadOnlyList<ModProject> ParseProjects(string json)
    {
        using var document = Open(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ModCatalogException("Ответ Modrinth не читается. Попробуй ещё раз позже.");

        var projects = new List<ModProject>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = Str(element, "id");
            if (!IsSafeId(id)) continue;
            projects.Add(new ModProject(
                id!, Str(element, "slug") ?? id!, Str(element, "title") ?? id!, Str(element, "description") ?? "",
                "", Long(element, "downloads"), Str(element, "icon_url")));
        }
        return projects;
    }

    public static IReadOnlyList<ModVersion> ParseVersions(string json)
    {
        using var document = Open(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ModCatalogException("Ответ Modrinth не читается. Попробуй ещё раз позже.");

        var versions = new List<ModVersion>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var version = ReadVersion(element);
            if (version is not null) versions.Add(version);
        }
        return versions.OrderByDescending(v => v.PublishedUtc).ToList();
    }

    public static ModVersion? ParseVersion(string json)
    {
        using var document = Open(json);
        return ReadVersion(document.RootElement);
    }

    /// <summary>Ответ «хэш → версия» (<c>/version_files</c>, <c>/version_files/update</c>): ключи — хэши из запроса, приведённые к нижнему регистру.</summary>
    public static IReadOnlyDictionary<string, ModVersion> ParseHashMap(string json)
    {
        using var document = Open(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ModCatalogException("Ответ Modrinth не читается. Попробуй ещё раз позже.");

        var map = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var version = ReadVersion(property.Value);
            if (version is not null) map[property.Name.ToLowerInvariant()] = version;
        }
        return map;
    }

    private static ModProject? ReadSearchHit(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = Str(element, "project_id");
        if (!IsSafeId(id)) return null;
        var slug = Str(element, "slug");
        return new ModProject(
            id!,
            IsSafeId(slug) ? slug! : id!,
            Str(element, "title") ?? id!,
            Str(element, "description") ?? "",
            Str(element, "author") ?? "",
            Long(element, "downloads"),
            Str(element, "icon_url"));
    }

    private static ModVersion? ReadVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var id = Str(element, "id");
        var projectId = Str(element, "project_id");
        if (!IsSafeId(id) || !IsSafeId(projectId)) return null;

        var file = ReadFile(element);
        if (file is null) return null;

        var type = Str(element, "version_type") switch
        {
            "beta" => ModReleaseType.Beta,
            "alpha" => ModReleaseType.Alpha,
            _ => ModReleaseType.Release,
        };

        var published = DateTime.TryParse(Str(element, "date_published"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTime.MinValue;

        var number = Str(element, "version_number") ?? id!;
        return new ModVersion(id!, projectId!, Str(element, "name") ?? number, number, type, published, ReadDependencies(element), file);
    }

    private static IReadOnlyList<ModDependency> ReadDependencies(JsonElement version)
    {
        var result = new List<ModDependency>();
        if (!version.TryGetProperty("dependencies", out var array) || array.ValueKind != JsonValueKind.Array) return result;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var kind = Str(element, "dependency_type") switch
            {
                "required" => ModDependencyKind.Required,
                "optional" => ModDependencyKind.Optional,
                "incompatible" => ModDependencyKind.Incompatible,
                "embedded" => ModDependencyKind.Embedded,
                _ => (ModDependencyKind?)null,
            };
            if (kind is null) continue;

            var projectId = Str(element, "project_id");
            var versionId = Str(element, "version_id");
            if (!IsSafeId(projectId)) projectId = null;
            if (!IsSafeId(versionId)) versionId = null;
            if (projectId is null && versionId is null) continue;

            result.Add(new ModDependency(projectId, versionId, kind.Value));
        }
        return result;
    }

    /// <summary>Основной файл версии: помеченный <c>primary</c>, иначе первый. Негодные файлы пропускаются; нет годного — версия не нужна.</summary>
    private static ModFile? ReadFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;

        ModFile? first = null;
        foreach (var element in files.EnumerateArray())
        {
            var file = ReadOneFile(element);
            if (file is null) continue;
            if (element.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True) return file;
            first ??= file;
        }
        return first;
    }

    private static ModFile? ReadOneFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var name = Str(element, "filename");
        var url = Str(element, "url");
        if (!IsSafeFileName(name) || !IsTrustedUrl(url)) return null;

        if (!element.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) return null;
        var sha1 = Str(hashes, "sha1")?.ToLowerInvariant();
        var sha512 = Str(hashes, "sha512")?.ToLowerInvariant();
        if (!IsHex(sha1, 40) || !IsHex(sha512, 128)) return null;

        var size = Long(element, "size");
        if (size <= 0) return null;

        return new ModFile(name!, url!, sha1!, sha512!, size);
    }

    /// <summary>Простое имя файла .jar: без путей, без служебных символов.</summary>
    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength) return false;
        if (name.StartsWith('.') || name != name.Trim()) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) return false;
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, CdnHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
