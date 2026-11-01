using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Loaders;

/// <summary>Список сборок загрузчика получить не удалось (нет сети и нет сохранённой копии, ответ сервера не разобрать).</summary>
public sealed class LoaderCatalogException : Exception
{
    public LoaderCatalogException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Какие сборки загрузчика есть для версии игры. Источник — мета-сервер загрузчика (Fabric, Quilt).</summary>
public interface ILoaderCatalog
{
    /// <summary>
    /// Сборки загрузчика для версии игры, новые первыми. Пустой список — загрузчик эту версию не поддерживает
    /// (или он не из тех, что лаунчер ставит сам: Forge и NeoForge). Нет сети — берётся сохранённая копия, если она есть.
    /// Бросает <see cref="LoaderCatalogException"/>; отмена — <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<IReadOnlyList<LoaderBuild>> GetBuildsAsync(LoaderKind kind, string mcVersion, CancellationToken ct = default);
}

/// <param name="VersionId">Имя версии в общем кэше игры — с ним запускается игра (профиль с <c>inheritsFrom</c>).</param>
/// <param name="LoaderVersion">Версия загрузчика, которая выбрана на самом деле (если просили «последнюю», здесь конкретный номер).</param>
public sealed record LoaderInstallResult(bool Succeeded, string? VersionId, string? LoaderVersion, string? Error)
{
    public static LoaderInstallResult Success(string versionId, string loaderVersion) => new(true, versionId, loaderVersion, null);

    public static LoaderInstallResult Failure(string error) => new(false, null, null, error);
}

/// <summary>Кладёт профиль загрузчика в общий кэш игры. Библиотеки загрузчика и ванильный родитель скачивает обычная установка версии.</summary>
public interface ILoaderInstaller
{
    /// <summary>
    /// Гарантирует, что профиль загрузчика на месте. <paramref name="loaderVersion"/> = null — взять последнюю стабильную сборку.
    /// Профиль, который уже лежит на диске и читается, повторно не скачивается. Ожидаемые сбои возвращаются в результате, отмена — исключением.
    /// </summary>
    Task<LoaderInstallResult> EnsureAsync(LoaderKind kind, string mcVersion, string? loaderVersion, CancellationToken ct = default);
}
