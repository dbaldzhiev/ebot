namespace EBot.Core.Execution;

/// <summary>
/// Virtual key codes for keyboard input simulation (subset of Win32 VK codes).
/// </summary>
public enum VirtualKey : ushort
{
    // Modifiers
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,

    // Function keys
    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73,
    F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77,
    F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,

    // Navigation
    Escape = 0x1B,
    Tab = 0x09,
    Enter = 0x0D,
    Space = 0x20,
    Backspace = 0x08,
    Delete = 0x2E,

    // Arrows
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,

    // Letters (A-Z)
    A = 0x41, B = 0x42, C = 0x43, D = 0x44,
    E = 0x45, F = 0x46, G = 0x47, H = 0x48,
    I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C,
    M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50,
    Q = 0x51, R = 0x52, S = 0x53, T = 0x54,
    U = 0x55, V = 0x56, W = 0x57, X = 0x58,
    Y = 0x59, Z = 0x5A,

    // Numbers
    D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34,
    D5 = 0x35, D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,
}

/// <summary>
/// Base class for all bot actions that will be executed by the input simulator.
/// </summary>
public abstract record BotAction
{
    /// <summary>Optional delay before executing this action.</summary>
    public TimeSpan PreDelay { get; init; } = TimeSpan.Zero;
}

/// <summary>Left-click at screen coordinates.</summary>
public sealed record ClickAction(int X, int Y) : BotAction;

/// <summary>Right-click at screen coordinates.</summary>
public sealed record RightClickAction(int X, int Y) : BotAction;

/// <summary>Double-click at screen coordinates.</summary>
public sealed record DoubleClickAction(int X, int Y) : BotAction;

/// <summary>Drag from one point to another.</summary>
public sealed record DragAction(int FromX, int FromY, int ToX, int ToY) : BotAction;

/// <summary>Press and release a key, optionally with modifiers.</summary>
public sealed record KeyPressAction(VirtualKey Key, VirtualKey[] Modifiers) : BotAction
{
    public KeyPressAction(VirtualKey key) : this(key, []) { }
}

/// <summary>Wait for a specified duration.</summary>
public sealed record WaitAction(TimeSpan Duration) : BotAction;

/// <summary>Type a text string character by character.</summary>
public sealed record TypeTextAction(string Text) : BotAction;

/// <summary>
/// A queue of bot actions to be executed by the execution engine.
/// </summary>
public sealed class ActionQueue
{
    private readonly Queue<BotAction> _queue = new();

    public int Count => _queue.Count;
    public bool IsEmpty => _queue.Count == 0;

    public void Enqueue(BotAction action) => _queue.Enqueue(action);

    public BotAction? Dequeue() => _queue.Count > 0 ? _queue.Dequeue() : null;

    public BotAction? Peek() => _queue.Count > 0 ? _queue.Peek() : null;

    public void Clear() => _queue.Clear();

    public IReadOnlyList<BotAction> ToList() => [.. _queue];
}
