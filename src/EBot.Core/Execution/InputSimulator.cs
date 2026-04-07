using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace EBot.Core.Execution;

/// <summary>
/// Simulates mouse and keyboard input using Win32 SendInput with
/// MOUSEEVENTF_ABSOLUTE coordinates.
///
/// Key design decisions for VM / fullscreen-game compatibility:
///
///   1. Mouse moves use SendInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE) instead
///      of SetCursorPos.  SetCursorPos only repositions the visible cursor; it does
///      not inject a MOUSEMOVE event into the hardware input stream that DirectInput
///      and Raw Input consumers (like EVE Online) rely on.  MOUSEEVENTF_ABSOLUTE
///      injects at the OS input level and works correctly for full-screen DX games.
///
///   2. Humanized movement: optional Bézier-curved paths with per-step delays and
///      micro-tremors to mimic natural hand motion.
///
///   3. PostMessage is NOT used — it doesn't reach Direct-Input or Raw Input
///      consumers and is therefore ineffective for full-screen EVE.
/// </summary>
public sealed class InputSimulator
{
    private readonly ILogger<InputSimulator> _logger;
    private readonly Random _rng = new();


    // ─── Timing parameters (set by BotSettings) ────────────────────────────
    public int MinDelayMs { get; set; } = 50;
    public int MaxDelayMs { get; set; } = 200;
    public int ClickHoldMinMs { get; set; } = 40;
    public int ClickHoldMaxMs { get; set; } = 90;

    // ─── Human-movement parameters ─────────────────────────────────────────
    /// <summary>When true (default), clicks travel along a Bézier-curved path.</summary>
    public bool UseHumanMovement { get; set; } = true;

    /// <summary>Arc strength: 0 = straight line, 0.25 = gentle curve (default).</summary>
    public float BezierCurveStrength { get; set; } = 0.25f;

    /// <summary>Delay between each waypoint step (ms).</summary>
    public int StepDelayMinMs { get; set; } = 6;
    public int StepDelayMaxMs { get; set; } = 14;

    /// <summary>Tiny random offset applied to intermediate waypoints (pixels).</summary>
    public bool MicroTremors { get; set; } = true;

    // ─── Coordinate settings (static — shared across all instances) ────────

    /// <summary>Maximum random jitter applied to the final click target (±pixels).</summary>
    public int CoordinateJitter { get; set; } = 3;

    /// <summary>
    /// Manual coordinate scale factor applied to EVE client-area coords before
    /// translating to screen coords.  1.0 = no scaling (correct with PER_MONITOR_V2).
    /// </summary>
    public static float CoordinateScale { get; set; } = 1.0f;

    /// <summary>
    /// Screen X of the EVE window's client-area top-left.
    /// 0 for fullscreen; non-zero for windowed mode.
    /// Updated each tick by ActionExecutor via ClientToScreen.
    /// </summary>
    public static int WindowClientOffsetX { get; set; }

    /// <summary>Screen Y of the EVE window's client-area top-left.</summary>
    public static int WindowClientOffsetY { get; set; }

    // ─── Screen metrics (cached, refreshed on demand) ──────────────────────
    private static int _screenW, _screenH;

    private static void EnsureScreenMetrics()
    {
        if (_screenW <= 0) _screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        if (_screenH <= 0) _screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
    }

    /// <summary>Forces a re-read of primary-screen dimensions on the next operation.</summary>
    public static void InvalidateScreenMetrics() => _screenW = _screenH = 0;

    // ──────────────────────────────────────────────────────────────────────

    public InputSimulator(ILogger<InputSimulator> logger)
    {
        _logger = logger;
        EnsureScreenMetrics();
    }

    /// <summary>
    /// Sets Per-Monitor V2 DPI awareness so that coordinate maths uses physical
    /// pixels matching EVE's _displayX/_displayY coordinate space.
    /// Call once at startup, before any Win32 calls.
    /// </summary>
    public static void SetDpiAwareness()
    {
        if (OperatingSystem.IsWindows())
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CTX_PER_MONITOR_V2);
    }

    /// <summary>Returns the system DPI (96 = 100 %, 144 = 150 %, 192 = 200 %).</summary>
    public static uint GetSystemDpi() =>
        OperatingSystem.IsWindows() ? NativeMethods.GetDpiForSystem() : 96u;

    // ─── Public Mouse API ──────────────────────────────────────────────────

    /// <summary>
    /// Moves the cursor to <paramref name="screenX"/>, <paramref name="screenY"/>
    /// (physical screen coordinates) via a single SendInput absolute move.
    /// </summary>
    public async Task MoveTo(int screenX, int screenY, CancellationToken ct = default)
    {
        SendAbsoluteMove(screenX, screenY);
        _logger.LogTrace("MoveTo screen ({X},{Y})", screenX, screenY);
        await HumanDelay(ct);
    }

    /// <summary>
    /// Moves the cursor to EVE client-area coordinates (same coordinate space as Click/RightClick).
    /// Translates to screen coords, applies jitter, and moves via humanized Bézier arc.
    /// Use this for hover-triggered submenus (context menu entries with ▶).
    /// </summary>
    public async Task MoveToClient(int clientX, int clientY, CancellationToken ct = default)
    {
        var (sx, sy) = ClientToScreen(clientX, clientY);
        var (jx, jy) = ApplyJitter(sx, sy);
        await MoveToSmooth(jx, jy, ct);
    }

    /// <summary>
    /// Moves to target screen coordinates along a humanized Bézier arc.
    /// Falls back to a single move when human movement is disabled or distance is tiny.
    /// </summary>
    public async Task MoveToSmooth(int toScreenX, int toScreenY, CancellationToken ct = default)
    {
        if (!UseHumanMovement)
        {
            await MoveTo(toScreenX, toScreenY, ct);
            return;
        }

        NativeMethods.GetCursorPos(out var cur);
        float dist = MousePath.Distance(cur.X, cur.Y, toScreenX, toScreenY);

        // Very short hops: just warp directly — a human barely moves for <8 px
        if (dist < 8f)
        {
            SendAbsoluteMove(toScreenX, toScreenY);
            return;
        }

        int steps = MousePath.StepsForDistance(dist);
        var waypoints = MousePath.Generate(
            cur.X, cur.Y, toScreenX, toScreenY,
            steps, BezierCurveStrength, _rng, MicroTremors);

        foreach (var (wx, wy) in waypoints)
        {
            ct.ThrowIfCancellationRequested();
            SendAbsoluteMove(wx, wy);
            await Task.Delay(_rng.Next(StepDelayMinMs, StepDelayMaxMs + 1), ct);
        }
    }

    /// <summary>
    /// Left-click at EVE client-area coordinates.
    /// Translates to screen coords, applies jitter, moves (with bezier arc), clicks.
    /// </summary>
    public async Task Click(int clientX, int clientY, CancellationToken ct = default)
    {
        var (sx, sy) = ClientToScreen(clientX, clientY);
        var (jx, jy) = ApplyJitter(sx, sy);

        await MoveToSmooth(jx, jy, ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(_rng.Next(ClickHoldMinMs, ClickHoldMaxMs + 1), ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_LEFTUP);
        _logger.LogDebug("Click  client({CX},{CY}) → screen({SX},{SY})", clientX, clientY, jx, jy);
        await HumanDelay(ct);
    }

    /// <summary>Right-click at EVE client-area coordinates.</summary>
    public async Task RightClick(int clientX, int clientY, CancellationToken ct = default)
    {
        var (sx, sy) = ClientToScreen(clientX, clientY);
        var (jx, jy) = ApplyJitter(sx, sy);

        await MoveToSmooth(jx, jy, ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        await Task.Delay(_rng.Next(ClickHoldMinMs, ClickHoldMaxMs + 1), ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_RIGHTUP);
        _logger.LogDebug("RClick client({CX},{CY}) → screen({SX},{SY})", clientX, clientY, jx, jy);
        await HumanDelay(ct);
    }

    /// <summary>Double left-click at EVE client-area coordinates.</summary>
    public async Task DoubleClick(int clientX, int clientY, CancellationToken ct = default)
    {
        await Click(clientX, clientY, ct);
        await Task.Delay(_rng.Next(50, 120), ct);
        await Click(clientX, clientY, ct);
        _logger.LogDebug("DblClick client({X},{Y})", clientX, clientY);
    }

    /// <summary>Click-drag from one EVE client-area position to another.</summary>
    public async Task Drag(int fromX, int fromY, int toX, int toY, CancellationToken ct = default)
    {
        var (fsx, fsy) = ClientToScreen(fromX, fromY);
        var (tsx, tsy) = ClientToScreen(toX, toY);

        await MoveToSmooth(fsx, fsy, ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(_rng.Next(100, 200), ct);
        await MoveToSmooth(tsx, tsy, ct);
        await Task.Delay(_rng.Next(50, 100), ct);
        SendMouseButton(NativeMethods.MOUSEEVENTF_LEFTUP);
        _logger.LogDebug("Drag ({Fx},{Fy})→({Tx},{Ty})", fromX, fromY, toX, toY);
        await HumanDelay(ct);
    }

    // ─── Public Keyboard API ───────────────────────────────────────────────

    /// <summary>Presses and releases a virtual key, with optional modifier keys.</summary>
    public async Task KeyPress(VirtualKey key, VirtualKey[]? modifiers = null, CancellationToken ct = default)
    {
        modifiers ??= [];

        foreach (var mod in modifiers) SendKey((ushort)mod, keyUp: false);
        SendKey((ushort)key, keyUp: false);
        await Task.Delay(_rng.Next(30, 80), ct);
        SendKey((ushort)key, keyUp: true);
        foreach (var mod in modifiers.Reverse()) SendKey((ushort)mod, keyUp: true);

        _logger.LogDebug("KeyPress {Key}{Mods}", key,
            modifiers.Length > 0 ? " + " + string.Join("+", modifiers) : "");
        await HumanDelay(ct);
    }

    /// <summary>Types a string one character at a time using Unicode scan codes.</summary>
    public async Task TypeText(string text, CancellationToken ct = default)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            var inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wScan   = ch;
            inputs[0].u.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wScan   = ch;
            inputs[1].u.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
            NativeMethods.SendInput(2, inputs, NativeMethods.InputSize);
            await Task.Delay(_rng.Next(30, 100), ct);
        }
        _logger.LogDebug("TypeText \"{Text}\"", text);
    }

    // ─── Emergency release ────────────────────────────────────────────────

    /// <summary>
    /// Immediately releases all held mouse buttons and common modifier keys.
    /// Call after an emergency stop to prevent stuck input.
    /// </summary>
    public static void ReleaseAllInput()
    {
        if (!OperatingSystem.IsWindows()) return;

        var inputs = new NativeMethods.INPUT[5];

        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP;

        inputs[1].type = NativeMethods.INPUT_MOUSE;
        inputs[1].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP;

        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].u.ki.wVk    = 0x10; // VK_SHIFT
        inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].u.ki.wVk    = 0x11; // VK_CONTROL
        inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        inputs[4].type = NativeMethods.INPUT_KEYBOARD;
        inputs[4].u.ki.wVk    = 0x12; // VK_MENU (Alt)
        inputs[4].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        NativeMethods.SendInput(5, inputs, NativeMethods.InputSize);
    }

    // ─── Low-level helpers ────────────────────────────────────────────────

    /// <summary>
    /// Sends a single absolute mouse-move event via SendInput.
    /// MOUSEEVENTF_ABSOLUTE coordinates are normalised 0–65535 across the primary screen.
    /// This injects into the hardware input stream (unlike SetCursorPos) so DirectInput
    /// and Raw Input consumers receive the event.
    /// </summary>
    internal static void SendAbsoluteMove(int screenX, int screenY)
    {
        EnsureScreenMetrics();
        int normX = _screenW > 1 ? (int)((long)screenX * 65535 / (_screenW - 1)) : 0;
        int normY = _screenH > 1 ? (int)((long)screenY * 65535 / (_screenH - 1)) : 0;

        var input = new NativeMethods.INPUT();
        input.type        = NativeMethods.INPUT_MOUSE;
        input.u.mi.dx     = normX;
        input.u.mi.dy     = normY;
        input.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE;
        NativeMethods.SendInput(1, [input], NativeMethods.InputSize);
    }

    private static void SendMouseButton(uint flags)
    {
        var input = new NativeMethods.INPUT();
        input.type        = NativeMethods.INPUT_MOUSE;
        input.u.mi.dwFlags = flags;
        NativeMethods.SendInput(1, [input], NativeMethods.InputSize);
    }

    private static void SendKey(ushort vkCode, bool keyUp)
    {
        var input = new NativeMethods.INPUT();
        input.type        = NativeMethods.INPUT_KEYBOARD;
        input.u.ki.wVk    = vkCode;
        input.u.ki.dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
        NativeMethods.SendInput(1, [input], NativeMethods.InputSize);
    }

    private (int X, int Y) ClientToScreen(int clientX, int clientY)
    {
        float sx = clientX, sy = clientY;
        if (CoordinateScale != 1.0f)
        {
            sx = MathF.Round(sx * CoordinateScale);
            sy = MathF.Round(sy * CoordinateScale);
        }
        return ((int)sx + WindowClientOffsetX, (int)sy + WindowClientOffsetY);
    }

    private (int X, int Y) ApplyJitter(int x, int y)
    {
        if (CoordinateJitter <= 0) return (x, y);
        return (
            x + _rng.Next(-CoordinateJitter, CoordinateJitter + 1),
            y + _rng.Next(-CoordinateJitter, CoordinateJitter + 1)
        );
    }

    private async Task HumanDelay(CancellationToken ct) =>
        await Task.Delay(_rng.Next(MinDelayMs, MaxDelayMs + 1), ct);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Win32 P/Invoke declarations
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class NativeMethods
{
    // ─── INPUT type constants ──────────────────────────────────────────────
    public const uint INPUT_MOUSE    = 0;
    public const uint INPUT_KEYBOARD = 1;

    // ─── MOUSEEVENTF flags ────────────────────────────────────────────────
    public const uint MOUSEEVENTF_MOVE      = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    public const uint MOUSEEVENTF_ABSOLUTE  = 0x8000;

    // ─── KEYEVENTF flags ──────────────────────────────────────────────────
    public const uint KEYEVENTF_KEYUP   = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    // ─── GetSystemMetrics indices ─────────────────────────────────────────
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    // ─── INPUT structs ────────────────────────────────────────────────────
    //  Using properly typed nested structs avoids the offset-12 keyboard-dwFlags
    //  bug present in the old flat-union layout.

    /// <summary>Mouse input data passed to SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int  dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    /// <summary>Keyboard input data passed to SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint   dwFlags, time;
        public nint   dwExtraInfo;
    }

    /// <summary>Union of MOUSEINPUT and KEYBDINPUT (both start at offset 0).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT  mi;
        [FieldOffset(0)] public KEYBDINPUT  ki;
    }

    /// <summary>Top-level INPUT structure sent to SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint      type;
        public INPUT_UNION u;
    }

    /// <summary>Cached size of INPUT in bytes (required by SendInput's cbSize parameter).</summary>
    public static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // ─── SendInput ────────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    public static partial uint SendInput(
        uint nInputs,
        [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs,
        int cbSize);

    // ─── Cursor position ──────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    // ─── Screen metrics ───────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    // ─── Window management ────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    // ─── Thread input attachment (robust SetForegroundWindow in VMs) ──────
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    // ─── DPI awareness ────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4)</summary>
    public const nint DPI_AWARENESS_CTX_PER_MONITOR_V2 = -4;

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    // ─── Structs ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width  => Right  - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
}
