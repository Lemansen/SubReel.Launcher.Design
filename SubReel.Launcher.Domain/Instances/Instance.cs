using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Domain.Instances;

/// <summary>
/// Экземпляр (сборка): отдельная папка с версией игры, загрузчиком, своими настройками запуска, модами и сохранениями.
/// Идентификатор совпадает с именем папки экземпляра и не меняется при переименовании.
/// </summary>
public sealed class Instance
{
    /// <summary>Имя папки экземпляра. Назначается при создании хранилищем; у ещё не сохранённого экземпляра пусто.</summary>
    public string Id { get; set; } = "";

    /// <summary>Название, которое видит пользователь.</summary>
    public required string Name { get; set; }

    /// <summary>Версия Minecraft (1.21.1).</summary>
    public required string McVersion { get; set; }

    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    /// <summary>Версия загрузчика; для ванили пусто.</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>Максимум памяти для JVM (МБ). null — значение по умолчанию.</summary>
    public int? MaxMemoryMb { get; set; }

    /// <summary>Начальный размер памяти JVM (МБ). null — значение по умолчанию.</summary>
    public int? MinMemoryMb { get; set; }

    /// <summary>Дополнительные аргументы JVM одной строкой.</summary>
    public string JvmArguments { get; set; } = "";

    /// <summary>Путь к java; пусто — выбрать автоматически (Java Manager).</summary>
    public string? JavaPath { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastPlayedUtc { get; set; }
}
