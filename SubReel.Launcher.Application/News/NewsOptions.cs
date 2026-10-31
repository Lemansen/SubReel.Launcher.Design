namespace SubReel.Launcher.Application.News;

/// <summary>
/// Настройки новостей. Раздел конфигурации «News» (appsettings.json или переменная окружения <c>News__SubReelEndpoint</c>).
/// </summary>
public sealed class NewsOptions
{
    public const string SectionName = "News";

    /// <summary>
    /// Адрес JSON-ленты новостей SubReel (сайт или API платформы). Пока адрес не задан, категории SubReel / лаунчер / серверы / моды
    /// показывают честное «источник не подключён». Формат ленты описан в README (раздел «Новости»).
    /// </summary>
    public string? SubReelEndpoint { get; set; }
}
