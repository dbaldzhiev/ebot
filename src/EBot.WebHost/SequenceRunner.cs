using EBot.Core.Execution;
using EBot.Core.GameState;
using Microsoft.Extensions.Logging;

namespace EBot.WebHost;

/// <summary>
/// Interprets and executes a <see cref="SequenceGraph"/> built in the visual canvas.
/// Walks the node graph following edges, resolves UI targets from live <see cref="ParsedUI"/>,
/// and dispatches clicks/key-presses/text-input via <see cref="BotOrchestrator"/>.
/// </summary>
public sealed class SequenceRunner
{
    private readonly BotOrchestrator        _orch;
    private readonly ILogger<SequenceRunner> _log;
    private CancellationTokenSource?         _cts;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public SequenceRunner(BotOrchestrator orch, ILogger<SequenceRunner> log)
    {
        _orch = orch;
        _log  = log;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public Task RunAsync(SequenceGraph graph)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        return Task.Run(() => ExecuteAsync(graph, ct), ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Core walk
    // ────────────────────────────────────────────────────────────────────────

    private async Task ExecuteAsync(SequenceGraph graph, CancellationToken ct)
    {
        _log.LogInformation("[Sequence] Starting '{Name}'", graph.Name);
        try
        {
            var start = graph.Nodes.FirstOrDefault(n => n.Type == "start");
            if (start == null) { _log.LogWarning("[Sequence] No start node found"); return; }
            await WalkAsync(graph, start.Id, ct, depth: 0);
            _log.LogInformation("[Sequence] '{Name}' completed", graph.Name);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("[Sequence] '{Name}' stopped", graph.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Sequence] '{Name}' failed", graph.Name);
        }
    }

    private async Task WalkAsync(SequenceGraph graph, string nodeId, CancellationToken ct, int depth)
    {
        if (depth > 512) { _log.LogWarning("[Sequence] Max depth exceeded — possible infinite loop"); return; }
        ct.ThrowIfCancellationRequested();

        var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return;

        _log.LogDebug("[Sequence] Node {Id} ({Type})", node.Id, node.Type);

        string nextPort;

        switch (node.Type)
        {
            case "start":
                nextPort = "next";
                break;

            case "left_click":
                await LeftClickAsync(node, ct);
                nextPort = "next";
                break;

            case "right_click":
                await RightClickAsync(node, ct);
                nextPort = "next";
                break;

            case "key_press":
                await KeyPressAsync(node, ct);
                nextPort = "next";
                break;

            case "type_text":
                await TypeTextAsync(node, ct);
                nextPort = "next";
                break;

            case "wait_until":
                await WaitUntilAsync(node, ct);
                nextPort = "next";
                break;

            case "stop_sequence":
                _log.LogInformation("[Sequence] stop_sequence reached — stopping");
                _cts?.Cancel();
                ct.ThrowIfCancellationRequested();
                return; // unreachable

            case "condition":
                nextPort = EvaluateCondition(node) ? "true" : "false";
                break;

            case "wait":
            {
                var ms = ParseInt(node, "ms", 1000);
                await Task.Delay(Math.Clamp(ms, 0, 60_000), ct);
                nextPort = "next";
                break;
            }

            case "loop":
                await ExecuteLoopAsync(graph, node, ct, depth);
                nextPort = "done";
                break;

            case "end":
                return;

            default:
                _log.LogWarning("[Sequence] Unknown node type: '{Type}'", node.Type);
                nextPort = "next";
                break;
        }

        var edge = graph.Edges.FirstOrDefault(e => e.From == nodeId && e.FromPort == nextPort);
        if (edge != null)
            await WalkAsync(graph, edge.To, ct, depth + 1);
    }

    private async Task ExecuteLoopAsync(SequenceGraph graph, SequenceNode node, CancellationToken ct, int depth)
    {
        var iterations = ParseInt(node, "iterations", 0); // 0 = infinite
        var bodyEdge   = graph.Edges.FirstOrDefault(e => e.From == node.Id && e.FromPort == "body");
        if (bodyEdge == null) return;

        for (var i = 0; iterations == 0 || i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            await WalkAsync(graph, bodyEdge.To, ct, depth + 1);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Action nodes
    // ────────────────────────────────────────────────────────────────────────

    private async Task WaitUntilAsync(SequenceNode node, CancellationToken ct)
    {
        var pollMs    = Math.Clamp(ParseInt(node, "poll_ms",    500),   50, 5_000);
        var timeoutMs = Math.Clamp(ParseInt(node, "timeout_ms", 30_000), 100, 300_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _log.LogDebug("[Sequence] wait_until '{Check}' timeout={T}ms", node.Params.GetValueOrDefault("check"), timeoutMs);
        while (!EvaluateCondition(node))
        {
            ct.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                _log.LogWarning("[Sequence] wait_until timed out after {T}ms", timeoutMs);
                break;
            }
            await Task.Delay(pollMs, ct);
        }
    }

    private async Task LeftClickAsync(SequenceNode node, CancellationToken ct)
    {
        var target = ResolveTarget(node);
        if (target == null)
        {
            _log.LogWarning("[Sequence] left_click: target '{T}' not found in live UI",
                node.Params.GetValueOrDefault("target"));
            return;
        }
        _log.LogInformation("[Sequence] Left-click → {T}", node.Params.GetValueOrDefault("target"));
        _orch.LastContext!.Click(target);
        await Task.Delay(400, ct);
    }

    private async Task RightClickAsync(SequenceNode node, CancellationToken ct)
    {
        var target = ResolveTarget(node);
        if (target == null)
        {
            _log.LogWarning("[Sequence] right_click: target '{T}' not found in live UI",
                node.Params.GetValueOrDefault("target"));
            return;
        }
        _log.LogInformation("[Sequence] Right-click → {T}", node.Params.GetValueOrDefault("target"));
        _orch.LastContext!.RightClick(target);
        await Task.Delay(400, ct);
    }

    private async Task KeyPressAsync(SequenceNode node, CancellationToken ct)
    {
        var keyName = node.Params.GetValueOrDefault("key") ?? "";
        var key     = ParseVirtualKey(keyName);
        if (key == null)
        {
            _log.LogWarning("[Sequence] key_press: unknown key '{K}'", keyName);
            return;
        }

        var mods = new List<VirtualKey>();
        if (node.Params.GetValueOrDefault("ctrl")  == "true") mods.Add(VirtualKey.Control);
        if (node.Params.GetValueOrDefault("shift") == "true") mods.Add(VirtualKey.Shift);
        if (node.Params.GetValueOrDefault("alt")   == "true") mods.Add(VirtualKey.Alt);

        _log.LogInformation("[Sequence] KeyPress: {K}  mods=[{M}]", keyName, string.Join(",", mods));
        _orch.LastContext!.KeyPress(key.Value, [.. mods]);
        await Task.Delay(200, ct);
    }

    private async Task TypeTextAsync(SequenceNode node, CancellationToken ct)
    {
        // Optional: click a target before typing
        var targetId = node.Params.GetValueOrDefault("target") ?? "";
        if (!string.IsNullOrEmpty(targetId))
        {
            var target = ResolveTarget(node);
            if (target != null)
            {
                _orch.LastContext!.Click(target);
                await Task.Delay(300, ct);
            }
        }

        var text = node.Params.GetValueOrDefault("text") ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            _log.LogInformation("[Sequence] TypeText: '{T}'", text);
            _orch.LastContext!.TypeText(text);
            await Task.Delay(100 + text.Length * 30, ct);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Target resolution
    // ────────────────────────────────────────────────────────────────────────

    private UITreeNodeWithDisplayRegion? ResolveTarget(SequenceNode node)
    {
        var ui = _orch.LastContext?.GameState.ParsedUI;
        if (ui == null) return null;

        var targetId    = node.Params.GetValueOrDefault("target")       ?? "";
        var targetParam = node.Params.GetValueOrDefault("target_param") ?? "";

        return targetId switch
        {
            // ── Ship UI ───────────────────────────────────────────────────
            "ship_ui.module_slot" =>
                int.TryParse(targetParam, out var slot)
                    ? ui.ShipUI?.ModuleButtons.ElementAtOrDefault(slot - 1)?.UINode
                    : null,
            "ship_ui.stop_button"      => ui.ShipUI?.StopButton,
            "ship_ui.max_speed_button" => ui.ShipUI?.MaxSpeedButton,

            // ── Overview ─────────────────────────────────────────────────
            "overview.first_entry" =>
                ui.OverviewWindows.FirstOrDefault()?.Entries.FirstOrDefault()?.UINode,
            "overview.entry_by_name" =>
                FindOverviewEntry(ui, targetParam)?.UINode,
            "overview.closest_entry" or "overview.entry_by_distance" =>
                ui.OverviewWindows
                  .SelectMany(w => w.Entries)
                  .MinBy(e => e.DistanceInMeters ?? double.MaxValue)?.UINode,
            "overview.tab_by_name" =>
                ui.OverviewWindows
                  .SelectMany(w => w.Tabs)
                  .FirstOrDefault(t => t.Name?.Contains(targetParam, StringComparison.OrdinalIgnoreCase) ?? false)?.UINode,

            // ── Context Menu ─────────────────────────────────────────────
            "ctx.first_entry" =>
                ui.ContextMenus.FirstOrDefault()?.Entries.FirstOrDefault()?.UINode,
            "ctx.entry_by_text" =>
                ui.ContextMenus
                  .SelectMany(m => m.Entries)
                  .FirstOrDefault(e => e.Text?.Contains(targetParam, StringComparison.OrdinalIgnoreCase) ?? false)?.UINode,
            "ctx.entry_by_index" =>
                int.TryParse(targetParam, out var ctxIdx)
                    ? ui.ContextMenus.FirstOrDefault()?.Entries.ElementAtOrDefault(ctxIdx - 1)?.UINode
                    : null,

            // ── Locked Targets ───────────────────────────────────────────
            "targets.first"   => ui.Targets.FirstOrDefault()?.UINode,
            "targets.active"  => ui.Targets.FirstOrDefault(t => t.IsActiveTarget)?.UINode,
            "targets.by_name" =>
                ui.Targets
                  .FirstOrDefault(t => t.TextLabel?.Contains(targetParam, StringComparison.OrdinalIgnoreCase) ?? false)?.UINode,

            // ── Message Box ───────────────────────────────────────────────
            "msgbox.first_button" =>
                ui.MessageBoxes.FirstOrDefault()?.Buttons.FirstOrDefault(),
            "msgbox.button_by_text" =>
                ui.MessageBoxes.FirstOrDefault()?.Buttons
                  .FirstOrDefault(b => b.GetAllContainedDisplayTexts()
                      .Any(t => t.Contains(targetParam, StringComparison.OrdinalIgnoreCase))),

            // ── Inventory ─────────────────────────────────────────────────
            "inv.nav_entry_by_name" =>
                ui.InventoryWindows
                  .SelectMany(w => w.NavEntries)
                  .FirstOrDefault(e => e.Label?.Contains(targetParam, StringComparison.OrdinalIgnoreCase) ?? false)?.UINode,
            "inv.stack_all_button" =>
                ui.InventoryWindows.FirstOrDefault()?.ButtonToStackAll,

            // ── Selected Item ─────────────────────────────────────────────
            "sel.first_button" => ui.SelectedItemWindow?.ActionButtons.FirstOrDefault(),
            "sel.button_by_index" =>
                int.TryParse(targetParam, out var selIdx)
                    ? ui.SelectedItemWindow?.ActionButtons.ElementAtOrDefault(selIdx - 1)
                    : null,

            // ── Station ───────────────────────────────────────────────────
            "station.undock_button" => ui.StationWindow?.UndockButton,

            // ── Info Panel ────────────────────────────────────────────────
            "infopanel.route_marker" =>
                ui.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers.FirstOrDefault(),

            // ── Neocom ────────────────────────────────────────────────────
            "neocom.self" => ui.Neocom?.UINode,

            _ => null,
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Condition evaluation
    // ────────────────────────────────────────────────────────────────────────

    private bool EvaluateCondition(SequenceNode node)
    {
        var check    = node.Params.GetValueOrDefault("check") ?? "";
        var valueStr = node.Params.GetValueOrDefault("value") ?? "0";
        double.TryParse(valueStr, out var threshold);

        var ctx = _orch.LastContext;
        if (ctx == null) return false;

        var gs   = ctx.GameState;
        var ui   = gs.ParsedUI;
        var ship = ui.ShipUI;

        bool result = check switch
        {
            "is_docked"                   => gs.IsDocked,
            "is_in_space"                 => gs.IsInSpace,
            "is_warping"                  => gs.IsWarping,
            "has_target"                  => ui.Targets.Count > 0,
            "target_count_above"          => ui.Targets.Count > (int)threshold,
            "shield_below"                => (ship?.HitpointsPercent?.Shield    ?? 100) < threshold,
            "armor_below"                 => (ship?.HitpointsPercent?.Armor     ?? 100) < threshold,
            "structure_below"             => (ship?.HitpointsPercent?.Structure ?? 100) < threshold,
            "cap_below"                   => (ship?.Capacitor?.LevelPercent     ?? 100) < threshold,
            "context_menu_open"           => ui.ContextMenus.Count > 0,
            "message_box_visible"         => ui.MessageBoxes.Count > 0,
            "overview_has_entry"          => ui.OverviewWindows.Any(w => w.Entries.Any(e =>
                                                string.IsNullOrEmpty(valueStr) ||
                                                (e.Name?.Contains(valueStr, StringComparison.OrdinalIgnoreCase) ?? false))),
            "module_active"               => IsModuleActive((int)threshold),
            "module_inactive"             => !IsModuleActive((int)threshold),
            "cargo_full"                  => IsCargoAbove(threshold),
            "cargo_below_percent"         => IsCargoBelow(threshold),
            "route_jumps_remaining_above" => gs.RouteJumpsRemaining > (int)threshold,
            "route_jumps_remaining_below" => gs.RouteJumpsRemaining < (int)threshold,
            _                             => false,
        };

        _log.LogDebug("[Sequence] Condition {Check}={Value} → {Result}", check, valueStr, result);
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static OverviewEntry? FindOverviewEntry(ParsedUI ui, string nameFilter)
    {
        foreach (var win in ui.OverviewWindows)
        {
            var entry = string.IsNullOrEmpty(nameFilter)
                ? win.Entries.FirstOrDefault()
                : win.Entries.FirstOrDefault(e =>
                    e.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false);
            if (entry != null) return entry;
        }
        return null;
    }

    private bool IsModuleActive(int slot)
    {
        if (slot < 1) return false;
        var ui = _orch.LastContext?.GameState.ParsedUI;
        return ui?.ShipUI?.ModuleButtons.ElementAtOrDefault(slot - 1)?.IsActive == true;
    }

    private bool IsCargoAbove(double percent)
    {
        var inv = _orch.LastContext?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        return inv?.CapacityGauge != null && inv.CapacityGauge.FillPercent >= percent;
    }

    private bool IsCargoBelow(double percent)
    {
        var inv = _orch.LastContext?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        return inv?.CapacityGauge != null && inv.CapacityGauge.FillPercent < percent;
    }

    private static int ParseInt(SequenceNode node, string key, int fallback)
    {
        var s = node.Params.GetValueOrDefault(key);
        return int.TryParse(s, out var v) ? v : fallback;
    }

    private static VirtualKey? ParseVirtualKey(string name) => name switch
    {
        "F1"       => VirtualKey.F1,  "F2"  => VirtualKey.F2,  "F3"  => VirtualKey.F3,
        "F4"       => VirtualKey.F4,  "F5"  => VirtualKey.F5,  "F6"  => VirtualKey.F6,
        "F7"       => VirtualKey.F7,  "F8"  => VirtualKey.F8,  "F9"  => VirtualKey.F9,
        "F10"      => VirtualKey.F10, "F11" => VirtualKey.F11, "F12" => VirtualKey.F12,
        "Escape"   => VirtualKey.Escape,   "Enter"    => VirtualKey.Enter,
        "Space"    => VirtualKey.Space,    "Tab"      => VirtualKey.Tab,
        "Backspace"=> VirtualKey.Backspace,"Delete"   => VirtualKey.Delete,
        "Home"     => VirtualKey.Home,     "End"      => VirtualKey.End,
        "PageUp"   => VirtualKey.PageUp,   "PageDown" => VirtualKey.PageDown,
        "Left"     => VirtualKey.Left,     "Right"    => VirtualKey.Right,
        "Up"       => VirtualKey.Up,       "Down"     => VirtualKey.Down,
        "A" => VirtualKey.A, "B" => VirtualKey.B, "C" => VirtualKey.C, "D" => VirtualKey.D,
        "E" => VirtualKey.E, "F" => VirtualKey.F, "G" => VirtualKey.G, "H" => VirtualKey.H,
        "I" => VirtualKey.I, "J" => VirtualKey.J, "K" => VirtualKey.K, "L" => VirtualKey.L,
        "M" => VirtualKey.M, "N" => VirtualKey.N, "O" => VirtualKey.O, "P" => VirtualKey.P,
        "Q" => VirtualKey.Q, "R" => VirtualKey.R, "S" => VirtualKey.S, "T" => VirtualKey.T,
        "U" => VirtualKey.U, "V" => VirtualKey.V, "W" => VirtualKey.W, "X" => VirtualKey.X,
        "Y" => VirtualKey.Y, "Z" => VirtualKey.Z,
        "0" => VirtualKey.D0, "1" => VirtualKey.D1, "2" => VirtualKey.D2, "3" => VirtualKey.D3,
        "4" => VirtualKey.D4, "5" => VirtualKey.D5, "6" => VirtualKey.D6, "7" => VirtualKey.D7,
        "8" => VirtualKey.D8, "9" => VirtualKey.D9,
        _ => null,
    };
}
