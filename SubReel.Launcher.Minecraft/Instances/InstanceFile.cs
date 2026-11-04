namespace SubReel.Launcher.Minecraft.Instances;

/// <summary>Формат файла <c>instance.json</c>. Загрузчик хранится строкой: неизвестное значение из будущей версии не делает файл нечитаемым.</summary>
internal sealed class InstanceFile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>У файла без этого поля версия 0 — формат совместим с первой.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "Vanilla";
    public string? LoaderVersion { get; set; }
    public int? MinMemoryMb { get; set; }
    public int? MaxMemoryMb { get; set; }
    public string JvmArguments { get; set; } = "";
    public string? JavaPath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public long PlayTimeSeconds { get; set; }
}
