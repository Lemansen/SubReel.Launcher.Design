namespace SubReel.Launcher.Application.Downloads;

/// <summary>
/// Загрузка файлов для игры: общий лимит параллелизма, проверка SHA-1 и размера, докачка, повтор при сбое.
/// Ожидаемые сбои (сеть, неверный хэш) возвращаются как <see cref="DownloadStatus.Failed"/>, отмена — исключением
/// <see cref="OperationCanceledException"/>; недокачанные данные при отмене остаются для докачки.
/// </summary>
public interface IDownloadManager
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Качает набор файлов с общим лимитом параллелизма. Результаты идут в том же порядке, что и запросы.</summary>
    Task<IReadOnlyList<DownloadResult>> DownloadAllAsync(IReadOnlyList<DownloadRequest> requests, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default);
}
