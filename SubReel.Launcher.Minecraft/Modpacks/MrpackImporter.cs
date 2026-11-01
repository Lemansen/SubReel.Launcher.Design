using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Modpacks;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Modpacks;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Minecraft.Modpacks;

/// <summary>
/// Импорт .mrpack: читает <c>modrinth.index.json</c>, создаёт сборку, скачивает файлы (SHA-1 проверяет менеджер загрузок, SHA-512 — мы),
/// затем раскладывает <c>overrides</c> и <c>client-overrides</c>. Любой сбой и отмена удаляют недоделанную сборку целиком.
/// Файлы игры и загрузчика не качаются: их, как у обычной сборки, подготовит первый запуск.
/// </summary>
public sealed class MrpackImporter : IModpackImporter
{
    private const long MaxIndexBytes = 8L * 1024 * 1024;
    private const int MaxEntries = 50_000;
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxNameAttempts = 50;

    private readonly InstanceService _instances;
    private readonly IDownloadManager _downloads;
    private readonly ILogger<MrpackImporter> _logger;

    public MrpackImporter(InstanceService instances, IDownloadManager downloads, ILogger<MrpackImporter> logger)
    {
        _instances = instances;
        _downloads = downloads;
        _logger = logger;
    }

    public async Task<ModpackImportResult> ImportMrpackAsync(string packPath, IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);

        Instance? created = null;
        try
        {
            progress?.Report(new ModpackImportProgress(ModpackImportStage.ReadingPack, 0));

            await using var file = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new ModpackException("В архиве слишком много файлов.");

            var index = await ReadIndexAsync(zip, ct).ConfigureAwait(false);
            if (!LoaderInfo.CanInstall(index.Loader) && index.Loader != LoaderKind.Vanilla)
                throw new ModpackException("Загрузчик этой сборки лаунчер пока не поддерживает.");

            progress?.Report(new ModpackImportProgress(ModpackImportStage.CreatingBuild, 0.03));
            created = await CreateInstanceAsync(index, ct).ConfigureAwait(false);
            var gameDirectory = _instances.GetGameDirectory(created.Id);
            Directory.CreateDirectory(gameDirectory);

            await DownloadFilesAsync(index, gameDirectory, progress, ct).ConfigureAwait(false);
            await VerifyFilesAsync(index, gameDirectory, progress, ct).ConfigureAwait(false);

            progress?.Report(new ModpackImportProgress(ModpackImportStage.ExtractingFiles, 0.9));
            await Task.Run(() => ExtractOverrides(zip, gameDirectory, ct), ct).ConfigureAwait(false);

            progress?.Report(new ModpackImportProgress(ModpackImportStage.ExtractingFiles, 1));
            _logger.LogInformation("Сборка «{Name}» импортирована: {Files} файлов, Minecraft {Version}, {Loader}.", created.Name, index.Files.Count, index.McVersion, index.Loader);
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
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Файл {Path} не читается как архив.", packPath);
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail("Файл не похож на сборку .mrpack или повреждён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось импортировать сборку из {Path}.", packPath);
            await RollbackAsync(created).ConfigureAwait(false);
            return ModpackImportResult.Fail("Не удалось прочитать файл или записать сборку на диск. Проверь свободное место и права на папку данных.");
        }
    }

    private static async Task<MrpackIndex> ReadIndexAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(MrpackParser.IndexFileName) ?? throw new ModpackException("В архиве нет файла modrinth.index.json: это не сборка .mrpack.");
        if (entry.Length > MaxIndexBytes) throw new ModpackException("Файл описания сборки слишком большой.");

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return MrpackParser.Parse(json);
    }

    private async Task<Instance> CreateInstanceAsync(MrpackIndex index, CancellationToken ct)
    {
        var baseName = index.Name.Length > InstanceRules.MaxNameLength ? index.Name[..InstanceRules.MaxNameLength].TrimEnd() : index.Name;

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var name = attempt == 1 ? baseName : WithSuffix(baseName, attempt);
            var result = await _instances.CreateAsync(name, index.McVersion, index.Loader, index.LoaderVersion, ct).ConfigureAwait(false);
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

    private async Task DownloadFilesAsync(MrpackIndex index, string gameDirectory, IProgress<ModpackImportProgress>? progress, CancellationToken ct)
    {
        if (index.Files.Count == 0) return;

        var requests = index.Files
            .Select(f => new DownloadRequest(f.Url, ResolveInside(gameDirectory, f.Path), f.Sha1, f.Size))
            .ToList();

        var batch = progress is null ? null : new Progress<DownloadBatchProgress>(p =>
        {
            var share = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : (double)p.CompletedFiles / Math.Max(1, p.TotalFiles);
            progress.Report(new ModpackImportProgress(ModpackImportStage.DownloadingFiles, 0.05 + 0.75 * Math.Clamp(share, 0, 1)));
        });

        var results = await _downloads.DownloadAllAsync(requests, batch, ct).ConfigureAwait(false);
        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count == 0) return;

        var first = failed[0];
        _logger.LogWarning("Не скачано {Count} файлов сборки; первый: {Url}: {Error}", failed.Count, first.Request.Url, first.Error);
        var name = Path.GetFileName(first.Request.DestinationPath);
        throw new ModpackException($"Не удалось скачать файлов: {failed.Count} (например, «{name}»). Проверь подключение к интернету и попробуй ещё раз.");
    }

    /// <summary>SHA-1 уже проверил менеджер загрузок; SHA-512 из описания сверяем сами, чтобы подмена файла не прошла по слабому хэшу.</summary>
    private async Task VerifyFilesAsync(MrpackIndex index, string gameDirectory, IProgress<ModpackImportProgress>? progress, CancellationToken ct)
    {
        for (var i = 0; i < index.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = index.Files[i];
            var path = ResolveInside(gameDirectory, file.Path);

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false));
                if (!string.Equals(hash, file.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new ModpackException($"Файл «{file.Path}» не прошёл проверку целостности (SHA-512). Сборка не импортирована.");
            }

            progress?.Report(new ModpackImportProgress(ModpackImportStage.CheckingFiles, 0.8 + 0.1 * (i + 1) / index.Files.Count));
        }
    }

    private static void ExtractOverrides(ZipArchive zip, string gameDirectory, CancellationToken ct)
    {
        long total = 0;
        // Сначала общие файлы, затем клиентские: при совпадении путей побеждают клиентские.
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;

                var relative = MrpackParser.NormalizePath(name[prefix.Length..])
                    ?? throw new ModpackException($"В сборке небезопасный путь файла: «{entry.FullName}».");
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
    }

    /// <summary>Полный путь файла внутри папки игры. Выход за её пределы (даже после нормализации) — ошибка сборки.</summary>
    internal static string ResolveInside(string root, string relative)
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
