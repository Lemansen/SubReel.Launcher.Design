using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Application.News;

/// <summary>Источник новостей. Новый источник (сайт SubReel, сервер, GitHub) подключается регистрацией реализации в контейнере.</summary>
public interface INewsSource
{
    string Name { get; }

    /// <summary>false — источник ещё не подключён (нет адреса/бэкенда): его не опрашивают и не считают сбоем.</summary>
    bool IsConfigured { get; }

    IReadOnlyCollection<NewsCategory> Categories { get; }

    Task<IReadOnlyList<NewsItem>> LoadAsync(CancellationToken ct);
}
