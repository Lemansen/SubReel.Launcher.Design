using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Minecraft.Loaders;

/// <summary>
/// Адреса мета-серверов загрузчиков. Fabric — <c>meta.fabricmc.net/v2</c>, Quilt — <c>meta.quiltmc.org/v3</c>: форматы ответов у них общие
/// (<c>versions/loader/&lt;игра&gt;</c> — список сборок, <c>versions/loader/&lt;игра&gt;/&lt;загрузчик&gt;/profile/json</c> — профиль для лаунчера).
/// Отдельным объектом, чтобы тесты подставляли свой «сервер».
/// </summary>
public sealed record LoaderEndpoints(string FabricMeta, string QuiltMeta)
{
    public static LoaderEndpoints Default { get; } = new("https://meta.fabricmc.net/v2", "https://meta.quiltmc.org/v3");

    /// <summary>Список сборок загрузчика для версии игры.</summary>
    public string BuildListUrl(LoaderKind kind, string mcVersion) =>
        $"{Base(kind)}/versions/loader/{Uri.EscapeDataString(mcVersion)}";

    /// <summary>Профиль (version.json) загрузчика, который подходит стандартному лаунчеру.</summary>
    public string ProfileUrl(LoaderKind kind, string mcVersion, string loaderVersion) =>
        $"{Base(kind)}/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

    private string Base(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => FabricMeta.TrimEnd('/'),
        LoaderKind.Quilt => QuiltMeta.TrimEnd('/'),
        _ => throw new NotSupportedException($"У загрузчика {LoaderInfo.Name(kind)} нет мета-сервера: его ставит установщик."),
    };
}
