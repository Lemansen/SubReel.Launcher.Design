namespace SubReel.Launcher.Infrastructure.Storage;

/// <summary>
/// Единственное место, где определяются пути к данным лаунчера. Раскладка совпадает с прежней
/// (%AppData%\SubReel и %LocalAppData%\SubReel), поэтому существующие файлы пользователей находятся на своих местах.
/// Корни можно переопределить — так тесты работают во временной папке.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? roamingRoot = null, string? localRoot = null, string? gameDirectory = null)
    {
        RoamingRoot = roamingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SubReel");
        LocalRoot = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubReel");
        DefaultGameDirectory = gameDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    /// <summary>Данные пользователя (настройки, аватарки): %AppData%\SubReel.</summary>
    public string RoamingRoot { get; }

    /// <summary>Данные, которые можно пересоздать (кэш, журналы): %LocalAppData%\SubReel.</summary>
    public string LocalRoot { get; }

    /// <summary>Стандартная папка игры: %AppData%\.minecraft.</summary>
    public string DefaultGameDirectory { get; }

    public string SettingsFile => Path.Combine(RoamingRoot, "design-settings.json");
    public string AvatarsDirectory => Path.Combine(RoamingRoot, "avatars");
    /// <summary>Экземпляры (сборки): по одной папке на экземпляр.</summary>
    public string InstancesDirectory => Path.Combine(RoamingRoot, "instances");
    public string CacheDirectory => Path.Combine(LocalRoot, "cache");
    public string ImageCacheDirectory => Path.Combine(CacheDirectory, "images");
    /// <summary>Сохранённая копия списка версий Mojang: по ней каталог с датами и картинками работает без сети.</summary>
    public string VersionManifestCacheFile => Path.Combine(CacheDirectory, "version_manifest.json");
    /// <summary>Сохранённая копия патчноутов (описания и картинки обновлений).</summary>
    public string PatchNotesCacheFile => Path.Combine(CacheDirectory, "patch_notes.json");
    public string NewsCacheFile => Path.Combine(CacheDirectory, "news.json");
    /// <summary>Общий кэш игры (версии, библиотеки, ассеты): один набор файлов на все экземпляры. Скачанное можно пересоздать.</summary>
    public string GameDataDirectory => Path.Combine(LocalRoot, "game");
    /// <summary>Скачанные лаунчером Java-рантаймы: по папке на компонент Mojang (java-runtime-delta …).</summary>
    public string RuntimesDirectory => Path.Combine(LocalRoot, "runtimes");
    public string LogsDirectory => Path.Combine(LocalRoot, "logs");
    /// <summary>Зашифрованные токены входа (по файлу на аккаунт). Потеря папки означает лишь повторный вход.</summary>
    public string AuthDirectory => Path.Combine(LocalRoot, "auth");
}
