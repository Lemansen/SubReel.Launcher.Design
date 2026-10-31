namespace SubReel.Launcher.Domain.Versions;

/// <summary>Как загрузчик называется и какой монограммой подписывается. Цвета — забота представления (см. LoaderAccent в приложении).</summary>
public static class LoaderInfo
{
    public static string Name(LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "Vanilla",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Quilt => "Quilt",
        _ => "Другие загрузчики",
    };

    public static string Monogram(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fa",
        LoaderKind.Forge => "Fo",
        LoaderKind.NeoForge => "NF",
        LoaderKind.Quilt => "Q",
        _ => "•",
    };
}
