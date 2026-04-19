using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    private static IBehaviorNode NavigateToMiningHold() =>
        new SequenceNode("Navigate to Mining Hold",
            new ConditionNode("Need to find mining hold?", ctx =>
                ctx.GameState.IsInSpace && FindOreHoldWindow(ctx) == null),
            new ActionNode("Search and select hold", ctx =>
            {
                var anyInv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
                if (anyInv == null)
                {
                    ctx.Log("[Navigation] Inventory not open. Pressing Alt+C.");
                    ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]);
                    ctx.Wait(TimeSpan.FromSeconds(1.5));
                    return NodeStatus.Running;
                }

                // Inventory is open but FindOreHoldWindow returned null (wrong hold selected)
                var oreEntry = anyInv.NavEntries.FirstOrDefault(e => 
                    e.Label?.Contains("ore",    StringComparison.OrdinalIgnoreCase) == true ||
                    e.Label?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true ||
                    e.HoldType == InventoryHoldType.Mining);

                if (oreEntry != null)
                {
                    if (oreEntry.IsSelected)
                    {
                        // It's already selected but parser didn't pick up the hold type?
                        // This shouldn't happen with the new parser, but as a safeguard:
                        ctx.Log("[Navigation] Mining hold entry is SELECTED but window not recognized. Toggling compact mode or inventory.");
                        ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]); // toggle
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        return NodeStatus.Running;
                    }

                    ctx.Log($"[Navigation] Switching to {oreEntry.Label} in inventory");
                    ctx.Click(oreEntry.UINode);
                    ctx.Wait(TimeSpan.FromSeconds(1));
                    return NodeStatus.Success;
                }

                // If we can't find it in the list, maybe try toggling it once
                ctx.Log("[Navigation] Mining hold not found in navigation panel. Toggling inventory.");
                ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]);
                ctx.Wait(TimeSpan.FromSeconds(1.5));
                return NodeStatus.Running;
            }));

    private IBehaviorNode BT_MineAtBelt() =>
        // ActionNode (not a SequenceNode) so it can return Failure when belt is empty,
        // allowing the StatelessSelectorNode parent to fall through to WarpToBelt.
        // A SequenceNode wrapper would retain _currentIndex=1 across ticks and skip
        // the asteroid re-check, blocking WarpToBelt even on an empty grid.
        new ActionNode("Mine at belt", ctx =>
        {
            if (!AnyAsteroidsInOverview(ctx))
            {
                // Belt exhausted — reset mining phase so next belt starts from scratch
                ctx.Blackboard.Remove("mining_phase");
                ctx.Blackboard.Set("mining_tick", 0);
                return NodeStatus.Failure;
            }

            var phase = ctx.Blackboard.Get<string>("mining_phase") ?? "open_surveyor";
                var world = ctx.Blackboard.Get<WorldState>("world")!;
                var ui    = ctx.GameState.ParsedUI;
                var ticks = ctx.Blackboard.Get<int>("mining_tick");
                ctx.Blackboard.Set("mining_tick", ticks + 1);

                void Progress(string next) { ctx.Blackboard.Set("mining_phase", next); ctx.Blackboard.Set("mining_tick", 0); }

                switch (phase)
                {
                    case "open_surveyor":
                    {
                        if (ui.MiningScanResultsWindow != null) { Progress("scan_surveyor"); return NodeStatus.Running; }
                        if (ctx.Blackboard.IsCooldownReady("surveyor_toggle"))
                        {
                            ctx.Log("[Mining] Opening Mining Surveyor (M)");
                            ctx.KeyPress(VirtualKey.M);
                            ctx.Blackboard.SetCooldown("surveyor_toggle", TimeSpan.FromSeconds(5));
                        }
                        return NodeStatus.Running;
                    }

                    case "scan_surveyor":
                    {
                        if (ui.MiningScanResultsWindow == null) { Progress("open_surveyor"); return NodeStatus.Running; }
                        
                        bool timerReady = ctx.Blackboard.IsCooldownReady("surveyor_scan_long");
                        bool isEmpty    = ui.MiningScanResultsWindow.Entries.Count == 0;

                        if (isEmpty || timerReady)
                        {
                            if (ui.MiningScanResultsWindow.ScanButton != null && ctx.Blackboard.IsCooldownReady("surveyor_scan_btn"))
                            {
                                ctx.Log("[Mining] Triggering Surveyor Scan");
                                ctx.Click(ui.MiningScanResultsWindow.ScanButton);
                                ctx.Blackboard.SetCooldown("surveyor_scan_btn", TimeSpan.FromSeconds(10));
                                ctx.Blackboard.SetCooldown("surveyor_scan_long", TimeSpan.FromSeconds(120 + Random.Shared.Next(-10, 10)));
                                ctx.Blackboard.Set("surveyor_scroll_done", false);
                            }
                            return NodeStatus.Running;
                        }
                        Progress("scroll_surveyor");
                        return NodeStatus.Running;
                    }

                    case "scroll_surveyor":
                    {
                        if (ui.MiningScanResultsWindow == null) { Progress("open_surveyor"); return NodeStatus.Running; }
                        
                        var groups = ui.MiningScanResultsWindow.Entries.Where(e => e.IsGroup).ToList();
                        var asteroids = ui.MiningScanResultsWindow.Entries.Where(e => !e.IsGroup).ToList();

                        if (groups.Count > 0 && ctx.Blackboard.IsCooldownReady("surveyor_log_groups"))
                        {
                            ctx.Log($"[Mining] Surveyor groups: {string.Join(", ", groups.Select(g => $"{(g.IsExpanded ? "*" : "")}{g.OreName}({g.ValuePerM3:F0})"))}");
                            ctx.Blackboard.SetCooldown("surveyor_log_groups", TimeSpan.FromSeconds(10));
                        }

                        var bestGroup = groups.OrderByDescending(g => g.ValuePerM3 ?? 0).FirstOrDefault();

                        if (bestGroup != null)
                        {
                            // 2. Collapse all groups that are NOT the best one
                            var toCollapse = groups.Where(g => g.IsExpanded && g.OreName != bestGroup.OreName).ToList();
                            if (toCollapse.Count > 0 && ctx.Blackboard.IsCooldownReady("surveyor_collapse"))
                            {
                                var g = toCollapse.First();
                                ctx.Log($"[Mining] Collapsing less valuable group: {g.OreName}");
                                if (g.ExpanderNode != null) ctx.Click(g.ExpanderNode);
                                ctx.Blackboard.SetCooldown("surveyor_collapse", TimeSpan.FromSeconds(2));
                                return NodeStatus.Running;
                            }

                            // 3. Expand the best group if it's collapsed
                            if (!bestGroup.IsExpanded && ctx.Blackboard.IsCooldownReady("surveyor_expand"))
                            {
                                ctx.Log($"[Mining] Expanding best group: {bestGroup.OreName} ({bestGroup.ValuePerM3:F1} ISK/m3)");
                                if (bestGroup.ExpanderNode != null) ctx.Click(bestGroup.ExpanderNode);
                                ctx.Blackboard.SetCooldown("surveyor_expand", TimeSpan.FromSeconds(2));
                                return NodeStatus.Running;
                            }
                        }

                        // 4. Scroll if we still don't have enough entries visible AND we haven't found asteroids in our best group yet
                        bool hasRocksInBestGroup = asteroids.Any(a => 
                            bestGroup != null && bestGroup.OreName != null &&
                            (a.OreName?.Contains(bestGroup.OreName, StringComparison.OrdinalIgnoreCase) == true ||
                             bestGroup.OreName.Contains(a.OreName ?? "", StringComparison.OrdinalIgnoreCase)));

                        if (!hasRocksInBestGroup && !ctx.Blackboard.Get<bool>("surveyor_scroll_done") && ctx.Blackboard.IsCooldownReady("surveyor_scroll"))
                        {
                            var scrollNode = ui.MiningScanResultsWindow.UINode.QueryFirst("@Scroll");
                            if (scrollNode != null)
                            {
                                ctx.Log("[Mining] Scrolling Surveyor to reveal rocks");
                                ctx.Scroll(scrollNode, -400); 
                                ctx.Blackboard.SetCooldown("surveyor_scroll", TimeSpan.FromSeconds(3));
                                if (ui.MiningScanResultsWindow.Entries.Count > 12) ctx.Blackboard.Set("surveyor_scroll_done", true);
                                return NodeStatus.Running;
                            }
                        }
                        Progress("approach_lock");
                        return NodeStatus.Running;
                    }

                    case "approach_lock":
                    {
                        if (!ctx.Blackboard.Has("laser_range_m")) { Progress("get_range"); return NodeStatus.Running; }
                        
                        var best = world.PrimaryTarget;
                        if (best == null) { Progress("scan_surveyor"); return NodeStatus.Running; }

                        var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
                        var safeRange = range - 2000;

                        // 1. Propulsion - Start it ONCE per belt visit when we arrive at a target
                        if (best.DistanceM > safeRange && !ctx.Blackboard.Get<bool>("belt_prop_started"))
                        {
                            var prop = FindPropulsionModule(ui.ShipUI!);
                            if (prop != null && !prop.IsBusy) 
                            { 
                                ctx.Log($"[Mining] Starting propulsion for belt operations."); 
                                ctx.Click(prop.UINode); 
                                ctx.Blackboard.Set("belt_prop_started", true);
                            }
                        }

                        // 2. Approach
                        if (best.DistanceM > safeRange)
                        {
                            if (world.ShipSpeed < 20 && ctx.Blackboard.IsCooldownReady("approach_cmd"))
                            {
                                ctx.Log($"[Mining] Approaching {best.Name} ({best.DistanceText}) via Surveyor");
                                // Prefer Surveyor node for approach interaction
                                ctx.Click(best.SurveyorUINode ?? best.UINode, [VirtualKey.Q]);
                                ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                            }
                            
                            // Periodic scan while approaching to update distances
                            if (ctx.Blackboard.IsCooldownReady("surveyor_scan_approach") && ui.MiningScanResultsWindow?.ScanButton != null)
                            {
                                ctx.Log("[Mining] Scanning to update distance while approaching...");
                                ctx.Click(ui.MiningScanResultsWindow.ScanButton);
                                ctx.Blackboard.SetCooldown("surveyor_scan_approach", TimeSpan.FromSeconds(6 + Random.Shared.Next(-2, 3)));
                            }
                        }

                        // 3. Lock
                        var lockedCount = world.Asteroids.Count(a => a.IsLocked);
                        if (lockedCount < world.TotalLaserCount && ctx.Blackboard.IsCooldownReady("lock_asteroid"))
                        {
                            var nextToLock = world.Asteroids
                                .Where(a => !a.IsLocked && a.DistanceM < range + 5000)
                                .OrderByDescending(a => a.Value).FirstOrDefault();

                            if (nextToLock != null)
                            {
                                ctx.Log($"[Mining] Locking {nextToLock.Name} (Distance={nextToLock.DistanceText}) via Surveyor (Ctrl+Click)");
                                // Use Surveyor node for locking interaction as requested.
                                // IMPORTANT: Use only Ctrl+Click. Do NOT use standard Click on Surveyor entries
                                // as it might trigger unwanted UI behaviors.
                                ctx.Click(nextToLock.SurveyorUINode ?? nextToLock.UINode, [VirtualKey.Control]);
                                ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(12));
                            }
                        }

                        if (world.Asteroids.Any(a => a.IsLocked && a.DistanceM < range)) Progress("fire_lasers");
                        return NodeStatus.Running;
                    }

                    case "get_range":
                    {
                        if (ui.ModuleButtonTooltip != null)
                        {
                            var meters = ui.ModuleButtonTooltip.OptimalRangeMeters ?? 
                                         (ui.ModuleButtonTooltip.OptimalRangeText != null ? ParseDistanceM(ui.ModuleButtonTooltip.OptimalRangeText) : null);
                            if (meters.HasValue) { ctx.Blackboard.Set("laser_range_m", meters.Value); Progress("approach_lock"); return NodeStatus.Running; }
                        }
                        var laser = GetMiningModules(ui.ShipUI!).FirstOrDefault();
                        if (laser != null) { ctx.Hover(laser.UINode); ctx.Wait(TimeSpan.FromSeconds(1)); }
                        else { ctx.Blackboard.Set("laser_range_m", 15000.0); Progress("approach_lock"); }
                        return NodeStatus.Running;
                    }

                    case "fire_lasers":
                    {
                        var idleLasers = GetMiningModules(ui.ShipUI!).Where(m => m.IsActive != true && !m.IsBusy).ToList();
                        var targets    = world.Asteroids.Where(a => a.IsLocked && a.DistanceM < world.LaserRangeM - 500).ToList();
                        
                        if (idleLasers.Count == 0) { if (ticks > 5) Progress("approach_lock"); return NodeStatus.Running; }
                        if (targets.Count == 0) { Progress("approach_lock"); return NodeStatus.Running; }

                        var unassignedInRangeCount = world.Asteroids.Count(a => !a.IsLocked && a.DistanceM < world.LaserRangeM + 5000);
                        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();
                        var allLasers   = GetMiningModules(ui.ShipUI!).ToList();
                        var activeIdx   = allLasers.Select((m, i) => new { m, i }).Where(x => x.m.IsActive == true).Select(x => x.i).ToHashSet();
                        foreach (var key in assignments.Keys.ToList()) if (!activeIdx.Contains(key)) assignments.Remove(key);

                        foreach (var laser in idleLasers)
                        {
                            var assignedIds = new HashSet<string>(assignments.Values);
                            var best = targets.OrderByDescending(a => a.Value).FirstOrDefault(a => !assignedIds.Contains(a.UINode.Node.PythonObjectAddress));
                            
                            // Fallback: If no unassigned locked targets exist, but we have locked targets,
                            // AND there are no other unlockable asteroids in range, put multiple lasers on the same target.
                            if (best == null && targets.Count > 0 && unassignedInRangeCount == 0)
                            {
                                best = targets.OrderByDescending(a => a.Value).First();
                            }

                            if (best != null)
                            {
                                ctx.Log($"[Mining] Firing {laser.Name} -> {best.Name}");
                                ctx.Click(best.TargetUINode ?? best.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(200));
                                ctx.Click(laser.UINode);
                                assignments[allLasers.IndexOf(laser)] = best.UINode.Node.PythonObjectAddress;
                                ctx.Blackboard.Set("laser_targets", assignments);
                                return NodeStatus.Running;
                            }
                        }
                        Progress("approach_lock");
                        return NodeStatus.Running;
                    }
                }
                return NodeStatus.Running;
            });

    // ─── Sub-Tree: Drone Security ────────────────────────────────────────────

    private static IBehaviorNode BT_DroneSecurity() =>
        new ActionNode("Drone Defense", ctx =>
        {
            var ui = ctx.GameState.ParsedUI;

            // Detection 1: Overview visible hostiles
            var hostiles = ui.OverviewWindows.SelectMany(w => w.Entries)
                .Where(e => e.IsHostile || e.IsAttackingMe).ToList();

            // Detection 2: Combat log messages
            var combatThreats = ui.CombatMessages.Any(m =>
                m.Contains("misses you",  StringComparison.OrdinalIgnoreCase) ||
                m.Contains("hits you",    StringComparison.OrdinalIgnoreCase) ||
                m.Contains("washes over", StringComparison.OrdinalIgnoreCase));

            var dronesInSpace = ui.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0;
            var dronesInBay   = ui.DronesWindow?.DronesInBay?.QuantityCurrent   ?? 0;
            bool underAttack  = hostiles.Count > 0 || combatThreats;

            if (underAttack)
            {
                // Step 1: Launch all drones if none are in space
                if (dronesInSpace == 0 && dronesInBay > 0 && ctx.Blackboard.IsCooldownReady("drone_launch"))
                {
                    ctx.Log("[Defense] COMBAT DETECTED! Launching drones (Shift+F).");
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    ctx.Blackboard.SetCooldown("drone_launch", TimeSpan.FromSeconds(10));
                    return NodeStatus.Running;
                }

                if (hostiles.Count > 0)
                {
                    var nearest = hostiles.OrderBy(h => h.DistanceInMeters ?? double.MaxValue).First();

                    // Step 2: Lock the nearest hostile if not already locked.
                    // Use name-based comparison — Target and OverviewEntry are in different UI panels
                    // and will never share a PythonObjectAddress.
                    bool alreadyLocked = ui.Targets.Any(t =>
                        !string.IsNullOrEmpty(t.TextLabel) && !string.IsNullOrEmpty(nearest.Name) &&
                        (t.TextLabel.Contains(nearest.Name, StringComparison.OrdinalIgnoreCase) ||
                         nearest.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)));

                    if (!alreadyLocked && ctx.Blackboard.IsCooldownReady("drone_lock"))
                    {
                        ctx.Log($"[Defense] Locking hostile: {nearest.Name}");
                        ctx.Click(nearest.UINode, VirtualKey.Control);
                        ctx.Blackboard.SetCooldown("drone_lock", TimeSpan.FromSeconds(5));
                    }

                    // Step 3: Order drones to engage once we have a locked target in space
                    if (dronesInSpace > 0 && ui.Targets.Count > 0 && ctx.Blackboard.IsCooldownReady("drone_engage"))
                    {
                        ctx.Log($"[Defense] Commanding drones to ENGAGE {nearest.Name}.");
                        ctx.KeyPress(VirtualKey.F);
                        ctx.Blackboard.SetCooldown("drone_engage", TimeSpan.FromSeconds(10));
                    }

                    // No drones available at all — we can't fight. Let mining continue;
                    // shield emergency will handle escape if damage reaches the threshold.
                    if (dronesInSpace == 0 && dronesInBay == 0) return NodeStatus.Failure;

                    return NodeStatus.Running;
                }

                // Combat messages but attacker not visible in overview — scroll to find them
                if (combatThreats && dronesInSpace > 0 && ctx.Blackboard.IsCooldownReady("drone_hunt_scroll"))
                {
                    ctx.Log("[Defense] Attack via combat log but attacker not in overview. Scrolling.");
                    var scroll = ui.OverviewWindows.FirstOrDefault()?.UINode.QueryFirst("@Scroll");
                    if (scroll != null) ctx.Scroll(scroll, -500);
                    ctx.Blackboard.SetCooldown("drone_hunt_scroll", TimeSpan.FromSeconds(3));
                    return NodeStatus.Running;
                }

                if (ctx.Blackboard.IsCooldownReady("drone_defense_warn"))
                {
                    ctx.Log("[Defense] Threat detected but no visible hostiles — proceeding with caution.");
                    ctx.Blackboard.SetCooldown("drone_defense_warn", TimeSpan.FromSeconds(30));
                }
            }

            // Precaution: launch drones when we arrive at a belt, before any shooting starts
            if (dronesInSpace == 0 && dronesInBay > 0)
            {
                var miningPhase = ctx.Blackboard.Get<string>("mining_phase") ?? "";
                bool atBelt = miningPhase != "" || AnyAsteroidsInOverview(ctx) || ui.MiningScanResultsWindow != null;
                if (atBelt && ctx.Blackboard.IsCooldownReady("drone_precaution_launch"))
                {
                    ctx.Log("[Defense] Belt arrival — launching drones as precaution (Shift+F).");
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    ctx.Blackboard.SetCooldown("drone_precaution_launch", TimeSpan.FromMinutes(10));
                    return NodeStatus.Running;
                }
            }

            // Recall drones when the grid is clear and we are leaving the belt
            if (dronesInSpace > 0 && !underAttack && !AnyAsteroidsInOverview(ctx) &&
                ctx.Blackboard.IsCooldownReady("drone_recall"))
            {
                ctx.Log("[Defense] Grid clear — recalling drones (Shift+R).");
                ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
                ctx.Blackboard.SetCooldown("drone_recall", TimeSpan.FromSeconds(10));
            }

            return NodeStatus.Failure; // Safe — let mining proceed
        });

    // ─── Navigation helpers (Old implementations retained for stability) ─────

    private static int OreValueOf(OverviewEntry e)
    {
        var texts = e.UINode.GetAllContainedDisplayTexts()
            .Select(t => t.ToLowerInvariant()).ToList();
        foreach (var (ore, val) in _oreValue)
            if (texts.Any(t => t.Contains(ore)))
                return val;
        return 0;
    }

    private static IEnumerable<ShipUIModuleButton> GetMiningModules(ShipUI shipUI)
    {
        static bool IsMiningName(ShipUIModuleButton m) =>
            m.Name != null && (
                m.Name.Contains("Mining",    StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Strip",     StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Laser",     StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Harvester", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Modulated", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Crystal",   StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Module 482", StringComparison.OrdinalIgnoreCase));

        var top = shipUI.ModuleButtonsRows.Top.Where(m => !m.IsOffline).ToList();
        var namedTop = top.Where(IsMiningName).ToList();
        if (namedTop.Count > 0) return namedTop;

        var allButtons = shipUI.ModuleButtons.Where(m => !m.IsOffline).ToList();
        var namedAny = allButtons.Where(IsMiningName).ToList();
        if (namedAny.Count > 0) return namedAny;

        return top;
    }

    private static ShipUIModuleButton? FindPropulsionModule(ShipUI shipUI)
    {
        var mid = shipUI.ModuleButtonsRows.Middle.Where(m => !m.IsOffline).ToList();
        var prop = mid.FirstOrDefault(m => m.Name != null && (
            m.Name.Contains("Afterburner",    StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Microwarpdrive", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("MWD",            StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Module 6001",    StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Module 2054",    StringComparison.OrdinalIgnoreCase)));
        return prop ?? mid.FirstOrDefault();
    }

    private static void StopAllModules(BotContext ctx)
    {
        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null) return;
        
        // Stop mining lasers
        foreach (var mod in GetMiningModules(shipUI).Where(m => m.IsActive == true))
        {
            ctx.Click(mod.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(200));
        }

        // Stop propulsion
        var prop = FindPropulsionModule(shipUI);
        if (prop != null && prop.IsActive == true)
        {
            ctx.Log("[Mining] Belt cycle ended. Stopping propulsion.");
            ctx.Click(prop.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(200));
        }
        
        ctx.Blackboard.Set("belt_prop_started", false);
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
    }
}
