using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Mods.Installed;
using SubReel.Launcher.Mods.Modrinth;

namespace SubReel.Launcher.Mods;

/// <summary>
/// Моды сборки. Список лежит в <c>mods.json</c> рядом с <c>instance.json</c>, файлы — в <c>minecraft/mods</c>. Правила:
/// <list type="bullet">
/// <item>обязательные зависимости ставятся вместе с модом (если их ещё нет), несовместимые установленные моды блокируют установку;</item>
/// <item>файл сверяется по SHA-1 (менеджер загрузок) и SHA-512 (здесь); при любом сбое и отмене новые файлы удаляются;</item>
/// <item>при обновлении старый файл удаляется только после того, как новые скачаны, проверены и записаны в список;</item>
/// <item>чужие файлы в папке mods не трогаются; известные каталогу берутся под учёт по хэшу;</item>
/// <item>операции над одной сборкой идут по очереди.</item>
/// </list>
/// </summary>
public sealed class ModManager : IModManager
{
    private const int MaxPlanItems = 50;

    private readonly IReadOnlyList<IModCatalog> _catalogs;
    private readonly IDownloadManager _downloads;
    private readonly InstanceService _instances;
    private readonly ModsManifestStore _manifests;
    private readonly ILogger<ModManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public ModManager(IEnumerable<IModCatalog> catalogs, IDownloadManager downloads, InstanceService instances, JsonFileStore store, ILogger<ModManager> logger)
    {
        _catalogs = catalogs.ToList();
        _downloads = downloads;
        _instances = instances;
        _manifests = new ModsManifestStore(store, logger);
        _logger = logger;
    }

    private IModCatalog CatalogFor(ModSource source) =>
        _catalogs.FirstOrDefault(c => c.Source == source) ?? throw new ModCatalogException("Этот источник модов недоступен.");

    // ───────────────────────── Список ─────────────────────────

    public async Task<ModListing> ListAsync(Instance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var paths = PathsFor(instance);

        using var lease = await EnterAsync(instance.Id, ct).ConfigureAwait(false);
        try
        {
            var manifest = LoadReconciled(paths, out var changed);

            var unrecognized = new List<string>();
            changed |= await AdoptKnownFilesAsync(paths, manifest, unrecognized, ct).ConfigureAwait(false);
            if (changed) _manifests.Save(paths.ManifestPath, manifest);

            var mods = manifest.Mods.Select(e => ToInstalled(e, manifest.Mods)).OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            unrecognized.Sort(StringComparer.CurrentCultureIgnoreCase);
            return new ModListing(mods, unrecognized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось прочитать моды сборки {Id}.", instance.Id);
            return new ModListing(Array.Empty<InstalledMod>(), Array.Empty<string>());
        }
    }

    /// <summary>Файлы папки mods, которых нет в списке: известные каталогу (по SHA-1) берутся под учёт, остальные уходят в <paramref name="unrecognized"/>.</summary>
    private async Task<bool> AdoptKnownFilesAsync(Paths paths, ModsFile manifest, List<string> unrecognized, CancellationToken ct)
    {
        if (!Directory.Exists(paths.ModsDirectory)) return false;

        var tracked = manifest.Mods.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untracked = Directory.EnumerateFiles(paths.ModsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(f => !tracked.Contains(Path.GetFileName(f)))
            .ToList();
        if (untracked.Count == 0) return false;

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // sha1 → путь
        foreach (var file in untracked)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sha1 = await Sha1OfAsync(file, ct).ConfigureAwait(false);
                if (!hashes.TryAdd(sha1, file)) unrecognized.Add(Path.GetFileName(file)); // точная копия другого файла
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Не удалось прочитать файл мода {File}.", file);
                unrecognized.Add(Path.GetFileName(file));
            }
        }

        IReadOnlyDictionary<string, ModVersion> known;
        IReadOnlyList<ModProject> projects;
        try
        {
            var modrinth = CatalogFor(ModSource.Modrinth);
            known = await modrinth.IdentifyFilesAsync(hashes.Keys.ToList(), ct).ConfigureAwait(false);
            projects = known.Count == 0
                ? Array.Empty<ModProject>()
                : await modrinth.GetProjectsAsync(known.Values.Select(v => v.ProjectId).Distinct().ToList(), ct).ConfigureAwait(false);
        }
        catch (ModCatalogException ex)
        {
            // Без сети моды просто остаются «неопознанными» до следующего открытия списка.
            _logger.LogInformation("Не удалось опознать файлы модов: {Message}", ex.Message);
            unrecognized.AddRange(hashes.Values.Select(f => Path.GetFileName(f)!));
            return false;
        }

        var changed = false;
        foreach (var (sha1, file) in hashes)
        {
            var name = Path.GetFileName(file);
            if (!known.TryGetValue(sha1, out var version) || manifest.Mods.Any(m => m.ProjectId == version.ProjectId))
            {
                unrecognized.Add(name); // неизвестный файл или второй файл того же мода — не трогаем
                continue;
            }

            var title = projects.FirstOrDefault(p => p.Id == version.ProjectId)?.Title ?? version.ProjectId;
            manifest.Mods.Add(NewEntry(version, title, ModInstallReason.UserChoice, name, sha1, ModSource.Modrinth));
            changed = true;
        }
        return changed;
    }

    // ───────────────────────── План ─────────────────────────

    public async Task<ModInstallPlan> PlanInstallAsync(Instance instance, string projectId, ModSource source = ModSource.Modrinth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (LoaderProblem(instance) is { } problem) return ModInstallPlan.Fail(problem);

        try
        {
            var catalog = CatalogFor(source);
            var manifest = LoadReconciled(PathsFor(instance), out _);
            if (manifest.Mods.Any(m => m.ProjectId == projectId)) return ModInstallPlan.Fail("Этот мод уже установлен.");

            var versions = await catalog.GetVersionsAsync(projectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false);
            var chosen = PickBest(versions);
            if (chosen is null)
            {
                var title = (await catalog.GetProjectsAsync(new[] { projectId }, ct).ConfigureAwait(false)).FirstOrDefault()?.Title ?? "Этот мод";
                return ModInstallPlan.Fail($"У мода «{title}» нет версии для Minecraft {instance.McVersion} и {Describe(instance)}.");
            }

            return await ResolveAsync(instance, catalog, chosen, ModInstallReason.UserChoice, manifest.Mods, replacingProjectId: null, ct).ConfigureAwait(false);
        }
        catch (ModCatalogException ex)
        {
            return ModInstallPlan.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось прочитать список модов сборки {Id}.", instance.Id);
            return ModInstallPlan.Fail("Не удалось прочитать список модов сборки.");
        }
    }

    /// <summary>
    /// Находит всё, что надо поставить вместе с <paramref name="root"/>: обязательные зависимости, которых нет в сборке (рекурсивно, не больше <see cref="MaxPlanItems"/> позиций).
    /// <paramref name="replacingProjectId"/> — проект, который сейчас обновляется: его старая запись зависимостью не считается.
    /// </summary>
    private async Task<ModInstallPlan> ResolveAsync(Instance instance, IModCatalog catalog, ModVersion root, ModInstallReason rootReason, IReadOnlyList<ModEntry> installed, string? replacingProjectId, CancellationToken ct)
    {
        var chosen = new List<(ModVersion Version, ModInstallReason Reason, string? RequiredBy)> { (root, rootReason, null) };
        var planned = new HashSet<string>(StringComparer.Ordinal) { root.ProjectId };
        var incompatible = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(ModDependency Dependency, string RequiredBy)>();

        void Collect(ModVersion version)
        {
            foreach (var dependency in version.Dependencies)
            {
                if (dependency.Kind == ModDependencyKind.Required) queue.Enqueue((dependency, version.ProjectId));
                else if (dependency.Kind == ModDependencyKind.Incompatible && dependency.ProjectId is { } id) incompatible.Add(id);
            }
        }

        Collect(root);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dependency, requiredBy) = queue.Dequeue();

            var projectId = dependency.ProjectId;
            if (projectId is null)
            {
                var pinned = await catalog.GetVersionAsync(dependency.VersionId!, ct).ConfigureAwait(false);
                if (pinned is null) return ModInstallPlan.Fail("Не удалось найти одну из нужных этому моду зависимостей.");
                projectId = pinned.ProjectId;
            }

            if (planned.Contains(projectId)) continue;
            if (projectId != replacingProjectId && installed.Any(e => e.ProjectId == projectId)) continue;

            var versions = await catalog.GetVersionsAsync(projectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false);
            var version = (dependency.VersionId is null ? null : versions.FirstOrDefault(v => v.Id == dependency.VersionId)) ?? PickBest(versions);
            if (version is null)
            {
                var title = (await catalog.GetProjectsAsync(new[] { projectId }, ct).ConfigureAwait(false)).FirstOrDefault()?.Title ?? projectId;
                return ModInstallPlan.Fail($"Моду нужна «{title}», но у неё нет версии для Minecraft {instance.McVersion} и {Describe(instance)}.");
            }

            if (chosen.Count >= MaxPlanItems) return ModInstallPlan.Fail("У этого мода слишком много зависимостей. Установка отменена.");
            planned.Add(projectId);
            chosen.Add((version, ModInstallReason.Dependency, requiredBy));
            Collect(version);
        }

        // Названия всех проектов плана и несовместимых — одним запросом.
        var projects = await catalog.GetProjectsAsync(planned.Concat(incompatible).Distinct().ToList(), ct).ConfigureAwait(false);
        string TitleOf(string id) => projects.FirstOrDefault(p => p.Id == id)?.Title ?? installed.FirstOrDefault(e => e.ProjectId == id)?.Title ?? id;

        var conflicts = incompatible
            .Where(id => planned.Contains(id) || (id != replacingProjectId && installed.Any(e => e.ProjectId == id)))
            .Select(TitleOf)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var items = chosen
            .Select(c => new ModPlanItem(
                projects.FirstOrDefault(p => p.Id == c.Version.ProjectId) ?? new ModProject(c.Version.ProjectId, c.Version.ProjectId, c.Version.ProjectId, "", "", 0, null),
                c.Version, c.Reason, c.RequiredBy))
            .ToList();

        return new ModInstallPlan(items, conflicts, null, catalog.Source);
    }

    // ───────────────────────── Установка ─────────────────────────

    public async Task<ModInstallResult> InstallAsync(Instance instance, ModInstallPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Error is not null) return ModInstallResult.Fail(plan.Error);
        if (plan.Conflicts.Count > 0) return ModInstallResult.Fail($"Мод несовместим с уже установленными: {string.Join(", ", plan.Conflicts)}.");
        if (plan.Items.Count == 0) return ModInstallResult.Fail("Нечего устанавливать.");

        var paths = PathsFor(instance);
        using var lease = await EnterAsync(instance.Id, ct).ConfigureAwait(false);
        try
        {
            var manifest = LoadReconciled(paths, out _);
            var items = plan.Items.Where(i => manifest.Mods.All(m => m.ProjectId != i.Version.ProjectId)).ToList();
            if (items.Count == 0) return ModInstallResult.Fail("Этот мод уже установлен.");

            return await ApplyAsync(paths, manifest, items, plan.Source, replacing: null, progress, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось установить мод в сборку {Id}.", instance.Id);
            return ModInstallResult.Fail("Не удалось записать мод на диск. Проверь свободное место и права на папку данных.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Нет доступа к папке модов сборки {Id}.", instance.Id);
            return ModInstallResult.Fail("Нет доступа к папке модов сборки. Закрой игру и попробуй ещё раз.");
        }
    }

    public async Task<ModInstallResult> UpdateAsync(Instance instance, string projectId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (LoaderProblem(instance) is { } problem) return ModInstallResult.Fail(problem);

        var paths = PathsFor(instance);
        using var lease = await EnterAsync(instance.Id, ct).ConfigureAwait(false);
        try
        {
            var manifest = LoadReconciled(paths, out _);
            var entry = manifest.Mods.FirstOrDefault(m => m.ProjectId == projectId);
            if (entry is null) return ModInstallResult.Fail("Этого мода нет в сборке.");

            var latest = await FindUpdateAsync(instance, entry, ct).ConfigureAwait(false);
            if (latest is null) return ModInstallResult.Fail($"У мода «{entry.Title}» нет обновления.");

            var plan = await ResolveAsync(instance, CatalogFor(ParseSource(entry.Source)), latest, ParseReason(entry.Reason), manifest.Mods, replacingProjectId: entry.ProjectId, ct).ConfigureAwait(false);
            if (plan.Error is not null) return ModInstallResult.Fail(plan.Error);
            if (plan.Conflicts.Count > 0) return ModInstallResult.Fail($"Обновление несовместимо с установленными модами: {string.Join(", ", plan.Conflicts)}.");

            return await ApplyAsync(paths, manifest, plan.Items, plan.Source, replacing: entry, progress, ct).ConfigureAwait(false);
        }
        catch (ModCatalogException ex)
        {
            return ModInstallResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось обновить мод {Project} в сборке {Id}.", projectId, instance.Id);
            return ModInstallResult.Fail("Не удалось обновить мод на диске. Если игра запущена, закрой её и попробуй ещё раз.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Нет доступа к папке модов сборки {Id}.", instance.Id);
            return ModInstallResult.Fail("Нет доступа к папке модов сборки. Закрой игру и попробуй ещё раз.");
        }
    }

    /// <summary>
    /// Скачивает, проверяет и записывает позиции плана. Новые файлы, которых раньше не было, при сбое и отмене удаляются.
    /// Старый файл заменяемого мода удаляется последним, после записи списка.
    /// </summary>
    private async Task<ModInstallResult> ApplyAsync(Paths paths, ModsFile manifest, IReadOnlyList<ModPlanItem> items, ModSource source, ModEntry? replacing, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ModsDirectory);

        var created = new List<string>();
        var requests = new List<DownloadRequest>();
        try
        {
            foreach (var item in items)
            {
                var file = item.Version.File;
                if (!ModrinthParser.IsSafeFileName(file.FileName)) return ModInstallResult.Fail("У мода неподходящее имя файла. Установка отменена.");

                var destination = Path.Combine(paths.ModsDirectory, file.FileName);
                if (File.Exists(destination))
                {
                    var owner = manifest.Mods.FirstOrDefault(m => string.Equals(m.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));
                    var ownedByOther = owner is not null && owner.ProjectId != item.Version.ProjectId;
                    var sameAsNew = string.Equals(await Sha1OfAsync(destination, ct).ConfigureAwait(false), file.Sha1, StringComparison.OrdinalIgnoreCase);
                    if (ownedByOther || (owner is null && !sameAsNew))
                        return ModInstallResult.Fail($"В папке mods уже есть другой файл «{file.FileName}». Переименуй или удали его и повтори.");
                }
                else created.Add(destination);

                requests.Add(new DownloadRequest(file.Url, destination, file.Sha1, file.Size));
            }

            var batch = progress is null ? null : new Progress<DownloadBatchProgress>(p =>
            {
                var share = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : (double)p.CompletedFiles / Math.Max(1, p.TotalFiles);
                progress.Report(Math.Clamp(share, 0, 1) * 0.9);
            });

            var results = await _downloads.DownloadAllAsync(requests, batch, ct).ConfigureAwait(false);
            var failed = results.FirstOrDefault(r => !r.Succeeded);
            if (failed is not null)
            {
                _logger.LogWarning("Не скачан мод {Url}: {Error}", failed.Request.Url, failed.Error);
                DeleteQuietly(created);
                return ModInstallResult.Fail($"Не удалось скачать «{Path.GetFileName(failed.Request.DestinationPath)}». Проверь подключение к интернету и попробуй ещё раз.");
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(paths.ModsDirectory, item.Version.File.FileName);
                if (item.Version.File.Sha512 is { } expected512 && !string.Equals(await Sha512OfAsync(path, ct).ConfigureAwait(false), expected512, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteQuietly(created);
                    return ModInstallResult.Fail($"Файл «{item.Version.File.FileName}» не прошёл проверку целостности (SHA-512). Мод не установлен.");
                }
            }

            var installed = new List<ModEntry>();
            foreach (var item in items)
            {
                manifest.Mods.RemoveAll(m => m.ProjectId == item.Version.ProjectId);
                var entry = NewEntry(item.Version, item.Project.Title, item.Reason, item.Version.File.FileName, item.Version.File.Sha1, source);
                manifest.Mods.Add(entry);
                installed.Add(entry);
            }

            if (!_manifests.Save(paths.ManifestPath, manifest))
            {
                DeleteQuietly(created);
                return ModInstallResult.Fail("Не удалось сохранить список модов сборки.");
            }

            if (replacing is not null && !string.Equals(replacing.FileName, items[0].Version.File.FileName, StringComparison.OrdinalIgnoreCase))
                DeleteQuietly(new[] { Path.Combine(paths.ModsDirectory, replacing.FileName) });

            progress?.Report(1);
            _logger.LogInformation("В сборке установлено модов: {Count} (первый: {Title}).", installed.Count, installed[0].Title);
            return ModInstallResult.Ok(installed.Select(e => ToInstalled(e, manifest.Mods)).ToList());
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(created);
            throw;
        }
    }

    // ───────────────────────── Обновления ─────────────────────────

    public async Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(Instance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (LoaderProblem(instance) is not null) return Array.Empty<ModUpdateInfo>();

        ModsFile manifest;
        try
        {
            manifest = LoadReconciled(PathsFor(instance), out _);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось прочитать список модов сборки {Id}.", instance.Id);
            return Array.Empty<ModUpdateInfo>();
        }
        if (manifest.Mods.Count == 0) return Array.Empty<ModUpdateInfo>();

        var updates = new List<ModUpdateInfo>();
        foreach (var group in manifest.Mods.GroupBy(m => ParseSource(m.Source)))
        {
            var catalog = CatalogFor(group.Key);
            if (group.Key == ModSource.Modrinth)
            {
                // Modrinth отвечает на всю пачку хэшей одним запросом.
                var latest = await catalog.FindLatestForFilesAsync(group.Select(m => m.Sha1).ToList(), instance.McVersion, instance.Loader, ct).ConfigureAwait(false);
                foreach (var entry in group)
                    if (latest.TryGetValue(entry.Sha1, out var version) && IsNewer(version, entry))
                        updates.Add(new ModUpdateInfo(ToInstalled(entry, manifest.Mods), version));
            }
            else
            {
                // CurseForge ищет по своему отпечатку, поэтому смотрим список файлов каждого мода (по одному запросу на мод).
                foreach (var entry in group)
                {
                    var best = PickBest(await catalog.GetVersionsAsync(entry.ProjectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false));
                    if (best is not null && IsNewer(best, entry)) updates.Add(new ModUpdateInfo(ToInstalled(entry, manifest.Mods), best));
                }
            }
        }
        return updates.OrderBy(u => u.Installed.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<ModVersion?> FindUpdateAsync(Instance instance, ModEntry entry, CancellationToken ct)
    {
        var source = ParseSource(entry.Source);
        var catalog = CatalogFor(source);
        if (source == ModSource.Modrinth)
        {
            var map = await catalog.FindLatestForFilesAsync(new[] { entry.Sha1 }, instance.McVersion, instance.Loader, ct).ConfigureAwait(false);
            return map.TryGetValue(entry.Sha1, out var version) && IsNewer(version, entry) ? version : null;
        }

        var best = PickBest(await catalog.GetVersionsAsync(entry.ProjectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false));
        return best is not null && IsNewer(best, entry) ? best : null;
    }

    private static bool IsNewer(ModVersion version, ModEntry entry) =>
        !string.Equals(version.Id, entry.VersionId, StringComparison.Ordinal) && version.PublishedUtc > entry.PublishedUtc;

    // ───────────────────────── Удаление ─────────────────────────

    public async Task<ModRemoveResult> RemoveAsync(Instance instance, string projectId, bool force = false, bool removeOrphans = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var paths = PathsFor(instance);

        using var lease = await EnterAsync(instance.Id, ct).ConfigureAwait(false);
        try
        {
            var manifest = LoadReconciled(paths, out _);
            var entry = manifest.Mods.FirstOrDefault(m => m.ProjectId == projectId);
            if (entry is null) return new ModRemoveResult(false, Array.Empty<string>(), Array.Empty<InstalledMod>(), "Этого мода нет в сборке.");

            var dependents = manifest.Mods.Where(m => m.ProjectId != projectId && m.Requires.Contains(projectId)).Select(m => m.Title).ToList();
            if (dependents.Count > 0 && !force) return new ModRemoveResult(false, dependents, Array.Empty<InstalledMod>(), null);

            DeleteFile(Path.Combine(paths.ModsDirectory, entry.FileName));
            manifest.Mods.Remove(entry);

            // Моды, которые ставились только ради удалённого и больше никому не нужны (цепочкой: убрали один — освободился следующий).
            var orphans = new List<ModEntry>();
            for (var again = true; again;)
            {
                again = false;
                foreach (var candidate in manifest.Mods.ToList())
                {
                    if (candidate.Reason != nameof(ModInstallReason.Dependency)) continue;
                    if (manifest.Mods.Any(m => m != candidate && m.Requires.Contains(candidate.ProjectId))) continue;
                    manifest.Mods.Remove(candidate);
                    orphans.Add(candidate);
                    again = true;
                }
            }

            var orphanInfo = orphans.Select(o => ToInstalled(o, Array.Empty<ModEntry>())).ToList();
            if (removeOrphans)
            {
                foreach (var orphan in orphans) DeleteFile(Path.Combine(paths.ModsDirectory, orphan.FileName));
            }
            else
            {
                manifest.Mods.AddRange(orphans); // оставляем в списке: пользователь решит сам
            }

            if (!_manifests.Save(paths.ManifestPath, manifest))
                return new ModRemoveResult(false, Array.Empty<string>(), Array.Empty<InstalledMod>(), "Не удалось сохранить список модов сборки.");

            _logger.LogInformation("Из сборки удалён мод {Title}; ненужных зависимостей: {Count}, удалено: {Removed}.", entry.Title, orphans.Count, removeOrphans);
            return new ModRemoveResult(true, Array.Empty<string>(), orphanInfo, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось удалить мод {Project} из сборки {Id}.", projectId, instance.Id);
            return new ModRemoveResult(false, Array.Empty<string>(), Array.Empty<InstalledMod>(), "Не удалось удалить файл мода. Если игра запущена, закрой её и попробуй ещё раз.");
        }
    }

    // ───────────────────────── Вспомогательное ─────────────────────────

    private sealed record Paths(string ModsDirectory, string ManifestPath);

    private Paths PathsFor(Instance instance)
    {
        var game = Path.GetFullPath(_instances.GetGameDirectory(instance.Id)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetDirectoryName(game) ?? game;
        return new Paths(Path.Combine(game, "mods"), Path.Combine(root, "mods.json"));
    }

    /// <summary>Список модов без записей, чьих файлов уже нет на диске (пользователь мог удалить их руками).</summary>
    private ModsFile LoadReconciled(Paths paths, out bool changed)
    {
        var manifest = _manifests.Load(paths.ManifestPath);
        var removed = manifest.Mods.RemoveAll(m => !File.Exists(Path.Combine(paths.ModsDirectory, m.FileName)));
        changed = removed > 0;
        if (changed) _logger.LogInformation("Из списка модов убрано записей без файла: {Count}.", removed);
        return manifest;
    }

    private async Task<IDisposable> EnterAsync(string instanceId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static string? LoaderProblem(Instance instance) =>
        ModrinthCatalog.LoadersFor(instance.Loader).Count == 0
            ? "Моды нужны сборке с загрузчиком (Fabric, Quilt, Forge или NeoForge). Создай такую сборку и выбери её."
            : null;

    private static string Describe(Instance instance) => SubReel.Launcher.Domain.Versions.LoaderInfo.Name(instance.Loader);

    /// <summary>Лучшая версия: самая новая из релизов, а если релизов нет — самая новая из остальных.</summary>
    private static ModVersion? PickBest(IReadOnlyList<ModVersion> versions)
    {
        var ordered = versions.OrderByDescending(v => v.PublishedUtc).ToList();
        return ordered.FirstOrDefault(v => v.Type == ModReleaseType.Release) ?? ordered.FirstOrDefault();
    }

    private static ModEntry NewEntry(ModVersion version, string title, ModInstallReason reason, string fileName, string sha1, ModSource source) => new()
    {
        Source = source.ToString(),
        ProjectId = version.ProjectId,
        Title = title,
        VersionId = version.Id,
        VersionNumber = version.VersionNumber,
        PublishedUtc = version.PublishedUtc,
        FileName = fileName,
        Sha1 = sha1.ToLowerInvariant(),
        Reason = reason.ToString(),
        Requires = version.Dependencies
            .Where(d => d.Kind == ModDependencyKind.Required && d.ProjectId is not null)
            .Select(d => d.ProjectId!)
            .Distinct(StringComparer.Ordinal)
            .ToList(),
    };

    private static ModSource ParseSource(string value) =>
        Enum.TryParse<ModSource>(value, out var source) ? source : ModSource.Modrinth;

    private static ModInstallReason ParseReason(string value) =>
        Enum.TryParse<ModInstallReason>(value, out var reason) ? reason : ModInstallReason.UserChoice;

    private static InstalledMod ToInstalled(ModEntry entry, IReadOnlyList<ModEntry> all) => new(
        entry.ProjectId, entry.Title, entry.VersionId, entry.VersionNumber, entry.PublishedUtc, entry.FileName, entry.Sha1,
        ParseReason(entry.Reason),
        all.Where(m => m.ProjectId != entry.ProjectId && m.Requires.Contains(entry.ProjectId)).Select(m => m.ProjectId).ToList(),
        entry.Requires.ToList(),
        ParseSource(entry.Source));

    private void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Не удалось убрать недоустановленный файл {Path}.", path);
            }
        }
    }

    private static async Task<string> Sha1OfAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<string> Sha512OfAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
