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

                var oreEntry = anyInv.NavEntries.FirstOrDefault(e => 
                    e.Label?.Contains("ore",    StringComparison.OrdinalIgnoreCase) == true ||
                    e.Label?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true ||
                    e.HoldType == InventoryHoldType.Mining);

                if (oreEntry != null)
                {
                    if (oreEntry.IsSelected)
                    {
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

                ctx.Log("[Navigation] Mining hold not found in navigation panel. Toggling inventory.");
                ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]);
                ctx.Wait(TimeSpan.FromSeconds(1.5));
                return NodeStatus.Running;
            }));

    private IBehaviorNode BT_MineAtBelt() =>
        new ActionNode("Mine at belt", ctx =>
        {
            if (!AnyAsteroidsInOverview(ctx))
            {
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
                    
                    // Surveyor Scan Logic
                    bool haveAnyScores = world.Asteroids.Any(a => a.ValuePerM3 != null);
                    if ((best == null || !haveAnyScores) && ctx.Blackboard.IsCooldownReady("surveyor_scan_btn"))
                    {
                        if (ui.MiningScanResultsWindow == null)
                        {
                            if (ctx.Blackboard.IsCooldownReady("surveyor_toggle"))
                            {
                                ctx.KeyPress(VirtualKey.M);
                                ctx.Blackboard.SetCooldown("surveyor_toggle", TimeSpan.FromSeconds(5));
                            }
                            return NodeStatus.Running;
                        }
                        
                        if (ui.MiningScanResultsWindow.ScanButton != null)
                        {
                            ctx.Log("[Mining] Scanning Surveyor...");
                            ctx.Click(ui.MiningScanResultsWindow.ScanButton);
                            ctx.Blackboard.SetCooldown("surveyor_scan_btn", TimeSpan.FromSeconds(15));
                        }
                    }

                    if (best == null) return NodeStatus.Running; 

                    var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
                    var safeRange = 5000;
                    var propStartRange = 8000;

                    // 1. Propulsion Control
                    var prop = FindPropulsionModule(ui.ShipUI!);
                    if (prop != null && !prop.IsBusy && ctx.Blackboard.IsCooldownReady("prop_toggle"))
                    {
                        bool isPropActive = prop.IsActive == true;
                        if (best.DistanceM > propStartRange && !isPropActive)
                        {
                            ctx.Log($"[Mining] Approaching target ({best.DistanceText}) — activating propulsion.");
                            ctx.Click(prop.UINode);
                            ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5));
                        }
                        else if (best.DistanceM < safeRange && isPropActive)
                        {
                            ctx.Log($"[Mining] Near target — deactivating propulsion.");
                            ctx.Click(prop.UINode);
                            ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5));
                        }
                    }

                    // 1.5 Hard Break
                    if (best.DistanceM < 4500 && world.ShipSpeed > 10 && ctx.Blackboard.IsCooldownReady("hard_break"))
                    {
                        ctx.KeyPress(VirtualKey.Space, [VirtualKey.Control]);
                        ctx.Blackboard.SetCooldown("hard_break", TimeSpan.FromSeconds(10));
                    }

                    // 2. Approach command
                    if (best.DistanceM > safeRange && world.ShipSpeed < 20 && ctx.Blackboard.IsCooldownReady("approach_cmd"))
                    {
                        ctx.Log($"[Mining] Approaching {best.Name} ({best.DistanceText})");
                        ctx.Click(best.UINode, [VirtualKey.Q]);
                        ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                    }

                    // 3. Locking Logic (up to TotalLaserCount)
                    int currentLocks = ui.Targets.Count;
                    int pendingLocks = world.Asteroids.Count(a => a.IsLockPending);
                    
                    if (currentLocks + pendingLocks < world.TotalLaserCount && ctx.Blackboard.IsCooldownReady("lock_asteroid"))
                    {
                        var toLock = world.Asteroids
                            .Where(a => !a.IsLocked && !a.IsLockPending && a.DistanceM < range)
                            .OrderByDescending(a => a.Score)
                            .FirstOrDefault();

                        if (toLock != null)
                        {
                            ctx.Log($"[Mining] Locking: {toLock.Name} ({toLock.DistanceText})");
                            ctx.Click(toLock.UINode, [VirtualKey.Control]);
                            ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(3));
                            
                            var pending = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
                            pending[toLock.UINode.Node.PythonObjectAddress] = DateTimeOffset.UtcNow;
                            ctx.Blackboard.Set("assumed_locked", pending);
                        }
                    }

                    // 4. Laser Firing
                    TryFireIdleLasers(ctx, world, ui);

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
            }
            return NodeStatus.Running;
        });

    private static void TryFireIdleLasers(BotContext ctx, WorldState world, ParsedUI ui)
    {
        if (ui.ShipUI == null || ui.Targets.Count == 0) return;
        
        var allLasers = GetMiningModules(ui.ShipUI).ToList();
        var idleLasers = allLasers.Where(m => m.IsActive != true && !m.IsBusy && ctx.Blackboard.IsCooldownReady($"fire_module_{m.UINode.Node.PythonObjectAddress}")).ToList();
        if (idleLasers.Count == 0) return;

        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();
        
        // Cleanup stale assignments
        var currentHudAddresses = new HashSet<string>(ui.Targets.Select(t => t.UINode.Node.PythonObjectAddress));
        foreach (var key in assignments.Keys.ToList())
            if (!currentHudAddresses.Contains(assignments[key])) assignments.Remove(key);

        foreach (var laser in idleLasers)
        {
            var assignedHudAddresses = new HashSet<string>(assignments.Values);
            
            // Find a HUD target that doesn't have a laser
            var targetToFire = ui.Targets.FirstOrDefault(t => !assignedHudAddresses.Contains(t.UINode.Node.PythonObjectAddress));
                
            if (targetToFire != null)
            {
                ctx.Log($"[Mining] Explicitly selecting Target Bar entry for {laser.Name}");
                
                // 1. Select the target in HUD
                ctx.Click(targetToFire.UINode);
                
                // 2. Wait for EVE to register selection (Registration Heartbeat)
                ctx.Wait(TimeSpan.FromMilliseconds(650));
                
                // 3. Fire Laser
                ctx.Click(laser.UINode);
                
                assignments[allLasers.IndexOf(laser)] = targetToFire.UINode.Node.PythonObjectAddress;
                ctx.Blackboard.Set("laser_targets", assignments);
                ctx.Blackboard.SetCooldown($"fire_module_{laser.UINode.Node.PythonObjectAddress}", TimeSpan.FromSeconds(12));
                
                break; // One fire per tick
            }
        }
    }

    // ─── Sub-Tree: Drone Security ────────────────────────────────────────────

    private static IBehaviorNode BT_DroneSecurity() =>
        new ActionNode("Drone Defense", ctx =>
        {
            var ui = ctx.GameState.ParsedUI;
            var hostiles = ui.OverviewWindows.SelectMany(w => w.Entries)
                .Where(e => e.IsHostile || e.IsAttackingMe).ToList();

            var combatThreats = hostiles.Count > 0 && ui.CombatMessages.Any(m =>
                m.Contains("misses you",  StringComparison.OrdinalIgnoreCase) ||
                m.Contains("hits you",    StringComparison.OrdinalIgnoreCase) ||
                m.Contains("washes over", StringComparison.OrdinalIgnoreCase));

            var dronesInSpace = ui.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0;
            var dronesInBay   = ui.DronesWindow?.DronesInBay?.QuantityCurrent   ?? 0;
            bool underAttack  = hostiles.Count > 0 || combatThreats;

            if (underAttack)
            {
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
                    if (dronesInSpace == 0 && dronesInBay == 0) return NodeStatus.Failure;
                    return NodeStatus.Running;
                }
            }

            // Precautionary launch
            if (dronesInSpace == 0 && dronesInBay > 0 && ctx.GameState.IsInSpace)
            {
                var phase = ctx.Blackboard.Get<string>("mining_phase") ?? "";
                bool inMiningPhase = phase == "approach_lock" || phase == "fire_lasers";
                bool actuallyAtBelt = inMiningPhase && AnyAsteroidsInOverview(ctx);
                
                if (actuallyAtBelt && ctx.Blackboard.IsCooldownReady("drone_precaution_launch"))
                {
                    ctx.Log("[Defense] Belt arrival — launching drones (Shift+F).");
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    ctx.Blackboard.SetCooldown("drone_precaution_launch", TimeSpan.FromMinutes(10));
                    return NodeStatus.Running;
                }
            }

            // Recall drones
            if (dronesInSpace > 0 && !underAttack && !AnyAsteroidsInOverview(ctx) &&
                ctx.Blackboard.IsCooldownReady("drone_recall"))
            {
                ctx.Log("[Defense] Grid clear — recalling drones (Shift+R).");
                ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
                ctx.Blackboard.SetCooldown("drone_recall", TimeSpan.FromSeconds(10));
            }

            return NodeStatus.Failure; 
        });

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static int OreValueOf(OverviewEntry e)
    {
        var texts = e.UINode.GetAllContainedDisplayTexts().Select(t => t.ToLowerInvariant()).ToList();
        foreach (var (ore, val) in _oreValue)
            if (texts.Any(t => t.Contains(ore))) return val;
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
        foreach (var mod in GetMiningModules(shipUI).Where(m => m.IsActive == true))
        {
            ctx.Click(mod.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(200));
        }
        var prop = FindPropulsionModule(shipUI);
        if (prop != null && prop.IsActive == true)
        {
            ctx.Log("[Mining] Stopping propulsion.");
            ctx.Click(prop.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(200));
        }
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
    }
}
