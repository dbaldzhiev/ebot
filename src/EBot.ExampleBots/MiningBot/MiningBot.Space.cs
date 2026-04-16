using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    private IBehaviorNode HandleInSpace() =>
        new SequenceNode("In space",
            new ConditionNode("Is in space?", ctx => ctx.GameState.IsInSpace),
            new SelectorNode("Space actions",
                WaitCapRegen(),
                ReturnToStation(),
                BT_DroneSecurity(),
                NavigateToMiningHold(),
                DiscoverBeltsOnce(),
                BT_MineAtBelt(),
                WarpToBelt()));

    private static IBehaviorNode WaitCapRegen() =>
        new SequenceNode("Capacitor regen",
            new ConditionNode("Capacitor low?", ctx =>
                (ctx.GameState.ParsedUI.ShipUI?.Capacitor?.LevelPercent ?? 100) < 20),
            new ActionNode("Wait for regen", _ => NodeStatus.Running));

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
                    return NodeStatus.Success;
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
                        return NodeStatus.Success;
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
                return NodeStatus.Success;
            }));

    private IBehaviorNode BT_MineAtBelt() =>
        new SequenceNode("Mine at belt",
            new ConditionNode("Asteroids visible?", ctx => AnyAsteroidsInOverview(ctx)),
            new SelectorNode("Mining tasks",
                BT_AcquireLaserRange(),
                BT_ApproachPrimaryAsteroid(),
                BT_MaintainTargets(),
                BT_ActivateLasers()
            ));

    // ─── Sub-Tree: Laser Range ───────────────────────────────────────────────

    private static IBehaviorNode BT_AcquireLaserRange() =>
        new SequenceNode("Acquire laser range",
            new ConditionNode("Range unknown?", ctx => !ctx.Blackboard.Has("laser_range_m")),
            new ConditionNode("Fetch cooldown?", ctx => ctx.Blackboard.IsCooldownReady("range_fetch")),
            new SelectorNode("Detection",
                new SequenceNode("Read from tooltip",
                    new ConditionNode("Tooltip present?", ctx => ctx.GameState.ParsedUI.ModuleButtonTooltip != null),
                    new ActionNode("Store range", ctx =>
                    {
                        var tt = ctx.GameState.ParsedUI.ModuleButtonTooltip!;
                        var meters = tt.OptimalRangeMeters.HasValue
                            ? (double)tt.OptimalRangeMeters.Value
                            : (tt.OptimalRangeText != null ? ParseDistanceM(tt.OptimalRangeText) : null);
                        
                        if (meters.HasValue)
                        {
                            ctx.Blackboard.Set("laser_range_m", meters.Value);
                            ctx.Log($"[Mining] Laser range detected: {meters.Value / 1000:F1} km");
                            return NodeStatus.Success;
                        }
                        return NodeStatus.Failure;
                    })),
                new ActionNode("Hover first laser", ctx =>
                {
                    var laser = GetMiningModules(ctx.GameState.ParsedUI.ShipUI!).FirstOrDefault();
                    if (laser == null) return NodeStatus.Failure;
                    ctx.Log("[Mining] Hovering laser to acquire range tooltip");
                    ctx.Hover(laser.UINode);
                    ctx.Blackboard.SetCooldown("range_fetch", TimeSpan.FromSeconds(5));
                    return NodeStatus.Success;
                })
            ));

    // ─── Sub-Tree: Navigation & Approach ─────────────────────────────────────

    private static IBehaviorNode BT_ApproachPrimaryAsteroid() =>
        new SequenceNode("Approach primary target",
            new ActionNode("Approach logic", ctx =>
            {
                var world = ctx.Blackboard.Get<WorldState>("world")!;
                var best = world.PrimaryTarget;
                if (best == null) return NodeStatus.Failure;

                var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
                var safeRange = range - 2000; // Aim for a buffer inside optimal

                // 1. If already close enough, we don't need to approach
                if (best.DistanceM < safeRange) return NodeStatus.Failure;

                // 2. Manage Propulsion (AB/MWD) - Do this early
                if (!world.HasPropulsionActive)
                {
                    var prop = FindPropulsionModule(ctx.GameState.ParsedUI.ShipUI!);
                    if (prop != null && !prop.IsBusy)
                    {
                        ctx.Log($"[Mining] Activating {prop.Name} to close distance to {best.Name}");
                        ctx.Click(prop.UINode);
                    }
                }

                // 3. Issue Approach command if not already moving fast
                if (world.ShipSpeed < 20 && ctx.Blackboard.IsCooldownReady("approach_cmd"))
                {
                    ctx.Log($"[Mining] Approaching {best.Name} ({best.DistanceText})");
                    ctx.Click(best.UINode, [VirtualKey.Q]); // Q+Click = Approach
                    ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                }

                // 4. Attempt to lock while approaching if not already locked
                if (!best.IsLocked && ctx.Blackboard.IsCooldownReady("lock_asteroid"))
                {
                    ctx.Log($"[Mining] Attempting to lock {best.Name} while approaching");
                    ctx.Click(best.UINode, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                }

                // RETURN FAILURE so the selector can proceed to BT_ActivateLasers
                return NodeStatus.Failure;
            }));

    // ─── Sub-Tree: Target Management ─────────────────────────────────────────

    private static IBehaviorNode BT_MaintainTargets() =>
        new SequenceNode("Maintain targets",
            new ActionNode("Locking logic", ctx =>
            {
                var world = ctx.Blackboard.Get<WorldState>("world")!;
                if (world.IdleLaserCount == 0) return NodeStatus.Failure;
                
                var lockedAsteroids = world.Asteroids.Count(a => a.IsLocked);
                if (lockedAsteroids >= world.TotalLaserCount) return NodeStatus.Failure;

                if (!ctx.Blackboard.IsCooldownReady("lock_asteroid")) return NodeStatus.Failure;

                var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
                
                // Pick best asteroid that isn't locked and is reasonably reachable
                var next = world.Asteroids
                    .Where(a => !a.IsLocked && a.DistanceM < range + 5000)
                    .OrderByDescending(a => a.Value)
                    .FirstOrDefault();

                if (next == null) return NodeStatus.Failure;

                ctx.Log($"[Mining] Locking next target: {next.Name} at {next.DistanceText}");
                ctx.Click(next.UINode, [VirtualKey.Control]);
                ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                
                // RETURN FAILURE so the selector can proceed to BT_ActivateLasers
                return NodeStatus.Failure;
            }));

    // ─── Sub-Tree: Laser Activation ──────────────────────────────────────────

    private static IBehaviorNode BT_ActivateLasers() =>
        new ActionNode("Activate lasers", ctx =>
        {
            var world = ctx.Blackboard.Get<WorldState>("world")!;
            if (world.IdleLaserCount == 0) return NodeStatus.Failure;

            var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;
            var firingRange = range - 1000; // 1km safety buffer

            // Match idle lasers to locked targets that are in range
            var idleLasers = GetMiningModules(ctx.GameState.ParsedUI.ShipUI!)
                .Where(m => m.IsActive != true && !m.IsBusy)
                .ToList();

            var reachableTargets = world.Asteroids
                .Where(a => a.IsLocked && a.DistanceM < firingRange)
                .ToList();

            if (reachableTargets.Count == 0) return NodeStatus.Failure;

            // Simple 1:1 mapping logic
            // For this tick, just fire the first available laser at the first available target
            var laser = idleLasers.First();
            var target = reachableTargets.First();

            ctx.Log($"[Mining] Firing {laser.Name} at {target.Name} ({target.DistanceText})");
            ctx.Click(target.UINode);
            ctx.Click(laser.UINode);
            
            return NodeStatus.Success; // One laser per tick for reliability
        });

    // ─── Sub-Tree: Drone Security ────────────────────────────────────────────

    private static IBehaviorNode BT_DroneSecurity() =>
        new SelectorNode("Drone security",
            new SequenceNode("Launch drones if attacked",
                new ConditionNode("Attacked?", ctx => 
                    ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries).Any(e => e.IsAttackingMe)),
                new ActionNode("Launch drones", ctx =>
                {
                    var bay = ctx.GameState.ParsedUI.DronesWindow?.DronesInBay;
                    if (bay == null || (bay.QuantityCurrent ?? 0) == 0) return NodeStatus.Failure;
                    
                    ctx.Log("[Defense] Under attack! Launching drones.");
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    return NodeStatus.Success;
                })),
            new SequenceNode("Recall if safe",
                new ConditionNode("Safe?", ctx => 
                    !ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries).Any(e => e.IsAttackingMe)),
                new ConditionNode("Drones out?", ctx => 
                    (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0),
                new ActionNode("Recall drones", ctx =>
                {
                    ctx.Log("[Defense] Belt clear. Recalling drones.");
                    ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
                    return NodeStatus.Success;
                }))
        );

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
                m.Name.Contains("Modulated", StringComparison.OrdinalIgnoreCase));

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
            m.Name.Contains("MWD",            StringComparison.OrdinalIgnoreCase)));
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
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
    }
}
