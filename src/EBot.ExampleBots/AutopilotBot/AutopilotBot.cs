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
            HandleMessageBoxes(),
            SetupDestination(),
            HandleDocked(),
            HandleInSpace());

    // ─── Sub-trees ───────────────────────────────────────────────────────────

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

                    // ── Step 2: click search field, type, Enter ──────────────
                    case "typing":
                    {
                        // Click any focused/visible text input field
                        var field = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                            n.Node.PythonObjectTypeName.Contains("EditText",
                                StringComparison.OrdinalIgnoreCase) ||
                            n.Node.PythonObjectTypeName.Contains("SingleLineEditText",
                                StringComparison.OrdinalIgnoreCase));
                        if (field != null)
                            ctx.Click(field);

                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);   // select all
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
            new ConditionNode("Is docked?", ctx => ctx.GameState.IsDocked),
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

                // Context menu is open — pick the right entry
                new SequenceNode("Handle context menu",
                    new ConditionNode("Context menu visible?",
                        ctx => ctx.GameState.HasContextMenu),
                    new ActionNode("Click jump / dock / warp entry", ctx =>
                    {
                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null) return NodeStatus.Failure;

                        var entries = menu.Entries;

                        // Priority: jump → dock → warp-to-0 → any warp
                        var chosen =
                            entries.FirstOrDefault(e =>
                                e.Text?.Contains("jump", StringComparison.OrdinalIgnoreCase) == true) ??
                            entries.FirstOrDefault(e =>
                                e.Text?.Contains("dock", StringComparison.OrdinalIgnoreCase) == true) ??
                            entries.FirstOrDefault(e =>
                                e.Text?.Contains("warp", StringComparison.OrdinalIgnoreCase) == true &&
                                (e.Text.Contains(" 0 ", StringComparison.OrdinalIgnoreCase) ||
                                 e.Text.Contains("0 m", StringComparison.OrdinalIgnoreCase) ||
                                 e.Text.EndsWith(" 0", StringComparison.OrdinalIgnoreCase))) ??
                            entries.FirstOrDefault(e =>
                                e.Text?.Contains("warp", StringComparison.OrdinalIgnoreCase) == true);

                        if (chosen == null)
                        {
                            // Wrong menu (e.g., residual from something else) — dismiss
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        ctx.Click(chosen.UINode);

                        if (chosen.Text?.Contains("jump", StringComparison.OrdinalIgnoreCase) == true ||
                            chosen.Text?.Contains("dock", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // Give extra time for session change / docking
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                        }

                        return NodeStatus.Success;
                    })),

                // No menu open → right-click the next route element marker
                new SequenceNode("Right-click route marker",
                    new ConditionNode("Has route markers?", ctx =>
                        FirstRouteMarker(ctx.GameState.ParsedUI) != null),
                    new ConditionNode("Action cooldown ready?", ctx =>
                        ctx.Blackboard.IsCooldownReady("marker_click")),
                    new ActionNode("Right-click next marker", ctx =>
                    {
                        var marker = FirstRouteMarker(ctx.GameState.ParsedUI);
                        if (marker == null) return NodeStatus.Failure;
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
