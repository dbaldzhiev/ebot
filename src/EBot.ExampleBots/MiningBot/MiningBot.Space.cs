using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    // How far inside laser range we require before locking or stopping approach.
    // Keeps asteroids comfortably in range even if the ship drifts slightly.
    private const double RangeMarginM = 2000;

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

    private IBehaviorNode BT_MineAtBelt() =>
        new ActionNode("Mine at belt", ctx =>
        {
            if (!AnyAsteroidsInOverview(ctx)) return NodeStatus.Failure;

            var world = ctx.Blackboard.Get<WorldState>("world")!;
            var ui    = ctx.GameState.ParsedUI;
            var range = world.LaserRangeM > 0 ? world.LaserRangeM : DefaultLaserRangeM;

            // Comfortable lock/approach radius: well inside laser range so drift never breaks firing.
            var safeRange = range - RangeMarginM;

            // ── Range discovery: hover a laser once to read optimal range from tooltip ──
            if (world.LaserRangeM <= 0 && ui.ShipUI != null && ctx.Blackboard.IsCooldownReady("hover_laser_range"))
            {
                var laserForTooltip = GetMiningModules(ui.ShipUI).FirstOrDefault();
                if (laserForTooltip != null)
                {
                    ctx.Hover(laserForTooltip.UINode);
                    ctx.Blackboard.SetCooldown("hover_laser_range", TimeSpan.FromSeconds(8));
                }
            }

            // ── Step 1: Anchor management ─────────────────────────────────────────────
            // The anchor is the single asteroid we're flying towards.
            // Commit to it until it enters safe range or disappears — prevents oscillation
            // between targets when scores update mid-flight.
            var anchorAddr = ctx.Blackboard.Get<string>("mining_anchor");
            var anchor     = world.Asteroids.FirstOrDefault(a =>
                a.UINode.Node.PythonObjectAddress == anchorAddr);

            // Anchor becomes stale when: it's gone, or it's in safe range AND already locked
            // (we're mining it — nothing more to fly towards for this asteroid).
            bool anchorStale = anchor == null ||
                               (anchor.DistanceM <= safeRange && anchor.IsLocked);

            if (anchorStale)
            {
                // Pick the best unlocked asteroid as next anchor.
                // Prefer ones outside safe range (still need to fly there);
                // fall back to any unlocked if all are already close.
                var newAnchor = world.Asteroids
                    .Where(a => !a.IsLocked && !a.IsLockPending)
                    .OrderByDescending(a => a.DistanceM > safeRange)   // out-of-range first
                    .ThenByDescending(a => a.Score)
                    .FirstOrDefault();

                if (newAnchor != null && newAnchor.UINode.Node.PythonObjectAddress != anchorAddr)
                {
                    anchor = newAnchor;
                    ctx.Blackboard.Set("mining_anchor", anchor.UINode.Node.PythonObjectAddress);
                    if (anchorAddr != null)
                        ctx.Log($"[Mining] New anchor: {anchor.Name} @ {anchor.DistanceText}");
                }
            }

            // ── Step 2: Propulsion & approach ────────────────────────────────────────
            if (anchor != null && ui.ShipUI != null)
            {
                var prop = FindPropulsionModule(ui.ShipUI);
                if (prop != null && !prop.IsBusy && ctx.Blackboard.IsCooldownReady("prop_toggle"))
                {
                    bool active = prop.IsActive == true;
                    if      (anchor.DistanceM > 16_000 && !active) { ctx.Click(prop.UINode); ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5)); }
                    else if (anchor.DistanceM < 8_000  && active)  { ctx.Click(prop.UINode); ctx.Blackboard.SetCooldown("prop_toggle", TimeSpan.FromSeconds(5)); }
                }

                // Hard brake when very close at speed
                if (anchor.DistanceM < 5_500 && world.ShipSpeed > 10 && ctx.Blackboard.IsCooldownReady("hard_break"))
                {
                    ctx.KeyPress(VirtualKey.Space, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("hard_break", TimeSpan.FromSeconds(10));
                }

                // Approach only while anchor is outside safe range — stop issuing approach
                // commands once inside to avoid overshooting and drifting back out.
                if (anchor.DistanceM > safeRange && ctx.Blackboard.IsCooldownReady("approach_cmd"))
                {
                    ctx.Hover(anchor.UINode);
                    ctx.Wait(TimeSpan.FromMilliseconds(220));
                    var r = anchor.UINode.Region;
                    ctx.ClickAt(r.X + r.Width / 2, r.Y + r.Height / 4, VirtualKey.Q);
                    ctx.Blackboard.SetCooldown("approach_cmd", TimeSpan.FromSeconds(10));
                }
            }

            // ── Step 3: Lock best in-safe-range targets ───────────────────────────────
            // Lock up to TotalLaserCount asteroids, picking the highest-scoring ones
            // within safeRange. No distinction between "primary" and "secondary" —
            // any asteroid inside safe range is a valid target.
            if (ctx.Blackboard.IsCooldownReady("lock_asteroid"))
            {
                var toLock = world.Asteroids
                    .Where(a => !a.IsLocked && !a.IsLockPending && a.DistanceM <= safeRange)
                    .OrderByDescending(a => a.Score)
                    .FirstOrDefault();

                if (toLock != null && ui.Targets.Count < world.TotalLaserCount)
                {
                    ctx.Log($"[Mining] Locking: {toLock.Name} @ {toLock.DistanceText} (score {toLock.Score:F0})");
                    ctx.Click(toLock.UINode, [VirtualKey.Control]);
                    ctx.Blackboard.SetCooldown("lock_asteroid", TimeSpan.FromSeconds(4));
                    var pending = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new();
                    pending[toLock.UINode.Node.PythonObjectAddress] = DateTimeOffset.UtcNow;
                    ctx.Blackboard.Set("assumed_locked", pending);
                }
            }

            // ── Step 4: Fire idle lasers ──────────────────────────────────────────────
            TryFireIdleLasers(ctx, ui, safeRange);

            return NodeStatus.Running;
        });

    private static void TryFireIdleLasers(BotContext ctx, ParsedUI ui, double safeRange)
    {
        if (ui.ShipUI == null || ui.Targets.Count == 0) return;

        var allLasers   = GetMiningModules(ui.ShipUI).ToList();
        var fireTimes   = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("laser_fire_times") ?? new();
        var assignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new();
        var hudByAddr   = ui.Targets.ToDictionary(t => t.UINode.Node.PythonObjectAddress, t => t);

        // ── Refire / failure detection ────────────────────────────────────────────
        foreach (var laser in allLasers)
        {
            var addr     = laser.UINode.Node.PythonObjectAddress;
            var laserIdx = allLasers.IndexOf(laser);

            if (laser.IsActive == true)
            {
                // Laser cycling — clear retry state
                fireTimes.Remove(addr);
                ctx.Blackboard.Remove($"laser_retries_{addr}");
                continue;
            }

            if (laser.IsBusy) continue;

            if (!fireTimes.TryGetValue(addr, out var firedAt)) continue;
            if ((DateTimeOffset.UtcNow - firedAt).TotalSeconds < 4) continue;

            // Laser was fired but never activated — target probably out of range or depleted
            var retryKey = $"laser_retries_{addr}";
            var retries  = ctx.Blackboard.Get<int>(retryKey) + 1;
            fireTimes.Remove(addr);

            if (retries >= 3)
            {
                // Unlock the assigned HUD target so the locking loop picks a closer one
                if (assignments.TryGetValue(laserIdx, out var failedHudAddr) &&
                    hudByAddr.TryGetValue(failedHudAddr, out var failedTarget))
                {
                    ctx.Log($"[Mining] {laser.Name} failed {retries}× — unlocking '{failedTarget.TextLabel}', selecting new target.");
                    ctx.Click(failedTarget.UINode, [VirtualKey.Shift, VirtualKey.Control]);
                }
                else
                {
                    ctx.Log($"[Mining] {laser.Name} failed {retries}× — clearing assignment.");
                }
                assignments.Remove(laserIdx);
                ctx.Blackboard.Remove($"fire_module_{addr}");
                ctx.Blackboard.Set(retryKey, 0);
            }
            else
            {
                ctx.Log($"[Mining] {laser.Name} did not activate — retry {retries}/3.");
                ctx.Blackboard.Set(retryKey, retries);
            }
        }

        ctx.Blackboard.Set("laser_fire_times", fireTimes);

        // ── Cleanup stale assignments ─────────────────────────────────────────────
        var idleAddrs = allLasers
            .Where(m => m.IsActive != true && !m.IsBusy)
            .Select(m => m.UINode.Node.PythonObjectAddress)
            .ToHashSet();

        foreach (var key in assignments.Keys.ToList())
        {
            var laserAddr = key < allLasers.Count ? allLasers[key].UINode.Node.PythonObjectAddress : null;
            if (!hudByAddr.ContainsKey(assignments[key]) ||
                (laserAddr != null && idleAddrs.Contains(laserAddr)))
                assignments.Remove(key);
        }

        // ── Fire each idle laser at the next unassigned in-range HUD target ───────
        var assignedHudAddrs = new HashSet<string>(assignments.Values);

        var idleLasers = allLasers
            .Where(m => m.IsActive != true && !m.IsBusy &&
                        ctx.Blackboard.IsCooldownReady($"fire_module_{m.UINode.Node.PythonObjectAddress}"))
            .ToList();

        foreach (var laser in idleLasers)
        {
            // Prefer targets within safe range; any locked HUD target otherwise
            var targetToFire = ui.Targets
                .Where(t => !assignedHudAddrs.Contains(t.UINode.Node.PythonObjectAddress))
                .OrderBy(t => t.DistanceInMeters.HasValue && t.DistanceInMeters.Value > safeRange ? 1 : 0)
                .FirstOrDefault();

            if (targetToFire == null) break;

            ctx.Log($"[Mining] Firing {laser.Name} at '{targetToFire.TextLabel}' ({targetToFire.DistanceText}).");
            ctx.Click(targetToFire.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(650));
            ctx.Click(laser.UINode);

            var laserAddr  = laser.UINode.Node.PythonObjectAddress;
            var laserIdx   = allLasers.IndexOf(laser);
            assignments[laserIdx]        = targetToFire.UINode.Node.PythonObjectAddress;
            assignedHudAddrs.Add(targetToFire.UINode.Node.PythonObjectAddress);
            fireTimes[laserAddr]         = DateTimeOffset.UtcNow;
            ctx.Blackboard.SetCooldown($"fire_module_{laserAddr}", TimeSpan.FromSeconds(12));
            break; // One laser fire per tick
        }

        ctx.Blackboard.Set("laser_targets",     assignments);
        ctx.Blackboard.Set("laser_fire_times",  fireTimes);
    }

    private static IBehaviorNode BT_DroneSecurity() =>
        new ActionNode("Drone Defense", ctx =>
        {
            var ui = ctx.GameState.ParsedUI;
            var hostiles      = ui.OverviewWindows.SelectMany(w => w.Entries).Where(e => e.IsHostile || e.IsAttackingMe).ToList();
            var dronesInSpace = ui.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0;
            var dronesInBay   = ui.DronesWindow?.DronesInBay?.QuantityCurrent   ?? 0;

            if (hostiles.Count > 0)
            {
                ctx.Blackboard.Set("had_hostiles_in_belt", true);

                var nearest   = hostiles.OrderBy(h => h.DistanceInMeters ?? 1e9).First();
                var hudTarget = ui.Targets.FirstOrDefault(t =>
                    t.TextLabel != null && nearest.Name != null &&
                    (t.TextLabel.Contains(nearest.Name, StringComparison.OrdinalIgnoreCase) ||
                     nearest.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase)));
                bool locked = hudTarget != null;

                if (!locked && ctx.Blackboard.IsCooldownReady("drone_lock"))
                {
                    ctx.Click(nearest.UINode, VirtualKey.Control);
                    ctx.Blackboard.SetCooldown("drone_lock", TimeSpan.FromSeconds(5));
                }

                if (locked && dronesInSpace == 0 && dronesInBay > 0 && hudTarget != null && ctx.Blackboard.IsCooldownReady("drone_launch"))
                {
                    ctx.Click(hudTarget.UINode);
                    ctx.Wait(TimeSpan.FromMilliseconds(300));
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                    ctx.Blackboard.SetCooldown("drone_launch", TimeSpan.FromSeconds(10));
                }

                if (locked && dronesInSpace > 0 && hudTarget != null && ctx.Blackboard.IsCooldownReady("drone_engage"))
                {
                    ctx.Click(hudTarget.UINode);
                    ctx.Wait(TimeSpan.FromMilliseconds(300));
                    ctx.KeyPress(VirtualKey.F);
                    ctx.Blackboard.SetCooldown("drone_engage", TimeSpan.FromSeconds(10));
                }
            }
            else if (ctx.Blackboard.Get<bool>("had_hostiles_in_belt"))
            {
                ctx.Blackboard.Remove("had_hostiles_in_belt");
                ctx.Blackboard.Remove(SurveyLastBeltKey);
                ctx.Blackboard.Remove(SurveyIskCacheKey);
                ctx.Blackboard.Set(SurveyPhaseKey, "");
                ctx.Log("[Defense] Belt cleared — forcing mining survey re-scan.");
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
        static bool IsMiningName(ShipUIModuleButton m) => m.Name != null && (
            m.Name.Contains("Mining") || m.Name.Contains("Strip") ||
            m.Name.Contains("Laser")  || m.Name.Contains("Harvester"));
        var top      = shipUI.ModuleButtonsRows.Top.Where(m => !m.IsOffline).ToList();
        var namedTop = top.Where(IsMiningName).ToList();
        return namedTop.Count > 0 ? namedTop : shipUI.ModuleButtons.Where(m => !m.IsOffline && IsMiningName(m));
    }

    private static ShipUIModuleButton? FindPropulsionModule(ShipUI shipUI)
    {
        static bool IsPropName(ShipUIModuleButton m) => m.Name != null && (
            m.Name.Contains("Afterburner",    StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Microwarpdrive", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("MWD",            StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Propulsion",     StringComparison.OrdinalIgnoreCase));

        var mid = shipUI.ModuleButtonsRows.Middle.Where(m => !m.IsOffline).ToList();
        var named = mid.FirstOrDefault(IsPropName);
        if (named != null) return named;

        var unnamed = mid.Where(m => m.Name == null || m.Name.StartsWith("Module ")).ToList();
        if (unnamed.Count == 1) return unnamed[0];

        return shipUI.ModuleButtons.FirstOrDefault(m => !m.IsOffline && IsPropName(m));
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
        if (prop?.IsActive == true) ctx.Click(prop.UINode);
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
    }
}
