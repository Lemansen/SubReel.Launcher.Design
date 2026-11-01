using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Loaders;

/// <summary>
/// Список сборок Fabric и Quilt для версии игры. Ответ мета-сервера лежит на диске (<c>loaders/&lt;загрузчик&gt;/&lt;версия&gt;.json</c> в общем кэше игры):
/// свежий файл (моложе 30 минут) используется как есть, иначе обновляется; нет сети — берётся устаревший; нет и его — <see cref="LoaderCatalogException"/>.
/// Так же работает список версий Mojang (<c>VersionManifestSource</c>), поэтому выпадающий список без сети показывает то, что уже видели.
/// </summary>
public sealed class LoaderCatalog : ILoaderCatalog
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly IDownloadManager _downloads;
    private readonly GameFileLayout _layout;
    private readonly LoaderEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly ILogger<LoaderCatalog> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public LoaderCatalog(IDownloadManager downloads, GameFileLayout layout, LoaderEndpoints endpoints, TimeProvider time, ILogger<LoaderCatalog> logger)
    {
        _downloads = downloads;
        _layout = layout;
        _endpoints = endpoints;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LoaderBuild>> GetBuildsAsync(LoaderKind kind, string mcVersion, CancellationToken ct = default)
    {
        if (!LoaderInfo.CanInstall(kind)) return Array.Empty<LoaderBuild>();
        if (!LoaderMetaParser.IsSafeVersionText(mcVersion)) throw new LoaderCatalogException($"Недопустимая версия игры «{mcVersion}».");

        var path = _layout.LoaderBuildsPath(kind, mcVersion);
        await RefreshAsync(kind, mcVersion, path, ct).ConfigureAwait(false);

        if (!File.Exists(path))
            throw new LoaderCatalogException($"Список сборок {LoaderInfo.Name(kind)} недоступен: нет сети и нет сохранённой копии.");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoaderCatalogException($"Не удалось прочитать сохранённый список сборок {LoaderInfo.Name(kind)}.", ex);
        }

        try
        {
            return LoaderMetaParser.ParseBuilds(json, kind, mcVersion);
        }
        catch (LoaderCatalogException)
        {
            // Битую копию убираем, чтобы следующий запрос скачал список заново, а не спотыкался об неё.
            TryDelete(path);
            throw;
        }
    }

    private async Task RefreshAsync(LoaderKind kind, string mcVersion, string path, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) < MaxAge) return;

            var request = new DownloadRequest(_endpoints.BuildListUrl(kind, mcVersion), path);
            var result = await _downloads.DownloadAsync(request, null, ct).ConfigureAwait(false);
            if (!result.Succeeded)
                _logger.LogWarning("Не удалось обновить список сборок {Loader} для {Version}: {Error}. Используется сохранённая копия, если она есть.", kind, mcVersion, result.Error);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не получилось убрать — в следующий раз попробуем снова
        }
    }
}
