namespace EBot.Core.GameState;

/// <summary>
/// An immutable, thread-safe snapshot of the game state at a point in time.
/// Consumed by the decision engine.
/// </summary>
public sealed class GameStateSnapshot
{
    /// <summary>Parsed UI elements from the memory reading.</summary>
    public ParsedUI ParsedUI { get; init; } = new();

    /// <summary>Timestamp when this snapshot was captured.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>PID of the EVE Online client this snapshot was taken from.</summary>
    public int ProcessId { get; init; }

    /// <summary>Time it took to perform the memory reading.</summary>
    public TimeSpan ReadDuration { get; init; }

    /// <summary>Sequential snapshot number since the bot started.</summary>
    public long SequenceNumber { get; init; }

    // ─── Convenience Properties ────────────────────────────────────────

    /// <summary>True if the ShipUI is visible (i.e., ship is in space).</summary>
    public bool IsInSpace => ParsedUI.ShipUI != null;

    /// <summary>True if the station window is visible (i.e., ship is docked).</summary>
    public bool IsDocked => ParsedUI.StationWindow != null;

    /// <summary>True if there are any locked targets.</summary>
    public bool HasTargets => ParsedUI.Targets.Count > 0;

    /// <summary>Number of locked targets.</summary>
    public int TargetCount => ParsedUI.Targets.Count;

    /// <summary>The capacitor level as a percentage (0-100), or null if unavailable.</summary>
    public int? CapacitorPercent => ParsedUI.ShipUI?.Capacitor?.LevelPercent;

    /// <summary>True if any context menu is visible.</summary>
    public bool HasContextMenu => ParsedUI.ContextMenus.Count > 0;

    /// <summary>True if there is a message box visible.</summary>
    public bool HasMessageBox => ParsedUI.MessageBoxes.Count > 0;

    /// <summary>
    /// True if the ship is warping (indication contains "warp").
    /// </summary>
    public bool IsWarping => ParsedUI.ShipUI?.Indication?.ManeuverType?
        .Contains("warp", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Number of jumps remaining on the autopilot route.</summary>
    public int RouteJumpsRemaining =>
        ParsedUI.InfoPanelContainer?.InfoPanelRoute?.NextSystemsCount ?? 0;
}
