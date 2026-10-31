using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Domain.Settings;

/// <summary>
/// Пользовательские настройки лаунчера (аккаунты, выбранная версия, недавние версии). Чистая модель данных:
/// чтение, запись и миграции форматов живут в слое Infrastructure.
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>Версия формата файла настроек, которую понимает эта сборка лаунчера.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Версия формата, в котором записан файл. У файлов, созданных до введения версионирования, поля нет — читается как 0
    /// и приводится к текущему формату миграцией.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>null — первый запуск: лаунчер сам выберет самый свежий релиз.</summary>
    public string? SelectedVersion { get; set; }

    /// <summary>
    /// Имя папки установленной версии (<c>versions\&lt;id&gt;</c>), выбранной в выпадающем списке подвала: нужно, чтобы отличать Fabric/Forge/NeoForge
    /// от ванили той же версии. null — выбрана ваниль по номеру (<see cref="SelectedVersion"/>) или версия не установлена.
    /// </summary>
    public string? SelectedInstallId { get; set; }

    /// <summary>Последние выбранные версии (новые первыми, не больше 6) — для строки «Недавние» на экране «Все версии».</summary>
    public List<string> RecentVersions { get; set; } = new();

    /// <summary>Добавленные аккаунты (пока офлайн-ники; Microsoft / SubReel ID подключим позже).</summary>
    public List<LauncherAccount> Accounts { get; set; } = new() { new LauncherAccount { Name = "Gamer", Kind = "Офлайн" } };

    /// <summary>Ник активного аккаунта.</summary>
    public string ActiveAccount { get; set; } = "Gamer";
}
