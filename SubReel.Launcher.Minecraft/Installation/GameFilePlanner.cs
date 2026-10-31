using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Installation;

/// <summary>
/// Составляет список файлов версии: куда класть и чем проверять. Один и тот же список используют установка (скачать) и проверка
/// целостности (сверить), поэтому они не могут разойтись в понимании, какие файлы нужны.
/// </summary>
public sealed class GameFilePlanner
{
    public const string AssetsBaseUrl = "https://resources.download.minecraft.net/";

    private readonly GameFileLayout _layout;

    public GameFilePlanner(GameFileLayout layout) => _layout = layout;

    /// <summary>Клиентский jar, библиотеки для платформы (вместе с нативными архивами), конфигурация логирования и индекс ассетов.</summary>
    public IReadOnlyList<DownloadRequest> PlanCore(ResolvedVersion version, OsEnvironment environment)
    {
        var requests = new List<DownloadRequest>();

        if (version.ClientDownload is { } client)
            requests.Add(ToRequest(client, _layout.ClientJarPath(version.ClientJarVersionId)));

        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment)) continue;

            if (library.Artifact is { } artifact && CanDownload(artifact))
                requests.Add(ToRequest(artifact.Download, _layout.LibraryPath(artifact.Path)));

            var classifier = library.GetNativeClassifier(environment);
            if (classifier is not null && library.Classifiers.TryGetValue(classifier, out var nativeArtifact) && CanDownload(nativeArtifact))
                requests.Add(ToRequest(nativeArtifact.Download, _layout.LibraryPath(nativeArtifact.Path)));
        }

        if (version.Logging is { } logging)
            requests.Add(ToRequest(logging.Download, _layout.LoggingConfigPath(logging.FileId)));

        if (version.AssetIndex is { } assetIndex)
            requests.Add(ToRequest(assetIndex.Download, _layout.AssetIndexPath(assetIndex.Id)));

        return Distinct(requests);
    }

    /// <summary>Объекты ассетов. Разные имена с одним хэшем — один файл.</summary>
    public IReadOnlyList<DownloadRequest> PlanAssets(AssetIndex index)
    {
        var requests = index.Objects.Values.Select(asset =>
            new DownloadRequest($"{AssetsBaseUrl}{asset.Hash[..2]}/{asset.Hash}", _layout.AssetObjectPath(asset.Hash), asset.Hash, asset.Size));
        return Distinct(requests);
    }

    /// <summary>Пустой адрес — библиотеку создаёт установщик загрузчика (Forge), скачивать её нечем.</summary>
    private static bool CanDownload(LibraryArtifact artifact) => !string.IsNullOrWhiteSpace(artifact.Download.Url);

    private static DownloadRequest ToRequest(FileDownloadInfo info, string path) => new(info.Url, path, info.Sha1, info.Size);

    private static IReadOnlyList<DownloadRequest> Distinct(IEnumerable<DownloadRequest> requests) =>
        requests.GroupBy(r => r.DestinationPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
}
