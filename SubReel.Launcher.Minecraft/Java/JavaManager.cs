using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Minecraft.Java;

/// <summary>
/// Java Manager: собирает установленные Java из всех источников и рантаймов лаунчера, подбирает подходящую по правилу
/// <see cref="JavaSelector"/> и при необходимости скачивает рантайм Mojang. Результат поиска кэшируется до явного обновления.
/// </summary>
public sealed class JavaManager : IJavaManager
{
    private readonly IReadOnlyList<IJavaHomeSource> _sources;
    private readonly JavaVersionReader _reader;
    private readonly ManagedJavaInstaller _managed;
    private readonly OsEnvironment _environment;
    private readonly ILogger<JavaManager> _logger;

    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private IReadOnlyList<JavaInstallation>? _cache;

    public JavaManager(IEnumerable<IJavaHomeSource> sources, JavaVersionReader reader, ManagedJavaInstaller managed, OsEnvironment environment, ILogger<JavaManager> logger)
    {
        _sources = sources.ToList();
        _reader = reader;
        _managed = managed;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<JavaInstallation>> GetInstallationsAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is not null) return _cache;

            var found = new List<JavaInstallation>(await _managed.ReadInstalledAsync(ct).ConfigureAwait(false));
            var seen = new HashSet<string>(found.Select(i => Normalize(i.Home)), StringComparer.OrdinalIgnoreCase);

            foreach (var source in _sources)
            {
                foreach (var home in SafeHomes(source))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(home) || !seen.Add(Normalize(home))) continue;

                    var installation = await _reader.ReadAsync(home, source.Source, null, ct).ConfigureAwait(false);
                    if (installation is not null) found.Add(installation);
                }
            }

            _logger.LogInformation("Найдено установок Java: {Count}.", found.Count);
            _cache = found;
            return found;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public async Task<JavaInstallation?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');

        if (Directory.Exists(path))
        {
            foreach (var home in new[] { path, Path.Combine(path, "Contents", "Home") })
            {
                var installation = await _reader.ReadAsync(home, JavaSource.Manual, null, ct).ConfigureAwait(false);
                if (installation is not null) return installation;
            }
            return null;
        }

        if (!File.Exists(path)) return null;

        var fileName = Path.GetFileNameWithoutExtension(path);
        var directory = Directory.GetParent(path);
        if (directory is null || directory.Parent is null || !fileName.Equals("java", StringComparison.OrdinalIgnoreCase) && !fileName.Equals("javaw", StringComparison.OrdinalIgnoreCase)) return null;
        if (!directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;

        return await _reader.ReadAsync(directory.Parent.FullName, JavaSource.Manual, null, ct).ConfigureAwait(false);
    }

    public async Task<JavaSelection> SelectAsync(JavaRequirement requirement, string? explicitJavaPath = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitJavaPath))
        {
            var chosen = await ProbeAsync(explicitJavaPath, ct).ConfigureAwait(false);
            if (chosen is null) return new JavaSelection(null, JavaSelectionStatus.ExplicitPathInvalid);

            var mismatch = chosen.MajorVersion != requirement.MajorVersion;
            if (mismatch) _logger.LogWarning("Выбранная вручную Java {Actual} не совпадает с требуемой {Required}.", chosen.MajorVersion, requirement.MajorVersion);
            return new JavaSelection(chosen, JavaSelectionStatus.ExplicitPathUsed, mismatch);
        }

        var installations = await GetInstallationsAsync(refresh: false, ct).ConfigureAwait(false);
        return JavaSelector.Select(requirement, installations, allow32Bit: !_environment.Is64Bit);
    }

    public async Task<JavaEnsureResult> EnsureAsync(JavaRequirement requirement, string? explicitJavaPath = null, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default)
    {
        var selection = await SelectAsync(requirement, explicitJavaPath, ct).ConfigureAwait(false);
        if (selection.HasInstallation) return new JavaEnsureResult(selection.Installation, selection.Status, selection.VersionMismatch, false, null);

        switch (selection.Status)
        {
            case JavaSelectionStatus.ExplicitPathInvalid:
                return Failure(selection.Status, "Указанный путь не ведёт к рабочей Java. Выберите java.exe или папку установки.");
            case JavaSelectionStatus.NotFound:
                return Failure(selection.Status, $"Для этой версии нужна Java {requirement.MajorVersion}, а подходящей нет и скачать её нельзя. Установите её и укажите путь вручную.");
        }

        var component = requirement.Component!;
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали очередь, другой вызов мог уже установить этот рантайм.
            var again = await SelectAsync(requirement, null, ct).ConfigureAwait(false);
            if (again.HasInstallation) return new JavaEnsureResult(again.Installation, again.Status, false, false, null);

            var result = await _managed.InstallAsync(component, progress, ct).ConfigureAwait(false);
            if (!result.Succeeded) return Failure(JavaSelectionStatus.NeedsDownload, result.Error ?? "Не удалось установить Java.");

            await GetInstallationsAsync(refresh: true, ct).ConfigureAwait(false);

            if (result.Installation!.MajorVersion != requirement.MajorVersion)
                return Failure(JavaSelectionStatus.NeedsDownload, $"Рантайм «{component}» содержит Java {result.Installation.MajorVersion}, а версия игры требует {requirement.MajorVersion}.");

            return new JavaEnsureResult(result.Installation, JavaSelectionStatus.Selected, false, true, null);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private static JavaEnsureResult Failure(JavaSelectionStatus status, string error) => new(null, status, false, false, error);

    private static string Normalize(string home) => Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Источник не должен ронять поиск: при сбое перечисления берём то, что он успел отдать.</summary>
    private IEnumerable<string> SafeHomes(IJavaHomeSource source)
    {
        var homes = new List<string>();
        try
        {
            homes.AddRange(source.GetHomes());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Источник Java {Source} недоступен.", source.Source);
        }
        return homes;
    }
}
