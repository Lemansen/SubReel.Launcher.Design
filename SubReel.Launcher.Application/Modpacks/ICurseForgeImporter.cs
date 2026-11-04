namespace SubReel.Launcher.Application.Modpacks;

/// <summary>Импорт сборки CurseForge (.zip с <c>manifest.json</c>). Требует ключ API CurseForge: адреса файлов выдаёт только он.</summary>
public interface ICurseForgeImporter
{
    /// <summary>
    /// Ожидаемые сбои (не тот файл, нет ключа, нет сети, неверный хэш, мод запретил скачивание) возвращаются в результате,
    /// при сбое и отмене недоделанная сборка удаляется. Отмена — <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ModpackImportResult> ImportAsync(string packPath, IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default);
}
