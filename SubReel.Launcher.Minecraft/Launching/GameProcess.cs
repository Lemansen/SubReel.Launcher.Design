using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Games;

namespace SubReel.Launcher.Minecraft.Launching;

/// <summary>Что запустить. <paramref name="LogHeader"/> пишется первыми строками файла вывода (команда уже без токенов).</summary>
public sealed record GameProcessSpec(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string? LogFilePath, string? LogHeader);

/// <summary>Запускает процесс игры. Отдельный контракт нужен, чтобы запуск проверялся тестами без настоящей Java.</summary>
public interface IGameProcessStarter
{
    /// <summary>Бросает <see cref="System.ComponentModel.Win32Exception"/>, если файл не запускается; <see cref="IOException"/> — если не открылась рабочая папка.</summary>
    IGameProcess Start(GameProcessSpec spec);
}

public sealed class SystemGameProcessStarter : IGameProcessStarter
{
    private readonly ILoggerFactory _loggers;

    public SystemGameProcessStarter(ILoggerFactory loggers) => _loggers = loggers;

    public IGameProcess Start(GameProcessSpec spec) => GameProcess.Start(spec, _loggers.CreateLogger<GameProcess>());
}

/// <summary>
/// Процесс игры: весь вывод (stdout и stderr) уходит в файл журнала и в буфер последних строк. Код 0 или остановка пользователем —
/// <see cref="GameStatus.Exited"/>, любой другой код — <see cref="GameStatus.Crashed"/>. Статус меняется после того, как прочитан весь вывод.
/// </summary>
public sealed class GameProcess : IGameProcess
{
    private const int RecentLineLimit = 200;

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly TaskCompletionSource<GameStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StreamWriter? _log;
    private GameStatus _status = GameStatus.Running;
    private int? _exitCode;
    private volatile bool _killed;

    private GameProcess(Process process, string gameDirectory, string? logFilePath, ILogger logger)
    {
        _process = process;
        GameDirectory = gameDirectory;
        LogFilePath = logFilePath;
        _logger = logger;
    }

    public string GameDirectory { get; }

    public string? LogFilePath { get; }

    public GameStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate) return _exitCode;
        }
    }

    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_gate) return _recent.ToList();
        }
    }

    public event EventHandler? StatusChanged;

    public static GameProcess Start(GameProcessSpec spec, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(spec);

        Directory.CreateDirectory(spec.WorkingDirectory);

        var info = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var game = new GameProcess(process, spec.WorkingDirectory, spec.LogFilePath, logger);
        game.OpenLog(spec.LogFilePath, spec.LogHeader);

        process.OutputDataReceived += (_, e) => game.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => game.OnLine(e.Data);
        process.Exited += (_, _) => game.OnExited();

        try
        {
            process.Start();
        }
        catch
        {
            game.CloseLog();
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return game;
    }

    public Task<GameStatus> WaitForExitAsync(CancellationToken ct = default) => _exited.Task.WaitAsync(ct);

    public void Kill()
    {
        _killed = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // процесс уже завершился между проверкой и остановкой — это не сбой
            _logger.LogDebug(ex, "Остановка игры: процесс уже завершён.");
        }
    }

    private void OpenLog(string? path, string? header)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            if (!string.IsNullOrEmpty(header)) _log.WriteLine(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // без файла игра всё равно запускается: вывод остаётся в буфере последних строк
            _logger.LogWarning(ex, "Не удалось открыть файл вывода игры {Path}.", path);
            _log = null;
        }
    }

    private void OnLine(string? line)
    {
        if (line is null) return;

        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentLineLimit) _recent.Dequeue();

            try
            {
                _log?.WriteLine(line);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Файл вывода игры недоступен для записи, запись прекращена.");
                _log = null;
            }
        }
    }

    private void OnExited()
    {
        // Без этого вызова последние строки вывода могут быть ещё не прочитаны: он ждёт конца потоков.
        try
        {
            _process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // процесс уже освобождён — выходим с тем, что есть
        }

        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        GameStatus status;
        lock (_gate)
        {
            status = code == 0 || _killed ? GameStatus.Exited : GameStatus.Crashed;
            _exitCode = code;
            _status = status;
            try
            {
                _log?.WriteLine($"[SubReel] Игра завершилась (код {code}{(_killed ? ", остановлена пользователем" : "")}).");
            }
            catch (IOException)
            {
                // журнал недоступен — статус важнее
            }
        }

        CloseLog();
        _logger.LogInformation("Игра завершилась: код {Code}, статус {Status}.", code, status);

        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // сбой подписчика не должен оставить ожидающих без ответа
            _logger.LogError(ex, "Обработчик статуса игры завершился ошибкой.");
        }

        _exited.TrySetResult(status);
        _process.Dispose();
    }

    private void CloseLog()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }
}
