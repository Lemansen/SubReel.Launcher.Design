using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Minecraft.Installation;

/// <summary>
/// Установка и проверка версии в общем кэше. Порядок: описание версии → клиент, библиотеки, логирование, индекс ассетов → объекты ассетов.
/// Уже скачанные и проверенные файлы пропускаются, поэтому повторная установка (в том числе после обрыва) докачивает только недостающее.
/// </summary>
public sealed class GameInstaller : IGameInstaller
{
    private const int VerifyParallelism = 4;

    private readonly IVersionMetadataProvider _metadata;
    private readonly IDownloadManager _downloads;
    private readonly GameFilePlanner _planner;
    private readonly GameFileLayout _layout;
    private readonly OsEnvironment _environment;
    private readonly ILogger<GameInstaller> _logger;

    public GameInstaller(IVersionMetadataProvider metadata, IDownloadManager downloads, GameFilePlanner planner, GameFileLayout layout, OsEnvironment environment, ILogger<GameInstaller> logger)
    {
        _metadata = metadata;
        _downloads = downloads;
        _planner = planner;
        _layout = layout;
        _environment = environment;
        _logger = logger;
    }

    public async Task<InstallResult> InstallAsync(string versionId, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new InstallProgress(InstallStage.ResolvingMetadata, 0, 0, 0, null));
            var version = await _metadata.ResolveAsync(versionId, allowNetwork: true, ct).ConfigureAwait(false);

            var coreFailures = await DownloadAsync(_planner.PlanCore(version, _environment), InstallStage.DownloadingFiles, progress, ct).ConfigureAwait(false);
            if (coreFailures.Count > 0) return Failed(versionId, coreFailures);

            if (version.AssetIndex is { } indexInfo)
            {
                var index = AssetIndexParser.Parse(await File.ReadAllTextAsync(_layout.AssetIndexPath(indexInfo.Id), ct).ConfigureAwait(false));

                var assetFailures = await DownloadAsync(_planner.PlanAssets(index), InstallStage.DownloadingAssets, progress, ct).ConfigureAwait(false);
                if (assetFailures.Count > 0) return Failed(versionId, assetFailures);

                if (index.IsVirtual) await CopyVirtualAssetsAsync(indexInfo.Id, index, ct).ConfigureAwait(false);
            }

            progress?.Report(new InstallProgress(InstallStage.Completed, 0, 0, 0, null));
            _logger.LogInformation("Версия {Id} установлена.", versionId);
            return InstallResult.Success(version);
        }
        catch (VersionMetadataException ex)
        {
            _logger.LogWarning(ex, "Не удалось установить версию {Id}.", versionId);
            return InstallResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ошибка диска при установке версии {Id}.", versionId);
            return InstallResult.Failure($"Ошибка записи на диск: {ex.Message}");
        }
    }

    public async Task<VerificationResult> VerifyAsync(string versionId, CancellationToken ct = default)
    {
        ResolvedVersion version;
        try
        {
            version = await _metadata.ResolveAsync(versionId, allowNetwork: false, ct).ConfigureAwait(false);
        }
        catch (VersionMetadataException ex)
        {
            _logger.LogInformation(ex, "Версия {Id} не может быть проверена: описание недоступно.", versionId);
            return new VerificationResult(false, Array.Empty<FileProblem>());
        }

        var problems = new ConcurrentBag<FileProblem>();
        var requests = new List<DownloadRequest>(_planner.PlanCore(version, _environment));

        if (version.AssetIndex is { } indexInfo)
        {
            var indexPath = _layout.AssetIndexPath(indexInfo.Id);
            // Индекс уже входит в основной список; если он на месте и читается, добавляем и объекты ассетов.
            if (File.Exists(indexPath))
            {
                try
                {
                    var index = AssetIndexParser.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));
                    requests.AddRange(_planner.PlanAssets(index));
                }
                catch (VersionMetadataException ex)
                {
                    _logger.LogWarning(ex, "Индекс ассетов {Id} повреждён.", indexInfo.Id);
                    problems.Add(new FileProblem(indexPath, FileProblemReason.WrongHash));
                }
            }
        }

        await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = VerifyParallelism, CancellationToken = ct }, async (request, token) =>
        {
            var check = await FileVerifier.CheckAsync(request.DestinationPath, request.ExpectedSha1, request.ExpectedSize, token).ConfigureAwait(false);
            if (check != FileCheck.Ok) problems.Add(new FileProblem(request.DestinationPath, ToReason(check)));
        }).ConfigureAwait(false);

        var distinct = problems.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new VerificationResult(true, distinct);
    }

    private async Task<IReadOnlyList<DownloadResult>> DownloadAsync(IReadOnlyList<DownloadRequest> requests, InstallStage stage, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        IProgress<DownloadBatchProgress>? batch = progress is null
            ? null
            : new BatchProgress(p => progress.Report(new InstallProgress(stage, p.CompletedFiles, p.TotalFiles, p.BytesReceived, p.TotalBytes)));

        var results = await _downloads.DownloadAllAsync(requests, batch, ct).ConfigureAwait(false);
        return results.Where(r => !r.Succeeded).ToList();
    }

    private InstallResult Failed(string versionId, IReadOnlyList<DownloadResult> failures)
    {
        _logger.LogWarning("Версия {Id}: не скачано файлов — {Count}. Первая ошибка: {Error}", versionId, failures.Count, failures[0].Error);
        return InstallResult.Failure($"Не удалось скачать файлов: {failures.Count}. {failures[0].Error}", failures);
    }

    private Task CopyVirtualAssetsAsync(string indexId, AssetIndex index, CancellationToken ct) =>
        Task.Run(() =>
        {
            foreach (var (name, asset) in index.Objects)
            {
                ct.ThrowIfCancellationRequested();
                var source = _layout.AssetObjectPath(asset.Hash);
                var destination = _layout.VirtualAssetPath(indexId, name);
                if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }, ct);

    private static FileProblemReason ToReason(FileCheck check) => check switch
    {
        FileCheck.WrongSize => FileProblemReason.WrongSize,
        FileCheck.WrongHash => FileProblemReason.WrongHash,
        _ => FileProblemReason.Missing,
    };

    private sealed class BatchProgress : IProgress<DownloadBatchProgress>
    {
        private readonly Action<DownloadBatchProgress> _handler;
        public BatchProgress(Action<DownloadBatchProgress> handler) => _handler = handler;
        public void Report(DownloadBatchProgress value) => _handler(value);
    }
}
