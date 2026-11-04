using System.Diagnostics;
using System.Globalization;

namespace SubReel.Updater;

/// <summary>
/// Заменяет файлы лаунчера после его закрытия. Работающий exe подменить нельзя, поэтому лаунчер запускает эту программу и закрывается.
/// Порядок: дождаться выхода лаунчера → сохранить заменяемые файлы → скопировать новые → запустить новый лаунчер.
/// При любом сбое прежние файлы возвращаются на место и запускается прежний лаунчер. Папка данных пользователя не затрагивается: заменяется только папка рядом с exe.
/// Аргументы: --pid N --source ПАПКА --target ПАПКА --exe ИМЯ.exe [--log ФАЙЛ]
/// </summary>
internal static class Program
{
    private const int WaitForExitSeconds = 60;
    private const int CopyAttempts = 20;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null) return 2;

        var log = new Log(options.LogPath);
        try
        {
            return Run(options, log);
        }
        catch (Exception ex)
        {
            log.Write("Критический сбой: " + ex);
            Start(options, log);
            return 1;
        }
    }

    private static int Run(Options options, Log log)
    {
        log.Write($"Обновление: источник «{options.Source}», цель «{options.Target}», запуск «{options.Exe}».");

        if (!WaitForLauncherExit(options.Pid, log)) return 1; // лаунчер всё ещё работает: ничего не трогаем и второй копии не запускаем

        var plan = BuildPlan(options, log);
        if (plan is null)
        {
            Start(options, log);
            return 1;
        }

        var backup = Path.Combine(Path.GetDirectoryName(options.Source.TrimEnd(Path.DirectorySeparatorChar))!, "backup");
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);

        var replaced = new List<(string Target, string Saved)>();
        var created = new List<string>();
        try
        {
            foreach (var (source, target, relative) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Copy(target, saved, overwrite: true), log, relative);
                    replaced.Add((target, saved));
                }
                else
                {
                    created.Add(target);
                }
                Retry(() => File.Copy(source, target, overwrite: true), log, relative);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Не удалось заменить файлы, возвращаю прежние: " + ex.Message);
            Rollback(replaced, created, log);
            Start(options, log);
            return 1;
        }

        log.Write($"Заменено файлов: {plan.Count}. Запускаю новый лаунчер.");
        var started = Start(options, log);

        TryDelete(options.Source, log);
        TryDelete(backup, log);
        return started ? 0 : 1;
    }

    private static bool WaitForLauncherExit(int pid, Log log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            log.Write($"Жду закрытия лаунчера (PID {pid}).");
            if (process.WaitForExit(TimeSpan.FromSeconds(WaitForExitSeconds))) return true;
            log.Write("Лаунчер не закрылся за отведённое время: обновление отменено, файлы не тронуты.");
            return false;
        }
        catch (ArgumentException)
        {
            return true; // процесса уже нет
        }
    }

    /// <summary>Список файлов к копированию. null — источник не годится (нет exe, небезопасные пути): тогда ничего не трогаем.</summary>
    private static List<(string Source, string Target, string Relative)>? BuildPlan(Options options, Log log)
    {
        if (!Directory.Exists(options.Source) || !Directory.Exists(options.Target))
        {
            log.Write("Папка с обновлением или папка лаунчера не найдена.");
            return null;
        }
        if (options.Exe.IndexOfAny(new[] { '/', '\\' }) >= 0 || !File.Exists(Path.Combine(options.Source, options.Exe)))
        {
            log.Write($"В обновлении нет файла «{options.Exe}»: установка отменена.");
            return null;
        }

        var source = Path.GetFullPath(options.Source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(options.Target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var plan = new List<(string, string, string)>();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Небезопасный путь «{relative}»: установка отменена.");
                return null;
            }
            plan.Add((file, destination, relative));
        }
        return plan;
    }

    /// <summary>Файлы могут быть заняты антивирусом или ещё не освобождены системой: повторяем несколько раз.</summary>
    private static void Retry(Action action, Log log, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < CopyAttempts)
            {
                if (attempt == 1) log.Write($"Файл «{what}» занят, жду: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    private static void Rollback(List<(string Target, string Saved)> replaced, List<string> created, Log log)
    {
        foreach (var (target, saved) in replaced)
        {
            try { File.Copy(saved, target, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"Не удалось вернуть «{target}»: {ex.Message}"); }
        }
        foreach (var file in created)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"Не удалось удалить «{file}»: {ex.Message}"); }
        }
    }

    private static bool Start(Options options, Log log)
    {
        try
        {
            var path = Path.Combine(options.Target, options.Exe);
            Process.Start(new ProcessStartInfo(path) { WorkingDirectory = options.Target, UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log.Write("Не удалось запустить лаунчер: " + ex.Message);
            return false;
        }
    }

    private static void TryDelete(string directory, Log log)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Не удалось удалить «{directory}»: {ex.Message}");
        }
    }

    private sealed record Options(int Pid, string Source, string Target, string Exe, string? LogPath)
    {
        public static Options? Parse(string[] args)
        {
            string? Get(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            if (!int.TryParse(Get("--pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return null;
            var source = Get("--source");
            var target = Get("--target");
            var exe = Get("--exe");
            if (source is null || target is null || exe is null) return null;
            return new Options(pid, source, target, exe, Get("--log"));
        }
    }

    private sealed class Log
    {
        private readonly string? _path;

        public Log(string? path)
        {
            _path = path;
            try
            {
                if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _path = null; // журнал — удобство, а не условие обновления
            }
        }

        public void Write(string message)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [updater] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // нечего делать: журнал недоступен
            }
        }
    }
}
