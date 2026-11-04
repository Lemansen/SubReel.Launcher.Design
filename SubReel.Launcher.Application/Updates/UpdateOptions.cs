namespace SubReel.Launcher.Application.Updates;

/// <summary>Раздел конфигурации «Updates» (appsettings.json или переменная окружения <c>Updates__ManifestUrl</c>).</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>Адрес JSON-манифеста (только https). Пока не задан, проверка обновлений выключена: «источник не подключён».</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>Как часто перепроверять, пока лаунчер открыт.</summary>
    public double CheckIntervalHours { get; set; } = 6;

    /// <summary>На сколько дней «Позже» прячет баннер.</summary>
    public int SnoozeDays { get; set; } = 3;

    /// <summary>
    /// Узлы, с которых можно качать архив, кроме узла самого манифеста (например, манифест на своём сайте, а файлы на GitHub Releases).
    /// Любой другой узел в манифесте отклоняется: подменённый манифест не должен слать лаунчер за файлом куда угодно.
    /// </summary>
    public List<string> AdditionalDownloadHosts { get; set; } = new() { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
}
