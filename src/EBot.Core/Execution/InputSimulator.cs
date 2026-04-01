using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace EBot.Core.Execution;

/// <summary>
/// Simulates mouse and keyboard input using Win32 SendInput API.
/// Includes humanization (randomized delays, coordinate jitter).
/// </summary>
public sealed class InputSimulator
{
    private readonly ILogger<InputSimulator> _logger;
    private readonly Random _rng = new();

    /// <summary>Minimum delay between actions in milliseconds.</summary>
    public int MinDelayMs { get; set; } = 50;

    /// <summary>Maximum delay between actions in milliseconds.</summary>
    public int MaxDelayMs { get; set; } = 200;

    /// <summary>Maximum random pixel offset for click coordinates (humanization).</summary>
    public int CoordinateJitter { get; set; } = 3;

    public InputSimulator(ILogger<InputSimulator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sets Per-Monitor V2 DPI awareness so that SetCursorPos uses physical pixels,
    /// matching EVE Online's _displayX/_displayY coordinate space.
    /// Call once at application startup before any Win32 input calls.
    /// </summary>
    public static void SetDpiAwareness()
    {
        if (OperatingSystem.IsWindows())
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CTX_PER_MONITOR_V2);
    }

    /// <summary>
    /// Returns the system DPI (96 = 100 %, 144 = 150 %, 192 = 200 %, etc.).
    /// Returns 96 on non-Windows or if the call fails.
    /// </summary>
    public static uint GetSystemDpi() =>
        OperatingSystem.IsWindows() ? NativeMethods.GetDpiForSystem() : 96u;

    /// <summary>
    /// Manual coordinate scale factor applied to every click / move.
    /// Default 1.0 = no scaling (correct when DPI awareness is set to PER_MONITOR_V2).
    /// Set to a value other than 1.0 as a fallback if clicks are still offset.
    /// For example: 0.667f corrects a 150 % DPI mismatch when DPI-awareness could not be set.
    /// </summary>
    public static float CoordinateScale { get; set; } = 1.0f;

    /// <summary>
    /// Screen X coordinate of the EVE window's client-area top-left corner.
    /// EVE UI coordinates are client-relative (0,0 = top-left of the EVE viewport).
    /// When EVE is windowed this offset translates them to physical screen coordinates.
    /// Updated each tick by ActionExecutor via ClientToScreen.
    /// Defaults to 0 (correct for fullscreen or when no window handle is available).
    /// </summary>
    public static int WindowClientOffsetX { get; set; }

    /// <summary>Screen Y coordinate of the EVE window's client-area top-left corner.</summary>
    public static int WindowClientOffsetY { get; set; }

    // ─── Public API ────────────────────────────────────────────────────

    public async Task MoveTo(int x, int y, CancellationToken ct = default)
    {
        var (jx, jy) = ApplyJitter(x, y);
        NativeMethods.SetCursorPos(jx, jy);
        _logger.LogTrace("Mouse moved to ({X}, {Y})", jx, jy);
        await HumanDelay(ct);
    }

    public async Task Click(int x, int y, CancellationToken ct = default)
    {
        await MoveTo(x, y, ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(_rng.Next(30, 80), ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_LEFTUP);
        _logger.LogDebug("Left click at ({X}, {Y})", x, y);
        await HumanDelay(ct);
    }

    public async Task RightClick(int x, int y, CancellationToken ct = default)
    {
        await MoveTo(x, y, ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        await Task.Delay(_rng.Next(30, 80), ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_RIGHTUP);
        _logger.LogDebug("Right click at ({X}, {Y})", x, y);
        await HumanDelay(ct);
    }

    public async Task DoubleClick(int x, int y, CancellationToken ct = default)
    {
        await Click(x, y, ct);
        await Task.Delay(_rng.Next(50, 120), ct);
        await Click(x, y, ct);
        _logger.LogDebug("Double click at ({X}, {Y})", x, y);
    }

    public async Task Drag(int fromX, int fromY, int toX, int toY, CancellationToken ct = default)
    {
        await MoveTo(fromX, fromY, ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(_rng.Next(100, 200), ct);
        await MoveTo(toX, toY, ct);
        await Task.Delay(_rng.Next(50, 100), ct);
        SendMouseInput(NativeMethods.MOUSEEVENTF_LEFTUP);
        _logger.LogDebug("Drag from ({FX}, {FY}) to ({TX}, {TY})", fromX, fromY, toX, toY);
        await HumanDelay(ct);
    }

    public async Task KeyPress(VirtualKey key, VirtualKey[]? modifiers = null, CancellationToken ct = default)
    {
        modifiers ??= [];

        // Press modifiers
        foreach (var mod in modifiers)
            SendKeyInput((ushort)mod, isKeyUp: false);

        // Press and release key
        SendKeyInput((ushort)key, isKeyUp: false);
        await Task.Delay(_rng.Next(30, 80), ct);
        SendKeyInput((ushort)key, isKeyUp: true);

        // Release modifiers (reverse order)
        foreach (var mod in modifiers.Reverse())
            SendKeyInput((ushort)mod, isKeyUp: true);

        _logger.LogDebug("Key press: {Key} (modifiers: {Mods})", key,
            modifiers.Length > 0 ? string.Join("+", modifiers) : "none");
        await HumanDelay(ct);
    }

    public async Task TypeText(string text, CancellationToken ct = default)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();

            var inputs = new NativeMethods.INPUT[2];

            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].ki.wScan = ch;
            inputs[0].ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].ki.wScan = ch;
            inputs[1].ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;

            NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            await Task.Delay(_rng.Next(30, 100), ct);
        }

        _logger.LogDebug("Typed text: \"{Text}\"", text);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private (int X, int Y) ApplyJitter(int x, int y)
    {
        // 1. Scale EVE client-area coordinates (DPI correction)
        if (CoordinateScale != 1.0f)
        {
            x = (int)MathF.Round(x * CoordinateScale);
            y = (int)MathF.Round(y * CoordinateScale);
        }

        // 2. Translate client-area → screen coordinates (windowed mode border offset)
        x += WindowClientOffsetX;
        y += WindowClientOffsetY;

        // 3. Humanization jitter
        if (CoordinateJitter <= 0) return (x, y);
        return (
            x + _rng.Next(-CoordinateJitter, CoordinateJitter + 1),
            y + _rng.Next(-CoordinateJitter, CoordinateJitter + 1)
        );
    }

    private async Task HumanDelay(CancellationToken ct)
    {
        var delay = _rng.Next(MinDelayMs, MaxDelayMs + 1);
        await Task.Delay(delay, ct);
    }

    private static void SendMouseInput(uint flags)
    {
        var inputs = new NativeMethods.INPUT[1];
        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].ki.dwFlags = flags;
        NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendKeyInput(ushort vkCode, bool isKeyUp)
    {
        var inputs = new NativeMethods.INPUT[1];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].ki.wVk = vkCode;
        inputs[0].ki.dwFlags = isKeyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
        NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Immediately releases all held mouse buttons and common modifier keys (Shift, Ctrl, Alt).
    /// Call after an emergency stop to prevent stuck input.
    /// </summary>
    public static void ReleaseAllInput()
    {
        if (!OperatingSystem.IsWindows()) return;

        Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[5];

        // Release mouse buttons
        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].ki.dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP;
        inputs[1].type = NativeMethods.INPUT_MOUSE;
        inputs[1].ki.dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP;

        // Release modifier keys
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].ki.wVk = 0x10; // VK_SHIFT
        inputs[2].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].ki.wVk = 0x11; // VK_CONTROL
        inputs[3].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
        inputs[4].type = NativeMethods.INPUT_KEYBOARD;
        inputs[4].ki.wVk = 0x12; // VK_MENU (Alt)
        inputs[4].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        NativeMethods.SendInput(5, [inputs[0], inputs[1], inputs[2], inputs[3], inputs[4]],
            Marshal.SizeOf<NativeMethods.INPUT>());
    }
}

/// <summary>
/// Win32 native methods for input simulation and window management.
/// </summary>
internal static partial class NativeMethods
{
    // ─── SendInput ─────────────────────────────────────────────────────

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEKEYBDHARDWAREINPUT ki;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct MOUSEKEYBDHARDWAREINPUT
    {
        // Mouse
        [FieldOffset(0)] public int dx;
        [FieldOffset(4)] public int dy;
        [FieldOffset(8)] public uint mouseData;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(20)] public nint dwExtraInfo;

        // Keyboard (overlaps with mouse via Explicit layout — use mi/ki union)
        [FieldOffset(0)] public ushort wVk;
        [FieldOffset(2)] public ushort wScan;
    }

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint nInputs,
        [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    // ─── Window Management ─────────────────────────────────────────────

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    /// <summary>
    /// Returns the client-area dimensions (top-left is always 0,0; Right/Bottom give size).
    /// Use ClientToScreen to convert client (0,0) to screen coordinates.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    /// <summary>
    /// Converts a point from client-area coordinates to screen coordinates.
    /// Pass (0,0) to get the screen position of the top-left of the client area.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    /// <summary>
    /// Sets the process DPI awareness to Per-Monitor V2 so that SetCursorPos
    /// and coordinate maths operate in physical screen pixels — matching the
    /// coordinate space that EVE Online stores in its UI tree (_displayX/_displayY).
    /// Must be called before any DPI-sensitive Win32 calls.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4)</summary>
    public const nint DPI_AWARENESS_CTX_PER_MONITOR_V2 = -4;

    /// <summary>Returns the system DPI value (96 = 100 %, 144 = 150 %, 192 = 200 %).</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }
}
