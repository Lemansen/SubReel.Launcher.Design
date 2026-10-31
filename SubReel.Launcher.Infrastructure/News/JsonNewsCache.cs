using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.News;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.News;

/// <summary>
/// Кэш новостей в %LocalAppData%\SubReel\cache\news.json. Формат файла прежний, запись атомарная с копией «.bak»
/// (<see cref="JsonFileStore"/>). Повреждённый кэш считается отсутствующим и перезаписывается свежими данными.
/// </summary>
public sealed class JsonNewsCache : INewsCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class CacheFile
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<NewsItem> Items { get; set; } = new();
    }

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ILogger<JsonNewsCache> _logger;

    public JsonNewsCache(AppPaths paths, JsonFileStore store, ILogger<JsonNewsCache> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public Task<NewsCacheSnapshot?> ReadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var result = _store.Read<CacheFile>(_paths.NewsCacheFile, Options);
        if (result.Status is StoreReadStatus.Missing) return null;
        if (result.Value is null)
        {
            _logger.LogWarning("Кэш новостей недоступен ({Status}), новости будут загружены заново.", result.Status);
            return null;
        }
        return new NewsCacheSnapshot(result.Value.FetchedAt, result.Value.Items);
    }, ct);

    public Task WriteAsync(NewsCacheSnapshot snapshot, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось создать папку кэша {Directory}.", _paths.CacheDirectory);
            return;
        }

        // JsonFileStore.Write сам записывает сбой в журнал и возвращает false: кэш не критичен.
        _store.Write(_paths.NewsCacheFile, new CacheFile { FetchedAt = snapshot.FetchedAt, Items = snapshot.Items.ToList() }, Options);
    }, ct);
}
