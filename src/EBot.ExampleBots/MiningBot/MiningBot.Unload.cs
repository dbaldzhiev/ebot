using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    // ═══════════════════════════════════════════════════════════════════════
    // Docked behaviors
    // ═══════════════════════════════════════════════════════════════════════

    private IBehaviorNode HandleDocked() =>
        new SequenceNode("Docked",
            new ConditionNode("Is docked?", ctx => ctx.GameState.IsDocked),
            new SelectorNode("Docked actions",
                PerformUnload(),
                RememberStationAndUndock()));

    // ─── Unload state machine ────────────────────────────────────────────────

    private IBehaviorNode PerformUnload() =>
        new SequenceNode("Unload ore",
            new ConditionNode("Needs unload?",
                ctx => ctx.Blackboard.Get<bool>("needs_unload")),
            new ActionNode("Unload state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("unload_phase") ?? "";
                switch (phase)
                {
                    case "":
                        ctx.KeyPress(VirtualKey.C, VirtualKey.Alt);
                        ctx.Wait(TimeSpan.FromSeconds(1.5));
                        ctx.Blackboard.Set("unload_phase", "find_orehold");
                        return NodeStatus.Running;

                    case "find_orehold":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null)
                        {
                            var link = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                                n.GetAllContainedDisplayTexts()
                                 .Any(t => t.Contains("Ore Hold",
                                     StringComparison.OrdinalIgnoreCase))
                                && n.Region.Width > 10 && n.Region.Height > 6);
                            if (link != null) { ctx.Click(link); ctx.Wait(TimeSpan.FromMilliseconds(700)); }
                            return NodeStatus.Running;
                        }
                        if (oreHold.Items.Count == 0) { FinishUnload(ctx, 0); return NodeStatus.Success; }
                        ctx.Blackboard.Set("unload_phase", "stack_all");
                        return NodeStatus.Running;
                    }

                    case "stack_all":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold?.ButtonToStackAll != null)
                        {
                            ctx.Click(oreHold.ButtonToStackAll);
                            ctx.Wait(TimeSpan.FromMilliseconds(500));
                        }
                        ctx.Blackboard.Set("unload_phase", "select_all");
                        return NodeStatus.Running;
                    }

                    case "select_all":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null || oreHold.Items.Count == 0)
                        { FinishUnload(ctx, 0); return NodeStatus.Success; }
                        ctx.Blackboard.Set("unload_vol_before", oreHold.CapacityGauge?.Used ?? 0.0);
                        ctx.Click(oreHold.Items[0].UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(200));
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);
                        ctx.Wait(TimeSpan.FromMilliseconds(300));
                        ctx.Blackboard.Set("unload_phase", "open_menu");
                        return NodeStatus.Running;
                    }

                    case "open_menu":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null || oreHold.Items.Count == 0)
                        { FinishUnload(ctx, ctx.Blackboard.Get<double>("unload_vol_before")); return NodeStatus.Success; }
                        ctx.Blackboard.Set("menu_expected", true);
                        ctx.RightClick(oreHold.Items[0].UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        ctx.Blackboard.Set("unload_phase", "click_move");
                        return NodeStatus.Running;
                    }

                    case "click_move":
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        if (!ctx.GameState.HasContextMenu)
                        { ctx.Blackboard.Set("unload_phase", "open_menu"); return NodeStatus.Running; }
                        var menu  = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var entry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("hangar",   StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Move To",  StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Move All", StringComparison.OrdinalIgnoreCase) == true);
                        if (entry != null)
                        {
                            ctx.Click(entry.UINode);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            ctx.Blackboard.Set("unload_phase", "verify");
                        }
                        else
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("unload_phase", "open_menu");
                        }
                        return NodeStatus.Running;
                    }

                    case "verify":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold?.Items.Count > 0)
                        { ctx.Blackboard.Set("unload_phase", "select_all"); return NodeStatus.Running; }
                        FinishUnload(ctx, ctx.Blackboard.Get<double>("unload_vol_before"));
                        return NodeStatus.Success;
                    }

                    default:
                        ctx.Blackboard.Set("unload_phase", "");
                        return NodeStatus.Running;
                }
            }));

    private void FinishUnload(BotContext ctx, double volume)
    {
        _totalUnloadedM3 += volume;
        _unloadCycles++;
        SyncStats(ctx);
        ctx.Blackboard.Set("needs_unload",       false);
        ctx.Blackboard.Set("unload_phase",        "");
        ctx.Blackboard.Set("unload_vol_before",   0.0);
        ctx.Blackboard.Set("belt_index",        0);   // restart belt cycle counter after unload
        ctx.Blackboard.Set("last_belt_target", -1);  // no current belt after station run
    }

    private void SyncStats(BotContext ctx)
    {
        ctx.Blackboard.Set("total_unloaded_m3", _totalUnloadedM3);
        ctx.Blackboard.Set("unload_cycles",     _unloadCycles);
    }

    // ─── Remember station and undock ────────────────────────────────────────

    private static IBehaviorNode RememberStationAndUndock() =>
        new ActionNode("Remember home + undock", ctx =>
        {
            if (!ctx.Blackboard.Get<bool>("home_station_set"))
            {
                var name = ctx.GameState.ParsedUI.StationWindow?.UINode
                    .GetAllContainedDisplayTexts()
                    .Where(t => t.Length > 5 && !t.All(char.IsDigit) &&
                                !t.Equals("Undock", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(name))
                {
                    ctx.Blackboard.Set("home_station",     name);
                    ctx.Blackboard.Set("home_station_set", true);
                }
                var sys = ctx.GameState.ParsedUI.InfoPanelContainer?
                    .InfoPanelLocationInfo?.SystemName ?? "";
                if (!string.IsNullOrEmpty(sys))
                    ctx.Blackboard.Set("home_system", sys);
            }
            var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
            if (btn == null) return NodeStatus.Failure;
            ctx.Click(btn);
            ctx.Wait(TimeSpan.FromSeconds(10));
            return NodeStatus.Success;
        });

    private static InventoryWindow? FindOreHoldWindow(BotContext ctx)
    {
        var invWindows = ctx.GameState.ParsedUI.InventoryWindows;
        
        // 1. Precise type match (now more robust due to parser updates)
        var byType = invWindows.FirstOrDefault(w => w.HoldType == InventoryHoldType.Mining);
        if (byType != null) return byType;

        // 2. Fallback: Check if any inventory window has "Ore Hold" or "Mining Hold" SELECTED in sidebar
        // even if the window title didn't tell us enough.
        foreach (var w in invWindows)
        {
            if (w.NavEntries.Any(e => e.IsSelected && e.HoldType == InventoryHoldType.Mining))
                return w;
        }

        // 3. Broad text match on caption (handles "Ore Hold", "Mining Hold", "Mining Frigate Hold", etc.)
        return invWindows.FirstOrDefault(w => 
            w.SubCaptionLabelText?.Contains("ore",    StringComparison.OrdinalIgnoreCase) == true ||
            w.SubCaptionLabelText?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true ||
            w.SubCaptionLabelText?.Contains("hold",   StringComparison.OrdinalIgnoreCase) == true);
    }

    private bool IsOreHoldFull(BotContext ctx)
    {
        var w = FindOreHoldWindow(ctx);
        return w?.CapacityGauge?.FillPercent >= OreHoldFullPercent;
    }
}
