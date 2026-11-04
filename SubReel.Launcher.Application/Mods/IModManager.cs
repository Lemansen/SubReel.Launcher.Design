using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Application.Mods;

/// <summary>Как мод попал в сборку: выбрал пользователь или поставлен как чья-то зависимость.</summary>
public enum ModInstallReason { UserChoice, Dependency }

/// <summary>Мод, который лаунчер знает в сборке.</summary>
/// <param name="RequiredBy">Проекты, которым этот мод нужен как зависимость (у выбранных пользователем может быть пусто).</param>
/// <param name="Requires">Проекты, без которых этот мод не работает (обязательные зависимости его версии).</param>
public sealed record InstalledMod(
    string ProjectId,
    string Title,
    string VersionId,
    string VersionNumber,
    DateTime PublishedUtc,
    string FileName,
    string Sha1,
    ModInstallReason Reason,
    IReadOnlyList<string> RequiredBy,
    IReadOnlyList<string> Requires,
    ModSource Source = ModSource.Modrinth);

/// <summary>Одна позиция плана: что скачать и зачем.</summary>
public sealed record ModPlanItem(ModProject Project, ModVersion Version, ModInstallReason Reason, string? RequiredByProjectId);

/// <summary>
/// План установки или обновления. Если <see cref="Error"/> заполнен — ставить нечего, текст можно показать пользователю.
/// <see cref="Conflicts"/> — установленные моды, объявленные несовместимыми с планом: установка в этом случае не выполняется.
/// </summary>
public sealed record ModInstallPlan(IReadOnlyList<ModPlanItem> Items, IReadOnlyList<string> Conflicts, string? Error, ModSource Source = ModSource.Modrinth)
{
    public bool CanInstall => Error is null && Conflicts.Count == 0 && Items.Count > 0;

    public static ModInstallPlan Fail(string error) => new(Array.Empty<ModPlanItem>(), Array.Empty<string>(), error);
}

public sealed record ModInstallResult(bool Succeeded, string? Error, IReadOnlyList<InstalledMod> Installed)
{
    public static ModInstallResult Ok(IReadOnlyList<InstalledMod> installed) => new(true, null, installed);

    public static ModInstallResult Fail(string error) => new(false, error, Array.Empty<InstalledMod>());
}

/// <param name="Mods">Моды, о которых лаунчер знает (отслеживаются), по названию.</param>
/// <param name="Unrecognized">Файлы из папки mods, которых нет в каталоге (чужие, самодельные): лаунчер их не трогает.</param>
public sealed record ModListing(IReadOnlyList<InstalledMod> Mods, IReadOnlyList<string> Unrecognized);

/// <summary>Обновление для установленного мода.</summary>
public sealed record ModUpdateInfo(InstalledMod Installed, ModVersion Latest);

/// <param name="Blocked">Удаление не выполнено: другим модам этот нужен. Список — их названия. Повторный вызов с <c>force</c> удалит всё равно.</param>
/// <param name="Orphans">Моды, которые ставились только ради удалённого и теперь никому не нужны (удалены, если просили).</param>
public sealed record ModRemoveResult(bool Removed, IReadOnlyList<string> Blocked, IReadOnlyList<InstalledMod> Orphans, string? Error);

/// <summary>
/// Моды сборки: поиск совместимой версии, зависимости, установка, обновление и удаление. Все операции над одной сборкой выполняются по очереди.
/// Ожидаемые сбои (нет сети, не тот загрузчик, неверный хэш) возвращаются в результате, отмена — <see cref="OperationCanceledException"/>.
/// </summary>
public interface IModManager
{
    /// <summary>Моды сборки. Файлы в папке mods, которые лаунчер не ставил, но каталог знает (например, из импортированной сборки), берутся под учёт.</summary>
    Task<ModListing> ListAsync(Instance instance, CancellationToken ct = default);

    /// <summary>Что понадобится поставить для выбранного мода: он сам и обязательные зависимости, которых в сборке ещё нет.</summary>
    Task<ModInstallPlan> PlanInstallAsync(Instance instance, string projectId, ModSource source = ModSource.Modrinth, CancellationToken ct = default);

    Task<ModInstallResult> InstallAsync(Instance instance, ModInstallPlan plan, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Какие из установленных модов можно обновить. <see cref="ModCatalogException"/> — каталог недоступен (нет сети).</summary>
    Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(Instance instance, CancellationToken ct = default);

    /// <summary>Заменяет файл мода на свежую версию (и ставит новые обязательные зависимости). Старый файл удаляется только после успешной загрузки новых.</summary>
    Task<ModInstallResult> UpdateAsync(Instance instance, string projectId, IProgress<double>? progress = null, CancellationToken ct = default);

    Task<ModRemoveResult> RemoveAsync(Instance instance, string projectId, bool force = false, bool removeOrphans = false, CancellationToken ct = default);
}
