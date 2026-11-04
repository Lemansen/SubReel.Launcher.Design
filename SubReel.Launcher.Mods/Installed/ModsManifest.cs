using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Mods.Installed;

/// <summary>Формат файла <c>mods.json</c> рядом с <c>instance.json</c>. Причина установки хранится строкой: неизвестное значение из будущей версии не ломает чтение.</summary>
internal sealed class ModsFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<ModEntry> Mods { get; set; } = new();
}

internal sealed class ModEntry
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public DateTime PublishedUtc { get; set; }
    public string FileName { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Reason { get; set; } = "UserChoice";

    /// <summary>Откуда мод (Modrinth, CurseForge). Старые файлы без поля читаются как Modrinth.</summary>
    public string Source { get; set; } = "Modrinth";

    /// <summary>Проекты, без которых мод не работает (обязательные зависимости его версии с известным проектом).</summary>
    public List<string> Requires { get; set; } = new();
}

/// <summary>
/// Список модов сборки на диске. Запись атомарная (<see cref="JsonFileStore"/>). Потерянный или повреждённый файл не страшен:
/// моды восстанавливаются по хэшам файлов в папке mods. Файл от более новой версии лаунчера не читается и не перезаписывается.
/// </summary>
internal sealed class ModsManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly JsonFileStore _store;
    private readonly ILogger _logger;

    public ModsManifestStore(JsonFileStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <exception cref="IOException">Файл есть, но прочитать его нельзя (занят, нет доступа) или он от более новой версии: перезаписывать его опасно.</exception>
    public ModsFile Load(string path)
    {
        var result = _store.Read<ModsFile>(path, JsonOptions);
        switch (result.Status)
        {
            case StoreReadStatus.Unavailable:
                throw new IOException("Список модов сборки сейчас недоступен.");
            case StoreReadStatus.Corrupt:
                _logger.LogWarning("Список модов {Path} повреждён и будет собран заново по файлам в папке mods.", path);
                return new ModsFile();
        }

        var file = result.Value ?? new ModsFile();
        if (file.SchemaVersion > ModsFile.CurrentSchemaVersion)
            throw new IOException("Список модов записан более новой версией лаунчера.");

        file.Mods = (file.Mods ?? new List<ModEntry>()).Where(IsUsable).ToList();
        return file;
    }

    public bool Save(string path, ModsFile file)
    {
        file.SchemaVersion = ModsFile.CurrentSchemaVersion;
        return _store.Write(path, file, JsonOptions);
    }

    private static bool IsUsable(ModEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.ProjectId) && !string.IsNullOrWhiteSpace(entry.FileName) && Modrinth.ModrinthParser.IsSafeFileName(entry.FileName);
}
