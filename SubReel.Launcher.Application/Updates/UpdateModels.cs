namespace SubReel.Launcher.Application.Updates;

/// <summary>Новая версия лаунчера из манифеста обновлений (проверена: https-адрес, SHA-256, размер).</summary>
/// <param name="Sha256">Хэш zip-архива, hex в нижнем регистре.</param>
/// <param name="Mandatory">Обязательное обновление: «Позже» недоступно.</param>
public sealed record LauncherUpdateInfo(Version Version, string DownloadUrl, string Sha256, long SizeBytes, bool Mandatory, string? ReleaseNotes);

public enum UpdateStage { Downloading, Verifying, Unpacking, Starting }

/// <param name="Fraction">Доля выполнения текущего этапа, 0..1.</param>
public readonly record struct UpdateProgress(UpdateStage Stage, double Fraction);

/// <summary>Итог подготовки обновления. Успех означает, что программа замены запущена и ждёт закрытия лаунчера: его нужно закрыть.</summary>
public sealed record UpdateApplyResult(bool Succeeded, string? Error)
{
    public static UpdateApplyResult Ok() => new(true, null);
    public static UpdateApplyResult Fail(string error) => new(false, error);
}

/// <summary>Манифест не читается или не проходит проверку. Текст — для пользователя и журнала.</summary>
public sealed class UpdateManifestException : Exception
{
    public UpdateManifestException(string message) : base(message) { }
    public UpdateManifestException(string message, Exception inner) : base(message, inner) { }
}
