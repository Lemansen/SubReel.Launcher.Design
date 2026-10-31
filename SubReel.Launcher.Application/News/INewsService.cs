using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Application.News;

/// <summary>Что нужно интерфейсу от ленты новостей: проверить наличие источника и загрузить новости.</summary>
public interface INewsService
{
    /// <summary>Подключён ли хоть один источник, дающий эту категорию.</summary>
    bool HasSourceFor(NewsCategory category);

    Task<NewsResult> LoadAsync(bool force, CancellationToken ct = default);
}
