using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Application.Versions;

namespace SubReel.Launcher.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Сервисы слоя Application. Реализации интерфейсов (диск, сеть) регистрирует Infrastructure.</summary>
    public static IServiceCollection AddSubReelApplication(this IServiceCollection services)
    {
        services.AddSingleton<AccountService>();
        services.AddSingleton<VersionStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INewsService, NewsService>();
        return services;
    }
}
