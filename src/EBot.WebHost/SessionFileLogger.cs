namespace EBot.WebHost;

/// <summary>
/// Writes every log entry to a timestamped session file in the repo's logs/ folder.
/// The file is created when the service starts and kept open (AutoFlush) so entries
/// appear in real-time without buffering.
/// </summary>
public sealed class SessionFileLogger(LogSink logSink) : BackgroundService
{
    private StreamWriter? _writer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logsDir = GetLogsDirectory();
        Directory.CreateDirectory(logsDir);

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var filePath  = Path.Combine(logsDir, $"session_{timestamp}.log");

        _writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };

        _writer.WriteLine($"# EBot Session Log — started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        _writer.WriteLine($"# File: {filePath}");
        _writer.WriteLine();

        logSink.EntryAdded += WriteEntry;

        // Announce the log file path so users can find it easily
        logSink.Add("Info", "Logger", $"Session log: {filePath}");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        logSink.EntryAdded -= WriteEntry;

        _writer.WriteLine();
        _writer.WriteLine($"# Session ended {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        await _writer.DisposeAsync();
    }

    private void WriteEntry(LogEntry entry)
    {
        try
        {
            _writer?.WriteLine(
                $"[{entry.Time.LocalDateTime:HH:mm:ss.fff}] [{entry.Level,-5}] [{entry.Category}] {entry.Message}");
        }
        catch { /* ignore transient I/O errors */ }
    }

    /// <summary>
    /// Walks up from AppContext.BaseDirectory until a .git folder is found.
    /// Returns {gitRoot}/logs/ or falls back to {exe dir}/logs/.
    /// </summary>
    public static string GetLogsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return Path.Combine(dir.FullName, "logs");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }
}
