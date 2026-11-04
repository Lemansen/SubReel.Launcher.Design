using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Minecraft.Layout;

/// <summary>
/// Раскладка общего кэша игры (как у Prism: один файл на диске для всех экземпляров):
/// <c>versions/&lt;id&gt;/&lt;id&gt;.json|.jar</c>, <c>libraries/…</c>, <c>assets/indexes</c>, <c>assets/objects/&lt;2 символа&gt;/&lt;хэш&gt;</c>.
/// Все пути строятся только внутри корня: относительные пути из version.json приходят из сети и не доверяются.
/// </summary>
public sealed class GameFileLayout
{
    public GameFileLayout(AppPaths paths) => Root = paths.GameDataDirectory;

    public string Root { get; }

    public string ManifestPath => Path.Combine(Root, "version_manifest_v2.json");

    /// <summary>Корень ассетов (<c>--assetsDir</c>): внутри indexes, objects, virtual.</summary>
    public string AssetsRoot => Path.Combine(Root, "assets");

    /// <summary>Корень библиотек (<c>${library_directory}</c>).</summary>
    public string LibrariesDirectory => Path.Combine(Root, "libraries");

    public string VersionJsonPath(string versionId) => Inside(Path.Combine(Root, "versions"), SimpleName(versionId), versionId + ".json");

    public string ClientJarPath(string versionId) => Inside(Path.Combine(Root, "versions"), SimpleName(versionId), versionId + ".jar");

    public string LibraryPath(string relativePath) => Inside(Path.Combine(Root, "libraries"), relativePath);

    public string AssetIndexPath(string indexId) => Inside(Path.Combine(Root, "assets", "indexes"), SimpleName(indexId) + ".json");

    /// <summary>Объект ассета по SHA-1: <c>assets/objects/ab/abcdef…</c>.</summary>
    public string AssetObjectPath(string hash)
    {
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new VersionMetadataException($"Некорректный хэш ассета «{hash}».");
        var lower = hash.ToLowerInvariant();
        return Inside(Path.Combine(Root, "assets", "objects"), lower[..2], lower);
    }

    /// <summary>Копия ассета под исходным именем для версий со старым («virtual») индексом.</summary>
    public string VirtualAssetPath(string indexId, string assetName) => Inside(Path.Combine(Root, "assets", "virtual", SimpleName(indexId)), assetName);

    /// <summary>Папка копии ассетов под исходными именами для старого («virtual») индекса: её игра получает как <c>${game_assets}</c>.</summary>
    public string VirtualAssetsDirectory(string indexId) => Inside(Path.Combine(Root, "assets", "virtual"), SimpleName(indexId));

    /// <summary>Память о файлах, у которых хэш уже проверен: <c>verified/files.json</c> (см. <c>VerifiedFilesCache</c>).</summary>
    public string VerifiedFilesPath => Path.Combine(Root, "verified", "files.json");

    public string LoggingConfigPath(string fileId) => Inside(Path.Combine(Root, "assets", "log_configs"), SimpleName(fileId));

    /// <summary>Сохранённый ответ мета-сервера со списком сборок загрузчика для версии игры: <c>loaders/&lt;fabric|quilt&gt;/&lt;версия&gt;.json</c>.</summary>
    public string LoaderBuildsPath(LoaderKind kind, string mcVersion) =>
        Inside(Path.Combine(Root, "loaders", SimpleName(LoaderInfo.Name(kind).ToLowerInvariant())), SimpleName(mcVersion) + ".json");

    /// <summary>Идентификатор версии, индекса или файла — одно имя без разделителей пути.</summary>
    private static string SimpleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new VersionMetadataException($"Недопустимое имя «{name}».");
        return name;
    }

    /// <summary>Склеивает части пути и гарантирует, что результат остался внутри <paramref name="baseDirectory"/>.</summary>
    private static string Inside(string baseDirectory, params string[] parts)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        var combined = Path.GetFullPath(Path.Combine(new[] { baseFull }.Concat(parts.Select(p => p.Replace('/', Path.DirectorySeparatorChar))).ToArray()));
        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new VersionMetadataException($"Путь «{string.Join('/', parts)}» выходит за пределы папки кэша.");
        return combined;
    }
}
