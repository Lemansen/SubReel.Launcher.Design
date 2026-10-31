using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Minecraft.Java;

public sealed record ManagedJavaInstallResult(JavaInstallation? Installation, string? Error)
{
    public bool Succeeded => Installation is not null;
}

/// <summary>Файл-отметка «рантайм установлен полностью». Пока её нет, папка считается недокачанной и в поиске Java не участвует.</summary>
internal sealed class ManagedRuntimeMarker
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Component { get; set; } = "";
    public string? VersionName { get; set; }
}

/// <summary>
/// Скачивает Java-рантаймы Mojang в <c>%LocalAppData%\SubReel\runtimes\&lt;компонент&gt;</c> через Download Manager
/// (проверка SHA-1, докачка) и находит уже установленные. Повторная установка докачивает только недостающее.
/// </summary>
public sealed class ManagedJavaInstaller
{
    public const string MarkerFileName = ".subreel-runtime.json";
    private const string CacheDirectoryName = ".cache";
    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IDownloadManager _downloads;
    private readonly JavaVersionReader _reader;
    private readonly JsonFileStore _store;
    private readonly AppPaths _paths;
    private readonly OsEnvironment _environment;
    private readonly JavaOptions _options;
    private readonly ILogger<ManagedJavaInstaller> _logger;

    public ManagedJavaInstaller(IDownloadManager downloads, JavaVersionReader reader, JsonFileStore store, AppPaths paths, OsEnvironment environment, IOptions<JavaOptions> options, ILogger<ManagedJavaInstaller> logger)
    {
        _downloads = downloads;
        _reader = reader;
        _store = store;
        _paths = paths;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Корень установки компонента. Имя компонента приходит из version.json и должно быть простым именем папки.</summary>
    public string HomeFor(string component)
    {
        if (string.IsNullOrWhiteSpace(component) || component.StartsWith('.') || component.IndexOfAny(new[] { '/', '\\' }) >= 0 || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new JavaRuntimeException($"Недопустимое имя рантайма «{component}».");
        return Path.Combine(_paths.RuntimesDirectory, component);
    }

    /// <summary>Полностью установленные рантаймы лаунчера.</summary>
    public async Task<IReadOnlyList<JavaInstallation>> ReadInstalledAsync(CancellationToken ct)
    {
        var result = new List<JavaInstallation>();
        if (!Directory.Exists(_paths.RuntimesDirectory)) return result;

        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_paths.RuntimesDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Папка рантаймов {Path} недоступна.", _paths.RuntimesDirectory);
            return result;
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name == CacheDirectoryName) continue;

            var marker = _store.Read<ManagedRuntimeMarker>(Path.Combine(directory, MarkerFileName), MarkerJson);
            if (marker.Value is null || marker.Value.SchemaVersion > ManagedRuntimeMarker.CurrentSchemaVersion) continue;

            var installation = await _reader.ReadAsync(directory, JavaSource.Managed, marker.Value.Component is { Length: > 0 } component ? component : name, ct).ConfigureAwait(false);
            if (installation is null) _logger.LogWarning("Рантайм {Name} помечен как установленный, но Java в нём не найдена.", name);
            else result.Add(installation);
        }
        return result;
    }

    public async Task<ManagedJavaInstallResult> InstallAsync(string component, IProgress<DownloadBatchProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await InstallCoreAsync(component, progress, ct).ConfigureAwait(false);
        }
        catch (JavaRuntimeException ex)
        {
            _logger.LogWarning(ex, "Не удалось установить рантайм {Component}.", component);
            return new ManagedJavaInstallResult(null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ошибка диска при установке рантайма {Component}.", component);
            return new ManagedJavaInstallResult(null, $"Ошибка записи на диск: {ex.Message}");
        }
    }

    private async Task<ManagedJavaInstallResult> InstallCoreAsync(string component, IProgress<DownloadBatchProgress>? progress, CancellationToken ct)
    {
        var home = HomeFor(component);
        var platform = JavaRuntimeIndex.PlatformKey(_environment)
            ?? throw new JavaRuntimeException($"Для платформы {_environment.OsName}/{_environment.Arch} Mojang не публикует Java-рантайм: выберите установленную Java вручную.");

        var cache = Path.Combine(_paths.RuntimesDirectory, CacheDirectoryName);
        Directory.CreateDirectory(cache);

        var indexPath = Path.Combine(cache, "all.json");
        var indexResult = await _downloads.DownloadAsync(new DownloadRequest(_options.RuntimeIndexUrl, indexPath), null, ct).ConfigureAwait(false);
        if (!indexResult.Succeeded)
        {
            if (!File.Exists(indexPath)) throw new JavaRuntimeException($"Не удалось получить список Java-рантаймов: {indexResult.Error}");
            _logger.LogWarning("Список рантаймов не обновлён ({Error}), используется сохранённая копия.", indexResult.Error);
        }

        var reference = JavaRuntimeIndex.FindComponent(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false), platform, component)
            ?? throw new JavaRuntimeException($"Mojang не предоставляет рантайм «{component}» для платформы {platform}.");

        var manifestPath = Path.Combine(cache, $"{component}-{platform}.manifest.json");
        var manifestResult = await _downloads.DownloadAsync(new DownloadRequest(reference.Url, manifestPath, reference.Sha1, reference.Size), null, ct).ConfigureAwait(false);
        if (!manifestResult.Succeeded) throw new JavaRuntimeException($"Не удалось получить описание рантайма «{component}»: {manifestResult.Error}");

        var entries = JavaRuntimeIndex.ParseManifest(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false));

        // Пока идёт установка, отметки быть не должно: прерванная установка не должна выглядеть завершённой.
        var markerPath = Path.Combine(home, MarkerFileName);
        if (File.Exists(markerPath)) File.Delete(markerPath);
        Directory.CreateDirectory(home);

        var requests = new List<DownloadRequest>();
        foreach (var entry in entries)
        {
            var path = Inside(home, entry.Path);
            switch (entry.Kind)
            {
                case RuntimeFileKind.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case RuntimeFileKind.File:
                    requests.Add(new DownloadRequest(entry.Raw!.Url, path, entry.Raw.Sha1, entry.Raw.Size));
                    break;
            }
        }

        var results = await _downloads.DownloadAllAsync(requests, progress, ct).ConfigureAwait(false);
        var failures = results.Where(r => !r.Succeeded).ToList();
        if (failures.Count > 0) throw new JavaRuntimeException($"Не удалось скачать файлов рантайма: {failures.Count}. {failures[0].Error}");

        ApplyUnixMetadata(home, entries);

        var installation = await _reader.ReadAsync(home, JavaSource.Managed, component, ct).ConfigureAwait(false)
            ?? throw new JavaRuntimeException($"После установки в «{component}» не найдена работающая Java.");

        if (!_store.Write(markerPath, new ManagedRuntimeMarker { Component = component, VersionName = reference.VersionName }, MarkerJson))
            throw new IOException($"Не удалось записать отметку об установке рантайма «{component}».");

        _logger.LogInformation("Рантайм {Component} ({Version}) установлен в {Home}.", component, installation.FullVersion, home);
        return new ManagedJavaInstallResult(installation, null);
    }

    /// <summary>В Windows права и ссылки не нужны. В Linux и macOS Mojang помечает исполняемые файлы и задаёт символические ссылки.</summary>
    private static void ApplyUnixMetadata(string home, IReadOnlyList<RuntimeFileEntry> entries)
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var entry in entries)
        {
            var path = Inside(home, entry.Path);
            if (entry.Kind == RuntimeFileKind.File && entry.Executable)
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            else if (entry.Kind == RuntimeFileKind.Link && !File.Exists(path) && !Directory.Exists(path))
            {
                Inside(home, Path.Combine(Path.GetDirectoryName(entry.Path) ?? "", entry.LinkTarget!)); // цель ссылки не должна выходить за пределы рантайма
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.CreateSymbolicLink(path, entry.LinkTarget!);
            }
        }
    }

    private static string Inside(string home, string relative)
    {
        var baseFull = Path.GetFullPath(home);
        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(baseFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new JavaRuntimeException($"Путь «{relative}» в описании рантайма выходит за пределы его папки.");
        return combined;
    }
}
