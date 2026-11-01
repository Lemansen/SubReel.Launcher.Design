using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Launching;

/// <summary>
/// Запуск игры. Порядок: сессия (быстро сообщает, что нужен вход, пока ничего не скачано) → проверка файлов версии и при необходимости
/// установка → Java → ассеты старых версий и нативные библиотеки → командная строка → процесс.
/// Общая шкала прогресса: сессия 0–5 %, проверка 5–10 %, установка 10–75 %, Java 75–90 %, нативы 90–95 %, старт 95–100 %.
/// Одну папку игры нельзя запустить дважды: пока идёт подготовка или игра работает, повторный запуск получает <see cref="LaunchFailure.AlreadyRunning"/>.
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    /// <summary>Максимум памяти, если в настройках экземпляра он не задан (МБ).</summary>
    public const int DefaultMaxMemoryMb = 2048;

    private const string NativesDirectoryName = "natives";
    private const string OutputLogRelativePath = "logs/subreel-game-output.log";

    private readonly IVersionMetadataProvider _metadata;
    private readonly IGameInstaller _installer;
    private readonly IJavaManager _java;
    private readonly IGameSessionSource _sessions;
    private readonly INativesExtractor _natives;
    private readonly ClasspathBuilder _classpath;
    private readonly LegacyAssetsPreparer _legacyAssets;
    private readonly GameFileLayout _layout;
    private readonly OsEnvironment _environment;
    private readonly IGameProcessStarter _starter;
    private readonly ILogger<GameLauncher> _logger;

    // Ключ — полный путь папки игры. null — подготовка идёт, процесса ещё нет.
    private readonly ConcurrentDictionary<string, IGameProcess?> _active = new(StringComparer.OrdinalIgnoreCase);

    public GameLauncher(
        IVersionMetadataProvider metadata,
        IGameInstaller installer,
        IJavaManager java,
        IGameSessionSource sessions,
        INativesExtractor natives,
        ClasspathBuilder classpath,
        LegacyAssetsPreparer legacyAssets,
        GameFileLayout layout,
        OsEnvironment environment,
        IGameProcessStarter starter,
        ILogger<GameLauncher> logger)
    {
        _metadata = metadata;
        _installer = installer;
        _java = java;
        _sessions = sessions;
        _natives = natives;
        _classpath = classpath;
        _legacyAssets = legacyAssets;
        _layout = layout;
        _environment = environment;
        _starter = starter;
        _logger = logger;
    }

    public IGameProcess? FindRunning(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        return _active.TryGetValue(Path.GetFullPath(gameDirectory), out var process) && process is { Status: GameStatus.Running } ? process : null;
    }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.VersionId)) throw new ArgumentException("Не указана версия игры.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.GameDirectory)) throw new ArgumentException("Не указана папка игры.", nameof(request));

        // Fabric, Quilt, Forge и NeoForge — обычная версия с inheritsFrom, профиль которой уже в кэше (его кладёт GamePlayService через ILoaderInstaller).
        if (request.Loader != LoaderKind.Vanilla && !LoaderInfo.CanInstall(request.Loader))
            return LaunchResult.Fail(LaunchFailure.UnsupportedLoader, $"Запуск с загрузчиком {LoaderInfo.Name(request.Loader)} пока не поддерживается.");

        var key = Path.GetFullPath(request.GameDirectory);
        if (!_active.TryAdd(key, null))
            return LaunchResult.Fail(LaunchFailure.AlreadyRunning, "Эта сборка уже запущена или запускается.");

        var handedOver = false;
        try
        {
            var result = await PrepareAndStartAsync(request, key, progress, ct).ConfigureAwait(false);
            handedOver = result.Succeeded;
            return result;
        }
        finally
        {
            if (!handedOver) _active.TryRemove(key, out _);
        }
    }

    private async Task<LaunchResult> PrepareAndStartAsync(LaunchRequest request, string key, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        void Report(LaunchStage stage, double fraction) => progress?.Report(new LaunchProgress(stage, Math.Clamp(fraction, 0, 1)));

        // 1. Сессия
        Report(LaunchStage.SigningIn, 0);
        ISession session;
        try
        {
            session = await _sessions.GetSessionAsync(request.Account, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            _logger.LogInformation("Сессия для аккаунта {Name} не получена: {Reason}.", request.Account.Name, ex.Reason);
            return LaunchResult.Fail(ex.Reason == AuthFailureReason.SignInRequired ? LaunchFailure.SignInRequired : LaunchFailure.SignInFailed, ex.Message);
        }

        // 2. Файлы версии
        Report(LaunchStage.PreparingFiles, 0.05);
        ResolvedVersion version;
        try
        {
            var verification = await _installer.VerifyAsync(request.VersionId, ct).ConfigureAwait(false);
            if (verification.IsValid)
            {
                version = await _metadata.ResolveAsync(request.VersionId, allowNetwork: false, ct).ConfigureAwait(false);
                Report(LaunchStage.InstallingFiles, 0.75);
            }
            else
            {
                _logger.LogInformation("Версия {Version}: нужна установка (описание есть: {Available}, проблемных файлов: {Count}).",
                    request.VersionId, verification.MetadataAvailable, verification.Problems.Count);

                var install = await _installer.InstallAsync(request.VersionId, new SyncProgress<InstallProgress>(p => Report(LaunchStage.InstallingFiles, MapInstall(p))), ct).ConfigureAwait(false);
                if (!install.Succeeded || install.Version is null)
                    return LaunchResult.Fail(LaunchFailure.FilesNotReady, install.Error ?? "Не удалось установить файлы игры.");

                version = install.Version;
            }
        }
        catch (VersionMetadataException ex)
        {
            _logger.LogWarning(ex, "Не удалось подготовить файлы версии {Version}.", request.VersionId);
            return LaunchResult.Fail(LaunchFailure.FilesNotReady, ex.Message);
        }

        // 3. Java
        Report(LaunchStage.PreparingJava, 0.75);
        var requirement = JavaRequirement.FromVersion(version);
        var javaResult = await _java.EnsureAsync(requirement, request.JavaPath,
            new SyncProgress<DownloadBatchProgress>(p => Report(LaunchStage.PreparingJava, 0.75 + 0.15 * Fraction(p.CompletedFiles, p.TotalFiles, p.BytesReceived, p.TotalBytes))), ct).ConfigureAwait(false);
        if (!javaResult.Succeeded)
            return LaunchResult.Fail(LaunchFailure.JavaUnavailable, javaResult.Error ?? $"Не найдена Java {requirement.MajorVersion}, и скачать её не удалось.");

        var java = javaResult.Installation!;
        if (javaResult.VersionMismatch)
            _logger.LogWarning("Java {Found} не совпадает с требуемой версией {Required}: используется путь из настроек экземпляра.", java.MajorVersion, requirement.MajorVersion);

        // 4. Ассеты старых версий и нативные библиотеки
        Report(LaunchStage.PreparingNatives, 0.90);
        string gameAssets;
        var nativesDirectory = Path.Combine(request.GameDirectory, NativesDirectoryName);
        try
        {
            Directory.CreateDirectory(request.GameDirectory);
            gameAssets = await _legacyAssets.PrepareAsync(version, request.GameDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VersionMetadataException)
        {
            _logger.LogError(ex, "Не удалось подготовить ассеты версии {Version}.", request.VersionId);
            return LaunchResult.Fail(LaunchFailure.FilesNotReady, $"Не удалось подготовить ассеты: {ex.Message}");
        }

        try
        {
            ResetDirectory(nativesDirectory);
            await _natives.ExtractAsync(version, nativesDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or VersionMetadataException)
        {
            _logger.LogError(ex, "Не удалось подготовить нативные библиотеки версии {Version}.", request.VersionId);
            return LaunchResult.Fail(LaunchFailure.NativesFailed, $"Не удалось подготовить нативные библиотеки: {ex.Message}. Возможно, игра уже запущена.");
        }

        // 5. Командная строка
        LaunchCommand command;
        try
        {
            var maxMemory = request.MaxMemoryMb ?? DefaultMaxMemoryMb;
            var minMemory = request.MinMemoryMb is { } min && min > maxMemory ? maxMemory : request.MinMemoryMb;

            var parameters = new LaunchParameters
            {
                PlayerName = session.Account.Name,
                PlayerUuid = session.Account.ProfileId,
                AccessToken = session.AccessToken,
                UserType = session.UserType,
                GameDirectory = request.GameDirectory,
                AssetsRoot = _layout.AssetsRoot,
                GameAssetsDirectory = gameAssets,
                NativesDirectory = nativesDirectory,
                LibraryDirectory = _layout.LibrariesDirectory,
                Classpath = _classpath.Build(version),
                LoggingConfigPath = version.Logging is { } logging ? _layout.LoggingConfigPath(logging.FileId) : null,
                MinMemoryMb = minMemory,
                MaxMemoryMb = maxMemory,
                ExtraJvmArguments = CommandLineTokenizer.Split(request.JvmArguments),
                WindowWidth = request.WindowWidth,
                WindowHeight = request.WindowHeight,
                LauncherVersion = LauncherVersion,
            };

            command = LaunchCommandBuilder.Build(version, parameters, _environment);
        }
        catch (VersionMetadataException ex)
        {
            _logger.LogError(ex, "Не удалось собрать команду запуска версии {Version}.", request.VersionId);
            return LaunchResult.Fail(LaunchFailure.FilesNotReady, ex.Message);
        }

        if (command.UnresolvedPlaceholders.Count > 0)
            _logger.LogWarning("В аргументах запуска остались неизвестные переменные: {Names}.", string.Join(", ", command.UnresolvedPlaceholders));

        // 6. Процесс
        Report(LaunchStage.Starting, 0.95);
        var logPath = Path.Combine(request.GameDirectory, OutputLogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var header =
            $"[SubReel] {DateTime.Now:yyyy-MM-dd HH:mm:ss} Minecraft {version.Id}, аккаунт {session.Account.Name}{Environment.NewLine}" +
            $"[SubReel] Java {java.FullVersion} ({java.Source}): {java.Executable}{Environment.NewLine}" +
            $"[SubReel] Команда: {command.ToDisplayString(session.AccessToken)}";

        IGameProcess process;
        try
        {
            process = _starter.Start(new GameProcessSpec(java.Executable, command.Arguments, request.GameDirectory, logPath, header));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось запустить процесс игры ({Java}).", java.Executable);
            return LaunchResult.Fail(LaunchFailure.StartFailed, $"Не удалось запустить Java: {ex.Message}");
        }

        _logger.LogInformation("Игра запущена: Minecraft {Version}, Java {Java}, аккаунт {Name}.", version.Id, java.MajorVersion, session.Account.Name);

        _active[key] = process;
        process.StatusChanged += (_, _) =>
        {
            if (process.Status != GameStatus.Running) _active.TryRemove(new KeyValuePair<string, IGameProcess?>(key, process));
        };
        // Игра могла завершиться, пока мы подписывались.
        if (process.Status != GameStatus.Running) _active.TryRemove(new KeyValuePair<string, IGameProcess?>(key, process));

        Report(LaunchStage.Starting, 1);
        return LaunchResult.Success(process);
    }

    private static string LauncherVersion =>
        typeof(GameLauncher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1";

    /// <summary>Папка нативов принадлежит лаунчеру: перед распаковкой очищается, чтобы библиотеки прошлой версии не попали в запуск.</summary>
    private static void ResetDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }

    private static double MapInstall(InstallProgress p) => p.Stage switch
    {
        InstallStage.ResolvingMetadata => 0.10,
        InstallStage.DownloadingFiles => 0.10 + 0.30 * Fraction(p.CompletedFiles, p.TotalFiles, p.BytesReceived, p.TotalBytes),
        InstallStage.DownloadingAssets => 0.40 + 0.35 * Fraction(p.CompletedFiles, p.TotalFiles, p.BytesReceived, p.TotalBytes),
        _ => 0.75,
    };

    private static double Fraction(int completedFiles, int totalFiles, long bytes, long? totalBytes) =>
        totalBytes is > 0 ? Math.Clamp((double)bytes / totalBytes.Value, 0, 1)
        : totalFiles > 0 ? Math.Clamp((double)completedFiles / totalFiles, 0, 1)
        : 0;

    /// <summary>Отчёт в том же потоке и сразу (в отличие от <see cref="Progress{T}"/>, которая откладывает вызов): порядок значений не нарушается.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
