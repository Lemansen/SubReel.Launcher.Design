using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Design.Views;

namespace SubReel.Launcher.Design.DependencyInjection;

internal static class PresentationServiceCollectionExtensions
{
    /// <summary>Сервисы и ViewModel-и WPF-приложения. Всё живёт весь сеанс, поэтому регистрируется как singleton.</summary>
    public static IServiceCollection AddSubReelPresentation(this IServiceCollection services)
    {
        // Сервисы, привязанные к WPF
        services.AddSingleton<IImageService, ImageService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IBrowserService, BrowserService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        // ViewModel-и
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ToastViewModel>();
        services.AddSingleton<IToastService>(provider => provider.GetRequiredService<ToastViewModel>());
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<LaunchViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<VersionDropdownViewModel>();
        services.AddSingleton<NewsViewModel>();
        services.AddSingleton<BuildsViewModel>();
        services.AddSingleton<HubViewModel>();
        services.AddSingleton<IOverlayFactory, OverlayFactory>();
        services.AddSingleton<MainViewModel>();

        // Окно
        services.AddSingleton<MainWindow>();
        return services;
    }
}
