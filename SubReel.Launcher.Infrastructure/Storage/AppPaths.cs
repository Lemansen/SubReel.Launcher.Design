namespace SubReel.Launcher.Infrastructure.Storage;

/// <summary>
/// Единственное место, где определяются пути к данным лаунчера. Все данные лежат в одном корне
/// (по умолчанию <c>%LocalAppData%\SubReel</c>, пользователь может выбрать другой, см. <see cref="DataLocationService"/>) и разделены на три зоны:
/// <list type="bullet">
/// <item><c>launcher</c> — настройки, аватарки, журналы, кэш, токены входа;</item>
/// <item><c>shared</c> — общее и тяжёлое: файлы игры (версии, библиотеки, ассеты) и Java-рантаймы, по одному экземпляру на все сборки;</item>
/// <item><c>profiles</c> — сборки (экземпляры): у каждой своя папка с описанием и папкой игры (saves, mods, options.txt).</item>
/// </list>
/// Корень можно переопределить — так тесты работают во временной папке.
/// </summary>
public sealed class AppPaths
{
    public const string LauncherZone = "launcher";
    public const string SharedZone = "shared";
    public const string ProfilesZone = "profiles";

    /// <summary>Зоны корня данных: именно они переносятся при смене папки данных.</summary>
    public static IReadOnlyList<string> Zones { get; } = new[] { LauncherZone, SharedZone, ProfilesZone };

    public AppPaths(string? root = null, string? gameDirectory = null)
    {
        Root = root ?? DefaultRoot;
        DefaultGameDirectory = gameDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    /// <summary>Корень данных по умолчанию, одинаковый для всех: %LocalAppData%\SubReel.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubReel");

    /// <summary>Прежний корень пользовательских данных (до выбора папки данных): %AppData%\SubReel. Нужен только для переноса.</summary>
    public static string LegacyRoamingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SubReel");

    /// <summary>Прежний корень кэша и скачанного (до выбора папки данных): %LocalAppData%\SubReel. Нужен только для переноса.</summary>
    public static string LegacyLocalRoot => DefaultRoot;

    /// <summary>Корень данных, выбранный на этом запуске.</summary>
    public string Root { get; }

    /// <summary>Стандартная папка игры: %AppData%\.minecraft (только для чтения списка установленных версий).</summary>
    public string DefaultGameDirectory { get; }

    // ───────────────────────── launcher: настройки, кэш, журналы ─────────────────────────

    public string LauncherDirectory => Path.Combine(Root, LauncherZone);
    public string SettingsFile => Path.Combine(LauncherDirectory, "design-settings.json");
    public string AvatarsDirectory => Path.Combine(LauncherDirectory, "avatars");
    public string CacheDirectory => Path.Combine(LauncherDirectory, "cache");
    public string ImageCacheDirectory => Path.Combine(CacheDirectory, "images");
    /// <summary>Сохранённая копия списка версий Mojang: по ней каталог с датами и картинками работает без сети.</summary>
    public string VersionManifestCacheFile => Path.Combine(CacheDirectory, "version_manifest.json");
    /// <summary>Сохранённая копия патчноутов (описания и картинки обновлений).</summary>
    public string PatchNotesCacheFile => Path.Combine(CacheDirectory, "patch_notes.json");
    public string NewsCacheFile => Path.Combine(CacheDirectory, "news.json");
    public string LogsDirectory => Path.Combine(LauncherDirectory, "logs");
    /// <summary>Зашифрованные токены входа (по файлу на аккаунт). Потеря папки означает лишь повторный вход.</summary>
    public string AuthDirectory => Path.Combine(LauncherDirectory, "auth");
    /// <summary>Скачанные и распакованные обновления лаунчера (по папке на версию). Всё можно удалять: при следующем запуске лаунчер сам убирает остатки.</summary>
    public string UpdatesDirectory => Path.Combine(LauncherDirectory, "updates");

    // ───────────────────────── shared: общее для всех сборок ─────────────────────────

    public string SharedDirectory => Path.Combine(Root, SharedZone);
    /// <summary>Общий кэш игры (версии, библиотеки, ассеты): один набор файлов на все экземпляры. Скачанное можно пересоздать.</summary>
    public string GameDataDirectory => Path.Combine(SharedDirectory, "game");
    /// <summary>Скачанные лаунчером Java-рантаймы: по папке на компонент Mojang (java-runtime-delta …).</summary>
    public string RuntimesDirectory => Path.Combine(SharedDirectory, "runtimes");

    // ───────────────────────── profiles: сборки ─────────────────────────

    /// <summary>Сборки (экземпляры): по одной папке на сборку.</summary>
    public string InstancesDirectory => Path.Combine(Root, ProfilesZone);
}
