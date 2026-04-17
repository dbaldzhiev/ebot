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
    public double SpeedTowardsPrimary { get; set; }

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
    public double? ValuePerM3 { get; set; }
    public bool IsLocked { get; set; }
    public bool IsBeingMined { get; set; }
    public UITreeNodeWithDisplayRegion UINode { get; set; } = null!;
    public UITreeNodeWithDisplayRegion? TargetUINode { get; set; }

    // History for speed calculation
    public Queue<(DateTimeOffset Time, double Distance)> History { get; } = new();
}

public sealed partial class MiningBot
{
    private static void SynthesizeWorldState(BotContext ctx)
    {
        var state = ctx.Blackboard.Get<WorldState>("world") ?? new WorldState();
        var ui = ctx.GameState.ParsedUI;

        // 1. Module info
        if (ui.ShipUI != null)
        {
            var lasers = GetMiningModules(ui.ShipUI).ToList();
            state.TotalLaserCount = lasers.Count;
            state.ActiveLaserCount = lasers.Count(m => m.IsActive == true);
            state.IdleLaserCount = lasers.Count(m => m.IsActive != true && !m.IsBusy);
            
            var prop = FindPropulsionModule(ui.ShipUI);
            state.HasPropulsionActive = prop?.IsActive == true;

            state.ShipSpeed = CurrentSpeed(ctx);
        }

        // 2. Laser Range (if cached in blackboard)
        state.LaserRangeM = ctx.Blackboard.Get<double>("laser_range_m", 0);

        // 2.5 Auto-capture home station if docked and not already set
        if (ctx.GameState.IsDocked && !ctx.Blackboard.Get<bool>("home_station_set"))
        {
            // Priority 1: Info panel "Nearest Location" (most reliable for structures)
            var name = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.NearestLocationName;
            
            // Priority 2: Station window content texts with strict filtering
            if (string.IsNullOrEmpty(name))
            {
                name = ctx.GameState.ParsedUI.StationWindow?.UINode
                    .GetAllContainedDisplayTexts()
                    .Where(t => t.Length is > 5 and < 60 && 
                                !t.All(char.IsDigit) &&
                                !t.Equals("Undock", StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains("Access your", StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains("hangars",    StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains("Leave the",   StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Length)
                    .FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(name))
            {
                ctx.Blackboard.Set("home_station", name);
                ctx.Blackboard.Set("home_station_set", true);
                ctx.Log($"[Mining] Origin station remembered as home: '{name}'");
                
                var sys = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.SystemName;
                if (!string.IsNullOrEmpty(sys)) ctx.Blackboard.Set("home_system", sys);
            }
        }

        // 3. Asteroids & Targets
        var targets = ui.Targets.Where(IsAsteroid).ToList();
        var currentAsteroids = AsteroidsInOverview(ctx).ToList();
        var surveyorEntries = ui.MiningScanResultsWindow?.Entries ?? [];
        var intendedTargetName = ctx.Blackboard.Get<string>("intended_target_name");

        // Build set of addresses currently assigned to lasers (for IsBeingMined)
        var laserAssignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets")
            ?? new Dictionary<int, string>();
        var assignedAddresses = new HashSet<string>(laserAssignments.Values);

        state.Asteroids.Clear();
        foreach (var ov in currentAsteroids)
        {
            var dist = ov.DistanceInMeters ?? 1e9;

            // Bidirectional substring matching: handles "Pyroxeres" vs "Pyroxeres III-Grade"
            var matchedTarget = targets.FirstOrDefault(t =>
                t.TextLabel != null && ov.Name != null && (
                    ov.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase) ||
                    t.TextLabel.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)));
            var isLocked = matchedTarget != null;
            
            // Try to find matching entry in surveyor for better value estimation
            var surveyorMatch = surveyorEntries.FirstOrDefault(s => !s.IsGroup && s.OreName != null && ov.Name != null &&
                (ov.Name.Contains(s.OreName, StringComparison.OrdinalIgnoreCase) || s.OreName.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)));

            double value = OreValueOf(ov);
            double? pricePerM3 = surveyorMatch?.ValuePerM3;

            if (pricePerM3.HasValue)
            {
                // Use explicit price from surveyor if available
                value = pricePerM3.Value;
                // Add a tiny bonus for quantity to break ties
                value += (surveyorMatch!.Quantity ?? 0) / 1_000_000.0;
            }

            var entity = new AsteroidEntity
            {
                Name = ov.Name ?? "Unknown",
                DistanceText = ov.DistanceText ?? "???",
                DistanceM = dist,
                Value = value,
                ValuePerM3 = pricePerM3,
                IsLocked = isLocked,
                IsBeingMined = assignedAddresses.Contains(ov.UINode.Node.PythonObjectAddress),
                UINode = ov.UINode,
                TargetUINode = matchedTarget?.UINode,
            };
            state.Asteroids.Add(entity);
        }

        // Target selection: 
        // 1. Prefer the target we ALREADY decided to mine
        // 2. Prefer already locked asteroids
        // 3. Prefer higher value (surveyor enriched)
        // 4. Prefer closest
        state.PrimaryTarget = state.Asteroids
            .OrderByDescending(a => intendedTargetName != null && a.Name.Equals(intendedTargetName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.IsLocked) 
            .ThenByDescending(a => a.Value)
            .ThenBy(a => a.DistanceM)
            .FirstOrDefault();

        if (state.PrimaryTarget != null)
        {
            ctx.Blackboard.Set("intended_target_name", state.PrimaryTarget.Name);
        }

        // 4. Persistence & Velocity (Optional refinement)
        if (state.PrimaryTarget != null && state.ShipSpeed > 5)
        {
            // Simple logic: if we are moving and distance is decreasing, we are approaching.
            // For now, keep it simple as requested.
        }

        ctx.Blackboard.Set("world", state);
    }
}
