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
    public double Score { get; set; }
    public double? ValuePerM3 { get; set; }
    public bool IsLocked { get; set; }
    public bool IsBeingMined { get; set; }
    public UITreeNodeWithDisplayRegion UINode { get; set; } = null!;
    public UITreeNodeWithDisplayRegion? SurveyorUINode { get; set; }
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

            if (ctx.Blackboard.IsCooldownReady("world_log_modules"))
            {
                ctx.Log($"[World] Modules: Lasers={state.ActiveLaserCount}/{state.TotalLaserCount}, Prop={prop?.Name ?? "None"}(Active={state.HasPropulsionActive})");
                ctx.Blackboard.SetCooldown("world_log_modules", TimeSpan.FromSeconds(30));
            }

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
        var surveyorEntries = ui.MiningScanResultsWindow?.Entries ?? [];
        var overviewAsteroids = AsteroidsInOverview(ctx).ToList();
        var intendedTargetName = ctx.Blackboard.Get<string>("intended_target_name");

        // Assumed locked memory (to handle text-less targets)
        var assumedLocked = ctx.Blackboard.Get<HashSet<string>>("assumed_locked") ?? new HashSet<string>();
        if (targets.Count == 0 && assumedLocked.Count > 0)
        {
            assumedLocked.Clear();
            ctx.Blackboard.Set("assumed_locked", assumedLocked);
        }

        // Build set of addresses currently assigned to lasers (for IsBeingMined)
        var laserAssignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets")
            ?? new Dictionary<int, string>();
        var assignedAddresses = new HashSet<string>(laserAssignments.Values);

        state.Asteroids.Clear();

        var assignedTargets = new HashSet<string>();
        var laserRange = state.LaserRangeM > 0 ? state.LaserRangeM : 15000;

        foreach (var ov in overviewAsteroids)
        {
            // Find corresponding surveyor entry by name matching.
            // Check both individual rocks AND group headers (if collapsed).
            var sMatch = surveyorEntries.FirstOrDefault(s => 
                s.OreName != null && ov.Name != null && 
                (ov.Name.Contains(s.OreName, StringComparison.OrdinalIgnoreCase) || 
                 s.OreName.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)));

            // Find if this specific asteroid is currently locked.
            var matchedTarget = targets.FirstOrDefault(t =>
                !assignedTargets.Contains(t.UINode.Node.PythonObjectAddress) &&
                t.TextLabel != null && ov.Name != null && (
                    ov.Name.Contains(t.TextLabel, StringComparison.OrdinalIgnoreCase) || 
                    t.TextLabel.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)
                ));
            
            // Fallback for text-less targets (memory reader failure)
            if (matchedTarget == null && assumedLocked.Contains(ov.UINode.Node.PythonObjectAddress))
            {
                matchedTarget = targets.FirstOrDefault(t => 
                    !assignedTargets.Contains(t.UINode.Node.PythonObjectAddress) && 
                    t.TextLabel == null);
            }

            if (matchedTarget != null)
                assignedTargets.Add(matchedTarget.UINode.Node.PythonObjectAddress);

            var dist = matchedTarget?.DistanceInMeters ?? ov.DistanceInMeters ?? 1e9;
            var isLocked = sMatch?.IsLocked == true || matchedTarget != null || assumedLocked.Contains(ov.UINode.Node.PythonObjectAddress);
            var isBeingMined = assignedAddresses.Contains(ov.UINode.Node.PythonObjectAddress) || (sMatch != null && assignedAddresses.Contains(sMatch.UINode.Node.PythonObjectAddress));

            // Scoring:
            // 1. Base value from surveyor ISK/m3 or rough Overview estimate
            double value = sMatch?.ValuePerM3 ?? OreValueOf(ov);
            
            // 2. Travel time penalty
            // Assume 150 m/s ship speed. 1 minute of travel costs ~100 ISK/m3 of "opportunity value"
            double distToTravel = Math.Max(0, dist - laserRange + 2000);
            double travelTimeSeconds = distToTravel / 150.0;
            double travelPenalty = travelTimeSeconds * 1.5; 

            double score = value - travelPenalty;

            // 3. Bonuses
            if (isLocked) score += 2000; // Prefer keeping existing locks
            if (isBeingMined) score += 5000; // Heavily prefer continuing what we're already mining

            state.Asteroids.Add(new AsteroidEntity
            {
                Name = ov.Name ?? sMatch?.OreName ?? "Unknown",
                DistanceText = ov.DistanceText ?? (sMatch?.DistanceInMeters.HasValue == true ? $"{sMatch.DistanceInMeters:F0} m" : "???"),
                DistanceM = dist,
                Value = value,
                Score = score,
                ValuePerM3 = sMatch?.ValuePerM3,
                IsLocked = isLocked,
                IsBeingMined = isBeingMined,
                UINode = ov.UINode, 
                SurveyorUINode = sMatch?.UINode, 
                TargetUINode = matchedTarget?.UINode,
            });
        }

        // Target selection: 
        // 1. Prefer by Score (Value - Travel)
        // 2. Prefer the target we ALREADY decided to mine (to avoid jitter)
        state.PrimaryTarget = state.Asteroids
            .OrderByDescending(a => a.Score)
            .ThenByDescending(a => intendedTargetName != null && a.Name.Equals(intendedTargetName, StringComparison.OrdinalIgnoreCase))
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
