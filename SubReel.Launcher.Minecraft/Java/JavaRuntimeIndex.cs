using System.Text.Json;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Minecraft.Java;

/// <summary>Ссылка на описание файлов рантайма (manifest) из общего списка Mojang.</summary>
public sealed record RuntimeManifestRef(string Url, string? Sha1, long? Size, string? VersionName);

public enum RuntimeFileKind { Directory, File, Link }

public sealed record RuntimeFileEntry(string Path, RuntimeFileKind Kind, FileDownloadInfo? Raw, bool Executable, string? LinkTarget);

/// <summary>Разбор списка рантаймов Mojang (all.json) и описания файлов одного рантайма. Только разбор, без сети и диска.</summary>
public static class JavaRuntimeIndex
{
    /// <summary>Ключ платформы в списке Mojang. null — для этой платформы Mojang рантайм не публикует (например, Linux ARM).</summary>
    public static string? PlatformKey(OsEnvironment environment) => (environment.OsName, environment.Arch) switch
    {
        ("windows", "x86_64") => "windows-x64",
        ("windows", "x86") => "windows-x86",
        ("windows", "arm64") => "windows-arm64",
        ("linux", "x86_64") => "linux",
        ("linux", "x86") => "linux-i386",
        ("osx", "x86_64") => "mac-os",
        ("osx", "arm64") => "mac-os-arm64",
        _ => null,
    };

    /// <summary>null — такого компонента для платформы нет.</summary>
    public static RuntimeManifestRef? FindComponent(string indexJson, string platformKey, string component)
    {
        try
        {
            using var doc = JsonDocument.Parse(indexJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JavaRuntimeException("Список Java-рантаймов должен быть JSON-объектом.");

            if (!doc.RootElement.TryGetProperty(platformKey, out var platform) || platform.ValueKind != JsonValueKind.Object) return null;
            if (!platform.TryGetProperty(component, out var versions) || versions.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in versions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object) continue;

                var url = Text(manifest, "url");
                if (url is null) continue;

                string? versionName = null;
                if (item.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object) versionName = Text(version, "name");
                return new RuntimeManifestRef(url, Text(manifest, "sha1"), Number(manifest, "size"), versionName);
            }
            return null;
        }
        catch (JsonException ex)
        {
            throw new JavaRuntimeException("Список Java-рантаймов не является корректным JSON.", ex);
        }
    }

    public static IReadOnlyList<RuntimeFileEntry> ParseManifest(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                throw new JavaRuntimeException("В описании рантайма нет раздела files.");

            var result = new List<RuntimeFileEntry>();
            foreach (var property in files.EnumerateObject())
            {
                var entry = property.Value;
                var type = Text(entry, "type");
                switch (type)
                {
                    case "directory":
                        result.Add(new RuntimeFileEntry(property.Name, RuntimeFileKind.Directory, null, false, null));
                        break;
                    case "link":
                        var target = Text(entry, "target") ?? throw new JavaRuntimeException($"У ссылки «{property.Name}» нет цели.");
                        result.Add(new RuntimeFileEntry(property.Name, RuntimeFileKind.Link, null, false, target));
                        break;
                    case "file":
                        if (!entry.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("raw", out var raw) || raw.ValueKind != JsonValueKind.Object)
                            throw new JavaRuntimeException($"У файла «{property.Name}» нет несжатой копии для скачивания.");
                        var url = Text(raw, "url") ?? throw new JavaRuntimeException($"У файла «{property.Name}» нет адреса.");
                        var executable = entry.TryGetProperty("executable", out var exec) && exec.ValueKind == JsonValueKind.True;
                        result.Add(new RuntimeFileEntry(property.Name, RuntimeFileKind.File, new FileDownloadInfo(url, Text(raw, "sha1"), Number(raw, "size")), executable, null));
                        break;
                    default:
                        throw new JavaRuntimeException($"Неизвестный тип записи «{type}» у «{property.Name}».");
                }
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new JavaRuntimeException("Описание рантайма не является корректным JSON.", ex);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
