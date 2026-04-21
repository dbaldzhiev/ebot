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

        var overviewAsteroids = AsteroidsInOverview(ctx).ToList();
        var surveyorEntries = ui.MiningScanResultsWindow?.Entries ?? [];
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

        state.Asteroids.Clear();
        foreach (var ov in overviewAsteroids)
        {
            var sMatch = surveyorEntries.FirstOrDefault(s => 
                s.OreName != null && ov.Name != null && 
                (ov.Name.Contains(s.OreName, StringComparison.OrdinalIgnoreCase) || 
                 s.OreName.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)));

            bool locked = IsLocked(ov);
            var dist = ov.DistanceInMeters ?? 1e9;
            double effectiveRange = state.LaserRangeM > 0 ? state.LaserRangeM : 15000;
            double iskPerM3       = sMatch?.ValuePerM3 ?? GetSurveyIsk(ctx, ov.Name) ?? 100.0;
            double travelKm       = Math.Max(0, dist - effectiveRange) / 1000.0;
            double score          = iskPerM3 - (iskPerM3 * travelKm * 0.015);
            if (locked) score    += iskPerM3 * 2.5;

            state.Asteroids.Add(new AsteroidEntity
            {
                Name = ov.Name ?? "Unknown",
                DistanceText = ov.DistanceText ?? "???",
                DistanceM = dist,
                Value = iskPerM3,
                Score = score,
                ValuePerM3 = sMatch?.ValuePerM3 ?? GetSurveyIsk(ctx, ov.Name),
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
