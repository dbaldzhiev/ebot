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

    public ActionExecutor(InputSimulator input, ILogger<ActionExecutor> logger)
    {
        _input = input;
        _logger = logger;
    }

    /// <summary>
    /// Executes all actions in the queue sequentially.
    /// </summary>
    public async Task ExecuteAllAsync(ActionQueue queue, nint windowHandle, CancellationToken ct = default)
    {
        EnsureWindowFocus(windowHandle);

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
        switch (action)
        {
            case ClickAction click:
                await _input.Click(click.X, click.Y, ct);
                break;

            case RightClickAction rightClick:
                await _input.RightClick(rightClick.X, rightClick.Y, ct);
                break;

            case DoubleClickAction doubleClick:
                await _input.DoubleClick(doubleClick.X, doubleClick.Y, ct);
                break;

            case DragAction drag:
                await _input.Drag(drag.FromX, drag.FromY, drag.ToX, drag.ToY, ct);
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

    private void EnsureWindowFocus(nint windowHandle)
    {
        if (windowHandle == 0) return;

        if (NativeMethods.IsWindow(windowHandle))
        {
            NativeMethods.SetForegroundWindow(windowHandle);
        }
        else
        {
            _logger.LogWarning("EVE window handle {Handle} is no longer valid", windowHandle);
        }
    }
}
