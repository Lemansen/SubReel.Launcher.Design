using System.Text.Json;
using SubReel.Launcher.Domain.Modpacks;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Mods.CurseForge;

internal sealed record CurseForgeFileRef(long ProjectId, long FileId);

/// <param name="Overrides">Папка внутри архива с файлами для папки игры (обычно <c>overrides</c>); проверена на безопасность.</param>
internal sealed record CurseForgeManifest(string Name, string McVersion, LoaderKind Loader, string? LoaderVersion, IReadOnlyList<CurseForgeFileRef> Files, string Overrides);

/// <summary>Разбор <c>manifest.json</c> сборки CurseForge. Файл приходит от кого угодно: пути и имена проверяются, неподдерживаемое отклоняется с понятным текстом.</summary>
internal static class CurseForgeManifestParser
{
    public const string FileName = "manifest.json";
    private const int MaxFiles = 5000;

    public static CurseForgeManifest Parse(string json)
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
            if (Str(root, "manifestType") is { } type && type != "minecraftModpack") throw new ModpackException("Это не сборка Minecraft.");

            if (!root.TryGetProperty("minecraft", out var minecraft) || minecraft.ValueKind != JsonValueKind.Object)
                throw new ModpackException("В сборке не указана версия игры.");
            var mc = Str(minecraft, "version")?.Trim();
            if (string.IsNullOrEmpty(mc) || mc.Length > 64 || mc.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
                throw new ModpackException("В сборке неверно указана версия игры.");

            var (loader, loaderVersion) = ReadLoader(minecraft);

            var files = new List<CurseForgeFileRef>();
            if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                if (array.GetArrayLength() > MaxFiles) throw new ModpackException("В сборке слишком много файлов.");
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    if (element.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.False) continue; // необязательные не ставим
                    if (element.TryGetProperty("projectID", out var p) && p.TryGetInt64(out var projectId) && projectId > 0
                        && element.TryGetProperty("fileID", out var f) && f.TryGetInt64(out var fileId) && fileId > 0)
                        files.Add(new CurseForgeFileRef(projectId, fileId));
                }
            }

            var name = Str(root, "name")?.Trim();
            if (string.IsNullOrEmpty(name)) name = "Сборка CurseForge";

            var overrides = Str(root, "overrides")?.Trim();
            overrides = string.IsNullOrEmpty(overrides) ? "overrides" : MrpackParser.NormalizePath(overrides)
                ?? throw new ModpackException("В сборке небезопасный путь папки overrides.");

            return new CurseForgeManifest(name, mc, loader, loaderVersion, files, overrides);
        }
    }

    // "forge-47.2.0", "fabric-0.15.11", "neoforge-21.1.172", "quilt-0.26.0"
    private static (LoaderKind, string?) ReadLoader(JsonElement minecraft)
    {
        if (!minecraft.TryGetProperty("modLoaders", out var loaders) || loaders.ValueKind != JsonValueKind.Array || loaders.GetArrayLength() == 0)
            return (LoaderKind.Vanilla, null);

        string? id = null;
        foreach (var loader in loaders.EnumerateArray())
        {
            var candidate = Str(loader, "id");
            if (candidate is null) continue;
            id ??= candidate;
            if (loader.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            {
                id = candidate;
                break;
            }
        }
        if (id is null) return (LoaderKind.Vanilla, null);

        var dash = id.IndexOf('-');
        if (dash <= 0 || dash == id.Length - 1) throw new ModpackException($"Не удалось разобрать загрузчик сборки «{id}».");
        var version = id[(dash + 1)..];
        if (version.Length > 64 || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
            throw new ModpackException($"Не удалось разобрать версию загрузчика «{id}».");

        var kind = id[..dash].ToLowerInvariant() switch
        {
            "forge" => LoaderKind.Forge,
            "fabric" => LoaderKind.Fabric,
            "neoforge" => LoaderKind.NeoForge,
            "quilt" => LoaderKind.Quilt,
            _ => throw new ModpackException($"Загрузчик «{id[..dash]}» лаунчер пока не поддерживает."),
        };
        return (kind, version);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
