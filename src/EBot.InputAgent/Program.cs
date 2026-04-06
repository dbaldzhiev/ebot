// EBot Input Agent
// ──────────────────────���──────────────────────────────────────────────────────
// PURPOSE
//   When EBot is started via SSH, the process lives in a non-interactive Windows
//   session that has no access to the interactive desktop.  Win32 SendInput,
//   GetSystemMetrics, and FindWindow all return zero / nothing in that context.
//
//   This agent solves the problem:
//     1. Start EBot.InputAgent ONCE from your RDP terminal (or physical console).
//     2. It listens on a named pipe: \\.\pipe\ebot-input
//     3. EBot (SSH session) connects to the pipe and sends mouse/keyboard
//        commands as text lines.
//     4. The agent executes SendInput here, in the interactive session, where
//        it actually works.
//
// USAGE
//   From your RDP terminal:
//     dotnet run --project src/EBot.InputAgent   (dev)
//     EBot.InputAgent.exe                        (published)
//
//   Leave it running in the background.  EBot will auto-detect the pipe and
//   route all input through it.  The pipe name is fixed: ebot-input.
//
// PROTOCOL  (newline-delimited text, one request → one response)
//   Request:   PING
//   Response:  OK
//
//   Request:   SCREENINFO
//   Response:  OK <width> <height>
//
//   Request:   MOVE <screenX> <screenY>
//   Response:  OK
//
//   Request:   LDOWN <screenX> <screenY>
//   Response:  OK
//
//   Request:   LUP
//   Response:  OK
//
//   Request:   RDOWN <screenX> <screenY>
//   Response:  OK
//
//   Request:   RUP
//   Response:  OK
//
//   Request:   KEYDOWN <vkCode>
//   Response:  OK
//
//   Request:   KEYUP <vkCode>
//   Response:  OK
//
//   Request:   TYPE <unicodeCodepoint>
//   Response:  OK
//
//   On error:  ERR <message>
// ─────────────────────────────────────────────────────────────────────────────

using System.IO.Pipes;
using System.Runtime.InteropServices;

const string PipeName = "ebot-input";

Console.Title = "EBot Input Agent";

// Verify we are in an interactive session with a real display
int screenW = NativeMethods.GetSystemMetrics(0); // SM_CXSCREEN
int screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║          EBot Input Agent                        ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine($"Pipe    : \\\\.\\pipe\\{PipeName}");
Console.WriteLine($"Screen  : {screenW}×{screenH}");

if (screenW == 0 || screenH == 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine();
    Console.WriteLine("ERROR: Screen metrics returned 0.");
    Console.WriteLine("This process is NOT running in an interactive desktop session.");
    Console.WriteLine("Start it from your RDP terminal, not SSH.");
    Console.ResetColor();
    Console.ReadKey();
    return 1;
}

Console.WriteLine($"Status  : interactive session OK — ready");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Accept clients in a loop; each client gets its own handler task
while (!cts.Token.IsCancellationRequested)
{
    try
    {
        // Create a new server stream for each incoming connection.
        // MaxAllowedServerInstances = 10 allows queuing multiple EBot restarts
        // without needing to restart the agent.
        var server = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            10,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(cts.Token);
        Log("Client connected");
        _ = HandleClientAsync(server, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex) { Log($"Accept error: {ex.Message}"); await Task.Delay(1000); }
}

Log("Agent stopped.");
return 0;

// ─── Client handler ────────────────────────────────────────────────��──────────

static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
{
    using var _ = pipe;
    using var reader = new StreamReader(pipe, leaveOpen: true);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

    try
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            var response = Execute(line);
            await writer.WriteLineAsync(response);
        }
    }
    catch (Exception)
    {
        // Client disconnected mid-command — not an error
    }

    Log("Client disconnected");
}

// ─── Command dispatcher ──────────────────────────────────────────────────────���

static string Execute(string cmd)
{
    try
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "OK";

        switch (parts[0].ToUpperInvariant())
        {
            // ── Diagnostics ─────────────────────────────────────────────────
            case "PING":
                return "OK";

            case "SCREENINFO":
            {
                int w = NativeMethods.GetSystemMetrics(0);
                int h = NativeMethods.GetSystemMetrics(1);
                return $"OK {w} {h}";
            }

            // ── Mouse movement ───────────────────────────────────────────────
            case "MOVE" when parts.Length >= 3:
            {
                int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
                SendAbsoluteMove(x, y);
                return "OK";
            }

            // ── Mouse buttons (position encoded in DOWN commands) ────────────
            case "LDOWN" when parts.Length >= 3:
            {
                int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
                SendAbsoluteMove(x, y);
                SendButton(NativeMethods.MOUSEEVENTF_LEFTDOWN);
                return "OK";
            }

            case "LUP":
                SendButton(NativeMethods.MOUSEEVENTF_LEFTUP);
                return "OK";

            case "RDOWN" when parts.Length >= 3:
            {
                int x = int.Parse(parts[1]), y = int.Parse(parts[2]);
                SendAbsoluteMove(x, y);
                SendButton(NativeMethods.MOUSEEVENTF_RIGHTDOWN);
                return "OK";
            }

            case "RUP":
                SendButton(NativeMethods.MOUSEEVENTF_RIGHTUP);
                return "OK";

            // ── Keyboard ─────────────────────────────────────────────────────
            case "KEYDOWN" when parts.Length >= 2:
                SendKey(ushort.Parse(parts[1]), keyUp: false);
                return "OK";

            case "KEYUP" when parts.Length >= 2:
                SendKey(ushort.Parse(parts[1]), keyUp: true);
                return "OK";

            case "TYPE" when parts.Length >= 2:
            {
                char ch = (char)int.Parse(parts[1]);
                SendUnicode(ch, keyUp: false);
                SendUnicode(ch, keyUp: true);
                return "OK";
            }

            default:
                return $"ERR Unknown: {parts[0]}";
        }
    }
    catch (Exception ex)
    {
        return $"ERR {ex.Message}";
    }
}

// ─── SendInput helpers ────────────────────────────────────────────────────────

static void SendAbsoluteMove(int screenX, int screenY)
{
    int sw = NativeMethods.GetSystemMetrics(0);
    int sh = NativeMethods.GetSystemMetrics(1);
    int normX = sw > 1 ? (int)((long)screenX * 65535 / (sw - 1)) : 0;
    int normY = sh > 1 ? (int)((long)screenY * 65535 / (sh - 1)) : 0;

    var input = default(NativeMethods.INPUT);
    input.type        = NativeMethods.INPUT_MOUSE;
    input.u.mi.dx     = normX;
    input.u.mi.dy     = normY;
    input.u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE;
    NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
}

static void SendButton(uint flags)
{
    var input = default(NativeMethods.INPUT);
    input.type        = NativeMethods.INPUT_MOUSE;
    input.u.mi.dwFlags = flags;
    NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
}

static void SendKey(ushort vk, bool keyUp)
{
    var input = default(NativeMethods.INPUT);
    input.type        = NativeMethods.INPUT_KEYBOARD;
    input.u.ki.wVk    = vk;
    input.u.ki.dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
    NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
}

static void SendUnicode(char ch, bool keyUp)
{
    var input = default(NativeMethods.INPUT);
    input.type        = NativeMethods.INPUT_KEYBOARD;
    input.u.ki.wScan  = ch;
    input.u.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u);
    NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
}

static void Log(string msg) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

// ─── Win32 declarations ───────────────────────────────────────────────────────

internal static partial class NativeMethods
{
    public const uint INPUT_MOUSE    = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE      = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    public const uint MOUSEEVENTF_ABSOLUTE  = 0x8000;
    public const uint KEYEVENTF_KEYUP       = 0x0002;
    public const uint KEYEVENTF_UNICODE     = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT  { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT  { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT       { public uint type; public INPUT_UNION u; }

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint n, [MarshalAs(UnmanagedType.LPArray)] INPUT[] p, int cb);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int n);
}
