using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Application.Modpacks;
using SubReel.Launcher.Minecraft.Downloads;
using SubReel.Launcher.Minecraft.Installation;
using SubReel.Launcher.Minecraft.Instances;
using SubReel.Launcher.Minecraft.Java;
using SubReel.Launcher.Minecraft.Launching;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Loaders;
using SubReel.Launcher.Minecraft.Modpacks;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Minecraft;

public static class MinecraftServiceCollectionExtensions
{
    /// <summary>
    /// Игровое ядро: менеджер загрузок, экземпляры, установка и проверка версий. Требует <c>AddSubReelInfrastructure</c> (AppPaths, JsonFileStore)
    /// и <c>AddSubReelApplication</c> (TimeProvider). Настройки загрузок — раздел «Downloads» конфигурации, привязывает приложение.
    /// </summary>
    public static IServiceCollection AddSubReelMinecraft(this IServiceCollection services)
    {
        services.AddOptions<DownloadOptions>();
        services.AddOptions<JavaOptions>();

        // Общий Timeout отключён: файлы бывают большими, «зависание» отслеживает сам менеджер (StallTimeout).
        services.AddHttpClient(HttpDownloadManager.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReel-Launcher/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });

        services.AddSingleton<IDownloadManager, HttpDownloadManager>();
        services.AddSingleton<IInstanceRepository, JsonInstanceRepository>();
        services.AddSingleton<InstanceService>();

        // Установка версий: общий кэш игры, описание версий, список файлов, установка и проверка, нативные библиотеки.
        services.AddSingleton(OsEnvironmentDetector.Detect());
        services.AddSingleton<GameFileLayout>();
        services.AddSingleton<VersionManifestSource>();
        services.AddSingleton<IVersionMetadataProvider, VersionMetadataProvider>();
        services.AddSingleton<GameFilePlanner>();
        services.AddSingleton<VerifiedFilesCache>();
        services.AddSingleton<IGameInstaller, GameInstaller>();
        services.AddSingleton<INativesExtractor, NativesExtractor>();

        // Загрузчики (фаза 10): список сборок и профили Fabric и Quilt с их мета-серверов.
        services.AddSingleton(LoaderEndpoints.Default);
        services.AddSingleton<ILoaderCatalog, LoaderCatalog>();
        services.AddSingleton<ILoaderInstaller, LoaderInstaller>();

        // Сборки (фаза 11): импорт .mrpack.
        services.AddSingleton<IModpackImporter, MrpackImporter>();

        // Java Manager: источники папок Java (каждый добавляет своих кандидатов), чтение версии, рантаймы Mojang.
        services.AddSingleton<IJavaHomeSource, JavaHomeEnvironmentSource>();
        services.AddSingleton<IJavaHomeSource, KnownFoldersJavaSource>();
        services.AddSingleton<IJavaHomeSource, OfficialLauncherJavaSource>();
        services.AddSingleton<IJavaHomeSource, WindowsRegistryJavaSource>();
        services.AddSingleton<JavaVersionReader>();
        services.AddSingleton<ManagedJavaInstaller>();
        services.AddSingleton<IJavaManager, JavaManager>();
        return services;
    }

    /// <summary>
    /// Запуск игры (фаза 9). Отдельный вызов, потому что нужен <c>IGameSessionSource</c> — его даёт <c>AddSubReelAuthentication</c>:
    /// без входа и менеджера сессий запускать игру нечем. Вызывается после <see cref="AddSubReelMinecraft"/> и AddSubReelAuthentication.
    /// </summary>
    public static IServiceCollection AddSubReelGameLauncher(this IServiceCollection services)
    {
        services.AddSingleton<ClasspathBuilder>();
        services.AddSingleton<LegacyAssetsPreparer>();
        services.AddSingleton<IGameProcessStarter, SystemGameProcessStarter>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<GamePlayService>();
        return services;
    }
}
