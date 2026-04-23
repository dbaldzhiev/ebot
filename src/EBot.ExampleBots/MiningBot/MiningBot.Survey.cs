using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    private const string SurveyIskCacheKey = "survey_isk_cache";
    private const string SurveyPhaseKey    = "survey_phase";
    private const string SurveyLastBeltKey = "survey_last_belt";
    private const string SurveyLastTickKey = "survey_scan_tick";
    private const int    SurveyStaleTicks  = 150; // ~5 min at 2 s/tick

    private IBehaviorNode EnsureSurveyScanned() =>
        new ActionNode("Survey scanner", ctx =>
        {
            if (!ctx.GameState.IsInSpace || ctx.GameState.IsWarping) return NodeStatus.Failure;

            var currentBelt = ctx.Blackboard.Get<int>("last_belt_target", -1);
            var lastBelt    = ctx.Blackboard.Get<int>(SurveyLastBeltKey, -2);
            var lastTick    = ctx.Blackboard.Get<long>(SurveyLastTickKey, -1L);
            var phase       = ctx.Blackboard.Get<string>(SurveyPhaseKey) ?? "";

            bool beltChanged = currentBelt != lastBelt;
            bool stale       = (ctx.TickCount - lastTick) >= SurveyStaleTicks;

            if (!beltChanged && !stale && phase == "") return NodeStatus.Failure;

            // For a new belt: block BT_MineAtBelt until scan completes so the primary
            // target is chosen with real ISK/m³ scores, not the flat-100 fallback.
            // For a stale re-scan on the same belt: run non-blocking in the background.
            NodeStatus waiting = beltChanged ? NodeStatus.Running : NodeStatus.Failure;

            // Clear stale cache when moving to a new belt so scoring falls back to
            // distance-only (100.0) rather than showing last belt's values while we scan.
            if (beltChanged && phase == "")
                ctx.Blackboard.Remove(SurveyIskCacheKey);

            var ui     = ctx.GameState.ParsedUI;
            var window = ui.MiningScanResultsWindow;

            switch (phase)
            {
                case "":
                    ctx.KeyPress(VirtualKey.M);
                    ctx.Blackboard.Set(SurveyPhaseKey, "scan");
                    ctx.Blackboard.SetCooldown("survey_wait", TimeSpan.FromSeconds(1.5));
                    return waiting;

                case "scan":
                    if (!ctx.Blackboard.IsCooldownReady("survey_wait"))
                        return waiting;

                    if (window?.ScanButton == null)
                    {
                        ctx.KeyPress(VirtualKey.M);
                        ctx.Blackboard.SetCooldown("survey_wait", TimeSpan.FromSeconds(1.5));
                        return waiting;
                    }

                    ctx.Log("[Survey] Clicking Scan...");
                    ctx.Click(window.ScanButton);
                    ctx.Blackboard.Set(SurveyPhaseKey, "scroll");
                    ctx.Blackboard.SetCooldown("survey_wait", TimeSpan.FromSeconds(3));
                    return waiting;

                case "scroll":
                    if (!ctx.Blackboard.IsCooldownReady("survey_wait"))
                        return waiting;

                    if (window == null) { ctx.Blackboard.Set(SurveyPhaseKey, "scan"); return waiting; }

                    // Scroll bottom → top so every ore type is rendered in the virtual list
                    ctx.Scroll(window.UINode, 3000);
                    ctx.Wait(TimeSpan.FromMilliseconds(200));
                    ctx.Scroll(window.UINode, -3000);
                    ctx.Blackboard.Set(SurveyPhaseKey, "collapse");
                    ctx.Blackboard.SetCooldown("survey_wait", TimeSpan.FromSeconds(1));
                    return waiting;

                case "collapse":
                    if (!ctx.Blackboard.IsCooldownReady("survey_wait"))
                        return waiting;

                    var expanded = (window?.Entries ?? [])
                        .FirstOrDefault(e => e.IsGroup && e.IsExpanded && e.ExpanderNode != null);

                    if (expanded != null)
                    {
                        ctx.Log($"[Survey] Collapsing expanded group: {expanded.OreName}");
                        ctx.Click(expanded.ExpanderNode!);
                        ctx.Blackboard.SetCooldown("survey_wait", TimeSpan.FromSeconds(1));
                        return waiting;
                    }

                    var groups = (window?.Entries ?? [])
                        .Where(e => e.IsGroup && e.ValuePerM3 > 0)
                        .ToList();

                    if (groups.Count == 0)
                    {
                        if (ctx.Blackboard.IsCooldownReady("survey_retry"))
                        {
                            ctx.Log("[Survey] No group entries after scan — retrying.");
                            ctx.Blackboard.Set(SurveyPhaseKey, "scan");
                            ctx.Blackboard.SetCooldown("survey_retry", TimeSpan.FromSeconds(10));
                        }
                        return waiting;
                    }

                    CacheSurveyValues(ctx, groups);
                    ctx.Blackboard.Set(SurveyPhaseKey, "");
                    ctx.Blackboard.Set(SurveyLastBeltKey, currentBelt);
                    ctx.Blackboard.Set(SurveyLastTickKey, ctx.TickCount);
                    ctx.Log($"[Survey] Cached {groups.Count} ore type(s) with ISK/m³ values.");
                    return NodeStatus.Failure; // done — always unblock

                default:
                    ctx.Blackboard.Set(SurveyPhaseKey, "");
                    return NodeStatus.Failure;
            }
        });

    private static void CacheSurveyValues(BotContext ctx, IEnumerable<MiningScanEntry> entries)
    {
        var cache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.OreName == null || !(e.ValuePerM3 > 0)) continue;
            cache[e.OreName] = e.ValuePerM3!.Value;
        }
        ctx.Blackboard.Set(SurveyIskCacheKey, cache);
    }

    private static double? GetSurveyIsk(BotContext ctx, string? asteroidName)
    {
        if (string.IsNullOrEmpty(asteroidName)) return null;
        var cache = ctx.Blackboard.Get<Dictionary<string, double>>(SurveyIskCacheKey);
        if (cache == null) return null;

        // Exact match first
        if (cache.TryGetValue(asteroidName, out var v)) return v;

        // Partial match: prefer the most specific (longest key) to avoid "Scordite"
        // matching "Scordite III-Grade" when a plain "Scordite" entry also exists.
        return cache
            .Where(kv => asteroidName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                         kv.Key.Contains(asteroidName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (double?)kv.Value)
            .FirstOrDefault();
    }
}
