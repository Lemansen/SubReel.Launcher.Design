namespace SubReel.Launcher.Domain.Versions;

/// <summary>Тип загрузчика. Порядок значений = порядок секций в выпадающем списке версий.</summary>
public enum LoaderKind { Vanilla, Fabric, Forge, NeoForge, Quilt, Other }

/// <summary>Одна установленная версия из папки <c>versions</c> игры (ваниль или версия с загрузчиком).</summary>
public sealed class InstalledVersion
{
    /// <summary>Имя папки в <c>versions</c> (например, <c>fabric-loader-0.16.5-1.21.1</c>) — по нему версия сохраняется в настройках.</summary>
    public required string Id { get; init; }
    /// <summary>Версия Minecraft, на которой основана сборка (1.21.1).</summary>
    public required string McVersion { get; init; }
    public required LoaderKind Loader { get; init; }
    /// <summary>Версия загрузчика (0.16.5 / 47.2.0 / 21.1.50), если удалось определить.</summary>
    public string? LoaderVersion { get; init; }
}

public sealed class InstalledScanResult
{
    public IReadOnlyList<InstalledVersion> Versions { get; init; } = Array.Empty<InstalledVersion>();
    public string GameDirectory { get; init; } = "";
    /// <summary>false — папки <c>versions</c> нет вообще (игра не установлена или лежит в другом месте).</summary>
    public bool DirectoryFound { get; init; }
}
