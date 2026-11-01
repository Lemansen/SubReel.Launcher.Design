using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Authentication;
using SubReel.Launcher.Infrastructure;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft;

namespace SubReel.Launcher.Tests;

public sealed class GameLauncherCompositionTests
{
    [Fact]
    public void Game_launcher_resolves_in_the_container_assembled_like_the_application()
    {
        using var dir = new TempDir();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<NewsOptions>();
        services.AddOptions<AuthenticationOptions>();
        services
            .AddSubReelInfrastructure(new AppPaths(dir.Root, dir.Root))
            .AddSubReelApplication()
            .AddSubReelMinecraft()
            .AddSubReelAuthentication()
            .AddSubReelGameLauncher();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IGameLauncher>());
        Assert.NotNull(provider.GetRequiredService<GamePlayService>());
        Assert.Same(provider.GetRequiredService<SessionManager>(), provider.GetRequiredService<IGameSessionSource>());
    }
}
