using System.Runtime.InteropServices;

namespace EBot.WebHost;

/// <summary>
/// Background service that registers the Pause/Break key as a global system hotkey.
/// When pressed (even if EVE or the bot has keyboard focus), triggers EmergencyStopAsync
/// on the orchestrator — stopping the bot and releasing all held mouse/keyboard input.
/// </summary>
public sealed class GlobalHotKeyService(
    BotOrchestrator orchestrator,
    ILogger<GlobalHotKeyService> logger) : BackgroundService
{
    private const int WM_HOTKEY    = 0x0312;
    private const uint VK_PAUSE    = 0x13;   // Pause/Break key
    private const uint MOD_NONE    = 0x0000;
    private const int HOTKEY_ID    = 9901;   // Arbitrary ID unique to this app
    private const uint PM_REMOVE   = 0x0001;

    private volatile uint _messageThreadId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("GlobalHotKeyService: Windows only — skipping hotkey registration");
            return;
        }

        var readyTcs = new TaskCompletionSource();

        var thread = new Thread(() =>
        {
            _messageThreadId = HkNative.GetCurrentThreadId();

            bool registered = HkNative.RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_NONE, VK_PAUSE);
            if (!registered)
                logger.LogWarning("Could not register Pause/Break hotkey — already claimed by another app. " +
                                  "Use the web UI Nuke button as fallback.");
            else
                logger.LogInformation("Emergency stop hotkey active: Pause/Break");

            readyTcs.SetResult();

            // Pump messages; RegisterHotKey delivers WM_HOTKEY to this thread's queue.
            while (!stoppingToken.IsCancellationRequested)
            {
                if (HkNative.PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
                    {
                        logger.LogWarning("Pause/Break pressed — triggering emergency stop");
                        _ = Task.Run(() => orchestrator.EmergencyStopAsync(), CancellationToken.None);
                    }
                    HkNative.TranslateMessage(ref msg);
                    HkNative.DispatchMessage(ref msg);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            HkNative.UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Name = "EBot-HotKey";
        thread.Start();

        await readyTcs.Task;

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Win32 P/Invokes for the hotkey message loop.</summary>
internal static partial class HkNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(
        out MSG lpMsg, nint hWnd,
        uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(ref MSG lpMsg);
}
