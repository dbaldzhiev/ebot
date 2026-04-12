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
            "Start traveling to a destination. Supports station aliases (e.g. 'Jita 4-4') " +
            "which automatically use bookmark-based docking. For systems without an alias, " +
            "uses autopilot warp-to-0. Example: travel_to(destination='Jita 4-4').",
            ("destination", "string", "Station alias (e.g. 'Jita 4-4') or solar system name (e.g. 'Jita')", true)),

        MakeTool("undock",
            "Click the Undock button in the station services window. Only works when docked."),

        MakeTool("dock",
            "Dock to the nearest station or structure visible in the overview."),

        MakeTool("get_cargo",
            "Get the current ship cargo: used/max volume and list of items."),

        MakeTool("get_ship_status",
            "Get ship HP, capacitor, speed, and active module count."),

        MakeTool("add_quick_travel",
            "Add a solar system or station alias to the quick travel button list.",
            ("station", "string", "Station alias or system name to save, e.g. 'Jita 4-4'", true)),

        MakeTool("get_quick_travel",
            "List all configured quick travel destinations."),

        MakeTool("get_station_aliases",
            "List all configured station aliases (e.g. 'Jita 4-4' → system + bookmark)."),

        MakeTool("add_station_alias",
            "Add or update a station alias. An alias maps a friendly name to an EVE system name " +
            "and an optional bookmark name for precise docking.",
            ("alias",    "string", "Friendly name, e.g. 'Jita 4-4'", true),
            ("system",   "string", "Solar system name, e.g. 'Jita'",  true),
            ("bookmark", "string", "Exact in-game bookmark name for warp+dock. Omit to use overview docking.", false)),
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
                await orchestrator.StartAsync("Travel Bot", destination: destination);
                return $"Travel started to '{destination}'.";
            }

            case "undock":
            {
                try { await orchestrator.UndockAsync(); return "Undock command sent."; }
                catch (Exception ex) { return $"Undock failed: {ex.Message}"; }
            }

            case "dock":
            {
                try { await orchestrator.DockAsync(); return "Dock command sent."; }
                catch (Exception ex) { return $"Dock failed: {ex.Message}"; }
            }

            case "get_cargo":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state available.";
                var inv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
                if (inv == null) return "No inventory window visible (open cargo hold in EVE).";
                var used = inv.CapacityGauge?.Used;
                var max  = inv.CapacityGauge?.Maximum;
                var items = inv.Items.Select(i => new { name = i.Name, qty = i.Quantity });
                return Serialize(new { used_m3 = used, max_m3 = max, fill_pct = inv.CapacityGauge?.FillPercent, items });
            }

            case "get_ship_status":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state available.";
                var ship = ctx.GameState.ParsedUI.ShipUI;
                if (ship == null) return "Ship UI not visible (are you in space?).";
                return Serialize(new
                {
                    capacitor_pct = ship.Capacitor?.LevelPercent,
                    shield_pct = ship.HitpointsPercent?.Shield,
                    armor_pct = ship.HitpointsPercent?.Armor,
                    structure_pct = ship.HitpointsPercent?.Structure,
                    speed = ship.SpeedText,
                    modules_total = ship.ModuleButtons.Count,
                    modules_active = ship.ModuleButtons.Count(m => m.IsActive == true),
                });
            }

            case "get_destinations":
            {
                var path = Path.Combine(AppContext.BaseDirectory, "data", "destinations.json");
                List<TravelDestination> list = [];
                try { if (File.Exists(path)) list = System.Text.Json.JsonSerializer.Deserialize<List<TravelDestination>>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; } catch { }
                return list.Count == 0 ? "No saved destinations." : Serialize(list.Select(d => new { d.Id, d.Name, d.SystemName }));
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
