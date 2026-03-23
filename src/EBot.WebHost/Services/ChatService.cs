using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using EBot.WebHost.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace EBot.WebHost.Services;

/// <summary>
/// Handles the AI chat pipeline:
///   1. Accepts a natural-language command from the user
///   2. Calls Claude (claude-opus-4-6) with adaptive thinking and a set of
///      bot-control tools
///   3. Executes tool calls against BotOrchestrator
///   4. Pushes ChatEvent messages back to the caller via SignalR
///
/// SignalR events pushed to the client connection:
///   ChatToolCall  { name, preview }    — a tool is being called
///   ChatToolResult{ name, result }     — tool returned
///   ChatMessage   { text }            — Claude's final text reply
///   ChatError     { error }           — something went wrong
/// </summary>
public sealed class ChatService(
    BotOrchestrator orchestrator,
    IHubContext<BotHub> hub,
    ILogger<ChatService> logger) : IChatService
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ─── Tools Claude can call ─────────────────────────────────────────────

    private static readonly IReadOnlyList<Tool> _tools =
    [
        MakeTool("get_status",
            "Get current bot state and live EVE Online game state (location, HP, capacitor, targets)."),

        MakeTool("list_bots",
            "List all bot types available to start."),

        MakeTool("start_bot",
            "Start a bot. Auto-detects the EVE Online process.",
            ("bot_name", "string", "Bot name, e.g. 'Mining Bot'", true)),

        MakeTool("stop_bot",
            "Stop the currently running bot."),

        MakeTool("start_monitor",
            "Start Monitor mode — read-only observation with no bot actions."),

        MakeTool("stop_monitor",
            "Stop Monitor mode."),

        MakeTool("pause_bot",
            "Pause the running bot (preserves state)."),

        MakeTool("resume_bot",
            "Resume a paused bot."),

        MakeTool("set_survival_mode",
            "Enable or disable Survival Mode (auto-tank, dismiss popups).",
            ("enabled", "boolean", "true to enable, false to disable", true)),

        MakeTool("get_overview",
            "Get the current overview window entries (asteroids, ships, gates, etc.)."),

        MakeTool("get_targets",
            "Get currently locked targets and their hit points."),

        MakeTool("get_log",
            "Get recent log entries from the bot framework.",
            ("count", "integer", "Number of log lines (default 20, max 50)", false)),

        MakeTool("travel_to",
            "Open People & Places in-game, search for the given system, set it as the destination, " +
            "then start the Autopilot Bot to travel there using warp-to-0 technique. " +
            "Example: 'travel to Jita' → call travel_to(destination='Jita').",
            ("destination", "string", "Target solar system name, e.g. 'Jita' or 'Amarr'", true)),
    ];

    // ─── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Processes a user command via Claude, executing tools and streaming
    /// results back to the SignalR client identified by <paramref name="connectionId"/>.
    /// </summary>
    public async Task ProcessAsync(string userMessage, string connectionId,
        CancellationToken cancellationToken = default)
    {
        var conn = hub.Clients.Client(connectionId);
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                await conn.SendAsync("ChatError",
                    new { error = "ANTHROPIC_API_KEY is not set. Add it to your environment." },
                    cancellationToken);
                return;
            }

            AnthropicClient client = new() { ApiKey = apiKey };

            var messages = new List<MessageParam>
            {
                new() { Role = Role.User, Content = userMessage },
            };

            var systemPrompt =
                "You are the Command & Control AI for an EVE Online bot framework. " +
                "You can control bots, query game state, and advise the pilot. " +
                "Be concise and action-oriented. When the user asks you to do something, " +
                "call the appropriate tool immediately. " +
                "After executing tools, summarise what happened in 1-2 sentences.";

            // ── Agentic tool-use loop ──────────────────────────────────────

            Message response;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parameters = new MessageCreateParams
                {
                    Model = Model.ClaudeOpus4_6,
                    MaxTokens = 4096,
                    Thinking = new ThinkingConfigAdaptive(),
                    System = systemPrompt,
                    Tools = [.. _tools.Select(t => (ToolUnion)t)],
                    Messages = messages,
                };

                response = await client.Messages.Create(parameters, cancellationToken);

                // Rebuild assistant message from response (no .ToParam() in C# SDK)
                var assistantContent = new List<ContentBlockParam>();
                var toolResults = new List<ContentBlockParam>();

                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out TextBlock? textBlock))
                    {
                        assistantContent.Add(new TextBlockParam { Text = textBlock.Text });
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thinkingBlock))
                    {
                        assistantContent.Add(new ThinkingBlockParam
                        {
                            Thinking = thinkingBlock.Thinking,
                            Signature = thinkingBlock.Signature,
                        });
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                    {
                        assistantContent.Add(new ToolUseBlockParam
                        {
                            ID = toolUse.ID,
                            Name = toolUse.Name,
                            Input = toolUse.Input,
                        });

                        // Notify client a tool is being called
                        await conn.SendAsync("ChatToolCall",
                            new { name = toolUse.Name },
                            cancellationToken);

                        // Execute the tool
                        string toolResult;
                        try
                        {
                            toolResult = await ExecuteToolAsync(toolUse.Name, toolUse.Input,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            toolResult = $"Error: {ex.Message}";
                            logger.LogWarning(ex, "Tool {Tool} failed", toolUse.Name);
                        }

                        await conn.SendAsync("ChatToolResult",
                            new { name = toolUse.Name, result = toolResult },
                            cancellationToken);

                        toolResults.Add(new ToolResultBlockParam
                        {
                            ToolUseID = toolUse.ID,
                            Content = toolResult,
                        });
                    }
                }

                // If no tool calls, we're done
                if (toolResults.Count == 0)
                    break;

                // Append assistant turn + tool results and loop
                messages = [
                    .. messages,
                    new() { Role = Role.Assistant, Content = assistantContent },
                    new() { Role = Role.User, Content = toolResults },
                ];
            }

            // ── Send final text to client ──────────────────────────────────

            var finalText = string.Concat(
                response.Content
                    .Where(b => b.TryPickText(out _))
                    .Select(b => { b.TryPickText(out TextBlock? t); return t!.Text; }));

            if (string.IsNullOrWhiteSpace(finalText))
                finalText = "Done.";

            await conn.SendAsync("ChatMessage", new { text = finalText }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — ignore
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatService error");
            try
            {
                await conn.SendAsync("ChatError", new { error = ex.Message }, cancellationToken);
            }
            catch { /* connection may be gone */ }
        }
    }

    // ─── Tool dispatch ─────────────────────────────────────────────────────

    private async Task<string> ExecuteToolAsync(string name,
        IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        switch (name)
        {
            case "get_status":
                return Serialize(orchestrator.GetStatus(0));

            case "list_bots":
                return Serialize(BotOrchestrator.AvailableBots
                    .Select(b => new { b.Name, b.Description }));

            case "start_bot":
            {
                var botName = Str(input, "bot_name") ?? "Mining Bot";
                await orchestrator.StartAsync(botName, tickMs: 0);
                return $"Bot '{botName}' started.";
            }

            case "stop_bot":
                await orchestrator.StopAsync();
                return "Bot stopped.";

            case "start_monitor":
                await orchestrator.EnsureMonitorAsync();
                return "Monitor is always active.";

            case "pause_bot":
                await orchestrator.PauseAsync();
                return "Bot paused.";

            case "resume_bot":
                await orchestrator.ResumeAsync();
                return "Bot resumed.";

            case "set_survival_mode":
            {
                var enabled = Bool(input, "enabled") ?? true;
                orchestrator.SetSurvivalMode(enabled);
                return $"Survival mode {(enabled ? "enabled" : "disabled")}.";
            }

            case "get_overview":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state — start monitor or a bot first.";
                var entries = ctx.GameState.ParsedUI.OverviewWindows
                    .FirstOrDefault()?.Entries ?? [];
                if (entries.Count == 0) return "Overview is empty.";
                return Serialize(entries.Select(e => new
                {
                    name = e.Name,
                    type = e.ObjectType,
                    distance = e.DistanceText,
                    attacking = e.IsAttackingMe,
                }));
            }

            case "get_targets":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state.";
                var targets = ctx.GameState.ParsedUI.Targets;
                if (targets.Count == 0) return "No targets locked.";
                return Serialize(targets.Select(t => new
                {
                    name = t.TextLabel,
                    active = t.IsActiveTarget,
                    shield = t.HitpointsPercent?.Shield,
                    armor = t.HitpointsPercent?.Armor,
                    structure = t.HitpointsPercent?.Structure,
                }));
            }

            case "get_log":
            {
                var count = Math.Clamp(Int(input, "count") ?? 20, 1, 50);
                var logs = orchestrator.GetRecentLogs(count);
                return Serialize(logs.Select(l => new
                {
                    time = l.Time.ToString("HH:mm:ss"),
                    level = l.Level,
                    message = l.Message,
                }));
            }

            case "travel_to":
            {
                var destination = Str(input, "destination");
                if (string.IsNullOrWhiteSpace(destination))
                    return "Error: destination is required.";
                await orchestrator.TravelToAsync(destination);
                return $"Autopilot Bot started. Searching for '{destination}' in People & Places and setting as destination. Navigation (warp-to-0) will begin once the route is set.";
            }

            default:
                return $"Unknown tool: {name}";
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static string Serialize(object obj) => JsonSerializer.Serialize(obj, _json);

    private static string? Str(IReadOnlyDictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(IReadOnlyDictionary<string, JsonElement> d, string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null;
    }

    private static int? Int(IReadOnlyDictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.TryGetInt32(out var i) ? i : null;

    // ─── Tool definition builder ───────────────────────────────────────────

    private static Tool MakeTool(string name, string description,
        params (string name, string type, string desc, bool required)[] parameters)
    {
        var props = new Dictionary<string, JsonElement>();
        var required = new List<string>();

        foreach (var (pName, pType, pDesc, pRequired) in parameters)
        {
            props[pName] = JsonSerializer.SerializeToElement(
                new { type = pType, description = pDesc });
            if (pRequired) required.Add(pName);
        }

        var schema = new InputSchema
        {
            Properties = props,
        };
        if (required.Count > 0)
            schema = schema with { Required = required };

        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = schema,
        };
    }
}
