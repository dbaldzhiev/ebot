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
                    case "scan_surveyor":
                    case "scroll_surveyor":
                        Progress("approach_lock");
                        return NodeStatus.Running;

                    case "approach_lock":
                    {
                        if (!ctx.Blackboard.Has("laser_range_m")) { Progress("get_range"); return NodeStatus.Running; }
                        
                        var best = world.PrimaryTarget;
                        
                        // ON-DEMAND SURVEYOR SCORING:
                        // Trigger a scan if we have no best target (start or depleted) 
                        // OR if we lack value data for the top rocks and the scan is ready.
                        bool haveAnyScores = world.Asteroids.Any(a => a.ValuePerM3 != null);
                        bool needsScoring = best == null || (!haveAnyScores && ctx.Blackboard.IsCooldownReady("surveyor_scan_long"));

                        if (needsScoring) 
                        {
                            // 1. Ensure Surveyor is Open
                            if (ui.MiningScanResultsWindow == null)
                            {
                                if (ctx.Blackboard.IsCooldownReady("surveyor_toggle"))
                                {
                                    ctx.Log("[Mining] Opening Surveyor to score new targets (M)");
                                    ctx.KeyPress(VirtualKey.M);
                                    ctx.Blackboard.SetCooldown("surveyor_toggle", TimeSpan.FromSeconds(5));
                                }
                                return NodeStatus.Running;
                            }
                            
                            // 2. Trigger Scan
                            if (ctx.Blackboard.IsCooldownReady("surveyor_scan_btn") && ui.MiningScanResultsWindow.ScanButton != null)
                            {
                                ctx.Log("[Mining] Scanning Surveyor to recalculate target scores...");
                                ctx.Click(ui.MiningScanResultsWindow.ScanButton);
                                ctx.Blackboard.SetCooldown("surveyor_scan_btn", TimeSpan.FromSeconds(10));
                                ctx.Blackboard.SetCooldown("surveyor_scan_long", TimeSpan.FromMinutes(2)); // Don't scan too often
                                
                                // Reset overview scroll to top to find nearest rocks
                                var ovScroll = ui.OverviewWindows.FirstOrDefault()?.UINode.QueryFirst("@Scroll");
                                if (ovScroll != null) ctx.Scroll(ovScroll, 1000); 
                            }
                            
                            // 3. Hunt for rocks if Overview is empty
                            if (best == null && ctx.Blackboard.IsCooldownReady("overview_scroll_hunt"))
                            {
                                var ovScroll = ui.OverviewWindows.FirstOrDefault()?.UINode.QueryFirst("@Scroll");
                                if (ovScroll != null)
                                {
                                    ctx.Log("[Mining] Scrolling Overview to find new targets...");
                                    ctx.Scroll(ovScroll, -300);
                                    ctx.Blackboard.SetCooldown("overview_scroll_hunt", TimeSpan.FromSeconds(5));
                                }
                            }
                            
                            if (best == null) return NodeStatus.Running; 
                        }

                        var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
                        var safeRange = 5000;

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
                                ctx.Log($"[Mining] Approaching {best.Name} ({best.DistanceText}) via Overview");
                                ctx.Click(best.UINode, [VirtualKey.Q]);
                                ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                            }
                        }

                        // 3. Lock
                        var lockedCount = world.Asteroids.Count(a => a.IsLocked);
                        
                        // PROACTIVE: Try to fire lasers at any already-locked targets while approaching primary
                        TryFireIdleLasers(ctx, world, ui);

                        if (ctx.Blackboard.IsCooldownReady("lock_asteroid"))
                        {
                            // Target 1: Always lock the PrimaryTarget
                            if (!best.IsLocked)
                            {
                                ctx.Log($"[Mining] Locking Primary: {best.Name} ({best.DistanceText})");
                                ctx.Click(best.UINode, [VirtualKey.Control]);
                                ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(12));
                                
                                var assumed = ctx.Blackboard.Get<HashSet<string>>("assumed_locked") ?? new HashSet<string>();
                                assumed.Add(best.UINode.Node.PythonObjectAddress);
                                ctx.Blackboard.Set("assumed_locked", assumed);
                            }
                            // Target 2+: Lock secondary ONLY if Primary is < 5000m and there's another rock in range
                            else if (best.DistanceM <= safeRange && lockedCount < world.TotalLaserCount)
                            {
                                // Pick secondary target by Score, excluding Primary
                                var secondary = world.Asteroids
                                    .Where(a => !a.IsLocked && a.DistanceM < range + 2000)
                                    .OrderByDescending(a => a.Score)
                                    .FirstOrDefault();

                                if (secondary != null)
                                {
                                    ctx.Log($"[Mining] Locking Secondary: {secondary.Name} ({secondary.DistanceText})");
                                    ctx.Click(secondary.UINode, [VirtualKey.Control]);
                                    ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(12));

                                    var assumed = ctx.Blackboard.Get<HashSet<string>>("assumed_locked") ?? new HashSet<string>();
                                    assumed.Add(secondary.UINode.Node.PythonObjectAddress);
                                    ctx.Blackboard.Set("assumed_locked", assumed);
                                }
                            }
                        }

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
                        var best = world.PrimaryTarget;
                        if (best == null || !best.IsLocked || best.DistanceM > (world.LaserRangeM > 0 ? world.LaserRangeM : 15000)) { Progress("approach_lock"); return NodeStatus.Running; }

                        // Use helper to handle laser activation and reactivation
                        TryFireIdleLasers(ctx, world, ui);

                        if (world.IdleLaserCount == 0 && ticks > 10) Progress("approach_lock");
                        return NodeStatus.Running;
                    }
                }
                return NodeStatus.Running;
            });

    private static void TryFireIdleLasers(BotContext ctx, WorldState world, ParsedUI ui)
    {
        if (ui.ShipUI == null) return;
        
        var allLasers = GetMiningModules(ui.ShipUI).ToList();
        var idleLasers = allLasers.Where(m => m.IsActive != true && !m.IsBusy && ctx.Blackboard.IsCooldownReady($"fire_module_{m.UINode.Node.PythonObjectAddress}")).ToList();
        var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
        var targetsInRange = world.Asteroids.Where(a => a.IsLocked && a.DistanceM < range).ToList();

        if (idleLasers.Count == 0 || targetsInRange.Count == 0) return;

        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();
        
        // Cleanup old assignments for modules that are no longer active
        var activeIdx = allLasers.Select((m, i) => new { m, i }).Where(x => x.m.IsActive == true).Select(x => x.i).ToHashSet();
        foreach (var key in assignments.Keys.ToList()) 
            if (!activeIdx.Contains(key)) assignments.Remove(key);

        foreach (var laser in idleLasers)
        {
            var assignedIds = new HashSet<string>(assignments.Values);
            
            // 1. Primary Target gets first priority
            AsteroidEntity? targetToFire = null;
            if (world.PrimaryTarget != null && world.PrimaryTarget.IsLocked && world.PrimaryTarget.DistanceM < range)
            {
                if (!assignedIds.Contains(world.PrimaryTarget.UINode.Node.PythonObjectAddress))
                    targetToFire = world.PrimaryTarget;
            }
            
            // 2. Second priority: any OTHER locked target in range
            targetToFire ??= targetsInRange
                .OrderByDescending(a => a.Value)
                .FirstOrDefault(a => !assignedIds.Contains(a.UINode.Node.PythonObjectAddress));
                
            // 3. Fallback: if all locked targets already have lasers, put multiple on Primary
            targetToFire ??= world.PrimaryTarget;

            if (targetToFire != null)
            {
                ctx.Log($"[Mining] Firing {laser.Name} -> {targetToFire.Name}");
                ctx.Click(targetToFire.TargetUINode ?? targetToFire.UINode);
                ctx.Wait(TimeSpan.FromMilliseconds(200));
                ctx.Click(laser.UINode);
                
                assignments[allLasers.IndexOf(laser)] = targetToFire.UINode.Node.PythonObjectAddress;
                ctx.Blackboard.Set("laser_targets", assignments);
                
                // Prevent toggle spam: 10s cooldown for this specific module
                ctx.Blackboard.SetCooldown($"fire_module_{laser.UINode.Node.PythonObjectAddress}", TimeSpan.FromSeconds(10));
                
                // One fire per tick to be safe
                break;
            }
        }
    }

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
                        var enemyTarget = ui.Targets.FirstOrDefault(t =>
                            !string.IsNullOrEmpty(t.TextLabel) && !string.IsNullOrEmpty(nearest.Name) &&
                            (t.TextLabel.Contains(nearest.Name, StringComparison.OrdinalIgnoreCase) ||
                             nearest.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)));
                        
                        if (enemyTarget != null)
                        {
                            ctx.Log($"[Defense] Commanding drones to ENGAGE {nearest.Name}.");
                            if (!enemyTarget.IsActiveTarget)
                            {
                                ctx.Click(enemyTarget.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(200));
                            }
                            ctx.KeyPress(VirtualKey.F);
                            ctx.Blackboard.SetCooldown("drone_engage", TimeSpan.FromSeconds(10));
                        }
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
