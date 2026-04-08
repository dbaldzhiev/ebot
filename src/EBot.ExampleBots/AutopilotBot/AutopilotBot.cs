using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.AutopilotBot;

/// <summary>
/// Warp-to-0 autopilot bot.
///
/// Algorithm (from the authoritative Elm reference bot):
///   1. Sort route element markers by (region.Left + region.Top) and take the first —
///      this is always the marker for the NEXT hop in the route.
///   2. If the ship is warping or jumping → wait.
///   3. If a context menu is open:
///      a. "jump" entry  → click it  (we are at the stargate)
///      b. "dock" entry  → click it  (we are at the destination station)
///      c. "warp" + "0"  → click it  (warp-to-0 explicitly shown)
///      d. any "warp"    → click it  (fallback; EVE will warp at configured range)
///      e. none match    → dismiss and right-click the marker again
///   4. No context menu → right-click the first route marker to open one.
///   5. No route left   → arrived.
///
/// Destination setup (when started via MCP "travel to …"):
///   • Shift+S focuses EVE's universal search bar.
///   • Types the system name, presses Enter.
///   • Waits for search results, right-clicks the first "Solar System" entry.
///   • Clicks "Set Destination" in the resulting context menu.
/// </summary>
public sealed class AutopilotBot : IBot
{
    /// <summary>
    /// Optional destination system name pre-loaded by the MCP "travel_to" command.
    /// When set, the bot searches for the system and sets the route before navigating.
    /// If null/empty, the bot uses whatever route is already set in-game.
    /// </summary>
    public string? Destination { get; init; }

    public string Name => "Autopilot Bot";
    public string Description =>
        "Warp-to-0 travel bot. Right-clicks each route marker to trigger warp/jump/dock. " +
        "Start with an in-game route set, or use the MCP 'travel_to' command.";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs = 2000,
        MinActionDelayMs = 100,
        MaxActionDelayMs = 300,
        CoordinateJitter = 3,
    };

    public void OnStart(BotContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(Destination))
            ctx.Blackboard.Set("autopilot_destination", Destination);
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Autopilot Root",
            TrackSystemChange(),     // Always Failure — side-effect: detects jumps, sets 2s cooldown
            HandleMessageBoxes(),
            SetupDestination(),
            HandleDocked(),
            HandleInSpace());

    // ─── Sub-trees ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tracks the current solar system each tick.
    /// When the system name changes (= a stargate jump just completed), sets a 2-second
    /// post-jump cooldown and clears the nav_menu_expected flag so stale menus are dismissed.
    /// Always returns Failure so the parent Selector continues to the real logic.
    /// </summary>
    private static IBehaviorNode TrackSystemChange() =>
        new ActionNode("Track system change", ctx =>
        {
            var sys = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.SystemName;
            var prev = ctx.Blackboard.Get<string>("nav_current_system");

            if (sys != null && prev != null &&
                !string.Equals(sys, prev, StringComparison.OrdinalIgnoreCase))
            {
                // System just changed → gate jump completed; wait 2 s then proceed
                ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(2));
                ctx.Blackboard.Set("nav_menu_expected", false);
                ctx.Log($"[Autopilot] Jump detected: {prev} → {sys} — 2 s cooldown");
            }

            if (sys != null)
                ctx.Blackboard.Set("nav_current_system", sys);

            return NodeStatus.Failure; // always fail — this is a side-effect-only node
        });

    private static IBehaviorNode HandleMessageBoxes() =>
        new SequenceNode("Close message boxes",
            new ConditionNode("Has message box?", ctx => ctx.GameState.HasMessageBox),
            new ActionNode("Click first button", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.MessageBoxes[0].Buttons.FirstOrDefault();
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                return NodeStatus.Success;
            }));

    /// <summary>
    /// Multi-tick state machine using Shift+S to set the in-game destination.
    ///
    /// Phases stored on blackboard key "dest_phase":
    ///   ""           → press Shift+S, advance to "typing"
    ///   "typing"     → find search field, Ctrl+A, type name, Enter, advance to "selecting"
    ///   "selecting"  → if context menu with "destination" → click it (done)
    ///                  else right-click first matching search result
    /// </summary>
    private static IBehaviorNode SetupDestination() =>
        new SequenceNode("Setup destination",
            new ConditionNode("Needs destination setup?", ctx =>
                !string.IsNullOrEmpty(ctx.Blackboard.Get<string>("autopilot_destination")) &&
                !ctx.Blackboard.Get<bool>("destination_set")),
            new ActionNode("Destination state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("dest_phase") ?? "";

                switch (phase)
                {
                    // ── Step 1: open EVE's search bar with Shift+S ───────────
                    case "":
                        ctx.KeyPress(VirtualKey.S, VirtualKey.Shift);
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        ctx.Blackboard.Set("dest_phase", "typing");
                        return NodeStatus.Running;

                    // ── Step 2: type into the search bar, Enter ──────────────
                    case "typing":
                    {
                        // Shift+S (from phase "") already focused the global "Search New Eden"
                        // bar.  Do NOT click any EditText here — a depth-first FindFirst would
                        // hit the hangar/cargo search bar while docked and send the text there
                        // instead, causing the "selecting" phase to never find results.
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);   // select all existing text
                        ctx.TypeText(ctx.Blackboard.Get<string>("autopilot_destination")!);
                        ctx.KeyPress(VirtualKey.Enter);
                        ctx.Wait(TimeSpan.FromSeconds(2));
                        ctx.Blackboard.Set("dest_phase", "selecting");
                        return NodeStatus.Running;
                    }

                    // ── Step 3: click "Set Destination" or right-click result ─
                    case "selecting":
                    {
                        // If context menu is showing "Set Destination" → click it
                        if (ctx.GameState.HasContextMenu)
                        {
                            var setDest = ctx.GameState.ParsedUI.ContextMenus
                                .FirstOrDefault()?.Entries
                                .FirstOrDefault(e => e.Text?.Contains(
                                    "destination", StringComparison.OrdinalIgnoreCase) == true);
                            if (setDest != null)
                            {
                                ctx.Click(setDest.UINode);
                                // Wait a few seconds for the route panel to populate so that
                                // HandleDocked sees RouteJumpsRemaining > 0 when it fires next.
                                ctx.Wait(TimeSpan.FromSeconds(3));
                                ctx.Blackboard.Set("destination_set", true);
                                ctx.Blackboard.Set("dest_phase", "done");
                                return NodeStatus.Success;
                            }
                        }

                        // Right-click the first search result that matches the system name
                        var dest = ctx.Blackboard.Get<string>("autopilot_destination")!;
                        var result = FindSearchResult(ctx.GameState.ParsedUI, dest);
                        if (result != null)
                        {
                            ctx.RightClick(result);
                            return NodeStatus.Running;
                        }

                        // Results not visible yet — wait another tick
                        return NodeStatus.Running;
                    }

                    default:
                        return NodeStatus.Success;
                }
            }));

    private static IBehaviorNode HandleDocked() =>
        new SequenceNode("Undock",
            // Only undock when there are still route hops to cover.
            // When route is empty we are at the destination — stay docked.
            new ConditionNode("Is docked with route remaining?", ctx =>
                ctx.GameState.IsDocked &&
                ctx.GameState.RouteJumpsRemaining > 0),
            new ActionNode("Click undock", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                ctx.Wait(TimeSpan.FromSeconds(8));
                return NodeStatus.Success;
            }));

    private static IBehaviorNode HandleInSpace() =>
        new SequenceNode("Navigate in space",
            new ConditionNode("Is in space?", ctx => ctx.GameState.IsInSpace),
            new SelectorNode("Navigation steps",

                // No route left → arrived at destination
                new SequenceNode("Arrived",
                    new ConditionNode("Route empty?",
                        ctx => ctx.GameState.RouteJumpsRemaining == 0),
                    new ActionNode("Idle (arrived)", _ => NodeStatus.Success)),

                // Warping or jumping → wait
                new SequenceNode("Wait while warping or jumping",
                    new ConditionNode("Is warping/jumping?",
                        ctx => ctx.GameState.IsWarping || IsJumping(ctx.GameState)),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Post-jump cooldown (session change / gate cloak)
                new SequenceNode("Post-jump cooldown",
                    new ConditionNode("Cooldown active?",
                        ctx => !ctx.Blackboard.IsCooldownReady("post_jump")),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Stray context menu (opened by user or leftover) — dismiss it
                // so we don't accidentally warp/jump to something unexpected.
                new SequenceNode("Dismiss stray menu",
                    new ConditionNode("Unexpected menu open?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        !ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Press Escape", ctx =>
                    {
                        ctx.KeyPress(VirtualKey.Escape);
                        return NodeStatus.Success;
                    })),

                // Context menu that WE opened by right-clicking a route marker
                new SequenceNode("Handle nav context menu",
                    new ConditionNode("Our nav menu visible?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Click jump / dock / warp entry", ctx =>
                    {
                        ctx.Blackboard.Set("nav_menu_expected", false);

                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null) return NodeStatus.Failure;

                        var entries = menu.Entries;

                        // Search by UINode full texts (more robust than ContextMenuEntry.Text
                        // which may still be a glyph for some EVE UI versions).
                        static bool HasText(ContextMenuEntry e, string keyword) =>
                            (e.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true) ||
                            e.UINode.GetAllContainedDisplayTexts()
                                    .Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                        // Priority: jump → dock → warp-to-0 → any warp
                        var chosen =
                            entries.FirstOrDefault(e => HasText(e, "jump")) ??
                            entries.FirstOrDefault(e => HasText(e, "dock")) ??
                            entries.FirstOrDefault(e =>
                                HasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ", StringComparison.OrdinalIgnoreCase) ||
                                    t.Contains("0 m", StringComparison.OrdinalIgnoreCase) ||
                                    t.EndsWith(" 0",  StringComparison.OrdinalIgnoreCase))) ??
                            entries.FirstOrDefault(e => HasText(e, "warp"));

                        if (chosen == null)
                        {
                            // Menu appeared but has no usable nav entry — dismiss
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        ctx.Click(chosen.UINode);

                        if (HasText(chosen, "jump"))
                        {
                            // The ship will warp to the gate (IsWarping handles the wait).
                            // System change detection sets post_jump = 2 s after landing.
                            // Only set a short marker_click to prevent immediate re-click
                            // during the warp initiation window.
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(5));
                        }
                        else if (HasText(chosen, "dock"))
                        {
                            // Short cooldown while docking animation plays
                            ctx.Blackboard.SetCooldown("post_jump",    TimeSpan.FromSeconds(5));
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(5));
                        }
                        else
                        {
                            // Warp to waypoint — no long cooldown; IsWarping state handles the wait
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(3));
                        }

                        return NodeStatus.Success;
                    })),

                // No menu open → right-click the next route element marker
                new SequenceNode("Right-click route marker",
                    new ConditionNode("Has route markers?", ctx =>
                        FirstRouteMarker(ctx.GameState.ParsedUI) != null),
                    new ConditionNode("No menu open?", ctx =>
                        !ctx.GameState.HasContextMenu),
                    new ConditionNode("Action cooldown ready?", ctx =>
                        ctx.Blackboard.IsCooldownReady("marker_click")),
                    new ActionNode("Right-click next marker", ctx =>
                    {
                        var marker = FirstRouteMarker(ctx.GameState.ParsedUI);
                        if (marker == null) return NodeStatus.Failure;
                        ctx.Blackboard.Set("nav_menu_expected", true);
                        ctx.RightClick(marker);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    })),

                // Fallback — no route markers visible yet (just jumped in)
                new ActionNode("Wait for route panel", _ => NodeStatus.Success)));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the next-hop route marker.
    /// Sorted by (Region.X + Region.Y) ascending — the topmost-leftmost marker
    /// in the info panel route is always the next hop, matching the Elm reference.
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FirstRouteMarker(ParsedUI ui) =>
        ui.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers
            .OrderBy(m => m.Region.X + m.Region.Y)
            .FirstOrDefault();

    private static bool IsJumping(GameStateSnapshot gs) =>
        gs.ParsedUI.ShipUI?.Indication?.ManeuverType?
            .Contains("jump", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Finds the first search result node whose displayed texts include the system
    /// name AND "Solar System" (the category label shown in EVE search results).
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FindSearchResult(ParsedUI ui, string systemName)
    {
        return ui.UITree?.FindFirst(n =>
        {
            var texts = n.GetAllContainedDisplayTexts().ToList();
            return texts.Any(t => t.Contains(systemName, StringComparison.OrdinalIgnoreCase))
                && texts.Any(t => t.Contains("Solar System", StringComparison.OrdinalIgnoreCase));
        });
    }
}
