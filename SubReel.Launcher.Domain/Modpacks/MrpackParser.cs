using System.Text.Json;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Domain.Modpacks;

/// <summary>
/// Разбор <c>modrinth.index.json</c> (формат .mrpack, formatVersion 1). Файл приходит от кого угодно, поэтому ничему не доверяем:
/// пути не должны выходить за папку игры, адреса — вести только на доверенные узлы по https, хэши обязаны быть настоящими.
/// </summary>
public static class MrpackParser
{
    public const string IndexFileName = "modrinth.index.json";

    /// <summary>Узлы, с которых спецификация Modrinth разрешает скачивать файлы сборок.</summary>
    public static IReadOnlyList<string> TrustedHosts { get; } = new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };

    public static MrpackIndex Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new ModpackException("Файл описания сборки повреждён.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ModpackException("Файл описания сборки повреждён.");

            if (!TryInt(root, "formatVersion", out var format) || format != 1)
                throw new ModpackException("Эта версия формата .mrpack не поддерживается.");
            if (Str(root, "game") != "minecraft") throw new ModpackException("Сборка сделана не для Minecraft.");

            var name = Str(root, "name")?.Trim();
            if (string.IsNullOrEmpty(name)) throw new ModpackException("У сборки нет названия.");

            if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                throw new ModpackException("В сборке не указаны версии игры и загрузчика.");
            var (mc, loader, loaderVersion) = ReadDependencies(deps);

            var files = new List<MrpackFile>();
            if (root.TryGetProperty("files", out var filesElement))
            {
                if (filesElement.ValueKind != JsonValueKind.Array) throw new ModpackException("Список файлов сборки повреждён.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in filesElement.EnumerateArray())
                {
                    var file = ReadFile(element);
                    if (file is null) continue; // только для сервера
                    if (!seen.Add(file.Path)) throw new ModpackException($"Файл «{file.Path}» указан в сборке дважды.");
                    files.Add(file);
                }
            }

            return new MrpackIndex(name, Str(root, "versionId"), Str(root, "summary"), mc, loader, loaderVersion, files);
        }
    }

    private static (string Mc, LoaderKind Loader, string? LoaderVersion) ReadDependencies(JsonElement deps)
    {
        string? mc = null;
        LoaderKind loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        foreach (var property in deps.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(value)) throw new ModpackException($"Версия «{property.Name}» в сборке указана неверно.");

            var kind = property.Name switch
            {
                "minecraft" => (LoaderKind?)null,
                "fabric-loader" => LoaderKind.Fabric,
                "quilt-loader" => LoaderKind.Quilt,
                "forge" => LoaderKind.Forge,
                "neoforge" => LoaderKind.NeoForge,
                _ => throw new ModpackException($"Сборка требует «{property.Name}», а он не поддерживается."),
            };

            if (kind is null)
            {
                mc = value;
                continue;
            }

            if (loader != LoaderKind.Vanilla) throw new ModpackException("В сборке указано несколько загрузчиков.");
            loader = kind.Value;
            loaderVersion = value;
        }

        if (string.IsNullOrEmpty(mc)) throw new ModpackException("В сборке не указана версия Minecraft.");
        return (mc, loader, loaderVersion);
    }

    private static MrpackFile? ReadFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ModpackException("Список файлов сборки повреждён.");

        // env.client = unsupported: файл нужен только серверу, на клиент не качаем.
        if (element.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object && Str(env, "client") == "unsupported") return null;

        var rawPath = Str(element, "path") ?? "";
        var path = NormalizePath(rawPath) ?? throw new ModpackException($"Небезопасный путь файла в сборке: «{rawPath}».");

        if (!element.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object)
            throw new ModpackException($"У файла «{path}» нет контрольных сумм.");
        var sha1 = Hash(hashes, "sha1", 40, path);
        var sha512 = Hash(hashes, "sha512", 128, path);

        string? url = null;
        if (element.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in downloads.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && IsTrustedUrl(candidate.GetString()))
                {
                    url = candidate.GetString();
                    break;
                }
            }
        }
        if (url is null) throw new ModpackException($"Файл «{path}» нельзя скачать с доверенного сервера.");

        long? size = null;
        if (element.TryGetProperty("fileSize", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt64(out var bytes) && bytes >= 0)
            size = bytes;

        return new MrpackFile(path, sha1, sha512, url, size);
    }

    private static string Hash(JsonElement hashes, string name, int length, string path)
    {
        var value = Str(hashes, name);
        if (value is null || value.Length != length || !value.All(Uri.IsHexDigit))
            throw new ModpackException($"У файла «{path}» неверная контрольная сумма {name.ToUpperInvariant()}.");
        return value.ToLowerInvariant();
    }

    public static bool IsTrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Приводит путь из архива к виду «a/b/c»: без начального «/», диска, «..», «.», пустых частей, двоеточий и символов, недопустимых в Windows.
    /// null — путь небезопасен. Используется и для файлов из списка, и для содержимого папок overrides.
    /// </summary>
    public static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 240) return null;

        var path = raw.Replace('\\', '/');
        if (path.StartsWith('/') || path.EndsWith('/')) return null;

        // Свои символы, а не только Path.GetInvalidFileNameChars(): правила Windows нужны на любой системе (и в тестах).
        var invalid = new[] { ':', '<', '>', '"', '|', '?', '*', '\0' };
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return null;
            if (part.IndexOfAny(invalid) >= 0 || part.Any(char.IsControl)) return null; // в том числе «:» (диск и потоки NTFS)
        }

        return string.Join('/', parts);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }
}
