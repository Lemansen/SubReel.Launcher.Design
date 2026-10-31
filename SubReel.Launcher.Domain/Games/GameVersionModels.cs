namespace SubReel.Launcher.Domain.Games;

public sealed record FileDownloadInfo(string Url, string? Sha1, long? Size);

/// <summary>Файл библиотеки: путь относительно папки libraries и откуда его скачать.</summary>
public sealed record LibraryArtifact(string Path, FileDownloadInfo Download);

public sealed record Library(
    string Name,
    LibraryArtifact? Artifact,
    IReadOnlyDictionary<string, string> NativeClassifiers,
    IReadOnlyDictionary<string, LibraryArtifact> Classifiers,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<string> ExtractExclude)
{
    public string Key => MavenCoordinate.KeyOf(Name);

    /// <summary>
    /// Классификатор нативного архива для платформы (старый способ: поле natives), например <c>natives-windows</c>.
    /// null — у библиотеки нет нативов для этой платформы. Новые версии кладут нативы отдельными библиотеками, для них здесь null.
    /// </summary>
    public string? GetNativeClassifier(OsEnvironment environment) =>
        NativeClassifiers.TryGetValue(environment.OsName, out var classifier)
            ? classifier.Replace("${arch}", environment.Is64Bit ? "64" : "32")
            : null;
}

/// <summary>Аргумент запуска; применяется, только если правила разрешают (у обычных аргументов правил нет).</summary>
public sealed record ArgumentValue(IReadOnlyList<string> Values, IReadOnlyList<Rule> Rules);

public sealed record AssetIndexInfo(string Id, FileDownloadInfo Download);

public sealed record LoggingConfigInfo(string Argument, string FileId, FileDownloadInfo Download);

/// <summary>Содержимое одного version.json как есть, без учёта наследования.</summary>
public sealed class GameVersionMetadata
{
    public required string Id { get; init; }
    public string? Type { get; init; }
    public string? InheritsFrom { get; init; }
    public string? MainClass { get; init; }
    public string? Assets { get; init; }
    public AssetIndexInfo? AssetIndex { get; init; }
    public FileDownloadInfo? ClientDownload { get; init; }
    public IReadOnlyList<Library> Libraries { get; init; } = Array.Empty<Library>();
    public IReadOnlyList<ArgumentValue> JvmArguments { get; init; } = Array.Empty<ArgumentValue>();
    public IReadOnlyList<ArgumentValue> GameArguments { get; init; } = Array.Empty<ArgumentValue>();
    /// <summary>Строка minecraftArguments у версий до 1.13.</summary>
    public string? LegacyGameArguments { get; init; }
    public LoggingConfigInfo? Logging { get; init; }
    public int? JavaMajorVersion { get; init; }
    /// <summary>Имя рантайма Mojang (java-runtime-delta, jre-legacy …).</summary>
    public string? JavaComponent { get; init; }
}

public sealed record AssetObject(string Hash, long Size);

/// <summary>Индекс ассетов: имя ресурса → объект в хранилище по хэшу.</summary>
public sealed record AssetIndex(IReadOnlyDictionary<string, AssetObject> Objects, bool IsVirtual, bool MapToResources);

/// <summary>Версия с учётом цепочки inheritsFrom: всё, что нужно для установки и запуска.</summary>
public sealed class ResolvedVersion
{
    public required string Id { get; init; }
    /// <summary>Идентификаторы цепочки, потомок первым (fabric-loader-… → 1.21.1).</summary>
    public required IReadOnlyList<string> Chain { get; init; }
    public required string MainClass { get; init; }
    public required string AssetsId { get; init; }
    public AssetIndexInfo? AssetIndex { get; init; }
    public FileDownloadInfo? ClientDownload { get; init; }
    /// <summary>Версия, чей jar используется как клиент (у Fabric это родительская ваниль).</summary>
    public required string ClientJarVersionId { get; init; }
    public required IReadOnlyList<Library> Libraries { get; init; }
    public required IReadOnlyList<ArgumentValue> JvmArguments { get; init; }
    public required IReadOnlyList<ArgumentValue> GameArguments { get; init; }
    public string? LegacyGameArguments { get; init; }
    public LoggingConfigInfo? Logging { get; init; }
    public int? JavaMajorVersion { get; init; }
    public string? JavaComponent { get; init; }
    public string? Type { get; init; }
}
