using EBot.WebHost.Services;
using Microsoft.AspNetCore.SignalR;

namespace EBot.WebHost.Hubs;

/// <summary>
/// SignalR hub for real-time bot events.
///
/// Server → Client events:
///   StateChanged(string)          — "Idle" | "Running" | "Paused"
///   TickUpdate(GameStateSummary)  — game state each tick
///   LogEntry(LogEntry)            — new log message
///   MonitorChanged(bool)          — monitor mode toggled
///   SurvivalChanged(bool)         — survival mode toggled
///   ChatToolCall { name }         — AI is calling a tool
///   ChatToolResult { name, result }
///   ChatMessage { text }          — AI final text reply
///   ChatError { error }           — AI error
///
/// Client → Server:
///   SendChatMessage(string)       — user sends a natural-language command
/// </summary>
public sealed class BotHub(IChatService chatService) : Hub
{
    /// <summary>
    /// Called by the web UI when the user submits a chat message.
    /// Processes the command through Claude and pushes results back to
    /// this specific connection only.
    /// </summary>
    public async Task SendChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        await chatService.ProcessAsync(message, Context.ConnectionId, Context.ConnectionAborted);
    }
}
