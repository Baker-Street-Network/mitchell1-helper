using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

/// <summary>
/// Minimal rolling file logger. The app runs as a WinForms process with no console
/// attached, so console/debug logging is invisible in production — without this a
/// failing sync looks identical to an idle one.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "logs"
    );

    private const int RetainDays = 14;

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            PruneOldLogs();
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static string LogDirectoryPath => LogDirectory;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        if (level < _minLevel) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append("Z [").Append(Abbreviate(level)).Append("] ")
          .Append(categoryName).Append(" - ").Append(message);

        if (exception != null)
        {
            sb.AppendLine().Append(exception);
        }

        string line = sb.ToString();

        lock (_writeLock)
        {
            try
            {
                string path = Path.Combine(LogDirectory, $"mitchell1-helper-{DateTime.UtcNow:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Disk full, file locked, permissions — never propagate.
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "___"
    };

    private static void PruneOldLogs()
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-RetainDays);
        foreach (string file in Directory.EnumerateFiles(LogDirectory, "mitchell1-helper-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // Leave it in place if it can't be removed.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
