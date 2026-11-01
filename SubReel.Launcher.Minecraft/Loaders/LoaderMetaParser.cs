using System.Text.Json;
using System.Text.RegularExpressions;
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
}
