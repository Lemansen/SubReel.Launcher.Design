using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Infrastructure.Authentication;
using SubReel.Launcher.Infrastructure.Avatars;
using SubReel.Launcher.Infrastructure.Images;
using SubReel.Launcher.Infrastructure.Network;
using SubReel.Launcher.Infrastructure.News;
using SubReel.Launcher.Infrastructure.Security;
using SubReel.Launcher.Infrastructure.Settings;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Infrastructure.Versions;

namespace SubReel.Launcher.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Реализации интерфейсов слоя Application: файлы, сеть, папка игры.</summary>
    public static IServiceCollection AddSubReelInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<JsonFileStore>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IInstalledVersionService, InstalledVersionService>();
        services.AddSingleton<IAvatarStorage, AvatarStorage>();
        services.AddSingleton<INetworkMonitor, NetworkMonitor>();

        // Токены входа: шифрование DPAPI (только Windows) и файловое хранилище поверх него.
        // TryAdd — чтобы тесты и другие платформы могли подставить свой ISecretProtector до этого вызова.
        services.TryAddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : throw new PlatformNotSupportedException("Защищённое хранилище токенов поддерживается только в Windows."));
        services.AddSingleton<ITokenStore, ProtectedFileTokenStore>();

        // HttpClient выдаёт фабрика: соединения переиспользуются, а DNS обновляется (PooledConnectionLifetime).
        services.AddHttpClient<IVersionCatalogService, MojangVersionCatalogService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReel-Launcher/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // Новости: источники получают HttpClient из фабрики; адрес ленты SubReel — из конфигурации (NewsOptions).
        services.AddSingleton<INewsCache, JsonNewsCache>();
        services.AddHttpClient<MojangNewsSource>(ConfigureJsonClient).ConfigurePrimaryHttpMessageHandler(CreatePooledHandler).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddHttpClient<SubReelNewsSource>(ConfigureJsonClient).ConfigurePrimaryHttpMessageHandler(CreatePooledHandler).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton<INewsSource>(provider => provider.GetRequiredService<MojangNewsSource>());
        services.AddSingleton<INewsSource>(provider => provider.GetRequiredService<SubReelNewsSource>());

        // Картинки: именованный клиент с увеличенным таймаутом (картинки больше, чем JSON).
        services.AddHttpClient(DiskImageCache.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReel-Launcher/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(CreatePooledHandler);
        services.AddSingleton<IImageCache, DiskImageCache>();

        return services;
    }

    private static void ConfigureJsonClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReel-Launcher/0.1");
    }

    private static HttpMessageHandler CreatePooledHandler() =>
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
}
