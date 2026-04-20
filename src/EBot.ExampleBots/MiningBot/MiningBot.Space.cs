using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    private static IBehaviorNode NavigateToMiningHold() =>
        new ActionNode("Search and select hold", ctx =>
        {
            if (!ctx.GameState.IsInSpace || FindOreHoldWindow(ctx) != null) return NodeStatus.Success;
            var anyInv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
            if (anyInv == null) { ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]); ctx.Wait(TimeSpan.FromSeconds(1.5)); return NodeStatus.Running; }
            var oreEntry = anyInv.NavEntries.FirstOrDefault(e => e.HoldType == InventoryHoldType.Mining || e.Label?.Contains("ore", StringComparison.OrdinalIgnoreCase) == true);
            if (oreEntry != null) { ctx.Click(oreEntry.UINode); ctx.Wait(TimeSpan.FromSeconds(1)); return NodeStatus.Success; }
            ctx.KeyPress(VirtualKey.C, [VirtualKey.Alt]); return NodeStatus.Running;
        });

    private IBehaviorNode BT_MineAtBelt() =>
        new ActionNode("Mine at belt", ctx =>
        {
            if (!AnyAsteroidsInOverview(ctx)) return NodeStatus.Failure;

            var world = ctx.Blackboard.Get<WorldState>("world")!;
            var ui    = ctx.GameState.ParsedUI;
            var range = world.LaserRangeM > 0 ? world.LaserRangeM : 15000;

            // 1. Propulsion & Positioning (Approach Primary)
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
                    ctx.Click(best.UINode, [VirtualKey.Q]);
                    ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                }
            }

            // 2. Sequential Locking (OVERVIEW ONLY)
            if (ctx.Blackboard.IsCooldownReady("lock_asteroid"))
            {
                // Target 1: Primary
                if (best != null && !best.IsLocked && !best.IsLockPending)
                {
                    ctx.Log($"[Mining] Locking Primary from Overview: {best.Name}");
                    ctx.Click(best.UINode, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                    var pending = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
                    pending[best.UINode.Node.PythonObjectAddress] = DateTimeOffset.UtcNow;
                    ctx.Blackboard.Set("assumed_locked", pending);
                }
                // Target 2+: If we have idle lasers and free HUD slots, lock another one
                else if (ui.Targets.Count < world.TotalLaserCount)
                {
                    var secondary = world.Asteroids
                        .Where(a => !a.IsLocked && !a.IsLockPending && a.DistanceM < range + 2000)
                        .OrderByDescending(a => a.Score)
                        .FirstOrDefault();

                    if (secondary != null)
                    {
                        ctx.Log($"[Mining] Locking Secondary from Overview: {secondary.Name}");
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
        var idleLasers = allLasers.Where(m => m.IsActive != true && !m.IsBusy && ctx.Blackboard.IsCooldownReady($"fire_module_{m.UINode.Node.PythonObjectAddress}")).ToList();
        if (idleLasers.Count == 0) return;

        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();
        
        // Cleanup stale assignments using HUD addresses only
        var currentHudAddresses = new HashSet<string>(ui.Targets.Select(t => t.UINode.Node.PythonObjectAddress));
        foreach (var key in assignments.Keys.ToList())
            if (!currentHudAddresses.Contains(assignments[key])) assignments.Remove(key);

        foreach (var laser in idleLasers)
        {
            var assignedHudAddresses = new HashSet<string>(assignments.Values);
            
            // Find any circle in HUD that isn't being mined yet
            var targetToFire = ui.Targets.FirstOrDefault(t => !assignedHudAddresses.Contains(t.UINode.Node.PythonObjectAddress));
                
            if (targetToFire != null)
            {
                ctx.Log($"[Mining] Firing {laser.Name} at HUD target circle.");
                ctx.Click(targetToFire.UINode);
                ctx.Wait(TimeSpan.FromMilliseconds(650));
                ctx.Click(laser.UINode);
                
                assignments[allLasers.IndexOf(laser)] = targetToFire.UINode.Node.PythonObjectAddress;
                ctx.Blackboard.Set("laser_targets", assignments);
                ctx.Blackboard.SetCooldown($"fire_module_{laser.UINode.Node.PythonObjectAddress}", TimeSpan.FromSeconds(12));
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
        var mid = shipUI.ModuleButtonsRows.Middle.Where(m => !m.IsOffline).ToList();
        return mid.FirstOrDefault(m => m.Name != null && (m.Name.Contains("Afterburner") || m.Name.Contains("Microwarpdrive") || m.Name.Contains("MWD")));
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
