using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    private static IBehaviorNode NavigateToMiningHold() =>
        new ActionNode("Search and select hold", ctx =>
        {
            if (!ctx.GameState.IsInSpace || FindOreHoldWindow(ctx) != null) return NodeStatus.Failure;
            var anyInv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
            if (anyInv == null) { ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]); ctx.Wait(TimeSpan.FromSeconds(1.5)); return NodeStatus.Running; }
            var oreEntry = anyInv.NavEntries.FirstOrDefault(e => e.HoldType == InventoryHoldType.Mining || 
                e.Label?.Contains("ore", StringComparison.OrdinalIgnoreCase) == true ||
                e.Label?.Contains("ShipGeneralMiningHold", StringComparison.OrdinalIgnoreCase) == true);
            if (oreEntry != null) { ctx.Click(oreEntry.UINode); ctx.Wait(TimeSpan.FromSeconds(1)); return NodeStatus.Success; }
            ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]); return NodeStatus.Running;
        });

    private static IBehaviorNode UnlockOutOfRangeTargets() =>
        new ActionNode("Unlock out-of-range targets", ctx =>
        {
            var ui = ctx.GameState.ParsedUI;
            if (ui.Targets.Count == 0) return NodeStatus.Failure;

            var range = ctx.Blackboard.Get<double>("laser_range_m", 0);
            if (range <= 0) range = DefaultLaserRangeM;

            var threshold = range + 500;

            var world = ctx.Blackboard.Get<WorldState>("world");
            var primaryName = world?.PrimaryTarget?.Name;

            // Build set of asteroid names currently in overview so we never unlock enemies
            var asteroidNames = new HashSet<string>(
                (world?.Asteroids.Select(a => a.Name) ?? []).Where(n => !string.IsNullOrEmpty(n))!,
                StringComparer.OrdinalIgnoreCase);

            var outOfRange = ui.Targets
                .Where(t => t.DistanceInMeters.HasValue && t.DistanceInMeters.Value > threshold)
                .Where(t =>
                {
                    // Never unlock the primary — it is being approached or mined
                    if (primaryName != null && t.TextLabel != null &&
                        (t.TextLabel.Contains(primaryName, StringComparison.OrdinalIgnoreCase) ||
                         primaryName.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    // Never unlock enemies / non-asteroids
                    if (t.TextLabel != null &&
                        !asteroidNames.Any(n => t.TextLabel.Contains(n, StringComparison.OrdinalIgnoreCase) ||
                                               n.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    return true;
                })
                .ToList();

            if (outOfRange.Count == 0) return NodeStatus.Failure;

            var target = outOfRange[0];
            ctx.Log($"[Mining] Unlocking out-of-range secondary '{target.TextLabel}' ({target.DistanceText}, range={range:F0} m)");
            ctx.Click(target.UINode, [VirtualKey.Shift, VirtualKey.Control]);
            ctx.Blackboard.SetCooldown("unlock_oor", TimeSpan.FromSeconds(3));
            return NodeStatus.Running;
        });

    private IBehaviorNode BT_MineAtBelt() =>
        new ActionNode("Mine at belt", ctx =>
        {
            if (!AnyAsteroidsInOverview(ctx)) return NodeStatus.Failure;

            var world = ctx.Blackboard.Get<WorldState>("world")!;
            var ui    = ctx.GameState.ParsedUI;
            var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;

            // Hover a mining module once to populate laser_range_m from tooltip
            if (world.LaserRangeM <= 0 && ui.ShipUI != null && ctx.Blackboard.IsCooldownReady("hover_laser_range"))
            {
                var laserForTooltip = GetMiningModules(ui.ShipUI).FirstOrDefault();
                if (laserForTooltip != null)
                {
                    ctx.Hover(laserForTooltip.UINode);
                    ctx.Blackboard.SetCooldown("hover_laser_range", TimeSpan.FromSeconds(8));
                }
            }

            // 1. Propulsion & Positioning
            var best = world.PrimaryTarget;
            if (best != null && ui.ShipUI != null)
            {
                var prop = FindPropulsionModule(ui.ShipUI);
                if (prop != null && !prop.IsBusy && ctx.Blackboard.IsCooldownReady("prop_toggle"))
                {
                    bool active = prop.IsActive == true;
                    if (best.DistanceM > 8000 && !active) { ctx.Click(prop.UINode); ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5)); }
                    else if (best.DistanceM < 5000 && active) { ctx.Click(prop.UINode); ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5)); }
                }
                if (best.DistanceM < 4500 && world.ShipSpeed > 10 && ctx.Blackboard.IsCooldownReady("hard_break"))
                {
                    ctx.KeyPress(VirtualKey.Space, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("hard_break", TimeSpan.FromSeconds(10));
                }
                if (best.DistanceM > 5000 && world.ShipSpeed < 20 && ctx.Blackboard.IsCooldownReady("approach_cmd"))
                {
                    // Pre-lock: approach via overview. Post-lock: approach via HUD target bar to avoid
                    // overlap with the overview-only locking logic and the unlock-loop.
                    UITreeNodeWithDisplayRegion approachNode = best.UINode;
                    if (best.IsLocked)
                    {
                        var hudMatch = ui.Targets.FirstOrDefault(t =>
                            t.TextLabel != null && best.Name != null &&
                            (t.TextLabel.Contains(best.Name, StringComparison.OrdinalIgnoreCase) ||
                             best.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)));
                        if (hudMatch != null) approachNode = hudMatch.UINode;
                    }
                    ctx.Click(approachNode, [VirtualKey.Q]);
                    ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                }
            }

            // 2. Sequential Locking — primary only when in range; secondaries fill idle laser slots
            if (ctx.Blackboard.IsCooldownReady("lock_asteroid"))
            {
                // Target 1: Primary — only lock when it's within laser range so we never need to unlock it
                if (best != null && !best.IsLocked && !best.IsLockPending && best.DistanceM <= range + 500)
                {
                    ctx.Log($"[Mining] Locking Primary (in range): {best.Name}");
                    ctx.Click(best.UINode, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                    var pending = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
                    pending[best.UINode.Node.PythonObjectAddress] = DateTimeOffset.UtcNow;
                    ctx.Blackboard.Set("assumed_locked", pending);
                }
                // Target 2+: fill idle laser slots with in-range secondaries
                else if (ui.Targets.Count < world.TotalLaserCount)
                {
                    var secondary = world.Asteroids
                        .Where(a => !a.IsLocked && !a.IsLockPending && a.DistanceM < range + 2000)
                        .OrderByDescending(a => a.Score)
                        .FirstOrDefault();

                    if (secondary != null)
                    {
                        ctx.Log($"[Mining] Locking Secondary (in range): {secondary.Name}");
                        ctx.Click(secondary.UINode, [VirtualKey.Control]);
                        ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                        var pending = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
                        pending[secondary.UINode.Node.PythonObjectAddress] = DateTimeOffset.UtcNow;
                        ctx.Blackboard.Set("assumed_locked", pending);
                    }
                }
            }

            // 3. Laser Firing (HUD ONLY)
            TryFireIdleLasers(ctx, world, ui);

            return NodeStatus.Running;
        });

    private static void TryFireIdleLasers(BotContext ctx, WorldState world, ParsedUI ui)
    {
        if (ui.ShipUI == null || ui.Targets.Count == 0) return;

        var allLasers = GetMiningModules(ui.ShipUI).ToList();

        // Refire check: if a laser was fired but never activated within 4 s, the fire was rejected
        // (e.g. target out of range). Clear the cooldown so it retries immediately next tick.
        var fireTimes = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("laser_fire_times") ?? new();
        foreach (var laser in allLasers)
        {
            var addr = laser.UINode.Node.PythonObjectAddress;
            if (laser.IsActive == true)
            {
                fireTimes.Remove(addr); // confirmed active — no longer tracking
            }
            else if (!laser.IsBusy && fireTimes.TryGetValue(addr, out var firedAt) &&
                     (DateTimeOffset.UtcNow - firedAt).TotalSeconds > 4)
            {
                ctx.Log($"[Mining] {laser.Name} did not activate after fire — clearing for retry.");
                fireTimes.Remove(addr);
                ctx.Blackboard.Remove($"fire_module_{addr}");
            }
        }
        ctx.Blackboard.Set("laser_fire_times", fireTimes);

        var idleLasers = allLasers.Where(m => m.IsActive != true && !m.IsBusy &&
            ctx.Blackboard.IsCooldownReady($"fire_module_{m.UINode.Node.PythonObjectAddress}")).ToList();
        if (idleLasers.Count == 0) return;

        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();

        // Cleanup stale assignments: HUD target gone, or laser is idle (fire didn't stick)
        var currentHudAddresses = new HashSet<string>(ui.Targets.Select(t => t.UINode.Node.PythonObjectAddress));
        var idleAddresses = new HashSet<string>(idleLasers.Select(l => l.UINode.Node.PythonObjectAddress));
        foreach (var key in assignments.Keys.ToList())
        {
            var laserAddr = key < allLasers.Count ? allLasers[key].UINode.Node.PythonObjectAddress : null;
            if (!currentHudAddresses.Contains(assignments[key]) ||
                (laserAddr != null && idleAddresses.Contains(laserAddr)))
                assignments.Remove(key);
        }

        foreach (var laser in idleLasers)
        {
            var assignedHudAddresses = new HashSet<string>(assignments.Values);

            var targetToFire = ui.Targets.FirstOrDefault(t => !assignedHudAddresses.Contains(t.UINode.Node.PythonObjectAddress));

            if (targetToFire != null)
            {
                ctx.Log($"[Mining] Firing {laser.Name} at HUD target circle.");
                ctx.Click(targetToFire.UINode);
                ctx.Wait(TimeSpan.FromMilliseconds(650));
                ctx.Click(laser.UINode);

                var laserAddr = laser.UINode.Node.PythonObjectAddress;
                assignments[allLasers.IndexOf(laser)] = targetToFire.UINode.Node.PythonObjectAddress;
                ctx.Blackboard.Set("laser_targets", assignments);
                ctx.Blackboard.SetCooldown($"fire_module_{laserAddr}", TimeSpan.FromSeconds(12));
                fireTimes[laserAddr] = DateTimeOffset.UtcNow;
                ctx.Blackboard.Set("laser_fire_times", fireTimes);
                break; // One per tick
            }
        }
    }

    private static IBehaviorNode BT_DroneSecurity() =>
        new ActionNode("Drone Defense", ctx =>
        {
            var ui = ctx.GameState.ParsedUI;
            var hostiles = ui.OverviewWindows.SelectMany(w => w.Entries).Where(e => e.IsHostile || e.IsAttackingMe).ToList();
            var dronesInSpace = ui.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0;
            var dronesInBay   = ui.DronesWindow?.DronesInBay?.QuantityCurrent   ?? 0;

            if (hostiles.Count > 0)
            {
                if (dronesInSpace == 0 && dronesInBay > 0 && ctx.Blackboard.IsCooldownReady("drone_launch"))
                {
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    ctx.Blackboard.SetCooldown("drone_launch", TimeSpan.FromSeconds(10));
                }
                var nearest = hostiles.OrderBy(h => h.DistanceInMeters ?? 1e9).First();
                bool locked = ui.Targets.Any(t => t.TextLabel != null && nearest.Name != null && t.TextLabel.Contains(nearest.Name, StringComparison.OrdinalIgnoreCase));
                if (!locked && ctx.Blackboard.IsCooldownReady("drone_lock"))
                {
                    ctx.Click(nearest.UINode, VirtualKey.Control);
                    ctx.Blackboard.SetCooldown("drone_lock", TimeSpan.FromSeconds(5));
                }
                if (dronesInSpace > 0 && ui.Targets.Count > 0 && ctx.Blackboard.IsCooldownReady("drone_engage"))
                {
                    ctx.KeyPress(VirtualKey.F);
                    ctx.Blackboard.SetCooldown("drone_engage", TimeSpan.FromSeconds(10));
                }
            }
            return NodeStatus.Failure; 
        });

    private static int OreValueOf(OverviewEntry e)
    {
        var texts = e.UINode.GetAllContainedDisplayTexts().Select(t => t.ToLowerInvariant()).ToList();
        foreach (var (ore, val) in _oreValue) if (texts.Any(t => t.Contains(ore))) return val;
        return 0;
    }

    private static IEnumerable<ShipUIModuleButton> GetMiningModules(ShipUI shipUI)
    {
        static bool IsMiningName(ShipUIModuleButton m) => m.Name != null && (m.Name.Contains("Mining") || m.Name.Contains("Strip") || m.Name.Contains("Laser") || m.Name.Contains("Harvester"));
        var top = shipUI.ModuleButtonsRows.Top.Where(m => !m.IsOffline).ToList();
        var namedTop = top.Where(IsMiningName).ToList();
        return namedTop.Count > 0 ? namedTop : shipUI.ModuleButtons.Where(m => !m.IsOffline && IsMiningName(m));
    }

    private static ShipUIModuleButton? FindPropulsionModule(ShipUI shipUI)
    {
        static bool IsPropName(ShipUIModuleButton m) => m.Name != null && (
            m.Name.Contains("Afterburner",   StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Microwarpdrive",StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("MWD",           StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Propulsion",    StringComparison.OrdinalIgnoreCase));

        var mid = shipUI.ModuleButtonsRows.Middle.Where(m => !m.IsOffline).ToList();

        var named = mid.FirstOrDefault(IsPropName);
        if (named != null) return named;

        // Fallback: if there is exactly one unidentified module in the mid rack it must be the prop
        var unnamed = mid.Where(m => m.Name == null || m.Name.StartsWith("Module ")).ToList();
        if (unnamed.Count == 1) return unnamed[0];

        // Last resort: scan all module buttons (handles rare row-parsing misplacements)
        return shipUI.ModuleButtons.FirstOrDefault(m => !m.IsOffline && IsPropName(m));
    }

    private static void StopAllModules(BotContext ctx)
    {
        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null) return;
        foreach (var mod in GetMiningModules(shipUI).Where(m => m.IsActive == true)) { ctx.Click(mod.UINode); ctx.Wait(TimeSpan.FromMilliseconds(200)); }
        var prop = FindPropulsionModule(shipUI);
        if (prop?.IsActive == true) { ctx.Click(prop.UINode); }
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
    }
}
