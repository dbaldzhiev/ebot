using System.Text;

namespace EBot.Core.Execution;

/// <summary>
/// Input system diagnostics.
///
/// Runs a series of verification tests and returns a human-readable report:
///   • Screen metrics (width, height, DPI)
///   • Current cursor position
///   • SendInput absolute-move test — moves cursor to screen centre and reads
///     back position to verify the OS actually moved it (smoke-test for VM input
///     injection issues)
///   • EVE window state (handle, rect, client area, foreground status)
///   • Coordinate mapping summary (scale factor, window client offset)
///
/// Exposed via POST /api/debug/input in Program.cs.
/// </summary>
public static class InputDiagnostics
{
    /// <summary>
    /// Runs all diagnostic tests.  The SendInput move test temporarily moves the
    /// cursor to the primary-screen centre — do not call while the bot is actively
    /// clicking.
    /// </summary>
    /// <param name="eveWindowHandle">Pass the current EVE window handle, or 0 if unknown.</param>
    public static InputDiagReport Run(nint eveWindowHandle = 0)
    {
        var r = new InputDiagReport();

        // ── Screen metrics ────────────────────────────────────────────────
        r.ScreenWidth  = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        r.ScreenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        r.SystemDpi    = InputSimulator.GetSystemDpi();
        r.CoordScale   = InputSimulator.CoordinateScale;
        r.WindowOffsetX = InputSimulator.WindowClientOffsetX;
        r.WindowOffsetY = InputSimulator.WindowClientOffsetY;

        // ── Cursor before test ────────────────────────────────────────────
        NativeMethods.GetCursorPos(out var before);
        r.CursorBefore = (before.X, before.Y);

        // ── SendInput absolute-move test ──────────────────────────────────
        // Move to screen centre and verify the OS landed there.
        int testX = r.ScreenWidth  / 2;
        int testY = r.ScreenHeight / 2;
        r.TestTarget = (testX, testY);

        var moveInput = new NativeMethods.INPUT();
        moveInput.type        = NativeMethods.INPUT_MOUSE;
        moveInput.u.mi.dx     = r.ScreenWidth  > 1 ? (int)((long)testX * 65535 / (r.ScreenWidth  - 1)) : 0;
        moveInput.u.mi.dy     = r.ScreenHeight > 1 ? (int)((long)testY * 65535 / (r.ScreenHeight - 1)) : 0;
        moveInput.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE;

        r.SendInputEventsAccepted = NativeMethods.SendInput(1, [moveInput], NativeMethods.InputSize);

        // Give OS ~60 ms to process the event before reading back
        Thread.Sleep(60);
        NativeMethods.GetCursorPos(out var after);
        r.CursorAfter    = (after.X, after.Y);
        int ex = Math.Abs(after.X - testX);
        int ey = Math.Abs(after.Y - testY);
        r.MoveErrorPx    = (int)MathF.Round(MathF.Sqrt(ex * ex + ey * ey));
        r.MoveOk         = r.SendInputEventsAccepted > 0 && r.MoveErrorPx <= 5;

        // ── EVE window ────────────────────────────────────────────────────
        r.EveHandle    = eveWindowHandle;
        r.EveValid     = eveWindowHandle != 0 && NativeMethods.IsWindow(eveWindowHandle);
        r.EveForeground = eveWindowHandle != 0 &&
                          NativeMethods.GetForegroundWindow() == eveWindowHandle;

        if (r.EveValid)
        {
            NativeMethods.GetWindowRect(eveWindowHandle, out var wr);
            NativeMethods.GetClientRect(eveWindowHandle, out var cr);
            var cOrigin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(eveWindowHandle, ref cOrigin);
            r.EveWindowRect   = (wr.Left, wr.Top, wr.Right, wr.Bottom);
            r.EveClientSize   = (cr.Width, cr.Height);
            r.EveClientOrigin = (cOrigin.X, cOrigin.Y);
        }

        return r;
    }
}

/// <summary>Results of an <see cref="InputDiagnostics.Run"/> call.</summary>
public sealed class InputDiagReport
{
    // Screen
    public int   ScreenWidth  { get; set; }
    public int   ScreenHeight { get; set; }
    public uint  SystemDpi    { get; set; }
    public float CoordScale   { get; set; }
    public int   WindowOffsetX { get; set; }
    public int   WindowOffsetY { get; set; }

    // Cursor / SendInput test
    public (int X, int Y) CursorBefore { get; set; }
    public (int X, int Y) CursorAfter  { get; set; }
    public (int X, int Y) TestTarget   { get; set; }
    public uint SendInputEventsAccepted { get; set; }
    public int  MoveErrorPx  { get; set; }
    public bool MoveOk       { get; set; }

    // EVE window
    public nint  EveHandle    { get; set; }
    public bool  EveValid     { get; set; }
    public bool  EveForeground { get; set; }
    public (int L, int T, int R, int B) EveWindowRect   { get; set; }
    public (int W, int H)               EveClientSize   { get; set; }
    public (int X, int Y)               EveClientOrigin { get; set; }

    /// <summary>Builds a human-readable summary string.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();

        sb.AppendLine("── Screen ──────────────────────────────────────────");
        sb.AppendLine($"  Resolution : {ScreenWidth}×{ScreenHeight}");
        sb.AppendLine($"  System DPI : {SystemDpi}  ({SystemDpi * 100 / 96}%)");
        sb.AppendLine($"  CoordScale : {CoordScale:F3}");
        sb.AppendLine($"  WinOffset  : X={WindowOffsetX}  Y={WindowOffsetY}");

        sb.AppendLine();
        sb.AppendLine("── SendInput absolute-move test ────────────────────");
        sb.AppendLine($"  Target     : ({TestTarget.X},{TestTarget.Y})");
        sb.AppendLine($"  Events sent: {SendInputEventsAccepted}  (0 = blocked by UIPI/integrity level)");
        sb.AppendLine($"  Before     : ({CursorBefore.X},{CursorBefore.Y})");
        sb.AppendLine($"  After      : ({CursorAfter.X},{CursorAfter.Y})");
        sb.AppendLine($"  Error      : {MoveErrorPx} px   [{(MoveOk ? "PASS ✓" : "FAIL ✗")}]");
        if (!MoveOk)
        {
            sb.AppendLine();
            sb.AppendLine("  ⚠ SendInput move FAILED.  Possible causes:");
            if (SendInputEventsAccepted == 0)
                sb.AppendLine("    • UIPI blocked injection — EVE or this process runs at higher integrity level.");
            else
                sb.AppendLine("    • Cursor did not land at target.");
            sb.AppendLine("    • Verify DPI awareness is set and CoordScale is correct.");
            sb.AppendLine("    • If running under RDP, ensure 'Remote Desktop' mouse integration is enabled.");
        }

        sb.AppendLine();
        sb.AppendLine("── EVE window ──────────────────────────────────────");
        if (!EveValid)
        {
            sb.AppendLine($"  Handle     : 0x{EveHandle:X}  (NOT FOUND — no EVE client running?)");
        }
        else
        {
            sb.AppendLine($"  Handle     : 0x{EveHandle:X}");
            sb.AppendLine($"  Foreground : {(EveForeground ? "YES ✓" : "NO  ✗ (EVE does not own focus)")}");
            sb.AppendLine($"  Window rect: ({EveWindowRect.L},{EveWindowRect.T}) – ({EveWindowRect.R},{EveWindowRect.B})");
            sb.AppendLine($"  Client size: {EveClientSize.W}×{EveClientSize.H}");
            sb.AppendLine($"  Client orig: ({EveClientOrigin.X},{EveClientOrigin.Y})");

            bool fullscreen = EveClientOrigin.X == 0 && EveClientOrigin.Y == 0 &&
                              EveClientSize.W == ScreenWidth && EveClientSize.H == ScreenHeight;
            sb.AppendLine($"  Mode       : {(fullscreen ? "FULLSCREEN" : "WINDOWED")}");

            if (!fullscreen && (WindowOffsetX != EveClientOrigin.X || WindowOffsetY != EveClientOrigin.Y))
            {
                sb.AppendLine($"  ⚠ WindowOffset ({WindowOffsetX},{WindowOffsetY}) doesn't match " +
                              $"client origin ({EveClientOrigin.X},{EveClientOrigin.Y}) — " +
                              "start a bot tick to refresh it.");
            }
        }

        return sb.ToString();
    }
}
