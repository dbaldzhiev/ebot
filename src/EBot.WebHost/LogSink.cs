using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EBot.WebHost;

/// <summary>
/// In-memory log sink. All application logs are routed here so the
/// terminal dashboard and web UI can display them without conflicts.
/// </summary>
public sealed class LogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 500;

    public event Action<LogEntry>? EntryAdded;

    public void Add(string level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, category, message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 100) =>
        _entries.TakeLast(count).ToList();
}

// ─── ILoggerProvider that routes to LogSink ───────────────────────────────

public sealed class LogSinkProvider(LogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new LogSinkLogger(sink, categoryName);

    public void Dispose() { }
}

internal sealed class LogSinkLogger(LogSink sink, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warn",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Crit",
            _ => "Log",
        };

        var msg = formatter(state, exception);
        if (exception != null)
            msg += $" | {exception.Message}";

        // Filter noisy ASP.NET internals
        if (category.StartsWith("Microsoft.AspNetCore") && logLevel < LogLevel.Warning)
            return;
        if (category.StartsWith("Microsoft.Hosting") && logLevel < LogLevel.Warning)
            return;

        sink.Add(level, ShortCategory(category), msg);
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }
}
