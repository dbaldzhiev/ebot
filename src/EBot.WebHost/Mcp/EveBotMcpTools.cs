using System.ComponentModel;
using System.Text.Json;
using EBot.Core.MemoryReading;
using ModelContextProtocol.Server;

namespace EBot.WebHost.Mcp;

/// <summary>
/// MCP tools exposed to local AI agents (Claude Desktop, etc.).
/// Connect via SSE at http://localhost:{port}/mcp
/// </summary>
[McpServerToolType]
public sealed class EveBotMcpTools(BotOrchestrator orchestrator)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ─── Status ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_status")]
    [Description(
        "Get the current bot state and a summary of the EVE Online game state. " +
        "Returns bot state (Idle/Running/Paused), game location (space/docked), " +
        "ship HP, capacitor, and target count.")]
    public string GetStatus()
    {
        var status = orchestrator.GetStatus(0);
        return Serialize(new
        {
            bot_state = status.State,
            bot_name = status.BotName,
            game = status.GameState == null ? null : new
            {
                in_space = status.GameState.IsInSpace,
                is_docked = status.GameState.IsDocked,
                is_warping = status.GameState.IsWarping,
                tick = status.GameState.TickCount,
                runtime = status.GameState.Runtime,
                capacitor_pct = status.GameState.CapacitorPercent,
                shield_pct = status.GameState.ShieldPercent,
                armor_pct = status.GameState.ArmorPercent,
                structure_pct = status.GameState.StructurePercent,
                targets = status.GameState.TargetCount,
            }
        });
    }

    // ─── Bots ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_bots")]
    [Description("List all available bot types that can be started.")]
    public string ListBots()
    {
        var bots = BotOrchestrator.AvailableBots.Select(b => new
        {
            name = b.Name,
            description = b.Description,
        });
        return Serialize(bots);
    }

    // ─── Processes ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_eve_processes")]
    [Description("List running EVE Online client processes. Returns PID and window title for each.")]
    public string ListEveProcesses()
    {
        var clients = EveProcessFinder.FindEveClients();
        if (clients.Count == 0)
            return "No EVE Online processes found. Make sure the client is running.";

        var result = clients.Select(c => new
        {
            pid = c.ProcessId,
            name = c.ProcessName,
            window_title = c.MainWindowTitle,
        });
        return Serialize(result);
    }

    // ─── Start / Stop / Pause / Resume ────────────────────────────────────

    [McpServerTool(Name = "start_bot")]
    [Description(
        "Start a bot. The bot will begin reading EVE Online memory, making decisions, " +
        "and executing actions. Use list_bots to see available bot names, " +
        "and list_eve_processes to get the process ID.")]
    public async Task<string> StartBot(
        [Description("Bot name to start. Example: 'Mining'")] string bot_name = "Mining",
        [Description("EVE Online process ID. Use 0 or omit for auto-detect.")] int pid = 0,
        [Description("Path to read-memory-64-bit.exe. Leave empty for default.")] string? exe_path = null,
        [Description("Tick interval in milliseconds (how often the bot thinks). Default: 1500")] int tick_ms = 0)
    {
        try
        {
            await orchestrator.StartAsync(bot_name, pid, exe_path, tick_ms);
            return $"Bot '{bot_name}' started successfully. It will now read EVE Online memory every {(tick_ms > 0 ? tick_ms : 1500)}ms and execute actions.";
        }
        catch (Exception ex)
        {
            return $"Failed to start bot: {ex.Message}";
        }
    }

    [McpServerTool(Name = "stop_bot")]
    [Description("Stop the currently running bot. The bot will finish its current tick and shut down cleanly.")]
    public async Task<string> StopBot()
    {
        try
        {
            await orchestrator.StopAsync();
            return "Bot stopped.";
        }
        catch (Exception ex)
        {
            return $"Failed to stop bot: {ex.Message}";
        }
    }

    [McpServerTool(Name = "pause_bot")]
    [Description("Pause the running bot. It stops ticking but preserves its state and blackboard data. Use resume_bot to continue.")]
    public async Task<string> PauseBot()
    {
        try
        {
            await orchestrator.PauseAsync();
            return "Bot paused.";
        }
        catch (Exception ex)
        {
            return $"Failed to pause bot: {ex.Message}";
        }
    }

    [McpServerTool(Name = "resume_bot")]
    [Description("Resume a paused bot. It will continue ticking from where it left off.")]
    public async Task<string> ResumeBot()
    {
        try
        {
            await orchestrator.ResumeAsync();
            return "Bot resumed.";
        }
        catch (Exception ex)
        {
            return $"Failed to resume bot: {ex.Message}";
        }
    }

    [McpServerTool(Name = "step_bot")]
    [Description("Execute a single bot tick while the bot is paused. Useful for step-by-step debugging.")]
    public async Task<string> StepBot()
    {
        try
        {
            await orchestrator.StepAsync();
            return "Single tick executed (Step).";
        }
        catch (Exception ex)
        {
            return $"Failed to step bot: {ex.Message}";
        }
    }

    // ─── Game state detail ─────────────────────────────────────────────────

    [McpServerTool(Name = "get_bot_state")]
    [Description("Get the deep internal state of the bot, including the full blackboard (variables) and current execution stack.")]
    public string GetBotState()
    {
        var state = orchestrator.GetFullState();
        if (state == null)
            return "No bot state available. Start a bot first.";

        return Serialize(state);
    }

    [McpServerTool(Name = "get_overview")]

    [Description("Get the current overview window entries (objects visible in space: asteroids, ships, gates, etc.).")]
    public string GetOverview()
    {
        var ctx = orchestrator.LastContext;
        if (ctx == null)
            return "No game state available. Start a bot first, or wait for the first tick.";

        var entries = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Entries ?? [];
        if (entries.Count == 0)
            return "Overview is empty or not visible.";

        var result = entries.Select(e => new
        {
            name = e.Name,
            type = e.ObjectType,
            distance = e.DistanceText,
            is_attacking_me = e.IsAttackingMe,
        });
        return Serialize(result);
    }

    [McpServerTool(Name = "get_targets")]
    [Description("Get the currently locked targets on the ship.")]
    public string GetTargets()
    {
        var ctx = orchestrator.LastContext;
        if (ctx == null)
            return "No game state available. Start a bot first.";

        var targets = ctx.GameState.ParsedUI.Targets;
        if (targets.Count == 0)
            return "No targets locked.";

        var result = targets.Select(t => new
        {
            name = t.TextLabel,
            is_active = t.IsActiveTarget,
            shield_pct = t.HitpointsPercent?.Shield,
            armor_pct = t.HitpointsPercent?.Armor,
            structure_pct = t.HitpointsPercent?.Structure,
        });
        return Serialize(result);
    }

    [McpServerTool(Name = "get_ship_status")]
    [Description("Get the current ship HP, capacitor, and module status.")]
    public string GetShipStatus()
    {
        var ctx = orchestrator.LastContext;
        if (ctx == null)
            return "No game state available. Start a bot first.";

        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null)
            return "Ship UI not visible. Are you in space?";

        return Serialize(new
        {
            capacitor_pct = shipUI.Capacitor?.LevelPercent,
            shield_pct = shipUI.HitpointsPercent?.Shield,
            armor_pct = shipUI.HitpointsPercent?.Armor,
            structure_pct = shipUI.HitpointsPercent?.Structure,
            maneuver = shipUI.Indication?.ManeuverType,
            modules = new
            {
                total = shipUI.ModuleButtons.Count,
                top_row = shipUI.ModuleButtonsRows.Top.Count,
                mid_row = shipUI.ModuleButtonsRows.Middle.Count,
                bot_row = shipUI.ModuleButtonsRows.Bottom.Count,
                active_count = shipUI.ModuleButtons.Count(m => m.IsActive == true),
            }
        });
    }

    [McpServerTool(Name = "get_log")]
    [Description("Get recent log entries from the bot framework.")]
    public string GetLog(
        [Description("Number of log entries to return (default 20, max 100)")] int count = 20)
    {
        count = Math.Clamp(count, 1, 100);
        var logs = orchestrator.GetRecentLogs(count);

        var result = logs.Select(l => new
        {
            time = l.Time.ToString("HH:mm:ss"),
            level = l.Level,
            category = l.Category,
            message = l.Message,
        });
        return Serialize(result);
    }

    [McpServerTool(Name = "get_bt_state")]
    [Description("Get the current execution path of the behavior tree and the world state synthesis.")]
    public string GetBTState()
    {
        var runner = orchestrator.Runner;
        if (runner == null) return "Bot not running.";

        var ctx = orchestrator.LastContext;
        var trace = ctx != null ? string.Join(" -> ", ctx.ActivePathSnapshot.Reverse()) : "N/A";
        var world = ctx?.Blackboard.Get<object>("world"); // Generic object for serialization

        return Serialize(new
        {
            trace,
            tick = ctx?.TickCount ?? 0,
            world_state = world
        });
    }

    [McpServerTool(Name = "trigger_diagnostic_dump")]
    [Description("Manually trigger a full diagnostic dump (JSON frame + Screenshot).")]
    public string TriggerDump(
        [Description("Reason for the dump (e.g. 'Stuck', 'ManualCheck')")] string reason = "Manual")
    {
        var runner = orchestrator.Runner;
        if (runner == null) return "Bot not running.";

        // Use reflection or make internal method public? 
        // Let's assume we can add a public method to orchestrator to trigger this.
        orchestrator.TriggerEmergencyDump(reason);
        return $"Diagnostic dump triggered with reason: {reason}. Check the logs folder.";
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static string Serialize(object obj) =>
        JsonSerializer.Serialize(obj, _json);
}
