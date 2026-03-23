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
    /// Enqueues typing a string (character by character via TypeTextAction).
    /// </summary>
    public void TypeText(string text)
    {
        Actions.Enqueue(new TypeTextAction(text));
    }
}
