using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Modpacks;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Modpacks;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Mods.Installed;

namespace SubReel.Launcher.Mods.CurseForge;

/// <summary>
/// Импорт сборки CurseForge (.zip с <c>manifest.json</c>). Сначала всё узнаём из сети (адреса файлов), и только потом создаём сборку:
/// без ключа API, без сети или при запретившем раздачу моде сборка не создаётся вообще. Любой сбой и отмена удаляют недоделанную сборку целиком.
/// Моды (.jar) попадают в <c>mods</c> и в список модов сборки (источник CurseForge), .zip — в <c>resourcepacks</c>, затем раскладывается папка overrides.
/// </summary>
internal sealed class CurseForgeImporter : ICurseForgeImporter
{
    private const long MaxManifestBytes = 8L * 1024 * 1024;
    private const int MaxEntries = 50_000;
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxNameAttempts = 50;

    private readonly InstanceService _instances;
    private readonly IDownloadManager _downloads;
    private readonly CurseForgeCatalog _catalog;
    private readonly JsonFileStore _store;
    private readonly ILogger<CurseForgeImporter> _logger;

    public CurseForgeImporter(InstanceService instances, IDownloadManager downloads, CurseForgeCatalog catalog, JsonFileStore store, ILogger<CurseForgeImporter> logger)
    {
        _instances = instances;
        _downloads = downloads;
        _catalog = catalog;
        _store = store;
        _logger = logger;
    }

    public async Task<ModpackImportResult> ImportAsync(string packPath, IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);

        Instance? created = null;
        try
        {
            progress?.Report(new ModpackImportProgress(ModpackImportStage.ReadingPack, 0));

            await using var file = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new ModpackException("В архиве слишком много файлов.");

            var manifest = await ReadManifestAsync(zip, ct).ConfigureAwait(false);
            if (manifest.Loader != LoaderKind.Vanilla && !LoaderInfo.CanInstall(manifest.Loader))
                throw new ModpackException("Загрузчик этой сборки лаунчер пока не поддерживает.");

            // Адреса файлов — до создания сборки: нет ключа или сети — ничего не создаём.
            var versions = await _catalog.GetFilesAsync(manifest.Files.Select(f => f.FileId).ToList(), ct).ConfigureAwait(false);
            await EnsureAllAvailableAsync(manifest, versions, ct).ConfigureAwait(false);
            var byFile = versions.ToDictionary(v => v.Id, StringComparer.Ordinal);

            progress?.Report(new ModpackImportProgress(ModpackImportStage.CreatingBuild, 0.03));
            created = await CreateInstanceAsync(manifest, ct).ConfigureAwait(false);
            var gameDirectory = Path.GetFullPath(_instances.GetGameDirectory(created.Id));
            Directory.CreateDirectory(gameDirectory);

            var items = manifest.Files.Select(f => (Ref: f, Version: byFile[f.FileId.ToString()])).ToList();
            await DownloadAsync(items.Select(i => i.Version).ToList(), gameDirectory, progress, ct).ConfigureAwait(false);
            await RecordModsAsync(created, items, gameDirectory, ct).ConfigureAwait(false);

            progress?.Report(new ModpackImportProgress(ModpackImportStage.ExtractingFiles, 0.9));
            await Task.Run(() => ExtractOverrides(zip, manifest.Overrides, gameDirectory, ct), ct).ConfigureAwait(false);

            progress?.Report(new ModpackImportProgress(ModpackImportStage.ExtractingFiles, 1));
            _logger.LogInformation("Сборка CurseForge «{Name}» импортирована: {Files} файлов, Minecraft {Version}, {Loader}.", created.Name, items.Count, manifest.McVersion, manifest.Loader);
            return ModpackImportResult.Success(created);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(created).ConfigureAwait(false);
            throw;
        }
        catch (ModpackException ex)
        {
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail(ex.Message);
        }
        catch (ModCatalogException ex)
        {
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Файл {Path} не читается как архив.", packPath);
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail("Файл не похож на сборку CurseForge или повреждён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось импортировать сборку из {Path}.", packPath);
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail("Не удалось прочитать файл или записать сборку на диск. Проверь свободное место и права на папку данных.");
        }
    }

    private static async Task<CurseForgeManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(CurseForgeManifestParser.FileName) ?? throw new ModpackException("В архиве нет файла manifest.json: это не сборка CurseForge.");
        if (entry.Length > MaxManifestBytes) throw new ModpackException("Файл описания сборки слишком большой.");

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return CurseForgeManifestParser.Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Не все файлы есть в ответе — значит, автор запретил раздачу сторонним лаунчерам или файл удалён. Называем моды, чтобы их можно было скачать вручную.</summary>
    private async Task EnsureAllAvailableAsync(CurseForgeManifest manifest, IReadOnlyList<ModVersion> versions, CancellationToken ct)
    {
        var have = versions.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var missing = manifest.Files.Where(f => !have.Contains(f.FileId.ToString())).ToList();
        if (missing.Count == 0) return;

        var ids = missing.Select(m => m.ProjectId.ToString()).Distinct().Take(5).ToList();
        string names;
        try
        {
            var projects = await _catalog.GetProjectsAsync(ids, ct).ConfigureAwait(false);
            names = string.Join(", ", ids.Select(id => projects.FirstOrDefault(p => p.Id == id)?.Title ?? $"№{id}"));
        }
        catch (ModCatalogException)
        {
            names = string.Join(", ", ids.Select(id => $"№{id}"));
        }

        throw new ModpackException($"Не удалось получить файлов: {missing.Count}. Авторы могли запретить их скачивание сторонними лаунчерами или файлы удалены: {names}{(missing.Select(m => m.ProjectId).Distinct().Count() > 5 ? " и др." : "")}. Сборка не импортирована.");
    }

    private async Task<Instance> CreateInstanceAsync(CurseForgeManifest manifest, CancellationToken ct)
    {
        var baseName = manifest.Name.Length > InstanceRules.MaxNameLength ? manifest.Name[..InstanceRules.MaxNameLength].TrimEnd() : manifest.Name;

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var name = attempt == 1 ? baseName : WithSuffix(baseName, attempt);
            var result = await _instances.CreateAsync(name, manifest.McVersion, manifest.Loader, manifest.LoaderVersion, ct).ConfigureAwait(false);
            if (result.Succeeded) return result.Instance!;
            if (result.StorageFailed) throw new IOException("Не удалось создать папку сборки.");
            if (result.Error != InstanceValidationError.NameDuplicate) throw new ModpackException("Не удалось создать сборку: название или версия игры в файле некорректны.");
        }
        throw new ModpackException("Не удалось подобрать свободное название для сборки.");
    }

    private static string WithSuffix(string name, int attempt)
    {
        var suffix = $" ({attempt})";
        var room = InstanceRules.MaxNameLength - suffix.Length;
        return (name.Length > room ? name[..room].TrimEnd() : name) + suffix;
    }

    private static string TargetPath(string gameDirectory, ModFile file)
    {
        var folder = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "resourcepacks" : "mods";
        return ResolveInside(gameDirectory, $"{folder}/{file.FileName}");
    }

    private async Task DownloadAsync(IReadOnlyList<ModVersion> versions, string gameDirectory, IProgress<ModpackImportProgress>? progress, CancellationToken ct)
    {
        if (versions.Count == 0) return;

        var requests = versions.Select(v => new DownloadRequest(v.File.Url, TargetPath(gameDirectory, v.File), v.File.Sha1, v.File.Size)).ToList();
        var batch = progress is null ? null : new Progress<DownloadBatchProgress>(p =>
        {
            var share = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : (double)p.CompletedFiles / Math.Max(1, p.TotalFiles);
            progress.Report(new ModpackImportProgress(ModpackImportStage.DownloadingFiles, 0.05 + 0.8 * Math.Clamp(share, 0, 1)));
        });

        var results = await _downloads.DownloadAllAsync(requests, batch, ct).ConfigureAwait(false);
        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count == 0) return;

        _logger.LogWarning("Не скачано {Count} файлов сборки; первый: {Url}: {Error}", failed.Count, failed[0].Request.Url, failed[0].Error);
        throw new ModpackException($"Не удалось скачать файлов: {failed.Count} (например, «{Path.GetFileName(failed[0].Request.DestinationPath)}»). Проверь подключение к интернету и попробуй ещё раз.");
    }

    /// <summary>Записывает моды в <c>mods.json</c> сборки, чтобы окно «Моды» сразу показало их и умело обновлять. Названия — из каталога; нет сети — из имени файла.</summary>
    private async Task RecordModsAsync(Instance instance, IReadOnlyList<(CurseForgeFileRef Ref, ModVersion Version)> items, string gameDirectory, CancellationToken ct)
    {
        var mods = items.Where(i => i.Version.File.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mods.Count == 0) return;

        IReadOnlyList<ModProject> projects;
        try
        {
            projects = await _catalog.GetProjectsAsync(mods.Select(m => m.Version.ProjectId).Distinct().ToList(), ct).ConfigureAwait(false);
        }
        catch (ModCatalogException ex)
        {
            _logger.LogInformation("Названия модов импортированной сборки не получены: {Message}", ex.Message);
            projects = Array.Empty<ModProject>();
        }

        var file = new ModsFile();
        foreach (var (_, version) in mods)
        {
            if (file.Mods.Any(m => m.ProjectId == version.ProjectId)) continue;
            file.Mods.Add(new ModEntry
            {
                ProjectId = version.ProjectId,
                Title = projects.FirstOrDefault(p => p.Id == version.ProjectId)?.Title ?? Path.GetFileNameWithoutExtension(version.File.FileName),
                VersionId = version.Id,
                VersionNumber = version.VersionNumber,
                PublishedUtc = version.PublishedUtc,
                FileName = version.File.FileName,
                Sha1 = version.File.Sha1,
                Reason = nameof(ModInstallReason.UserChoice),
                Source = nameof(ModSource.CurseForge),
                Requires = version.Dependencies.Where(d => d.Kind == ModDependencyKind.Required && d.ProjectId is not null).Select(d => d.ProjectId!).Distinct().ToList(),
            });
        }

        var root = Path.GetDirectoryName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? gameDirectory;
        var manifests = new ModsManifestStore(_store, _logger);
        if (!manifests.Save(Path.Combine(root, "mods.json"), file))
            _logger.LogWarning("Не удалось записать список модов сборки {Id}: моды останутся «неопознанными».", instance.Id);
    }

    private static void ExtractOverrides(ZipArchive zip, string overridesFolder, string gameDirectory, CancellationToken ct)
    {
        var prefix = overridesFolder.TrimEnd('/') + "/";
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;

            var relative = MrpackParser.NormalizePath(name[prefix.Length..]) ?? throw new ModpackException($"В сборке небезопасный путь файла: «{entry.FullName}».");
            var destination = ResolveInside(gameDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read; // считаем настоящие байты: размер из заголовка архива может лгать
                if (total > MaxExtractedBytes) throw new ModpackException("Содержимое сборки слишком большое.");
                output.Write(buffer, 0, read);
            }
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ModpackException($"Небезопасный путь файла в сборке: «{relative}».");
        return full;
    }

    private async Task RollbackAsync(Instance? created)
    {
        if (created is null) return;
        try
        {
            await _instances.DeleteAsync(created.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось удалить недоделанную сборку {Id}.", created.Id);
        }
    }
}
