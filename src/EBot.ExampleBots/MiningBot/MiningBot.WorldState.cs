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
    public bool IsLocked { get; set; }       // Confirmed in HUD
    public bool IsLockPending { get; set; }   // We sent a lock command, waiting for it to appear
    public bool IsBeingMined { get; set; }
    public UITreeNodeWithDisplayRegion UINode { get; set; } = null!;
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

        // 2. Laser Range
        state.LaserRangeM = ctx.Blackboard.Get<double>("laser_range_m", 0);

        // 2.5 Auto-capture home station
        if (ctx.GameState.IsDocked && !ctx.Blackboard.Get<bool>("home_station_set"))
        {
            var name = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.NearestLocationName;
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
            }
        }

        // 3. Asteroids & HUD Target matching
        var hudTargets = ui.Targets.ToList();
        var overviewAsteroids = AsteroidsInOverview(ctx).ToList();
        var surveyorEntries = ui.MiningScanResultsWindow?.Entries ?? [];
        var laserRange = state.LaserRangeM > 0 ? state.LaserRangeM : 15000;

        // Match HUD Targets to Overview entries by distance (highly precise in memory tree)
        var matchedOverviewAddresses = new HashSet<string>();
        var hudToOvMap = new Dictionary<string, string>(); // HUD Address -> OV Address

        foreach (var ht in hudTargets)
        {
            var bestMatch = overviewAsteroids
                .Where(a => !matchedOverviewAddresses.Contains(a.UINode.Node.PythonObjectAddress))
                .OrderBy(a => Math.Abs((a.DistanceInMeters ?? 1e9) - (ht.DistanceInMeters ?? 1e9)))
                .FirstOrDefault();
                
            if (bestMatch != null && Math.Abs((bestMatch.DistanceInMeters ?? 1e9) - (ht.DistanceInMeters ?? 1e9)) < 1500)
            {
                matchedOverviewAddresses.Add(bestMatch.UINode.Node.PythonObjectAddress);
                hudToOvMap[ht.UINode.Node.PythonObjectAddress] = bestMatch.UINode.Node.PythonObjectAddress;
            }
        }

        // Handle assumed (pending) locks
        var assumedLocked = ctx.Blackboard.Get<Dictionary<string, DateTimeOffset>>("assumed_locked") ?? new Dictionary<string, DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;
        
        // Remove from pending if now confirmed in HUD, or if it expired (15s) or disappeared from overview
        var allOvAddresses = new HashSet<string>(overviewAsteroids.Select(a => a.UINode.Node.PythonObjectAddress));
        foreach (var addr in matchedOverviewAddresses) assumedLocked.Remove(addr);
        foreach (var kvp in assumedLocked.ToList())
            if ((now - kvp.Value).TotalSeconds > 15 || !allOvAddresses.Contains(kvp.Key)) assumedLocked.Remove(kvp.Key);
        ctx.Blackboard.Set("assumed_locked", assumedLocked);

        // Laser assignments (tracked by HUD target address)
        var laserAssignments = ctx.Blackboard.Get<Dictionary<int, string>>("laser_targets") ?? new Dictionary<int, string>();
        var assignedHudAddresses = new HashSet<string>(laserAssignments.Values);

        state.Asteroids.Clear();

        foreach (var ov in overviewAsteroids)
        {
            var sMatch = surveyorEntries.FirstOrDefault(s => 
                s.OreName != null && ov.Name != null && 
                (ov.Name.Contains(s.OreName, StringComparison.OrdinalIgnoreCase) || 
                 s.OreName.Contains(ov.Name, StringComparison.OrdinalIgnoreCase)));

            string addr = ov.UINode.Node.PythonObjectAddress;
            bool isLocked = matchedOverviewAddresses.Contains(addr);
            bool isLockPending = assumedLocked.ContainsKey(addr);
            var dist = ov.DistanceInMeters ?? 1e9;

            // Scoring
            double value = sMatch?.ValuePerM3 ?? OreValueOf(ov);
            double distToTravel = Math.Max(0, dist - laserRange + 2000);
            double travelPenalty = (distToTravel / 150.0) * 1.5; 
            double score = value - travelPenalty;

            if (isLocked) score += 10000;       // Heavily prefer confirmed targets
            if (isLockPending) score += 8000;   // Prefer targets we are currently locking

            state.Asteroids.Add(new AsteroidEntity
            {
                Name = ov.Name ?? sMatch?.OreName ?? "Unknown",
                DistanceText = ov.DistanceText ?? (sMatch?.DistanceInMeters.HasValue == true ? $"{sMatch.DistanceInMeters:F0} m" : "???"),
                DistanceM = dist,
                Value = value,
                Score = score,
                ValuePerM3 = sMatch?.ValuePerM3,
                IsLocked = isLocked,
                IsLockPending = isLockPending,
                IsBeingMined = isLocked && hudToOvMap.Any(kvp => kvp.Value == addr && assignedHudAddresses.Contains(kvp.Key)),
                UINode = ov.UINode
            });
        }

        state.PrimaryTarget = state.Asteroids.OrderByDescending(a => a.Score).FirstOrDefault();
        ctx.Blackboard.Set("world", state);
    }
}
