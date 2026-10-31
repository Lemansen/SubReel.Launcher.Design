using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Application.Java;

/// <summary>Настройки Java Manager. Раздел конфигурации «Java».</summary>
public sealed class JavaOptions
{
    public const string SectionName = "Java";

    /// <summary>Список рантаймов Mojang. Вынесен в настройки, чтобы сменить адрес без перекомпиляции.</summary>
    public string RuntimeIndexUrl { get; set; } = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
}

public sealed record JavaEnsureResult(JavaInstallation? Installation, JavaSelectionStatus Status, bool VersionMismatch, bool Downloaded, string? Error)
{
    public bool Succeeded => Installation is not null;
}

/// <summary>Поиск, выбор и установка Java для запуска игры.</summary>
public interface IJavaManager
{
    /// <summary>Установленные Java: найденные в системе и скачанные лаунчером. Результат кэшируется до <paramref name="refresh"/> = true.</summary>
    Task<IReadOnlyList<JavaInstallation>> GetInstallationsAsync(bool refresh = false, CancellationToken ct = default);

    /// <summary>Проверяет путь (java, java.exe или папка установки), заданный пользователем. null — это не рабочая Java.</summary>
    Task<JavaInstallation?> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>Подбирает Java без скачивания. <paramref name="explicitJavaPath"/> — путь из настроек экземпляра: если задан, он главнее правила подбора.</summary>
    Task<JavaSelection> SelectAsync(JavaRequirement requirement, string? explicitJavaPath = null, CancellationToken ct = default);

    /// <summary>Подбирает Java и при необходимости скачивает рантайм Mojang. Ожидаемые сбои — в результате, отмена — исключением.</summary>
    Task<JavaEnsureResult> EnsureAsync(JavaRequirement requirement, string? explicitJavaPath = null, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default);
}
