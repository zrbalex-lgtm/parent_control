using System.Text;
using Microsoft.Extensions.Logging;

namespace ParentControl.Service;

/// <summary>Minimal daily-rolling file logger: logs\guard-YYYYMMDD.log, keeps 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;
    private readonly string _dir;
    private readonly object _lock = new();
    private string? _currentPath;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
        Cleanup();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var now = DateTime.Now;
        var path = Path.Combine(_dir, $"guard-{now:yyyyMMdd}.log");
        var shortCategory = category[(category.LastIndexOf('.') + 1)..];
        var sb = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ')
            .Append(Abbrev(level)).Append(' ')
            .Append(shortCategory).Append(": ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();

        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, sb.ToString());
                if (path != _currentPath)
                {
                    _currentPath = path;
                    Cleanup();
                }
            }
            catch (IOException)
            {
                // Logging must never break enforcement.
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "guard-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Abbrev(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
