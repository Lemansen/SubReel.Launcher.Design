using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Application.Modpacks;

public enum ModpackImportStage
{
    ReadingPack,
    CreatingBuild,
    DownloadingFiles,
    CheckingFiles,
    ExtractingFiles,
}

/// <param name="Fraction">Общий прогресс 0…1.</param>
public readonly record struct ModpackImportProgress(ModpackImportStage Stage, double Fraction);

/// <summary>Итог импорта: либо созданная сборка, либо текст причины, который можно показать пользователю.</summary>
public sealed record ModpackImportResult(Instance? Instance, string? Error)
{
    public bool Succeeded => Instance is not null;

    public static ModpackImportResult Success(Instance instance) => new(instance, null);

    public static ModpackImportResult Fail(string error) => new(null, error);
}

/// <summary>Импорт сборки из файла: создаёт экземпляр, скачивает моды, раскладывает файлы папки overrides.</summary>
public interface IModpackImporter
{
    /// <summary>
    /// Импортирует сборку Modrinth (.mrpack). Ожидаемые сбои (файл не тот, нет сети, неверный хэш) возвращаются в результате,
    /// при любом сбое и отмене недоделанная сборка удаляется. Отмена — <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ModpackImportResult> ImportMrpackAsync(string packPath, IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default);
}
