namespace EBot.Core.MemoryReading;

/// <summary>
/// Configuration for the Sanderling memory reading integration.
/// </summary>
public sealed class SanderlingConfig
{
    /// <summary>
    /// Path to the Sanderling read-memory-64-bit.exe executable.
    /// </summary>
    public string ExecutablePath { get; set; } = "read-memory-64-bit.exe";

    /// <summary>
    /// The PID of the EVE Online client process. 0 = auto-detect.
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Interval between memory reads in milliseconds.
    /// </summary>
    public int ReadIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Timeout for a single memory read operation in milliseconds.
    /// </summary>
    public int ReadTimeoutMs { get; set; } = 60000;

    /// <summary>
    /// Optional path to save the JSON output to. If null, reads from stdout.
    /// </summary>
    public string? OutputFilePath { get; set; }

    /// <summary>
    /// Base URL of a running alternate-ui HTTP server (e.g. "http://localhost:4008").
    /// When set, SanderlingReader uses the HTTP API instead of spawning a new process
    /// each tick, which eliminates the ~60 s .NET cold-start overhead.
    /// </summary>
    public string? HttpServerUrl { get; set; }
}
