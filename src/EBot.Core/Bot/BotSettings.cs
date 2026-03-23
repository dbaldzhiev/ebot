using EBot.Core.MemoryReading;

namespace EBot.Core.Bot;

/// <summary>
/// Configuration settings for a bot session.
/// </summary>
public sealed class BotSettings
{
    /// <summary>Sanderling memory reading configuration.</summary>
    public SanderlingConfig Sanderling { get; set; } = new();

    /// <summary>Interval between bot ticks in milliseconds.</summary>
    public int TickIntervalMs { get; set; } = 1500;

    /// <summary>Minimum random delay between input actions (ms).</summary>
    public int MinActionDelayMs { get; set; } = 50;

    /// <summary>Maximum random delay between input actions (ms).</summary>
    public int MaxActionDelayMs { get; set; } = 200;

    /// <summary>Max pixel jitter for mouse coordinates.</summary>
    public int CoordinateJitter { get; set; } = 3;

    /// <summary>Maximum runtime before the bot auto-stops (TimeSpan.Zero = no limit).</summary>
    public TimeSpan MaxRuntime { get; set; } = TimeSpan.Zero;

    /// <summary>Whether to log each memory reading to a file for debugging.</summary>
    public bool LogMemoryReadings { get; set; } = false;

    /// <summary>Directory to save memory reading logs to.</summary>
    public string LogDirectory { get; set; } = "logs";
}
