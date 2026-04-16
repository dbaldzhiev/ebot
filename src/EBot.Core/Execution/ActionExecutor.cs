using Microsoft.Extensions.Logging;

namespace EBot.Core.Execution;

/// <summary>
/// Processes the action queue sequentially, executing each action via the InputSimulator.
/// Applies humanization and ensures the EVE window is focused.
/// </summary>
public sealed class ActionExecutor
{
    private readonly InputSimulator _input;
    private readonly ILogger<ActionExecutor> _logger;

    /// <summary>
    /// Fired immediately before each action is executed.
    /// Payload is a human-readable description: "Click (x, y)", "KeyPress: S + Shift", etc.
    /// Subscribe in BotOrchestrator to broadcast to the web UI action log.
    /// </summary>
    public event Action<string>? ActionPerformed;

    public ActionExecutor(InputSimulator input, ILogger<ActionExecutor> logger)
    {
        _input = input;
        _logger = logger;
    }

    /// <summary>
    /// Executes all actions in the queue sequentially.
    /// Computes the EVE window client-area screen origin each call so windowed-mode
    /// border offsets are applied to every coordinate.
    /// </summary>
    public async Task ExecuteAllAsync(ActionQueue queue, nint windowHandle, CancellationToken ct = default)
    {
        // Keep the window in the foreground so keyboard SendInput reaches it.
        // Uses AttachThreadInput for reliability in VM guest environments.
        EnsureWindowFocus(windowHandle);
        UpdateWindowClientOffset(windowHandle);

        var actionCount = 0;
        while (!queue.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();

            var action = queue.Dequeue();
            if (action == null) break;

            // Pre-delay if specified
            if (action.PreDelay > TimeSpan.Zero)
                await Task.Delay(action.PreDelay, ct);

            await ExecuteAction(action, ct);
            actionCount++;
        }

        if (actionCount > 0)
            _logger.LogDebug("Executed {Count} actions", actionCount);
    }

    private async Task ExecuteAction(BotAction action, CancellationToken ct)
    {
        // Build description and fire event before executing
        var desc = action switch
        {
            ClickAction c        => c.Modifiers is { Length: > 0 }
                                       ? $"Click ({c.X}, {c.Y}) + {string.Join("+", c.Modifiers)}"
                                       : $"Click ({c.X}, {c.Y})",
            RightClickAction rc  => rc.Modifiers is { Length: > 0 }
                                       ? $"RightClick ({rc.X}, {rc.Y}) + {string.Join("+", rc.Modifiers)}"
                                       : $"RightClick ({rc.X}, {rc.Y})",
            DoubleClickAction dc => $"DoubleClick ({dc.X}, {dc.Y})",
            DragAction d         => $"Drag ({d.FromX},{d.FromY})→({d.ToX},{d.ToY})",
            MoveMouseAction mm   => $"MoveMouse ({mm.X}, {mm.Y})",
            KeyPressAction kp    => kp.Modifiers is { Length: > 0 }
                                       ? $"KeyPress: {kp.Key} + {string.Join("+", kp.Modifiers)}"
                                       : $"KeyPress: {kp.Key}",
            TypeTextAction tt    => $"Type: \"{tt.Text}\"",
            WaitAction w         => $"Wait: {w.Duration.TotalMilliseconds:0}ms",
            _                    => action.GetType().Name,
        };

        ActionPerformed?.Invoke(desc);

        switch (action)
        {
            case ClickAction click:
                await _input.Click(click.X, click.Y, ct, click.Modifiers);
                break;

            case RightClickAction rightClick:
                await _input.RightClick(rightClick.X, rightClick.Y, ct, rightClick.Modifiers);
                break;

            case DoubleClickAction doubleClick:
                await _input.DoubleClick(doubleClick.X, doubleClick.Y, ct);
                break;

            case DragAction drag:
                await _input.Drag(drag.FromX, drag.FromY, drag.ToX, drag.ToY, ct);
                break;

            case MoveMouseAction moveMouse:
                await _input.MoveToClient(moveMouse.X, moveMouse.Y, ct);
                break;

            case KeyPressAction keyPress:
                await _input.KeyPress(keyPress.Key, keyPress.Modifiers, ct);
                break;

            case TypeTextAction typeText:
                await _input.TypeText(typeText.Text, ct);
                break;

            case WaitAction wait:
                _logger.LogTrace("Waiting {Duration}ms", wait.Duration.TotalMilliseconds);
                await Task.Delay(wait.Duration, ct);
                break;

            default:
                _logger.LogWarning("Unknown action type: {Type}", action.GetType().Name);
                break;
        }
    }

    /// <summary>
    /// Brings the EVE window to the foreground using the AttachThreadInput trick,
    /// which works even when the calling process does not currently own the foreground.
    /// This is particularly important in VM guest environments where SetForegroundWindow
    /// alone is often silently ignored.
    /// </summary>
    private void EnsureWindowFocus(nint windowHandle)
    {
        if (windowHandle == 0) return;

        if (!NativeMethods.IsWindow(windowHandle))
        {
            _logger.LogWarning("EVE window handle {Handle} is no longer valid", windowHandle);
            return;
        }

        var targetThread  = NativeMethods.GetWindowThreadProcessId(windowHandle, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        bool attached = targetThread != currentThread &&
                        NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(windowHandle);
            NativeMethods.SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    /// <summary>
    /// Computes the screen coordinates of the EVE window's client-area top-left corner
    /// and stores them in InputSimulator.WindowClientOffsetX/Y.
    /// This corrects clicks when EVE is running in windowed mode (title bar + border offset).
    /// In fullscreen mode ClientToScreen(0,0) returns (0,0), which is a no-op.
    /// </summary>
    private void UpdateWindowClientOffset(nint windowHandle)
    {
        if (windowHandle == 0 || !NativeMethods.IsWindow(windowHandle))
        {
            InputSimulator.WindowClientOffsetX = 0;
            InputSimulator.WindowClientOffsetY = 0;
            return;
        }

        var pt = new NativeMethods.POINT { X = 0, Y = 0 };
        if (NativeMethods.ClientToScreen(windowHandle, ref pt))
        {
            int prevX = InputSimulator.WindowClientOffsetX;
            int prevY = InputSimulator.WindowClientOffsetY;
            InputSimulator.WindowClientOffsetX = pt.X;
            InputSimulator.WindowClientOffsetY = pt.Y;

            // Log when the offset changes (e.g. first tick, or window moved)
            if (pt.X != prevX || pt.Y != prevY)
                _logger.LogInformation(
                    "EVE client offset updated: ({X},{Y}) — {Mode}",
                    pt.X, pt.Y,
                    (pt.X == 0 && pt.Y == 0) ? "fullscreen" : "windowed/RDP fixed-window");
        }
    }
}
