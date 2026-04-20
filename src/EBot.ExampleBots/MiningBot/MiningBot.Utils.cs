using System.Text.RegularExpressions;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    // ═══════════════════════════════════════════════════════════════════════
    // UI Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static void HoverAndSlide(BotContext ctx, UITreeNodeWithDisplayRegion node)
    {
        var r    = node.Region;
        int midY = r.Y + r.Height / 2;
        ctx.Actions.Enqueue(new MoveMouseAction(r.X + r.Width / 2, midY));
        ctx.Wait(TimeSpan.FromMilliseconds(350));
        ctx.Actions.Enqueue(new MoveMouseAction(r.X + r.Width - 2, midY));
    }

    private static void RightClickInSpace(BotContext ctx)
    {
        var root = ctx.GameState.ParsedUI.UITree;
        if (root == null)
        {
            ctx.Actions.Enqueue(new RightClickAction(400, 400));
            return;
        }

        var cw = root.Region.Width;
        var ch = root.Region.Height;
        var cx = root.Region.X;
        var cy = root.Region.Y;

        // Ship is usually centered. UI is at edges.
        // Let's target a safe "True Empty" zone in the mid-left area, 
        // avoiding Neocom (left 80px) and top-left info panels (top 200px).
        // Moving further right (450px) and further down (250px) from top-left.
        int targetX = cx + 450 + Random.Shared.Next(-60, 60);
        int targetY = cy + 250 + Random.Shared.Next(-40, 40);

        // Check if Overview or another window is covering our target spot
        var overlappingWindow = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w => w.UINode.Region.Contains(targetX, targetY))
            ?? ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault(w => w.UINode.Region.Contains(targetX, targetY)) as object;

        if (overlappingWindow != null)
        {
            // If that spot is busy, try a spot further towards the middle-right
            targetX = cx + cw - 450 + Random.Shared.Next(-60, 60);
        }

        ctx.Log($"[Navigation] Right-clicking space at ({targetX}, {targetY}) to open context menu (Avoiding UI text).");
        ctx.Actions.Enqueue(new RightClickAction(targetX, targetY));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sensing Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static bool AnyAsteroidsInOverview(BotContext ctx)
    {
        var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        if (ov == null) return false;
        return ov.Entries.Any(IsAsteroid);
    }
    private static IEnumerable<OverviewEntry> AsteroidsInOverview(BotContext ctx)
    {
        var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        var parsed = (ov?.Entries.Where(IsAsteroid) ?? []).ToList();

        // If overview has rocks, use them
        if (parsed.Count > 0) return parsed;

        // If overview is empty but we are in space, it might be filtered or not loaded.
        // Tree fallback is slow but helps if the user is on a wrong tab.
        var treeFallback = GetAsteroidsFromTreeScan(ctx);
        if (treeFallback.Count > 0 && ctx.Blackboard.IsCooldownReady("tree_scan_log"))
        {
            ctx.Log($"[Mining] UITree fallback found {treeFallback.Count} asteroids in overview area");
            ctx.Blackboard.SetCooldown("tree_scan_log", TimeSpan.FromSeconds(30));
        }
        return treeFallback;
    }

    private static OverviewEntry? FindStationInOverview(BotContext ctx)
    {
        var homeStation = ctx.Blackboard.Get<string>("home_station");
        var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        if (ov == null) return null;

        ctx.Log($"[Mining] Searching for station in overview. Home set to: '{homeStation ?? "None"}'");

        if (!string.IsNullOrEmpty(homeStation))
        {
            var match = ov.Entries.FirstOrDefault(e =>
            {
                var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
                    .Concat(e.Texts).Concat(e.CellsTexts.Values);
                return texts.Any(t => t.Contains(homeStation, StringComparison.OrdinalIgnoreCase));
            });
            if (match != null) 
            {
                ctx.Log($"[Mining] Found exact home station match: '{match.Name}'");
                return match;
            }
            
            // Try partial match if no exact match
            var partial = ov.Entries.FirstOrDefault(e =>
            {
                var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
                    .Concat(e.Texts).Concat(e.CellsTexts.Values);
                return texts.Any(t => homeStation.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(homeStation, StringComparison.OrdinalIgnoreCase));
            });
            if (partial != null)
            {
                ctx.Log($"[Mining] Found partial home station match: '{partial.Name}'");
                return partial;
            }
        }

        // Broad fallback
        var keywords = _stationKeywords.Concat(new[] { "Station", "Structure", "Citadel", "Fortizar", "Keepstar", "Athanor", "Tatara", "Raitaru", "Azbel" }).ToList();

        var firstStation = ov.Entries.FirstOrDefault(e =>
        {
            var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
                .Concat(e.Texts).Concat(e.CellsTexts.Values);
            return texts.Any(t => keywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)));
        });

        if (firstStation != null)
        {
            ctx.Log($"[Mining] No home station match found. Falling back to first available dockable: '{firstStation.Name}' ({firstStation.ObjectType})");
        }
        else
        {
            ctx.Log("[Mining] No dockable objects found in current overview tab.");
        }

        return firstStation;
    }

    private static bool IsAsteroid(OverviewEntry e)
    {
        if (!string.IsNullOrEmpty(e.ObjectType) &&
            e.ObjectType.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) &&
            !e.ObjectType.Contains("Belt",    StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsAsteroid(e.Name)) return true;
        if (IsAsteroid(e.ObjectType)) return true;

        var texts = e.UINode.GetAllContainedDisplayTexts()
            .Select(t => t.ToLowerInvariant())
            .Where(t => !t.Contains("asteroid belt"));
        return texts.Any(t => IsAsteroid(t));
    }

    private static bool IsAsteroid(Target t)
    {
        if (IsAsteroid(t.TextLabel)) return true;
        return t.UINode.GetAllContainedDisplayTexts().Any(IsAsteroid);
    }

    private static bool IsAsteroid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("asteroid belt")) return false;
        return _asteroidKeywords.Any(k => lower.Contains(k));
    }

    private static List<OverviewEntry> GetAsteroidsFromTreeScan(BotContext ctx)
    {
        var nodes = ctx.GameState.ParsedUI.UITree?
            .FindAll(n => n.Node.PythonObjectTypeName.Contains("OverviewScrollEntry",
                StringComparison.OrdinalIgnoreCase)) ?? [];

        var result = new List<OverviewEntry>();
        foreach (var node in nodes)
        {
            var texts = node.GetAllContainedDisplayTexts().ToList();
            var lower = texts.Select(t => t.ToLowerInvariant())
                             .Where(t => !t.Contains("asteroid belt"))
                             .ToList();
            if (!lower.Any(t => _asteroidKeywords.Any(k => t.Contains(k))))
                continue;

            // Differentiate between Distance and Size
            var distCandidates = texts
                .Select(t => new { Text = t, Value = ParseDistanceM(t) })
                .Where(x => x.Value.HasValue)
                .ToList();

            double? dist = null;
            if (distCandidates.Count == 1)
            {
                dist = distCandidates[0].Value;
            }
            else if (distCandidates.Count > 1)
            {
                // Heuristic: Distance is usually the larger number when arriving, 
                // and it's often in 'km' while size is in 'm'.
                var inKm = distCandidates.Where(x => x.Text.Contains("km", StringComparison.OrdinalIgnoreCase)).ToList();
                if (inKm.Count == 1) dist = inKm[0].Value;
                else dist = distCandidates.MaxBy(x => x.Value!.Value)?.Value;
            }

            var name = texts
                .Where(t => !DistanceRegex().IsMatch(t) && t.Length > 1)
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();

            result.Add(new OverviewEntry
            {
                UINode          = node,
                Name            = name,
                DistanceText    = dist.HasValue ? $"{dist:F0} m" : null,
                DistanceInMeters = dist,
                Texts           = texts,
                CellsTexts      = new Dictionary<string, string>(),
            });
        }
        return result;
    }

    private static double CurrentSpeed(BotContext ctx)
    {
        var text = ctx.GameState.ParsedUI.ShipUI?.SpeedText;
        if (string.IsNullOrEmpty(text)) return 0;
        return ParseSpeed(text);
    }

    private static double ParseSpeed(string text)
    {
        // "324 m/s" or "1,234.5 m/s"
        var m = Regex.Match(text, @"([\d,.]+)\s*m/s", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        if (double.TryParse(m.Groups[1].Value.Replace(",", ""),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val)) return val;
        return 0;
    }

    private static double? ParseDistanceM(string text)
    {
        var m = DistanceRegex().Match(text);
        if (!m.Success) return null;

        // Robust numeric parsing: remove thousands-separators (commas or spaces)
        var valStr = m.Groups[1].Value.Replace(",", "").Replace(" ", "");
        
        if (!double.TryParse(valStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val)) return null;

        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "km" => val * 1_000,
            "au" => val * 149_597_870_700.0,
            _    => val,
        };
    }

    [GeneratedRegex(@"([\d.,]+)\s*(m|km|au)", RegexOptions.IgnoreCase)]
    private static partial Regex DistanceRegex();
}
