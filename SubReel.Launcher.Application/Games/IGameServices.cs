using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Application.Games;

/// <summary>Описание версии с учётом наследования (inheritsFrom). Берёт файлы с диска, при необходимости скачивает по каталогу Mojang.</summary>
public interface IVersionMetadataProvider
{
    /// <summary>
    /// <paramref name="allowNetwork"/> = false — только то, что уже лежит на диске (проверка перед запуском без сети).
    /// Бросает <see cref="VersionMetadataException"/> (в том числе <see cref="VersionNotFoundException"/>); отмена — <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ResolvedVersion> ResolveAsync(string versionId, bool allowNetwork = true, CancellationToken ct = default);
}

/// <summary>Установка и проверка файлов версии в общем кэше (клиент, библиотеки, ассеты, конфигурация логирования), общем для всех экземпляров.</summary>
public interface IGameInstaller
{
    /// <summary>Ожидаемые сбои (нет сети, версия не найдена, неверный хэш) возвращаются в <see cref="InstallResult"/>, отмена — исключением.</summary>
    Task<InstallResult> InstallAsync(string versionId, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Сверяет файлы с описанием версии без обращения к сети: наличие, размер, SHA-1.</summary>
    Task<VerificationResult> VerifyAsync(string versionId, CancellationToken ct = default);
}

/// <summary>Распаковка нативных библиотек версии (старый формат natives) в папку экземпляра перед запуском.</summary>
public interface INativesExtractor
{
    /// <summary>Возвращает число распакованных файлов. Нужные jar должны быть установлены, иначе <see cref="FileNotFoundException"/>.</summary>
    Task<int> ExtractAsync(ResolvedVersion version, string targetDirectory, CancellationToken ct = default);
}
