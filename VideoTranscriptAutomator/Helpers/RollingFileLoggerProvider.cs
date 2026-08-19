using Microsoft.Extensions.Logging;

namespace VideoTranscriptAutomator.Helpers;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public RollingFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);

        CleanupOldLogs();

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var logPath = Path.Combine(_logDirectory, $"execution-{timestamp}.log");
        _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Wait();
        _writer.Close();
        _lock.Dispose();
    }

    internal void WriteLog(string level, string category, string message, Exception? exception)
    {
        if (_disposed) return;

        _lock.Wait();
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _writer.WriteLine($"[{timestamp}] [{level,-5}] [{category}] {message}");
            if (exception is not null)
                _writer.WriteLine($"  Exception: {exception}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void CleanupOldLogs()
    {
        var files = Directory.GetFiles(_logDirectory, "execution-*.log")
            .OrderByDescending(f => f)
            .Skip(5)
            .ToList();

        foreach (var file in files)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "LOG"
            };

            provider.WriteLog(level, category, formatter(state, exception), exception);
        }
    }
}
