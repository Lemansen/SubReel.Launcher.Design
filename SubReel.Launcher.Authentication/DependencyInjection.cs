using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Вход через Microsoft и менеджер сессий. Требует зарегистрированных <see cref="ITokenStore"/> (подключается отдельно),
    /// <c>AccountService</c> и <see cref="AuthenticationOptions"/> (раздел <c>Authentication</c>).
    /// </summary>
    public static IServiceCollection AddSubReelAuthentication(this IServiceCollection services)
    {
        services.AddHttpClient<MicrosoftAccountProvider>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReel-Launcher/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<IAccountProvider>(provider => provider.GetRequiredService<MicrosoftAccountProvider>());
        services.AddSingleton<SessionManager>();
        services.AddSingleton<IGameSessionSource>(provider => provider.GetRequiredService<SessionManager>());
        return services;
    }
}
