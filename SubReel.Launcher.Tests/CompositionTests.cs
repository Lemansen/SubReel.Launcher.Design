using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Infrastructure;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft;

namespace SubReel.Launcher.Tests;

/// <summary>
/// Проверяет корень композиции без WPF: слои Infrastructure и Application регистрируются так же, как в App.OnStartup,
/// а контейнер строится с проверкой всех зависимостей (ValidateOnBuild) — пропущенная регистрация или цикл падают здесь, а не при запуске.
/// Регистрация WPF-части (ViewModel-и, окно) проверяется только запуском приложения на Windows.
/// </summary>
public sealed class CompositionTests
{
    private static ServiceProvider Build(TempDir dir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<NewsOptions>();
        services
            .AddSubReelInfrastructure(new AppPaths(dir.Root, dir.Root))
            .AddSubReelApplication()
            .AddSubReelMinecraft();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Container_builds_and_resolves_every_service_the_application_layer_exposes()
    {
        using var dir = new TempDir();
        using var provider = Build(dir);

        Assert.NotNull(provider.GetRequiredService<ISettingsService>());
        Assert.NotNull(provider.GetRequiredService<IInstalledVersionService>());
        Assert.NotNull(provider.GetRequiredService<IVersionCatalogService>());
        Assert.NotNull(provider.GetRequiredService<IAvatarStorage>());
        Assert.NotNull(provider.GetRequiredService<IImageCache>());
        Assert.NotNull(provider.GetRequiredService<INewsCache>());
        Assert.NotNull(provider.GetRequiredService<AccountService>());
        Assert.NotNull(provider.GetRequiredService<VersionStore>());
        Assert.NotNull(provider.GetRequiredService<INewsService>());
        Assert.NotNull(provider.GetRequiredService<IDownloadManager>());
        Assert.NotNull(provider.GetRequiredService<IInstanceRepository>());
        Assert.NotNull(provider.GetRequiredService<InstanceService>());
        Assert.NotNull(provider.GetRequiredService<IGameInstaller>());
        Assert.NotNull(provider.GetRequiredService<IVersionMetadataProvider>());
        Assert.NotNull(provider.GetRequiredService<INativesExtractor>());
        Assert.NotNull(provider.GetRequiredService<IJavaManager>());
        Assert.NotNull(provider.GetRequiredService<ILoaderCatalog>());
        Assert.NotNull(provider.GetRequiredService<ILoaderInstaller>());
    }

    [Fact]
    public void Shared_state_services_are_singletons()
    {
        using var dir = new TempDir();
        using var provider = Build(dir);

        Assert.Same(provider.GetRequiredService<VersionStore>(), provider.GetRequiredService<VersionStore>());
        Assert.Same(provider.GetRequiredService<AccountService>(), provider.GetRequiredService<AccountService>());
        Assert.Same(provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<ISettingsService>());
    }

    [Fact]
    public void Both_news_sources_are_registered_and_subreel_source_is_not_configured_by_default()
    {
        using var dir = new TempDir();
        using var provider = Build(dir);

        var sources = provider.GetServices<INewsSource>().ToList();

        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.IsConfigured);
        Assert.Contains(sources, s => !s.IsConfigured);
    }
}
