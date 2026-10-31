using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Application.Games;

public enum InstallStage
{
    ResolvingMetadata,
    DownloadingFiles,
    DownloadingAssets,
    Completed,
}

/// <summary>Ход установки: этап и счётчики текущего пакета загрузок (для этапа метаданных они нулевые).</summary>
public readonly record struct InstallProgress(InstallStage Stage, int CompletedFiles, int TotalFiles, long BytesReceived, long? TotalBytes);

public sealed record InstallResult(bool Succeeded, ResolvedVersion? Version, IReadOnlyList<DownloadResult> Failures, string? Error)
{
    public static InstallResult Success(ResolvedVersion version) => new(true, version, Array.Empty<DownloadResult>(), null);
    public static InstallResult Failure(string error, IReadOnlyList<DownloadResult>? failures = null) => new(false, null, failures ?? Array.Empty<DownloadResult>(), error);
}

public enum FileProblemReason { Missing, WrongSize, WrongHash }

public sealed record FileProblem(string Path, FileProblemReason Reason);

/// <summary>Итог проверки целостности. <see cref="MetadataAvailable"/> = false — версия не установлена (нет описания), проверять нечего.</summary>
public sealed record VerificationResult(bool MetadataAvailable, IReadOnlyList<FileProblem> Problems)
{
    public bool IsValid => MetadataAvailable && Problems.Count == 0;
}
