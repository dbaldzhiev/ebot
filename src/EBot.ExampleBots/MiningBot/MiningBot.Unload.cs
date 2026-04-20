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
                ctx => ctx.Blackboard.Get<bool>("needs_unload") || IsOreHoldFull(ctx)),
            new ActionNode("Unload state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("unload_phase") ?? "";
                var ticks = ctx.Blackboard.Get<int>("unload_ticks");
                ctx.Blackboard.Set("unload_ticks", ticks + 1);

                void Progress(string next) { ctx.Blackboard.Set("unload_phase", next); ctx.Blackboard.Set("unload_ticks", 0); }

                switch (phase)
                {
                    case "":
                        if (ctx.GameState.ParsedUI.InventoryWindows.Any())
                        {
                            ctx.Log("[Mining] Unload: Inventory already open.");
                            Progress("find_orehold");
                        }
                        else
                        {
                            ctx.Log("[Mining] Unload: Opening inventory (Alt+C)");
                            ctx.KeyPress(VirtualKey.C, VirtualKey.Alt);
                            ctx.Wait(TimeSpan.FromSeconds(1.5));
                            Progress("find_orehold");
                        }
                        return NodeStatus.Running;

                    case "find_orehold":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold != null)
                        {
                            ctx.Log($"[Mining] Unload: Found {oreHold.HoldType} hold.");
                            
                            // If it's already empty, we are DONE. Clear the flag immediately.
                            if (oreHold.Items.Count == 0) 
                            { 
                                ctx.Log("[Mining] Unload: Hold is already empty. Clearing needs_unload flag.");
                                FinishUnload(ctx, 0); 
                                return NodeStatus.Success; 
                            }
                            
                            Progress("stack_all");
                            return NodeStatus.Running;
                        }

                        // Not found. Check if ANY inventory is open.
                        var anyInv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
                        if (anyInv == null)
                        {
                            if (ticks > 5) { ctx.Log("[Mining] Unload: Inventory not found, retrying toggle."); ctx.KeyPress(VirtualKey.C, VirtualKey.Alt); ctx.Blackboard.Set("unload_ticks", 0); }
                            return NodeStatus.Running;
                        }

                        // If docked, we might need to expand "Active Ship" or select "Inventory" tab
                        if (ctx.GameState.IsDocked)
                        {
                            var activeShipNode = anyInv.NavEntries.FirstOrDefault(e => 
                                e.Label?.Contains("Active Ship", StringComparison.OrdinalIgnoreCase) == true ||
                                e.Label?.Contains("Retriever", StringComparison.OrdinalIgnoreCase) == true ||
                                e.Label?.Contains("Venture",   StringComparison.OrdinalIgnoreCase) == true);
                            
                            if (activeShipNode != null && ctx.Blackboard.IsCooldownReady("expand_ship"))
                            {
                                ctx.Log($"[Mining] Unload: Docked. Expanding '{activeShipNode.Label}' in sidebar.");
                                ctx.Click(activeShipNode.UINode);
                                ctx.Blackboard.SetCooldown("expand_ship", TimeSpan.FromSeconds(3));
                                return NodeStatus.Running;
                            }
                        }

                        // Inventory is open but Ore Hold not found in sidebar?
                        var oreEntry = anyInv.NavEntries.FirstOrDefault(e => e.HoldType == InventoryHoldType.Mining);
                        if (oreEntry != null)
                        {
                            ctx.Log($"[Mining] Unload: Clicking '{oreEntry.Label}' in sidebar.");
                            ctx.Click(oreEntry.UINode);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            return NodeStatus.Running;
                        }

                        // Try to find a link in the tree as last resort
                        var link = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                            (n.GetAllContainedDisplayTexts().Any(t => t.Contains("Ore Hold", StringComparison.OrdinalIgnoreCase)) ||
                             n.GetAllContainedDisplayTexts().Any(t => t.Contains("ShipGeneralMiningHold", StringComparison.OrdinalIgnoreCase)))
                            && n.Region.Width > 10 && n.Region.Height > 6);
                        
                        if (link != null) 
                        { 
                            ctx.Log("[Mining] Unload: Clicking Ore Hold link in UI tree.");
                            ctx.Click(link); 
                            ctx.Wait(TimeSpan.FromMilliseconds(700)); 
                        }
                        else if (ticks > 15)
                        {
                            ctx.Log("[Mining] Unload: Cannot find Ore Hold. Giving up on this cycle.");
                            FinishUnload(ctx, 0);
                            return NodeStatus.Failure;
                        }
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
                        if (oreHold == null) return NodeStatus.Running; // inventory still loading
                        if (oreHold.Items.Count == 0)
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
                        if (oreHold == null) return NodeStatus.Running; // inventory still loading
                        if (oreHold.Items.Count == 0)
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
                        
                        var menu  = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        
                        // Look for the specific "Item Hangar" or "Station Hangar" target
                        var targetEntry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Item Hangar", StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Station Hangar", StringComparison.OrdinalIgnoreCase) == true);
                        
                        // Fallback to "Move To..." or "Move All"
                        var moveEntry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Move To",  StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Move All", StringComparison.OrdinalIgnoreCase) == true);

                        var entry = targetEntry ?? moveEntry;

                        if (entry != null)
                        {
                            ctx.Log($"[Mining] Unload: Clicking '{entry.Text}'");
                            ctx.Click(entry.UINode);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            Progress("verify");
                        }
                        else
                        {
                            // If menu action fails, try Drag and Drop fallback
                            ctx.Log("[Mining] Unload: Menu action not found. Attempting drag and drop to sidebar.");
                            var anyInv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
                            var hangarEntry = anyInv?.NavEntries.FirstOrDefault(e =>
                                e.Label?.Contains("Item hangar",    StringComparison.OrdinalIgnoreCase) == true ||
                                e.Label?.Contains("Station hangar", StringComparison.OrdinalIgnoreCase) == true);

                            var oreHold = FindOreHoldWindow(ctx);
                            if (hangarEntry != null && oreHold != null && oreHold.Items.Count > 0)
                            {
                                ctx.Log($"[Mining] Unload: Dragging items from Mining Hold to '{hangarEntry.Label}'");
                                ctx.Drag(oreHold.Items[0].UINode, hangarEntry.UINode);
                                ctx.Wait(TimeSpan.FromSeconds(1.5));
                                Progress("verify");
                            }
                            else
                            {
                                ctx.Log("[Mining] Unload: Drag and drop fallback failed (hangar or items not found).");
                                ctx.KeyPress(VirtualKey.Escape);
                                Progress("open_menu");
                            }
                        }
                        return NodeStatus.Running;
                    }

                    case "verify":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null) return NodeStatus.Running; // inventory still loading — don't treat as empty
                        if (oreHold.Items.Count > 0)
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
        ctx.Blackboard.Set("belt_prop_started", false);
    }

    private void SyncStats(BotContext ctx)
    {
        ctx.Blackboard.Set("total_unloaded_m3", _totalUnloadedM3);
        ctx.Blackboard.Set("unload_cycles",     _unloadCycles);
    }

    // ─── Remember station and undock ────────────────────────────────────────

    private IBehaviorNode RememberStationAndUndock() =>
        new ActionNode("Remember home + undock", ctx =>
        {
            if (ctx.Blackboard.Get<bool>("bot_cancelled"))
            {
                ctx.Log("[Mining] Bot execution is CANCELLED. Staying docked.");
                return NodeStatus.Failure;
            }

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
            if (!ctx.Blackboard.IsCooldownReady("undock_cd")) return NodeStatus.Success;
            // Safety: never undock with a full ore hold — PerformUnload should have caught this,
            // but guard here in case the inventory window was closed before verify completed.
            if (IsOreHoldFull(ctx))
            {
                ctx.Log("[Mining] Undock blocked — ore hold still full. Re-triggering unload.");
                ctx.Blackboard.Set("needs_unload", true);
                ctx.Blackboard.Set("unload_phase", "");
                return NodeStatus.Failure;
            }
            ctx.Click(btn);
            ctx.Wait(TimeSpan.FromSeconds(10));
            ctx.Blackboard.SetCooldown("undock_cd", TimeSpan.FromSeconds(20));
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
        var byCaption = invWindows.FirstOrDefault(w =>
            (w.SubCaptionLabelText?.Contains("ore",    StringComparison.OrdinalIgnoreCase) == true ||
             w.SubCaptionLabelText?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true) &&
             w.SubCaptionLabelText?.Contains("cargo",  StringComparison.OrdinalIgnoreCase) != true);
        if (byCaption != null) return byCaption;

        // 4. BRUTE FORCE: Final fallback for when memory reader fails to map captions
        // Scan the UI tree for any visible text node saying "Mining Hold" or "Ore Hold"
        var link = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
            (n.GetAllContainedDisplayTexts().Any(t => t.Contains("Mining Hold", StringComparison.OrdinalIgnoreCase)) ||
             n.GetAllContainedDisplayTexts().Any(t => t.Contains("Ore Hold", StringComparison.OrdinalIgnoreCase)) ||
             n.GetAllContainedDisplayTexts().Any(t => t.Contains("ShipGeneralMiningHold", StringComparison.OrdinalIgnoreCase)))
            && n.Region.Width > 15 && n.Region.Height > 5);

        if (link != null)
        {
            // Find which InventoryWindow contains this text node
            return invWindows.FirstOrDefault(w => w.UINode.Region.Contains(link.Region.X + 5, link.Region.Y + 5));
        }

        return null;
    }

    private bool IsOreHoldFull(BotContext ctx)
    {
        var w = FindOreHoldWindow(ctx);
        if (w == null) return false;

        var pct = w.CapacityGauge?.FillPercent ?? 0;
        
        // Log if we are getting close or full
        if (pct >= 80)
        {
            if (ctx.Blackboard.IsCooldownReady("full_hold_log"))
            {
                ctx.Log($"[Mining] Mining hold status: {pct:F1}% full");
                ctx.Blackboard.SetCooldown("full_hold_log", TimeSpan.FromSeconds(30));
            }
        }

        if (pct >= OreHoldFullPercent)
        {
            ctx.Log($"[Mining] Mining hold is FULL ({pct:F1}%). Ready to unload.");
            return true;
        }

        return false;
    }
}
