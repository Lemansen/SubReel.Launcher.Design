using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Application.News;

/// <summary>
/// Собирает новости из всех подключённых источников, не блокируя интерфейс (вызывается из async-кода окна).
/// Хранит кэш через <see cref="INewsCache"/>: свежий кэш отдаётся сразу, а при потере связи показываются сохранённые новости.
/// </summary>
public sealed class NewsService : INewsService
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(20);

    private readonly IReadOnlyList<INewsSource> _sources;
    private readonly INewsCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<NewsService> _logger;

    public NewsService(IEnumerable<INewsSource> sources, INewsCache cache, TimeProvider time, ILogger<NewsService> logger)
    {
        _sources = sources.ToList();
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>Подключён ли хоть один источник, дающий эту категорию.</summary>
    public bool HasSourceFor(NewsCategory category) =>
        _sources.Any(s => s.IsConfigured && s.Categories.Contains(category));

    public async Task<NewsResult> LoadAsync(bool force, CancellationToken ct = default)
    {
        var active = _sources.Where(s => s.IsConfigured).ToList();
        if (active.Count == 0) return new NewsResult { AnySourceConfigured = false };

        var cache = await _cache.ReadAsync(ct).ConfigureAwait(false);
        if (!force && cache is not null && _time.GetUtcNow() - cache.FetchedAt < FreshFor)
            return new NewsResult { Items = Sorted(cache.Items), AnySourceConfigured = true, FromCache = true, FetchedAt = cache.FetchedAt };

        var results = await Task.WhenAll(active.Select(source => FetchAsync(source, ct))).ConfigureAwait(false);

        var failed = results.Where(r => r.Error is not null).Select(r => r.Source.Name).ToList();
        var merged = results.Where(r => r.Error is null).SelectMany(r => r.Items).ToList();

        if (failed.Count == active.Count)
        {
            // Ничего не загрузилось: если есть сохранённые новости — показываем их, иначе сообщаем об ошибке.
            return cache is { Items.Count: > 0 }
                ? new NewsResult { Items = Sorted(cache.Items), AnySourceConfigured = true, FromCache = true, FetchedAt = cache.FetchedAt, FailedSources = failed }
                : new NewsResult { AnySourceConfigured = true, Failed = true, FailedSources = failed };
        }

        // У источников, которые не ответили, оставляем их прежние сохранённые новости.
        if (cache is not null)
            merged.AddRange(cache.Items.Where(i => failed.Contains(i.Source)));

        var fetchedAt = _time.GetUtcNow();
        var sorted = Sorted(merged);
        await _cache.WriteAsync(new NewsCacheSnapshot(fetchedAt, sorted), ct).ConfigureAwait(false);
        return new NewsResult { Items = sorted, AnySourceConfigured = true, FetchedAt = fetchedAt, FailedSources = failed };
    }

    private sealed record SourceResult(INewsSource Source, IReadOnlyList<NewsItem> Items, Exception? Error);

    private async Task<SourceResult> FetchAsync(INewsSource source, CancellationToken ct)
    {
        try
        {
            return new SourceResult(source, await source.LoadAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Источник новостей «{Source}» не ответил.", source.Name);
            return new SourceResult(source, Array.Empty<NewsItem>(), ex);
        }
    }

    private static IReadOnlyList<NewsItem> Sorted(IEnumerable<NewsItem> items) =>
        items.OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue).ToList();
}
