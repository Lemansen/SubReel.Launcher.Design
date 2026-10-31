namespace SubReel.Launcher.Domain.News;

public enum NewsCategory { SubReel, Minecraft, Launcher, Servers, Mods }

/// <summary>Одна новость. Поля, которых источник не дал (картинка, дата, текст), остаются null — интерфейс их просто не показывает.</summary>
public sealed class NewsItem
{
    public string Id { get; set; } = "";
    public NewsCategory Category { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    /// <summary>Полный текст (простой текст). Если нет — в карточке новости показывается краткое описание.</summary>
    public string? Body { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>Ссылка на полную версию; открывается только http/https.</summary>
    public string? Url { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>Имя источника — по нему кэш отличает, чьи новости обновлять, если один из источников недоступен.</summary>
    public string Source { get; set; } = "";
}

public sealed class NewsResult
{
    public IReadOnlyList<NewsItem> Items { get; init; } = Array.Empty<NewsItem>();
    /// <summary>Хотя бы один источник подключён и должен был ответить.</summary>
    public bool AnySourceConfigured { get; init; }
    /// <summary>Показаны сохранённые новости, потому что свежие загрузить не удалось (или кэш ещё свежий).</summary>
    public bool FromCache { get; init; }
    /// <summary>Время, когда данные были получены с сервера.</summary>
    public DateTimeOffset? FetchedAt { get; init; }
    /// <summary>Источники, которые не ответили в этот раз.</summary>
    public IReadOnlyList<string> FailedSources { get; init; } = Array.Empty<string>();
    /// <summary>Не удалось получить новости ни из одного источника.</summary>
    public bool Failed { get; init; }
}
