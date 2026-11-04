using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Modpacks;
using SubReel.Launcher.Mods.CurseForge;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Mods.Modrinth;

namespace SubReel.Launcher.Mods;

public static class ModsServiceCollectionExtensions
{
    private const string ModrinthClientName = "modrinth";
    private const string CurseForgeClientName = "curseforge";

    /// <summary>
    /// Моды (фаза 11): каталог Modrinth и менеджер модов сборки. Требует <c>AddSubReelInfrastructure</c> (JsonFileStore) и <c>AddSubReelMinecraft</c>
    /// (менеджер загрузок, экземпляры).
    /// </summary>
    public static IServiceCollection AddSubReelMods(this IServiceCollection services)
    {
        // Modrinth требует уникальный User-Agent с названием приложения; общий таймаут нужен: ответы маленькие.
        // Каталоги регистрируются под своими именами клиентов: у них разные адреса и заголовки. Менеджер получает все каталоги и выбирает по источнику мода.
        services.AddHttpClient(ModrinthClientName, client =>
        {
            client.BaseAddress = new Uri(ModrinthCatalog.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SBUnivers/SubReel.Launcher/0.1 (github.com/SBUnivers/SubReel.Launcher)"); // формат «автор/проект/версия» не проходит разбор ProductInfoHeaderValue
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddHttpClient(CurseForgeClientName, client =>
        {
            client.BaseAddress = new Uri(CurseForgeCatalog.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SBUnivers/SubReel.Launcher/0.1"); // формат «автор/проект/версия» не проходит разбор ProductInfoHeaderValue
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddSingleton<ModrinthCatalog>(sp => new ModrinthCatalog(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ModrinthClientName), sp.GetRequiredService<ILogger<ModrinthCatalog>>()));
        services.AddSingleton<CurseForgeCatalog>(sp => new CurseForgeCatalog(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(CurseForgeClientName), sp.GetRequiredService<ICurseForgeKey>(), sp.GetRequiredService<ILogger<CurseForgeCatalog>>()));
        services.AddSingleton<IModCatalog>(sp => sp.GetRequiredService<ModrinthCatalog>());
        services.AddSingleton<IModCatalog>(sp => sp.GetRequiredService<CurseForgeCatalog>());

        services.AddSingleton<IModManager, ModManager>();
        services.AddSingleton<ICurseForgeImporter, CurseForgeImporter>();
        return services;
    }
}
