using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.AutopilotBot;

/// <summary>
/// Warp-to-0 travel bot with automatic final docking.
///
/// Behaviour:
///   1. Optionally sets an in-game route to a named system or station via universal search
///      (Shift+S). If no destination is provided, uses whatever route is already set.
///   2. Undocks if currently docked with route hops remaining.
///   3. Follows the route hop by hop: right-click next marker → Jump / Warp to 0.
///      Tracks system changes so the post-jump cloak cooldown is respected.
///   4. When the last hop brings the ship to the destination (RouteJumpsRemaining == 0
///      while still in space), docks at the nearest station:
///        a. Right-click the remaining route marker (shows "Dock" when already at station).
///        b. If context menu has a warp option → warp closer and retry.
///        c. Fall back to right-clicking the nearest station in the overview.
///   5. Once docked with no route remaining → RequestStop (monitoring resumes automatically).
///
/// Docking fix notes (vs old AutopilotBot / BookmarkDockBot):
///   • AutopilotBot used to idle on arrival without docking.
///   • BookmarkDockBot tried L-key / bookmark approach which was fragile.
///   • TravelBot uses a robust two-stage dock: route-marker menu first, overview fallback.
/// </summary>
public sealed class TravelBot : IBot
{
    /// <summary>
    /// Optional destination — solar system name or station name understood by EVE's
    /// universal search bar. When null/empty, the bot uses whatever in-game route is set.
    /// </summary>
    public string? Destination { get; init; }

    public string Name => "Travel Bot";

    public string Description => string.IsNullOrWhiteSpace(Destination)
        ? "Travel Bot — following preset in-game route, warp-to-0 + auto-dock"
        : $"Travel Bot → {Destination}";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs   = 2000,
        MinActionDelayMs = 100,
        MaxActionDelayMs = 300,
        CoordinateJitter = 3,
    };

    public void OnStart(BotContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(Destination))
            ctx.Blackboard.Set("travel_destination", Destination);
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Travel Root",
            TrackSystemChange(),   // always Failure — side-effect only
            HandleMessageBoxes(),
            SetupDestination(),    // sets in-game route; skipped if no destination
            HandleArrived(),       // docked + route empty → stop
            HandleUndock(),        // docked + route remaining → undock
            HandleFinalDock(),     // in space + route empty → dock at station
            HandleInSpace());      // in space + route remaining → warp/jump

    // ─── System-change tracker ────────────────────────────────────────────────

    /// <summary>
    /// Detects gate jumps by monitoring the current system name.
    /// On a system change sets a 2-second post-jump cooldown and clears the
    /// nav_menu_expected flag so stale context menus are dismissed cleanly.
    /// Always returns Failure so the parent Selector continues to the real logic.
    /// </summary>
    private static IBehaviorNode TrackSystemChange() =>
        new ActionNode("Track system change", ctx =>
        {
            var sys  = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.SystemName;
            var prev = ctx.Blackboard.Get<string>("travel_sys");

            // ── System changed → gate jump confirmed ────────────────────────
            if (sys != null && prev != null &&
                !string.Equals(sys, prev, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(2));
                ctx.Blackboard.Set("nav_menu_expected", false);
                ctx.Blackboard.Set("warp_just_stopped", false); // jump confirmed — stop waiting
                ctx.Log($"[Travel] Jump: {prev} → {sys}");
            }
            if (sys != null)
                ctx.Blackboard.Set("travel_sys", sys);

            // ── Track warp end for jump-confirmation window ─────────────────
            // When the ship stops warping (en-route to a gate), record it so
            // HandleInSpace can wait up to 8 s for the system name to change
            // before retrying the gate click.
            var wasWarping = ctx.Blackboard.Get<bool>("was_warping");
            var isWarping  = ctx.GameState.IsWarping;
            if (wasWarping && !isWarping && !ctx.GameState.IsDocked)
            {
                ctx.Blackboard.Set("warp_just_stopped", true);
                ctx.Blackboard.SetCooldown("jump_retry", TimeSpan.FromSeconds(8));
            }
            ctx.Blackboard.Set("was_warping", isWarping);

            return NodeStatus.Failure;
        });

    // ─── Message boxes ────────────────────────────────────────────────────────

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

    // ─── Destination setup ────────────────────────────────────────────────────

    /// <summary>
    /// Multi-tick state machine for setting the in-game autopilot destination.
    ///
    /// Phases (blackboard key "dest_phase"):
    ///   ""         → if route already set, clear it (right-click last marker); else click search bar
    ///   "clearing" → pick "Remove Waypoint" from menu; then click search bar
    ///   "typing"   → Ctrl+A, type destination name; wait 2 s for results
    ///   "selecting"→ if "Set Destination" context menu visible → click it (done)
    ///                else find station result row below "Stations" header, right-click it
    /// </summary>
    private static IBehaviorNode SetupDestination() =>
        new SequenceNode("Setup destination",
            new ConditionNode("Needs setup?", ctx =>
                !string.IsNullOrEmpty(ctx.Blackboard.Get<string>("travel_destination")) &&
                !ctx.Blackboard.Get<bool>("dest_ready")),
            new ActionNode("Destination state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("dest_phase") ?? "";
                var dest  = ctx.Blackboard.Get<string>("travel_destination")!;

                switch (phase)
                {
                    // Step 0: clear existing route (if any) before setting a new one
                    case "":
                    {
                        var markers = ctx.GameState.ParsedUI.InfoPanelContainer?
                            .InfoPanelRoute?.RouteElementMarkers ?? [];
                        if (markers.Count > 0)
                        {
                            ctx.RightClick(markers[^1]);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("dest_phase", "clearing");
                        }
                        else
                        {
                            ClickSearchBar(ctx);
                            ctx.Blackboard.Set("dest_phase", "typing");
                        }
                        return NodeStatus.Running;
                    }

                    // Step 1: pick "Remove Waypoint" from the menu, then click search bar
                    case "clearing":
                    {
                        if (ctx.GameState.HasContextMenu)
                        {
                            var remove = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault()?.Entries
                                .FirstOrDefault(e =>
                                    e.Text?.Contains("Remove",  StringComparison.OrdinalIgnoreCase) == true ||
                                    e.Text?.Contains("Clear",   StringComparison.OrdinalIgnoreCase) == true);
                            if (remove != null)
                            {
                                ctx.Click(remove.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(500));
                            }
                            else
                            {
                                ctx.KeyPress(VirtualKey.Escape);
                            }
                        }
                        ClickSearchBar(ctx);
                        ctx.Blackboard.Set("dest_phase", "typing");
                        return NodeStatus.Running;
                    }

                    // Step 2: type destination and press Enter to open the search results window
                    case "typing":
                    {
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);
                        ctx.TypeText(dest);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        ctx.KeyPress(VirtualKey.Enter);           // open search results window
                        ctx.Wait(TimeSpan.FromSeconds(2));        // wait for results to populate
                        ctx.Blackboard.Set("dest_phase", "selecting");
                        return NodeStatus.Running;
                    }

                    // Step 3: expand "Stations" group if collapsed, then right-click the row
                    case "selecting":
                    {
                        // Context menu open → look for "Set Destination"
                        if (ctx.GameState.HasContextMenu)
                        {
                            var setDest = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault()?.Entries
                                .FirstOrDefault(e => e.Text?.Contains("destination",
                                    StringComparison.OrdinalIgnoreCase) == true);
                            if (setDest != null)
                            {
                                ctx.Click(setDest.UINode);
                                ctx.Wait(TimeSpan.FromSeconds(1));
                                CloseSearchResultsWindow(ctx);
                                ctx.Wait(TimeSpan.FromSeconds(2));
                                ctx.Blackboard.Set("dest_ready", true);
                                ctx.Blackboard.Set("dest_phase", "done");
                                return NodeStatus.Success;
                            }
                            // Not our menu — dismiss and retry
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Running;
                        }

                        // Try to find the result row (visible after group is expanded)
                        var stationRow = FindStationResultRow(ctx.GameState.ParsedUI, dest);
                        if (stationRow != null)
                        {
                            ctx.RightClick(stationRow);
                            return NodeStatus.Running;
                        }

                        // Result row not visible — try to expand the "Stations" or "Systems" group
                        var group = FindResultGroup(ctx.GameState.ParsedUI, "Station")
                                 ?? FindResultGroup(ctx.GameState.ParsedUI, "Solar")
                                 ?? FindResultGroup(ctx.GameState.ParsedUI, "System");
                        if (group != null)
                        {
                            ctx.Click(group);
                            ctx.Wait(TimeSpan.FromMilliseconds(400));
                            return NodeStatus.Running;
                        }

                        // Results window not yet rendered — wait for next tick
                        return NodeStatus.Running;
                    }

                    default:
                        return NodeStatus.Success;
                }
            }));

    // ─── Arrived ─────────────────────────────────────────────────────────────

    private static IBehaviorNode HandleArrived() =>
        new SequenceNode("Arrived",
            new ConditionNode("Docked AND route empty?", ctx =>
                ctx.GameState.IsDocked && ctx.GameState.RouteJumpsRemaining == 0),
            new ActionNode("Request stop", ctx =>
            {
                ctx.Log("[Travel] Arrived and docked — stopping");
                ctx.RequestStop();
                return NodeStatus.Success;
            }));

    // ─── Undock ───────────────────────────────────────────────────────────────

    private static IBehaviorNode HandleUndock() =>
        new SequenceNode("Undock",
            new ConditionNode("Docked AND route remaining?", ctx =>
                ctx.GameState.IsDocked && ctx.GameState.RouteJumpsRemaining > 0),
            // Cooldown prevents spam-clicking the undock button if the client is slow
            new ConditionNode("Undock cooldown ready?", ctx =>
                ctx.Blackboard.IsCooldownReady("undock_cooldown")),
            new ActionNode("Click undock", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                // 15 s covers the undocking animation + session change window
                ctx.Blackboard.SetCooldown("undock_cooldown", TimeSpan.FromSeconds(15));
                return NodeStatus.Success;
            }));

    // ─── Final dock (in space, route complete) ────────────────────────────────

    /// <summary>
    /// Route is done but ship is still in space — dock at the destination station.
    ///
    /// Strategy (blackboard key "dock_phase"):
    ///   ""              → right-click remaining route marker (shows "Dock" when near station)
    ///   "marker_menu"   → menu visible: click Dock, or warp to 0 then retry from ""
    ///                     no menu: fall through to overview
    ///   "overview"      → right-click nearest station in overview
    ///   "overview_menu" → click Dock from overview context menu
    ///   "docking"       → animation in progress; wait for IsDocked (→ HandleArrived)
    /// </summary>
    private static IBehaviorNode HandleFinalDock() =>
        new SequenceNode("Final dock",
            new ConditionNode("In space AND route done?", ctx =>
                ctx.GameState.IsInSpace && ctx.GameState.RouteJumpsRemaining == 0),
            new ActionNode("Dock state machine", ctx =>
            {
                // Let warp complete before acting
                if (ctx.GameState.IsWarping) return NodeStatus.Running;
                if (!ctx.Blackboard.IsCooldownReady("post_jump")) return NodeStatus.Running;

                var phase = ctx.Blackboard.Get<string>("dock_phase") ?? "";

                switch (phase)
                {
                    // ── Try route marker first ─────────────────────────────────────────
                    case "":
                    {
                        var marker = ctx.GameState.ParsedUI.InfoPanelContainer?
                            .InfoPanelRoute?.RouteElementMarkers
                            .OrderBy(m => m.Region.X + m.Region.Y)
                            .FirstOrDefault();

                        if (marker != null)
                        {
                            ctx.RightClick(marker);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            // Give up to 6 s for the context menu to appear in the next snapshot
                            ctx.Blackboard.SetCooldown("dock_menu_timeout", TimeSpan.FromSeconds(6));
                            ctx.Blackboard.Set("dock_phase", "marker_menu");
                        }
                        else
                        {
                            ctx.Blackboard.Set("dock_phase", "overview");
                        }
                        return NodeStatus.Running;
                    }

                    // ── Handle context menu from route marker ──────────────────────────
                    case "marker_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            // Menu hasn't appeared yet — keep waiting until the timeout
                            if (!ctx.Blackboard.IsCooldownReady("dock_menu_timeout"))
                                return NodeStatus.Running;
                            // Timed out — fall back to overview docking
                            ctx.Log("[Travel] Dock menu timeout — falling back to overview");
                            ctx.Blackboard.Set("dock_phase", "overview");
                            return NodeStatus.Running;
                        }

                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null)
                        {
                            ctx.Blackboard.Set("dock_phase", "overview");
                            return NodeStatus.Running;
                        }

                        var dock = menu.Entries.FirstOrDefault(e =>
                            EntryHasText(e, "Dock"));
                        if (dock != null)
                        {
                            ctx.Click(dock.UINode);
                            ctx.Blackboard.Set("dock_phase", "docking");
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                            return NodeStatus.Running;
                        }

                        // Not yet at 0 m — warp closer, then retry marker menu
                        var warp =
                            menu.Entries.FirstOrDefault(e =>
                                EntryHasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ") || t.Contains("0 m") || t.EndsWith(" 0"))) ??
                            menu.Entries.FirstOrDefault(e => EntryHasText(e, "warp"));

                        if (warp != null)
                        {
                            ctx.Click(warp.UINode);
                            ctx.Blackboard.Set("dock_phase", "");   // re-enter after warp
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(3));
                            return NodeStatus.Running;
                        }

                        // Nothing useful — dismiss and fall back to overview
                        ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("dock_phase", "overview");
                        return NodeStatus.Running;
                    }

                    // ── Find station in overview and right-click it ────────────────────
                    case "overview":
                    {
                        // Dismiss any stray menu first
                        if (ctx.GameState.HasContextMenu)
                        {
                            var dock = ctx.GameState.ParsedUI.ContextMenus
                                .SelectMany(m => m.Entries)
                                .FirstOrDefault(e =>
                                    e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);
                            if (dock != null)
                            {
                                ctx.Click(dock.UINode);
                                ctx.Blackboard.Set("dock_phase", "docking");
                                return NodeStatus.Running;
                            }
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Running;
                        }

                        var station = FindStation(ctx.GameState.ParsedUI);
                        if (station == null)
                        {
                            // Station not in overview yet — wait
                            ctx.Log("[Travel] Waiting for station in overview…");
                            return NodeStatus.Running;
                        }

                        ctx.RightClick(station.UINode);
                        ctx.Blackboard.Set("dock_phase", "overview_menu");
                        return NodeStatus.Running;
                    }

                    // ── Click Dock from overview context menu ──────────────────────────
                    case "overview_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            ctx.Blackboard.Set("dock_phase", "overview");
                            return NodeStatus.Running;
                        }

                        var dock = ctx.GameState.ParsedUI.ContextMenus
                            .SelectMany(m => m.Entries)
                            .FirstOrDefault(e =>
                                e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);

                        if (dock != null)
                        {
                            ctx.Click(dock.UINode);
                            ctx.Blackboard.Set("dock_phase", "docking");
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                            return NodeStatus.Running;
                        }

                        // No Dock entry — dismiss and retry from overview
                        ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("dock_phase", "overview");
                        return NodeStatus.Running;
                    }

                    case "docking":
                        // Docking animation in progress — HandleArrived fires once IsDocked
                        return NodeStatus.Running;

                    default:
                        return NodeStatus.Running;
                }
            }));

    // ─── In-space navigation ──────────────────────────────────────────────────

    private static IBehaviorNode HandleInSpace() =>
        new SequenceNode("Navigate in space",
            new ConditionNode("In space with route?", ctx =>
                ctx.GameState.IsInSpace && ctx.GameState.RouteJumpsRemaining > 0),
            new SelectorNode("Nav steps",

                // Warping, jumping, or waiting to confirm the jump via system change
                new SequenceNode("Wait for jump confirmation",
                    new ConditionNode("Warping or awaiting jump?", ctx =>
                        ctx.GameState.IsWarping ||
                        IsJumping(ctx.GameState) ||
                        // Ship stopped warping but system name hasn't changed yet —
                        // wait up to 8 s; if still no change, retry the gate click.
                        (ctx.Blackboard.Get<bool>("warp_just_stopped") &&
                         !ctx.Blackboard.IsCooldownReady("jump_retry"))),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Respect post-jump cloak / session-change window
                new SequenceNode("Post-jump cooldown",
                    new ConditionNode("Cooldown?",
                        ctx => !ctx.Blackboard.IsCooldownReady("post_jump")),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Dismiss a context menu we didn't open (user leftover, or mis-click)
                new SequenceNode("Dismiss stray menu",
                    new ConditionNode("Unexpected menu?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        !ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Escape", ctx =>
                    {
                        ctx.KeyPress(VirtualKey.Escape);
                        return NodeStatus.Success;
                    })),

                // Context menu we opened — click Jump / Dock / Warp to 0
                new SequenceNode("Handle nav menu",
                    new ConditionNode("Our menu?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Click jump/dock/warp", ctx =>
                    {
                        ctx.Blackboard.Set("nav_menu_expected", false);

                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null) return NodeStatus.Failure;
                        var entries = menu.Entries;

                        // Priority: jump → dock → warp-to-0 → any warp
                        var chosen =
                            entries.FirstOrDefault(e => EntryHasText(e, "jump")) ??
                            entries.FirstOrDefault(e => EntryHasText(e, "dock")) ??
                            entries.FirstOrDefault(e =>
                                EntryHasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ") || t.Contains("0 m") || t.EndsWith(" 0"))) ??
                            entries.FirstOrDefault(e => EntryHasText(e, "warp"));

                        if (chosen == null)
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        ctx.Click(chosen.UINode);

                        if (EntryHasText(chosen, "jump"))
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(5));
                        else if (EntryHasText(chosen, "dock"))
                        {
                            ctx.Blackboard.SetCooldown("post_jump",    TimeSpan.FromSeconds(5));
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(5));
                        }
                        else
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(3));

                        return NodeStatus.Success;
                    })),

                // Right-click next route hop marker
                new SequenceNode("Right-click route marker",
                    new ConditionNode("Has markers?", ctx => FirstMarker(ctx) != null),
                    new ConditionNode("No menu open?", ctx => !ctx.GameState.HasContextMenu),
                    new ConditionNode("Click cooldown ready?",
                        ctx => ctx.Blackboard.IsCooldownReady("marker_click")),
                    new ActionNode("Right-click marker", ctx =>
                    {
                        var marker = FirstMarker(ctx);
                        if (marker == null) return NodeStatus.Failure;
                        ctx.Blackboard.Set("nav_menu_expected", true);
                        ctx.Blackboard.Set("warp_just_stopped", false); // reset for next gate
                        ctx.RightClick(marker);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    })),

                // Route panel not yet updated after jump — wait
                new ActionNode("Wait for route panel", _ => NodeStatus.Success)));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the "Close" button at the bottom of the Search Results window.
    /// No-op if the window is not visible.
    /// </summary>
    private static void CloseSearchResultsWindow(BotContext ctx)
    {
        var closeBtn = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 120) return false;
            if (n.Region.Height < 15 || n.Region.Height > 50) return false;
            if (n.Region.Width < 30 || n.Region.Width > 200) return false;
            return string.Equals(OwnText(n), "Close", StringComparison.OrdinalIgnoreCase);
        });
        if (closeBtn != null)
        {
            ctx.Click(closeBtn);
            ctx.Wait(TimeSpan.FromMilliseconds(300));
        }
    }

    /// <summary>
    /// Clicks the "Search New Eden" bar at the top-left of the EVE client.
    /// Falls back to Shift+S if the bar cannot be found in the UI tree.
    /// </summary>
    private static void ClickSearchBar(BotContext ctx)
    {
        var bar = FindSearchBar(ctx.GameState.ParsedUI);
        if (bar != null)
        {
            ctx.Click(bar);
            ctx.Wait(TimeSpan.FromMilliseconds(400));
        }
        else
        {
            // Fallback: EVE keyboard shortcut to open the search window
            ctx.KeyPress(VirtualKey.S, VirtualKey.Shift);
            ctx.Wait(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Finds the EVE "Search New Eden" edit box in the UI tree.
    /// It lives at the top of the screen (low Y) and has Search/New Eden in its hint or name.
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FindSearchBar(ParsedUI ui) =>
        ui.UITree?.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
            n.Region.Y < 100 &&
            n.Region.Height is > 8 and < 40 &&
            n.Region.Width > 80 &&
            (n.Node.GetDictString("_hint")?.Contains("Search",   StringComparison.OrdinalIgnoreCase) == true ||
             n.Node.GetDictString("_hint")?.Contains("New Eden", StringComparison.OrdinalIgnoreCase) == true ||
             n.Node.GetDictString("_name")?.Contains("search",   StringComparison.OrdinalIgnoreCase) == true));

    /// <summary>
    /// Locates a collapsible group header in EVE's search results window
    /// (e.g. "Stations (1)", "Solar Systems (3)") that needs to be clicked to expand.
    /// Must be below the nav bar (Y > 100).
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FindResultGroup(ParsedUI ui, string groupKeyword)
    {
        return ui.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 100) return false;
            if (n.Region.Height < 8 || n.Region.Height > 50) return false;
            var t = OwnText(n);
            // Matches "Stations (1)", "Solar Systems (3)", etc.
            return t.StartsWith(groupKeyword, StringComparison.OrdinalIgnoreCase) &&
                   t.Contains('(') && t.Length < 40;
        });
    }

    /// <summary>
    /// Locates a station/system result row in EVE's search results window (opened via Enter).
    /// The search window sits below the nav bar (Y > 100). Only visible after the group is expanded.
    /// Checks both the node's own text and all contained display texts to handle EVE's
    /// label-in-container row structure.
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FindStationResultRow(ParsedUI ui, string dest)
    {
        // Locate the "Stations" group header as an anchor
        UITreeNodeWithDisplayRegion? header = null;
        ui.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 100) return false;
            var t = OwnText(n);
            if (t.StartsWith("Station", StringComparison.OrdinalIgnoreCase) &&
                t.Contains('(') && t.Length < 40 && n.Region.Height < 50)
            {
                header = n;
                return true;
            }
            return false;
        });

        bool RowContainsDest(UITreeNodeWithDisplayRegion n) =>
            OwnText(n).Contains(dest, StringComparison.OrdinalIgnoreCase) ||
            n.GetAllContainedDisplayTexts()
             .Any(t => t.Contains(dest, StringComparison.OrdinalIgnoreCase));

        bool IsResultRow(UITreeNodeWithDisplayRegion n) =>
            n.Region.Y >= 120 &&
            n.Region.Height is >= 10 and <= 50 &&
            n.Region.Width >= 60 &&
            !n.Node.PythonObjectTypeName.Contains("Edit", StringComparison.OrdinalIgnoreCase);

        if (header != null)
        {
            var row = ui.UITree?.FindFirst(n =>
                n.Region.Y > header.Region.Y && IsResultRow(n) && RowContainsDest(n));
            if (row != null) return row;
        }

        // Fallback: any result row below the nav bar containing the destination text
        return ui.UITree?.FindFirst(n => IsResultRow(n) && RowContainsDest(n));
    }

    private static UITreeNodeWithDisplayRegion? FirstMarker(BotContext ctx) =>
        ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers
            .OrderBy(m => m.Region.X + m.Region.Y)
            .FirstOrDefault();

    private static bool IsJumping(GameStateSnapshot gs) =>
        gs.ParsedUI.ShipUI?.Indication?.ManeuverType?
            .Contains("jump", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Checks whether a context menu entry's displayed text contains the given keyword.
    /// Falls back to searching all display texts in the entry's sub-tree, covering EVE UI
    /// versions where ContextMenuEntry.Text may be a glyph or empty string.
    /// </summary>
    private static bool EntryHasText(ContextMenuEntry e, string keyword) =>
        e.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true ||
        e.UINode.GetAllContainedDisplayTexts()
                .Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the nearest dockable station/structure in the active overview.</summary>
    private static OverviewEntry? FindStation(ParsedUI ui)
    {
        var overview = ui.OverviewWindows.FirstOrDefault();
        if (overview == null) return null;

        static bool LooksLikeStation(OverviewEntry e)
        {
            string[] kw = [
                "Station", "Outpost", "Astrahus", "Fortizar", "Keepstar",
                "Raitaru", "Azbel", "Sotiyo", "Tatara", "Athanor", "Metenox",
                "Assembly", "Plant", "Structure", "Engineering", "Citadel", "Refinery",
            ];
            var texts = new[] { e.ObjectType ?? "", e.Name ?? "" }
                .Concat(e.Texts)
                .Concat(e.CellsTexts.Values);
            return texts.Any(t => kw.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }

        static bool LooksLikeNonDockable(OverviewEntry e)
        {
            string[] excl = ["Ship", "Pod", "Capsule", "Drone", "Fighter",
                              "Gate", "Beacon", "Asteroid", "Cloud", "Wreck"];
            var texts = new[] { e.ObjectType ?? "" }.Concat(e.CellsTexts.Values);
            return texts.Any(t => excl.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }

        // Prefer explicit station/structure match
        var match = overview.Entries
            .Where(e => LooksLikeStation(e) && !LooksLikeNonDockable(e))
            .OrderBy(e => e.DistanceInMeters ?? double.MaxValue)
            .FirstOrDefault();

        // Fall back to nearest non-gate/ship entry
        return match ?? overview.Entries
            .Where(e => !LooksLikeNonDockable(e) && e.DistanceInMeters.HasValue)
            .OrderBy(e => e.DistanceInMeters!.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reads a node's own _setText or _text (not its children) and strips EVE HTML tags.
    /// Used to avoid matching container group headers when searching for specific result rows.
    /// </summary>
    private static string OwnText(UITreeNodeWithDisplayRegion n)
    {
        var s = EveTextUtil.StripTags(n.Node.GetDictString("_setText"));
        if (!string.IsNullOrEmpty(s)) return s;
        return EveTextUtil.StripTags(n.Node.GetDictString("_text")) ?? "";
    }
}
