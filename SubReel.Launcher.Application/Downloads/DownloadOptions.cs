namespace SubReel.Launcher.Application.Downloads;

/// <summary>Настройки менеджера загрузок. Раздел конфигурации «Downloads».</summary>
public sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>Сколько файлов качается одновременно во всём лаунчере.</summary>
    public int MaxConcurrentDownloads { get; set; } = 8;

    /// <summary>Сколько попыток делается на один файл.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Пауза перед повтором; растёт с каждой попыткой (пауза × номер попытки).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Если за это время не пришло ни одного байта, попытка считается неудачной.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
