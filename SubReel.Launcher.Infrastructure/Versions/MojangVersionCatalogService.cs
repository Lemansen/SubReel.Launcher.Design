using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Versions;

/// <summary>
/// Загружает каталог релизов и картинок обновлений с серверов Mojang и сохраняет копию на диск.
/// Без сети берёт сохранённую копию (<see cref="VersionCatalog.IsStale"/>), а если её нет — встроенный каталог.
/// </summary>
public sealed class MojangVersionCatalogService : IVersionCatalogService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly string[] PatchNotesUrls =
    {
        "https://launchercontent.mojang.com/v2/javaPatchNotes.json",
        "https://launchercontent.mojang.com/javaPatchNotes.json",
    };

    private readonly HttpClient _http;
    private readonly ILogger<MojangVersionCatalogService> _logger;

    private readonly AppPaths? _paths;

    /// <param name="paths">Где хранить копию каталога. null — без сохранения (тесты).</param>
    public MojangVersionCatalogService(HttpClient http, ILogger<MojangVersionCatalogService> logger, AppPaths? paths = null)
    {
        _http = http;
        _logger = logger;
        _paths = paths;
    }

    public VersionCatalog CreateFallback() => VersionCatalog.CreateFallback();

    public async Task<VersionCatalog> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var manifestTask = _http.GetStringAsync(ManifestUrl, ct);
            var notesTask = LoadPatchNotesAsync(ct);
            await Task.WhenAll(manifestTask, notesTask).ConfigureAwait(false);

            var manifestJson = await manifestTask.ConfigureAwait(false);
            var (notesJson, notes) = await notesTask.ConfigureAwait(false);

            // Патчноуты не критичны: если они не загрузились, берём сохранённые, а не остаёмся без картинок.
            if (notesJson is null && ReadCache(_paths?.PatchNotesCacheFile) is { } cachedNotes)
                notes = TryParseNotes(cachedNotes);

            var catalog = MojangCatalogParser.BuildCatalog(manifestJson, notes);
            SaveCache(_paths?.VersionManifestCacheFile, manifestJson);
            if (notesJson is not null) SaveCache(_paths?.PatchNotesCacheFile, notesJson);
            _logger.LogInformation("Каталог версий загружен: {Count} релизов.", catalog.Releases.Count);
            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Граница внешней системы: любой сбой сети или формата — берём сохранённую копию, а без неё встроенный список.
            _logger.LogWarning(ex, "Не удалось загрузить каталог версий Mojang.");
            return LoadFromCache() ?? CreateFallback();
        }
    }

    private VersionCatalog? LoadFromCache()
    {
        try
        {
            var manifest = ReadCache(_paths?.VersionManifestCacheFile);
            if (manifest is null) return null;

            var notesJson = ReadCache(_paths?.PatchNotesCacheFile);
            var notes = notesJson is null ? new Dictionary<string, PatchNote>() : TryParseNotes(notesJson);
            var catalog = MojangCatalogParser.BuildCatalog(manifest, notes);
            if (catalog.Releases.Count == 0) return null;

            _logger.LogInformation("Каталог версий взят из сохранённой копии: {Count} релизов.", catalog.Releases.Count);
            return catalog.AsStale();
        }
        catch (Exception ex)
        {
            // Копия повреждена — не страшно, останется встроенный список; свежая загрузка перезапишет файл.
            _logger.LogWarning(ex, "Сохранённая копия каталога версий не читается.");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, PatchNote> TryParseNotes(string json)
    {
        try
        {
            return MojangCatalogParser.ParsePatchNotes(json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, PatchNote>();
        }
    }

    private static string? ReadCache(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveCache(string? path, string json)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить копию каталога версий {Path}.", path);
        }
    }

    /// <summary>Картинки и описания не критичны: при сбое карточки получат заглушку. Возвращает и исходный JSON — для сохранения копии.</summary>
    private async Task<(string? Json, IReadOnlyDictionary<string, PatchNote> Notes)> LoadPatchNotesAsync(CancellationToken ct)
    {
        foreach (var url in PatchNotesUrls)
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var notes = MojangCatalogParser.ParsePatchNotes(json);
                if (notes.Count > 0) return (json, notes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Патчноуты недоступны по адресу {Url}, пробуем следующий.", url);
            }
        }
        return (null, new Dictionary<string, PatchNote>());
    }
}
