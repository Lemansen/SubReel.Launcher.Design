using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Infrastructure.Logging;

/// <summary>
/// Пишет журнал в файл <c>launcher-ГГГГММДД.log</c> (новый файл каждый день, старше <c>retainDays</c> дней удаляются).
/// Потокобезопасен. Сбой записи самого журнала не должен влиять на лаунчер: он уходит в отладочный вывод.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _writerDate;
    private bool _disposed;

    public FileLoggerProvider(string directory, int retainDays = 14)
    {
        _directory = directory;
        DeleteOldFiles(retainDays);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string text)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var date = DateTime.Now.ToString("yyyyMMdd");
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    var stream = new FileStream(Path.Combine(_directory, $"launcher-{date}.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = date;
                }
                _writer.WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"SubReel: не удалось записать журнал: {ex.Message}");
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private void DeleteOldFiles(int retainDays)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var limit = DateTime.UtcNow.AddDays(-retainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "launcher-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"SubReel: не удалось очистить старые журналы: {ex.Message}");
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(Short(logLevel)).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null) text.AppendLine().Append(exception);

            _owner.Write(text.ToString());
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
