using EBot.Core.DecisionEngine;
using EBot.Core.GameState;
using System.Collections.Concurrent;

namespace EBot.ExampleBots.MiningBot;

public sealed class WorldState
{
    public double LaserRangeM { get; set; } = 0;
    public bool IsLaserRangeKnown => LaserRangeM > 0;

    public List<AsteroidEntity> Asteroids { get; } = [];
    public AsteroidEntity? PrimaryTarget { get; set; }
    
    public double ShipSpeed { get; set; }
    public int ActiveLaserCount { get; set; }
    public int IdleLaserCount { get; set; }
    public int TotalLaserCount { get; set; }

    public bool HasPropulsionActive { get; set; }
}

public class AsteroidEntity
{
    public string Name { get; set; } = "";
    public string DistanceText { get; set; } = "";
    public double DistanceM { get; set; }
    public double Value { get; set; }
    public double Score { get; set; }
    public double? ValuePerM3 { get; set; }
    public bool IsLocked { get; set; } 
    public bool IsLockPending { get; set; }
    public bool IsBeingMined { get; set; }
    public UITreeNodeWithDisplayRegion UINode { get; set; } = null!;
}

public sealed partial class MiningBot
{
    private static bool IsLocked(OverviewEntry? e) =>
        e?.UINode.FindFirst(c => (c.Node.GetDictString("_texturePath") ?? "").Contains("activeTarget.png")) != null;

    private static void SynthesizeWorldState(BotContext ctx)
    {
        var state = ctx.Blackboard.Get<WorldState>("world") ?? new WorldState();
        var ui = ctx.GameState.ParsedUI;

        if (ui.ShipUI != null)
        {
            var lasers = GetMiningModules(ui.ShipUI).ToList();
            state.TotalLaserCount = lasers.Count;
            state.ActiveLaserCount = lasers.Count(m => m.IsActive == true);
            state.IdleLaserCount = lasers.Count(m => m.IsActive != true && !m.IsBusy);
            state.HasPropulsionActive = FindPropulsionModule(ui.ShipUI)?.IsActive == true;
            state.ShipSpeed = CurrentSpeed(ctx);
        }

        // Cache laser optimal range from tooltip whenever it is visible
        var tooltipRange = ui.ModuleButtonTooltip?.OptimalRangeMeters;
        if (tooltipRange is > 0)
            ctx.Blackboard.Set("laser_range_m", (double)tooltipRange.Value);

        state.LaserRangeM = ctx.Blackboard.Get<double>("laser_range_m", 0);

        // Log when multiple overview windows are open
        var ovWindows = ui.OverviewWindows;
        if (ovWindows.Count > 1 && ctx.Blackboard.IsCooldownReady("ov_multiwindow_log"))
        {
            var names = string.Join(", ", ovWindows.Select(w =>
                $"'{w.WindowName ?? "?"}' ({w.Entries.Count} entries)"));
            ctx.Log($"[Overview] {ovWindows.Count} overview windows detected: {names}");
            ctx.Blackboard.SetCooldown("ov_multiwindow_log", TimeSpan.FromSeconds(30));
        }

        var overviewAsteroids = AsteroidsInOverview(ctx).ToList();
        var assumedLocked = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;

        var allOvAddresses = new HashSet<string>(overviewAsteroids.Select(a => a.UINode.Node.PythonObjectAddress));
        foreach (var kvp in assumedLocked.ToList())
        {
            if (IsLocked(overviewAsteroids.FirstOrDefault(a => a.UINode.Node.PythonObjectAddress == kvp.Key)) ||
                (now - kvp.Value).TotalSeconds > 15 || 
                !allOvAddresses.Contains(kvp.Key))
            {
                assumedLocked.Remove(kvp.Key);
            }
        }
        ctx.Blackboard.Set("assumed_locked", assumedLocked);

        var miningBot = ctx.Bot as MiningBot;
        if (miningBot == null && ctx.Bot is SurvivalWrappedBot swb && swb.Inner is MiningBot mb)
        {
            miningBot = mb;
        }

        state.Asteroids.Clear();
        foreach (var ov in overviewAsteroids)
        {
            bool locked = IsLocked(ov);
            var dist = ov.DistanceInMeters ?? 1e9;
            double effectiveRange = state.LaserRangeM > 0 ? state.LaserRangeM : 15000;
            // Always use the cache — exact match first, then most-specific partial match.
            // Never use the live window entries directly: partial matching there assigns
            // "Scordite III-Grade" price to plain "Scordite" asteroids.
            double? surveyIsk = GetSurveyIsk(ctx, ov.Name);
            double iskPerM3   = surveyIsk ?? 100.0;
            double travelKm   = Math.Max(0, dist - effectiveRange) / 1000.0;
            double score      = iskPerM3 - (iskPerM3 * travelKm * 0.05);
            if (dist <= effectiveRange) score += iskPerM3 * 0.10; // prefer in-range over marginally better out-of-range
            if (locked) score += iskPerM3 * 2.5;

            // ── Ore Preferences ─────────────────────────────────────────────
            if (miningBot != null)
            {
                var name = ov.Name ?? "";
                
                // If we have a filter and this ore isn't in it, give it a massive penalty
                if (miningBot.OresToMine.Count > 0 && 
                    !miningBot.OresToMine.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase)))
                {
                    score -= 1_000_000_000;
                }

                // If this is a preferred ore, give it a massive boost
                if (miningBot.OresToPrefer.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 1_000_000;
                }
            }

            state.Asteroids.Add(new AsteroidEntity
            {
                Name = ov.Name ?? "Unknown",
                DistanceText = ov.DistanceText ?? "???",
                DistanceM = dist,
                Value = iskPerM3,
                Score = score,
                ValuePerM3 = surveyIsk,
                IsLocked = locked,
                IsLockPending = assumedLocked.ContainsKey(ov.UINode.Node.PythonObjectAddress),
                IsBeingMined = locked && state.ActiveLaserCount > 0,
                UINode = ov.UINode
            });
        }

        state.PrimaryTarget = state.Asteroids.OrderByDescending(a => a.Score).FirstOrDefault();
        ctx.Blackboard.Set("world", state);
    }
}
