using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Games;

/// <summary>Этап подготовки запуска (для подписи рядом с кнопкой «Играть»).</summary>
public enum LaunchStage
{
    SigningIn,
    PreparingFiles,
    InstallingFiles,
    PreparingJava,
    PreparingNatives,
    Starting,
    /// <summary>Профиль загрузчика (Fabric, Quilt) скачивается с мета-сервера. Идёт до остальных этапов, но в перечислении добавлен в конец.</summary>
    PreparingLoader,
}

/// <summary>Общий ход подготовки: <paramref name="Fraction"/> от 0 до 1 за весь запуск, а не за один этап.</summary>
public readonly record struct LaunchProgress(LaunchStage Stage, double Fraction);

public enum LaunchFailure
{
    None,
    /// <summary>Эта папка игры уже запущена или запускается.</summary>
    AlreadyRunning,
    /// <summary>Загрузчик, который лаунчер пока не умеет запускать (Forge, NeoForge, особые сборки).</summary>
    UnsupportedLoader,
    SignInRequired,
    SignInFailed,
    /// <summary>Файлы версии не установлены и не скачались (нет сети, версия не найдена, повреждённые файлы).</summary>
    FilesNotReady,
    JavaUnavailable,
    NativesFailed,
    StartFailed,
    /// <summary>Не удалось найти или создать сборку (экземпляр) для выбранной версии: папка недоступна для записи.</summary>
    InstanceUnavailable,
    /// <summary>Профиль загрузчика (Fabric, Quilt) не удалось получить: нет сети, версия игры не поддерживается загрузчиком.</summary>
    LoaderNotReady,
}

/// <summary>Что запускать. Версия и папка игры заданы явно: запуск не зависит от того, откуда они взялись (экземпляр или выбор в списке).</summary>
/// <param name="VersionId">Идентификатор версии в общем кэше: для ванили — версия Minecraft, для Fabric и Quilt — имя профиля загрузчика (<c>fabric-loader-0.16.5-1.21.1</c>), который уже лежит в кэше.</param>
/// <param name="GameDirectory">Папка игры (saves, mods, options.txt); создаётся при необходимости.</param>
public sealed record LaunchRequest(
    string VersionId,
    string GameDirectory,
    LauncherAccount Account,
    LoaderKind Loader = LoaderKind.Vanilla,
    int? MinMemoryMb = null,
    int? MaxMemoryMb = null,
    string? JvmArguments = null,
    string? JavaPath = null,
    int? WindowWidth = null,
    int? WindowHeight = null)
{
    public static LaunchRequest ForInstance(Instance instance, string gameDirectory, LauncherAccount account) =>
        new(instance.McVersion, gameDirectory, account, instance.Loader, instance.MinMemoryMb, instance.MaxMemoryMb, instance.JvmArguments, instance.JavaPath);
}

public sealed record LaunchResult(IGameProcess? Process, LaunchFailure Failure, string? Error)
{
    public bool Succeeded => Process is not null;

    public static LaunchResult Success(IGameProcess process) => new(process, LaunchFailure.None, null);

    public static LaunchResult Fail(LaunchFailure failure, string error) => new(null, failure, error);
}

public enum GameStatus
{
    Running,
    /// <summary>Игра закрылась с кодом 0 или была остановлена пользователем.</summary>
    Exited,
    /// <summary>Игра завершилась с ненулевым кодом.</summary>
    Crashed,
}

/// <summary>Запущенный процесс игры.</summary>
public interface IGameProcess
{
    string GameDirectory { get; }

    GameStatus Status { get; }

    /// <summary>null, пока игра работает.</summary>
    int? ExitCode { get; }

    /// <summary>Файл, куда пишется весь вывод игры (stdout и stderr).</summary>
    string? LogFilePath { get; }

    /// <summary>Последние строки вывода игры — для показа при сбое.</summary>
    IReadOnlyList<string> RecentOutput { get; }

    /// <summary>Статус изменился. Вызывается не из потока интерфейса: ViewModel сама переходит в UI-поток.</summary>
    event EventHandler? StatusChanged;

    Task<GameStatus> WaitForExitAsync(CancellationToken ct = default);

    /// <summary>Останавливает игру (вместе с дочерними процессами). Остановленная пользователем игра получает статус <see cref="GameStatus.Exited"/>.</summary>
    void Kill();
}

/// <summary>Подготовка и запуск игры: сессия, файлы версии, Java, нативные библиотеки, командная строка, процесс.</summary>
public interface IGameLauncher
{
    /// <summary>
    /// Ожидаемые сбои возвращаются в <see cref="LaunchResult"/> с понятным текстом. Отмена действует только до старта процесса
    /// и выбрасывает <see cref="OperationCanceledException"/>; уже запущенную игру останавливает <see cref="IGameProcess.Kill"/>.
    /// </summary>
    Task<LaunchResult> LaunchAsync(LaunchRequest request, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Работающая игра в этой папке; null — не запущена.</summary>
    IGameProcess? FindRunning(string gameDirectory);
}
