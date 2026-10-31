namespace SubReel.Launcher.Application.Downloads;

/// <summary>Одна загрузка: откуда, куда и как проверить результат.</summary>
/// <param name="Url">Адрес файла.</param>
/// <param name="DestinationPath">Полный путь итогового файла.</param>
/// <param name="ExpectedSha1">Ожидаемый SHA-1 (hex, регистр не важен). null — хэш не проверяется.</param>
/// <param name="ExpectedSize">Ожидаемый размер в байтах. null — размер не проверяется.</param>
/// <param name="ReuseExisting">Если проверить существующий файл нечем (нет ни хэша, ни размера), считать его годным.</param>
public sealed record DownloadRequest(
    string Url,
    string DestinationPath,
    string? ExpectedSha1 = null,
    long? ExpectedSize = null,
    bool ReuseExisting = false);

public enum DownloadStatus
{
    /// <summary>Файл скачан и проверен.</summary>
    Downloaded,
    /// <summary>Файл уже был на месте и прошёл проверку, сеть не использовалась.</summary>
    AlreadyUpToDate,
    /// <summary>Не удалось скачать после всех попыток; причина в <see cref="DownloadResult.Error"/>.</summary>
    Failed,
}

public sealed record DownloadResult(DownloadRequest Request, DownloadStatus Status, long BytesReceived, string? Error = null)
{
    public bool Succeeded => Status != DownloadStatus.Failed;
}

/// <summary>Ход одной загрузки. <paramref name="TotalBytes"/> — null, если размер неизвестен.</summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>Ход пакета. <paramref name="TotalBytes"/> — null, если размер хотя бы одного файла неизвестен.</summary>
public readonly record struct DownloadBatchProgress(int CompletedFiles, int TotalFiles, long BytesReceived, long? TotalBytes);
