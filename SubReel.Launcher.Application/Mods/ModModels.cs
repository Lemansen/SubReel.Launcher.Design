using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Mods;

/// <summary>Не удалось получить данные каталога модов: нет сети, сервис ответил ошибкой или ответ не читается. Текст можно показать пользователю.</summary>
public sealed class ModCatalogException : Exception
{
    public ModCatalogException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Мод в каталоге (Modrinth).</summary>
/// <param name="Id">Идентификатор проекта.</param>
/// <param name="Slug">Короткое имя для адреса страницы.</param>
public sealed record ModProject(string Id, string Slug, string Title, string Summary, string Author, long Downloads, string? IconUrl);

/// <param name="Text">Поисковый запрос; пустой — «популярное».</param>
/// <param name="McVersion">Версия игры сборки: показываются только подходящие моды.</param>
/// <param name="Loader">Загрузчик сборки (ваниль модов не принимает).</param>
public sealed record ModSearchQuery(string Text, string McVersion, LoaderKind Loader, int Offset = 0, int Limit = 20);

public sealed record ModSearchResult(IReadOnlyList<ModProject> Hits, int TotalHits);

/// <summary>Откуда мод: от этого зависит, какой каталог ищет версии и обновления.</summary>
public enum ModSource { Modrinth, CurseForge }

public enum ModReleaseType { Release, Beta, Alpha }

public enum ModDependencyKind { Required, Optional, Incompatible, Embedded }

/// <summary>Зависимость версии мода. У записи заполнен проект, версия или оба поля.</summary>
public sealed record ModDependency(string? ProjectId, string? VersionId, ModDependencyKind Kind);

/// <param name="FileName">Имя файла без пути, проверено: только <c>.jar</c> и без разделителей пути.</param>
/// <param name="Url">https-адрес на доверенном узле.</param>
/// <param name="Sha1">hex в нижнем регистре.</param>
/// <param name="Sha512">hex в нижнем регистре; null, если источник его не даёт (CurseForge) — тогда проверяется только SHA-1.</param>
public sealed record ModFile(string FileName, string Url, string Sha1, string? Sha512, long Size);

public sealed record ModVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    ModReleaseType Type,
    DateTime PublishedUtc,
    IReadOnlyList<ModDependency> Dependencies,
    ModFile File);

/// <summary>Результат проверки обновлений: для каждого SHA-1 установленного файла — самая свежая подходящая версия (если она есть).</summary>
public sealed record ModUpdateOffer(string InstalledSha1, ModVersion Latest);

/// <summary>Источник модов: поиск и версии. Реализация — Modrinth; CurseForge добавится отдельной реализацией.</summary>
public interface IModCatalog
{
    ModSource Source { get; }

    Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct = default);

    /// <summary>Свежие версии проекта, подходящие для игры и загрузчика сборки, новые первыми. Пусто — подходящих нет.</summary>
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string mcVersion, LoaderKind loader, CancellationToken ct = default);

    Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct = default);

    Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyList<string> projectIds, CancellationToken ct = default);

    /// <summary>Какие версии соответствуют файлам на диске (по SHA-1). Неизвестные файлы в ответ не попадают.</summary>
    Task<IReadOnlyDictionary<string, ModVersion>> IdentifyFilesAsync(IReadOnlyList<string> sha1Hashes, CancellationToken ct = default);

    /// <summary>Самые свежие подходящие версии для файлов на диске (по SHA-1), в том числе совпадающие с установленными.</summary>
    Task<IReadOnlyDictionary<string, ModVersion>> FindLatestForFilesAsync(IReadOnlyList<string> sha1Hashes, string mcVersion, LoaderKind loader, CancellationToken ct = default);
}
