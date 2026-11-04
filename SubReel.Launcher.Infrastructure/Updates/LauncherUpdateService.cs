using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Updates;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Updates;

/// <summary>
/// Проверка и установка обновлений лаунчера. Манифест — JSON по https; архив проверяется по SHA-256 из манифеста; файлы заменяет отдельная
/// программа SubReel.Updater после закрытия лаунчера (работающий exe подменить нельзя). Папка данных пользователя не затрагивается.
/// </summary>
public sealed class LauncherUpdateService : ILauncherUpdateService
{
    public const string HttpClientName = "launcher-updates";
    private const int MaxManifestBytes = 256 * 1024;
    private const string UpdaterFolder = "Updater";
    private const string UpdaterExe = "SubReel.Updater.exe";

    private readonly IHttpClientFactory _http;
    private readonly ISettingsService _settings;
    private readonly UpdateOptions _options;
    private readonly IAppVersion _version;
    private readonly IDownloadManager _downloads;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<LauncherUpdateService> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _stateLock = new();

    private LauncherUpdateInfo? _available;
    private string? _lastError;
    private DateTime? _lastCheckedUtc;
    private bool _checking;
    private bool _applying;
    private bool _cleaned;

    public LauncherUpdateService(IHttpClientFactory http, ISettingsService settings, IOptions<UpdateOptions> options, IAppVersion version,
        IDownloadManager downloads, AppPaths paths, TimeProvider time, ILogger<LauncherUpdateService> logger)
    {
        _http = http;
        _settings = settings;
        _options = options.Value;
        _version = version;
        _downloads = downloads;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    public event EventHandler? StateChanged;

    public Version CurrentVersion => _version.Current;
    public bool IsConfigured => Uri.TryCreate(_options.ManifestUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    public LauncherUpdateInfo? Available { get { lock (_stateLock) return _available; } }
    public bool IsChecking { get { lock (_stateLock) return _checking; } }
    public bool IsApplying { get { lock (_stateLock) return _applying; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }
    public DateTime? LastCheckedUtc { get { lock (_stateLock) return _lastCheckedUtc; } }

    public bool IsBannerVisible =>
        UpdateRules.IsBannerVisible(Available, _settings.Current.PostponedUpdateVersion, _settings.Current.UpdateSnoozedUntilUtc, _time.GetUtcNow().UtcDateTime);

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false)) return; // проверка уже идёт

        try
        {
            CleanupLeftovers();
            Set(() => _checking = true);

            try
            {
                var manifestUrl = new Uri(_options.ManifestUrl!);
                using var client = _http.CreateClient(HttpClientName);
                using var response = await client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new UpdateManifestException("Манифест обновлений слишком большой.");

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (json.Length > MaxManifestBytes) throw new UpdateManifestException("Манифест обновлений слишком большой.");

                var info = UpdateRules.ParseManifest(json, manifestUrl, CurrentVersion, _options.AdditionalDownloadHosts);
                Set(() =>
                {
                    _available = info;
                    _lastError = null;
                    _lastCheckedUtc = _time.GetUtcNow().UtcDateTime;
                });
                if (info is null) _logger.LogInformation("Обновлений лаунчера нет (текущая версия {Version}).", CurrentVersion);
                else _logger.LogInformation("Найдена версия лаунчера {New} (текущая {Version}).", info.Version, CurrentVersion);
            }
            catch (Exception ex) when (ex is HttpRequestException or UpdateManifestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Нет сети или сервер недоступен — не повод беспокоить: прежняя найденная версия остаётся, причина видна на странице «О лаунчере».
                _logger.LogWarning(ex, "Не удалось проверить обновления лаунчера.");
                Set(() => _lastError = ex is UpdateManifestException ? ex.Message : "Не удалось связаться с сервером обновлений. Проверь подключение к интернету.");
            }
        }
        finally
        {
            Set(() => _checking = false);
            _checkGate.Release();
        }
    }

    public void Postpone()
    {
        var info = Available;
        if (info is null || info.Mandatory) return;

        _settings.Current.PostponedUpdateVersion = info.Version.ToString();
        _settings.Current.UpdateSnoozedUntilUtc = _time.GetUtcNow().UtcDateTime.AddDays(Math.Max(1, _options.SnoozeDays));
        _settings.Save();
        _logger.LogInformation("Обновление {Version} отложено до {Until:u}.", info.Version, _settings.Current.UpdateSnoozedUntilUtc);
        RaiseChanged();
    }

    public async Task<UpdateApplyResult> ApplyAsync(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var info = Available;
        if (info is null) return UpdateApplyResult.Fail("Нет доступного обновления.");
        if (!await _applyGate.WaitAsync(0, ct).ConfigureAwait(false)) return UpdateApplyResult.Fail("Обновление уже устанавливается.");

        try
        {
            Set(() => _applying = true);
            return await ApplyCoreAsync(info, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Не удалось подготовить обновление {Version}.", info.Version);
            return UpdateApplyResult.Fail(ex is InvalidDataException ? ex.Message : "Не удалось подготовить обновление: проверь свободное место и права на папку данных.");
        }
        finally
        {
            Set(() => _applying = false);
            _applyGate.Release();
        }
    }

    private async Task<UpdateApplyResult> ApplyCoreAsync(LauncherUpdateInfo info, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var target = AppContext.BaseDirectory;
        if (!CanWrite(target))
            return UpdateApplyResult.Fail("Лаунчер установлен в папку, куда ему нельзя писать (например, Program Files). Перенеси его в папку пользователя и повтори.");

        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "Subreel.Launcher.Design.exe";
        var updaterSource = Path.Combine(target, UpdaterFolder);
        if (!File.Exists(Path.Combine(updaterSource, UpdaterExe)))
            return UpdateApplyResult.Fail("Не найдена программа обновления (папка Updater рядом с лаунчером). Установи лаунчер заново.");

        var work = Path.Combine(_paths.UpdatesDirectory, info.Version.ToString());
        var zip = Path.Combine(work, "update.zip");
        var extracted = Path.Combine(work, "files");
        var updaterCopy = Path.Combine(work, "updater");
        Directory.CreateDirectory(work);
        DeleteDirectory(extracted);
        DeleteDirectory(updaterCopy);

        _logger.LogInformation("Скачивается обновление лаунчера {Version} ({Size} байт).", info.Version, info.SizeBytes);
        var downloadProgress = progress is null ? null : new Progress<DownloadProgress>(p =>
            progress.Report(new UpdateProgress(UpdateStage.Downloading, p.TotalBytes is > 0 ? Math.Clamp((double)p.BytesReceived / p.TotalBytes.Value, 0, 1) : 0)));
        var download = await _downloads.DownloadAsync(new DownloadRequest(info.DownloadUrl, zip, null, info.SizeBytes), downloadProgress, ct).ConfigureAwait(false);
        if (!download.Succeeded)
        {
            _logger.LogWarning("Обновление не скачано: {Error}", download.Error);
            return UpdateApplyResult.Fail("Не удалось скачать обновление. Проверь подключение к интернету и попробуй ещё раз.");
        }

        progress?.Report(new UpdateProgress(UpdateStage.Verifying, 0));
        if (!await UpdatePackage.VerifySha256Async(zip, info.Sha256, ct).ConfigureAwait(false))
        {
            _logger.LogError("SHA-256 архива обновления {Version} не совпал с манифестом: файл удалён.", info.Version);
            TryDelete(zip);
            return UpdateApplyResult.Fail("Файл обновления повреждён или подменён: контрольная сумма не совпала. Установка отменена.");
        }

        progress?.Report(new UpdateProgress(UpdateStage.Unpacking, 0));
        await UpdatePackage.ExtractAsync(zip, extracted, exeName, ct).ConfigureAwait(false);

        // Программу замены запускаем из копии: она сможет заменить и свои файлы в папке лаунчера.
        CopyDirectory(updaterSource, updaterCopy);

        progress?.Report(new UpdateProgress(UpdateStage.Starting, 0));
        var log = Path.Combine(_paths.LogsDirectory, $"updater-{_time.GetLocalNow():yyyyMMdd}.log");
        var start = new ProcessStartInfo(Path.Combine(updaterCopy, UpdaterExe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterCopy,
        };
        foreach (var argument in new[] { "--pid", Environment.ProcessId.ToString(), "--source", extracted, "--target", target.TrimEnd(Path.DirectorySeparatorChar), "--exe", exeName, "--log", log })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) return UpdateApplyResult.Fail("Не удалось запустить программу обновления.");

        _logger.LogInformation("Программа обновления запущена (PID {Pid}); лаунчер закрывается для замены файлов.", process.Id);
        return UpdateApplyResult.Ok();
    }

    /// <summary>Остатки прошлых обновлений (распакованные файлы, копия программы замены) удаляются при первой проверке после запуска.</summary>
    private void CleanupLeftovers()
    {
        if (_cleaned) return;
        _cleaned = true;
        DeleteDirectory(_paths.UpdatesDirectory);
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Не удалось удалить {Path}.", path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Не удалось удалить {Path}.", path);
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    private void Set(Action change)
    {
        lock (_stateLock) change();
        RaiseChanged();
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
