using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Minecraft.Loaders;

/// <summary>
/// Ставит Fabric и Quilt: профиль (version.json с <c>inheritsFrom</c>) скачивается с мета-сервера в общий кэш игры как обычная версия
/// (<c>versions/&lt;профиль&gt;/&lt;профиль&gt;.json</c>). Дальше работает обычная установка версии: <c>GameInstaller</c> по цепочке
/// <c>inheritsFrom</c> берёт ванильного родителя, а библиотеки загрузчика (имя + адрес Maven-репозитория) скачивает по тем же правилам.
/// Скачанный профиль проверяется до того, как лечь на место: годный JSON, родитель — именно эта версия игры, есть главный класс.
/// </summary>
public sealed class LoaderInstaller : ILoaderInstaller
{
    private readonly ILoaderCatalog _catalog;
    private readonly IDownloadManager _downloads;
    private readonly GameFileLayout _layout;
    private readonly LoaderEndpoints _endpoints;
    private readonly ILogger<LoaderInstaller> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LoaderInstaller(ILoaderCatalog catalog, IDownloadManager downloads, GameFileLayout layout, LoaderEndpoints endpoints, ILogger<LoaderInstaller> logger)
    {
        _catalog = catalog;
        _downloads = downloads;
        _layout = layout;
        _endpoints = endpoints;
        _logger = logger;
    }

    public async Task<LoaderInstallResult> EnsureAsync(LoaderKind kind, string mcVersion, string? loaderVersion, CancellationToken ct = default)
    {
        if (!LoaderInfo.CanInstall(kind))
            return LoaderInstallResult.Failure($"Загрузчик {LoaderInfo.Name(kind)} лаунчер пока не умеет устанавливать.");

        mcVersion = mcVersion?.Trim() ?? "";
        if (!LoaderMetaParser.IsSafeVersionText(mcVersion))
            return LoaderInstallResult.Failure($"Недопустимая версия игры «{mcVersion}».");

        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
        if (loaderVersion is not null && !LoaderMetaParser.IsSafeVersionText(loaderVersion))
            return LoaderInstallResult.Failure($"Недопустимая версия загрузчика «{loaderVersion}».");

        // Две одновременные установки одного профиля писали бы в один временный файл.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (loaderVersion is null)
            {
                var latest = await PickLatestAsync(kind, mcVersion, ct).ConfigureAwait(false);
                if (latest.Error is not null) return LoaderInstallResult.Failure(latest.Error);
                loaderVersion = latest.Version!;
            }

            var id = LoaderInfo.ProfileId(kind, mcVersion, loaderVersion);

            string path;
            try
            {
                path = _layout.VersionJsonPath(id);
            }
            catch (VersionMetadataException ex)
            {
                return LoaderInstallResult.Failure(ex.Message);
            }

            if (IsUsableProfile(path, mcVersion)) return LoaderInstallResult.Success(id, loaderVersion);

            return await DownloadProfileAsync(kind, mcVersion, loaderVersion, id, path, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string? Version, string? Error)> PickLatestAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        IReadOnlyList<LoaderBuild> builds;
        try
        {
            builds = await _catalog.GetBuildsAsync(kind, mcVersion, ct).ConfigureAwait(false);
        }
        catch (LoaderCatalogException ex)
        {
            _logger.LogWarning(ex, "Не удалось узнать последнюю сборку {Loader} для Minecraft {Version}.", kind, mcVersion);
            return (null, ex.Message);
        }

        var pick = builds.FirstOrDefault(b => b.Stable) ?? builds.FirstOrDefault();
        return pick is null
            ? (null, $"{LoaderInfo.Name(kind)} не поддерживает Minecraft {mcVersion}.")
            : (pick.Version, null);
    }

    private async Task<LoaderInstallResult> DownloadProfileAsync(LoaderKind kind, string mcVersion, string loaderVersion, string id, string path, CancellationToken ct)
    {
        var temp = path + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var url = _endpoints.ProfileUrl(kind, mcVersion, loaderVersion);
            var result = await _downloads.DownloadAsync(new DownloadRequest(url, temp), null, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Профиль {Loader} {LoaderVersion} для Minecraft {Version} не скачан: {Error}", kind, loaderVersion, mcVersion, result.Error);
                return LoaderInstallResult.Failure(
                    $"Не удалось скачать профиль {LoaderInfo.Name(kind)} {loaderVersion} для Minecraft {mcVersion}: {result.Error}");
            }

            var problem = CheckProfile(File.ReadAllText(temp), mcVersion);
            if (problem is not null)
            {
                _logger.LogWarning("Профиль {Id} отклонён: {Problem}", id, problem);
                return LoaderInstallResult.Failure($"Мета-сервер {LoaderInfo.Name(kind)} вернул неподходящий профиль: {problem}");
            }

            File.Move(temp, path, overwrite: true);
            _logger.LogInformation("Профиль загрузчика {Id} установлен.", id);
            return LoaderInstallResult.Success(id, loaderVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ошибка диска при установке профиля {Id}.", id);
            return LoaderInstallResult.Failure($"Ошибка записи на диск: {ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Профиль на диске читается и подходит версии игры; иначе его скачают заново.</summary>
    private static bool IsUsableProfile(string path, string mcVersion)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return CheckProfile(File.ReadAllText(path), mcVersion) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>null — профиль годный, иначе причина отказа.</returns>
    private static string? CheckProfile(string json, string mcVersion)
    {
        GameVersionMetadata metadata;
        try
        {
            metadata = VersionMetadataParser.Parse(json);
        }
        catch (VersionMetadataException ex)
        {
            return ex.Message;
        }

        if (!string.Equals(metadata.InheritsFrom, mcVersion, StringComparison.Ordinal))
            return $"он основан на «{metadata.InheritsFrom ?? "—"}», а нужна Minecraft {mcVersion}.";
        if (string.IsNullOrWhiteSpace(metadata.MainClass)) return "в нём не указан главный класс.";
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временный файл перезапишется при следующей установке
        }
    }
}
