using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.Core.DecisionEngine;

/// <summary>
/// Context provided to behavior tree nodes on each tick.
/// Contains the current game state, action queue, and persistent bot memory.
/// </summary>
public sealed class BotContext
{
    /// <summary>
    /// The current game state snapshot (read-only view of the EVE client).
    /// </summary>
    public GameStateSnapshot GameState { get; set; } = new();

    /// <summary>
    /// The blackboard for persistent data between ticks.
    /// </summary>
    public Blackboard Blackboard { get; } = new();

    /// <summary>
    /// The action queue — nodes enqueue actions here; the execution engine processes them.
    /// </summary>
    public ActionQueue Actions { get; } = new();

    /// <summary>
    /// Total number of ticks executed since bot start.
    /// </summary>
    public long TickCount { get; set; }

    /// <summary>
    /// Time when the bot started running.
    /// </summary>
    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How long the bot has been running.
    /// </summary>
    public TimeSpan RunDuration => DateTimeOffset.UtcNow - StartTime;

    /// <summary>
    /// Trace of currently executing behavior tree nodes (stack).
    /// Used for debugging hangs and verbose logging.
    /// </summary>
    public Stack<string> ActiveNodes { get; } = new();

    // ─── Diagnostic log (per-tick messages forwarded to ILogger by BotRunner) ──

    private readonly List<string> _diagMessages = [];

    /// <summary>
    /// Enqueue a diagnostic message to be logged this tick.
    /// Use sparingly — only for debugging hard-to-reproduce state machine issues.
    /// </summary>
    public void Log(string message) => _diagMessages.Add(message);

    /// <summary>Called by BotRunner after each tick to drain and log pending messages.</summary>
    internal IReadOnlyList<string> DrainDiagMessages()
    {
        var msgs = _diagMessages.ToList();
        _diagMessages.Clear();
        return msgs;
    }

    // ─── Action Enqueue Helpers ────────────────────────────────────────

    // ─── Self-termination ─────────────────────────────────────────────────

    /// <summary>
    /// Set by a behavior tree node to signal that the bot has finished its task
    /// and should return to monitor (idle) mode after this tick completes.
    /// Checked and consumed by BotRunner at the end of each tick.
    /// </summary>
    public bool StopRequested { get; private set; }

    /// <summary>
    /// Request the runner to stop this bot and return to idle/monitor mode.
    /// Safe to call from any behavior tree node.
    /// </summary>
    public void RequestStop() => StopRequested = true;

    /// <summary>Called by BotRunner after consuming the stop request.</summary>
    internal void ConsumeStopRequest() => StopRequested = false;

    // ─── Action Enqueue Helpers ────────────────────────────────────────

    /// <summary>
    /// Enqueues a left-click on a UI node's center.
    /// </summary>
    public void Click(UITreeNodeWithDisplayRegion node)
    {
        var (x, y) = node.Center;
        Actions.Enqueue(new ClickAction(x, y));
    }

    /// <summary>
    /// Enqueues a left-click on a UI node's center with optional modifiers.
    /// (e.g. Ctrl+Click to lock a target).
    /// </summary>
    public void Click(UITreeNodeWithDisplayRegion node, params VirtualKey[] modifiers)
    {
        var (x, y) = node.Center;
        Actions.Enqueue(new ClickAction(x, y, modifiers));
    }

    /// <summary>
    /// Enqueues a right-click on a UI node's center.
    /// </summary>
    public void RightClick(UITreeNodeWithDisplayRegion node)
    {
        var (x, y) = node.Center;
        Actions.Enqueue(new RightClickAction(x, y));
    }

    /// <summary>
    /// Enqueues a key press.
    /// </summary>
    public void KeyPress(VirtualKey key, params VirtualKey[] modifiers)
    {
        Actions.Enqueue(new KeyPressAction(key, modifiers));
    }

    /// <summary>
    /// Enqueues a wait period.
    /// </summary>
    public void Wait(TimeSpan duration)
    {
        Actions.Enqueue(new WaitAction(duration));
    }

    /// <summary>
    /// Enqueues a left-click at specific coordinates.
    /// </summary>
    public void ClickAt(int x, int y)
    {
        Actions.Enqueue(new ClickAction(x, y));
    }

    /// <summary>
    /// Enqueues a mouse move (hover) to a UI node's center — no button press.
    /// Use for context-menu entries that expand submenus on hover.
    /// </summary>
    public void Hover(UITreeNodeWithDisplayRegion node)
    {
        var (x, y) = node.Center;
        Actions.Enqueue(new MoveMouseAction(x, y));
    }

    /// <summary>
    /// Finds a context menu entry by text and enqueues a click on it.
    /// </summary>
    public void ClickMenuEntry(string text)
    {
        var entry = GameState.ParsedUI.ContextMenus
            .SelectMany(m => m.Entries)
            .FirstOrDefault(e => e.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        
        if (entry != null) Click(entry.UINode);
    }

    /// <summary>
    /// Enqueues typing a string (character by character via TypeTextAction).
    /// </summary>
    public void TypeText(string text)
    {
        Actions.Enqueue(new TypeTextAction(text));
    }
}
