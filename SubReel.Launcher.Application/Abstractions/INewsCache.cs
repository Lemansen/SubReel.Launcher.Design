using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Сохранённые новости и момент, когда они были получены с серверов.</summary>
public sealed record NewsCacheSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<NewsItem> Items);

/// <summary>Дисковый кэш новостей. Кэш не критичен: сбои чтения и записи не пробрасываются, а записываются в журнал.</summary>
public interface INewsCache
{
    /// <summary>null — кэша нет или он повреждён.</summary>
    Task<NewsCacheSnapshot?> ReadAsync(CancellationToken ct = default);

    Task WriteAsync(NewsCacheSnapshot snapshot, CancellationToken ct = default);
}
