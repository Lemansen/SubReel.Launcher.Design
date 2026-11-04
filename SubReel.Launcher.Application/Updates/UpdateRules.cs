using System.Globalization;
using System.Text.Json;

namespace SubReel.Launcher.Application.Updates;

/// <summary>Чистая логика обновлений: разбор версии и манифеста, правило показа баннера. Без сети и диска — проверяется тестами.</summary>
public static class UpdateRules
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxNotesLength = 4000;

    /// <summary>«1.2.0», «v1.2.0», «1.2.0-beta.1», «1.2.0+abc123» → 1.2.0 (суффиксы отбрасываются). Некорректное → null.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out var version)) return null;
        // Version("1.2") и Version("1.2.0") сравниваются как разные: приводим к трём числам.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>Баннер показывается, если версия есть и она не отложена. Отложенная снова видна по истечении срока или когда вышла более новая.</summary>
    public static bool IsBannerVisible(LauncherUpdateInfo? available, string? postponedVersion, DateTime? snoozedUntilUtc, DateTime nowUtc)
    {
        if (available is null) return false;
        if (available.Mandatory) return true;

        var postponed = ParseVersion(postponedVersion);
        if (postponed is null || snoozedUntilUtc is null) return true;
        if (available.Version > postponed) return true;
        return nowUtc >= snoozedUntilUtc.Value;
    }

    /// <summary>
    /// Разбирает манифест. Принимается только https; архив — с узла манифеста или из списка разрешённых. Возвращает null, если версия не новее текущей.
    /// </summary>
    /// <exception cref="UpdateManifestException">Файл повреждён или не проходит проверку.</exception>
    public static LauncherUpdateInfo? ParseManifest(string json, Uri manifestUrl, Version current, IEnumerable<string> additionalHosts)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new UpdateManifestException("Манифест обновлений повреждён.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new UpdateManifestException("Манифест обновлений повреждён.");

            var version = ParseVersion(Str(root, "version")) ?? throw new UpdateManifestException("В манифесте неверно указана версия.");
            if (version <= current) return null;

            var url = Str(root, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new UpdateManifestException("Адрес обновления должен начинаться с https://.");
            var allowed = additionalHosts.Append(manifestUrl.Host);
            if (!allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new UpdateManifestException($"Архив обновления размещён на неразрешённом узле «{uri.Host}».");

            var sha = Str(root, "sha256")?.Trim().ToLowerInvariant();
            if (sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) throw new UpdateManifestException("В манифесте неверно указан SHA-256.");

            long size = 0;
            if (root.TryGetProperty("sizeBytes", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number) sizeElement.TryGetInt64(out size);
            if (size <= 0 || size > MaxArchiveBytes) throw new UpdateManifestException("В манифесте неверно указан размер архива.");

            var mandatory = root.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
            var notes = Str(root, "releaseNotesRu") ?? Str(root, "releaseNotes") ?? Str(root, "releaseNotesEn");
            notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            if (notes is { Length: > MaxNotesLength }) notes = notes[..MaxNotesLength].TrimEnd() + "…";

            return new LauncherUpdateInfo(version, uri.AbsoluteUri, sha, size, mandatory, notes);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " МБ" : Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " КБ";
}
