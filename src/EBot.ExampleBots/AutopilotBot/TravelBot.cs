using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.AutopilotBot;

/// <summary>
/// Warp-to-0 travel bot with automatic final docking.
///
/// Key guards:
///   • HandleFinalDock only fires after navigation was active (was_in_space_with_route)
///     OR an emergency_dock is declared (shield &lt; 75%).
///   • HandleInSpace nav menu NEVER clicks "Dock" — only "Jump" or "Warp".
///   • FindStation only returns confirmed station/structure keywords.
///   • Watchdog: if in space with route and no progress for 15 s → retry marker click.
///   • HandleFinalDock: improved docking with explicit state for every sub-step and
///     a tick-counter wait (not a cooldown) for the overview station search.
/// </summary>
public sealed class TravelBot : IBot
{
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

        // Seed the progress heartbeat so the watchdog doesn't fire on the very first tick.
        ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Travel Root",
            TrackSystemChange(),      // always Failure — side-effects only (heartbeat + system log)
            HandleEmergencyDock(),    // shield < 75% → dock immediately
            HandleMessageBoxes(),
            SetupDestination(),
            HandleArrived(),          // docked + route empty → stop
            HandleUndock(),           // docked + route remaining → undock
            HandleFinalDock(),        // in space + route done (or emergency) → dock at station
            HandleInSpace());         // in space + route remaining → warp/jump

    // ─── System-change tracker ────────────────────────────────────────────────

    /// <summary>
    /// Tracks system changes (gate jumps) and warp state.
    /// Also resets the progress heartbeat whenever the ship is making forward motion
    /// so the stuck watchdog doesn't fire during legitimate travel.
    /// Always returns Failure so the parent Selector continues.
    /// </summary>
    private static IBehaviorNode TrackSystemChange() =>
        new ActionNode("Track system change", ctx =>
        {
            var sys  = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.SystemName;
            var prev = ctx.Blackboard.Get<string>("travel_sys");

            bool jumped = sys != null && prev != null &&
                          !string.Equals(sys, prev, StringComparison.OrdinalIgnoreCase);
            if (jumped)
            {
                ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(2));
                ctx.Blackboard.Set("nav_menu_expected", false);
                ctx.Blackboard.Set("warp_just_stopped", false);
                ctx.Log($"[Travel] Jump: {prev} → {sys}");
            }
            if (sys != null) ctx.Blackboard.Set("travel_sys", sys);

            var wasWarping = ctx.Blackboard.Get<bool>("was_warping");
            var isWarping  = ctx.GameState.IsWarping;
            if (wasWarping && !isWarping && !ctx.GameState.IsDocked)
            {
                ctx.Blackboard.Set("warp_just_stopped", true);
                ctx.Blackboard.SetCooldown("jump_retry", TimeSpan.FromSeconds(8));
            }
            ctx.Blackboard.Set("was_warping", isWarping);

            // Reset the stuck watchdog whenever the ship is visibly in motion.
            if (isWarping || IsJumping(ctx.GameState) || jumped)
                ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));

            return NodeStatus.Failure;
        });

    // ─── Emergency dock ───────────────────────────────────────────────────────

    private static IBehaviorNode HandleEmergencyDock() =>
        new SequenceNode("Emergency dock trigger",
            new ConditionNode("Shield < 75% while in space?", ctx =>
                ctx.GameState.IsInSpace &&
                !ctx.GameState.IsDocked &&
                !ctx.Blackboard.Get<bool>("emergency_dock") &&
                (ctx.GameState.ParsedUI.ShipUI?.HitpointsPercent?.Shield ?? 100) < 75),
            new ActionNode("Activate emergency dock", ctx =>
            {
                ctx.Log("[Travel] EMERGENCY: shield < 75% — docking immediately");
                ctx.Blackboard.Set("emergency_dock", true);
                ctx.Blackboard.Set("dock_phase", "");
                ctx.Blackboard.Set("was_in_space_with_route", true);
                return NodeStatus.Success;
            }));

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

                    case "clearing":
                    {
                        if (ctx.GameState.HasContextMenu)
                        {
                            var remove = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault()?.Entries
                                .FirstOrDefault(e =>
                                    e.Text?.Contains("Remove", StringComparison.OrdinalIgnoreCase) == true ||
                                    e.Text?.Contains("Clear",  StringComparison.OrdinalIgnoreCase) == true);
                            if (remove != null) { ctx.Click(remove.UINode); ctx.Wait(TimeSpan.FromMilliseconds(500)); }
                            else ctx.KeyPress(VirtualKey.Escape);
                        }
                        ClickSearchBar(ctx);
                        ctx.Blackboard.Set("dest_phase", "typing");
                        return NodeStatus.Running;
                    }

                    case "typing":
                    {
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);
                        ctx.TypeText(dest);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        ctx.KeyPress(VirtualKey.Enter);
                        ctx.Wait(TimeSpan.FromSeconds(2));
                        ctx.Blackboard.Set("dest_phase", "selecting");
                        return NodeStatus.Running;
                    }

                    case "selecting":
                    {
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
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Running;
                        }

                        var stationRow = FindStationResultRow(ctx.GameState.ParsedUI, dest);
                        if (stationRow != null) { ctx.RightClick(stationRow); return NodeStatus.Running; }

                        var group = FindResultGroup(ctx.GameState.ParsedUI, "Station")
                                 ?? FindResultGroup(ctx.GameState.ParsedUI, "Solar")
                                 ?? FindResultGroup(ctx.GameState.ParsedUI, "System");
                        if (group != null) { ctx.Click(group); ctx.Wait(TimeSpan.FromMilliseconds(400)); }
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
                ctx.Blackboard.Set("emergency_dock", false);
                ctx.Blackboard.Set("was_in_space_with_route", false);
                ctx.Log("[Travel] Arrived and docked — stopping");
                ctx.RequestStop();
                return NodeStatus.Success;
            }));

    // ─── Undock ───────────────────────────────────────────────────────────────

    private static IBehaviorNode HandleUndock() =>
        new SequenceNode("Undock",
            new ConditionNode("Docked AND route remaining?", ctx =>
                ctx.GameState.IsDocked && ctx.GameState.RouteJumpsRemaining > 0),
            new ConditionNode("Undock cooldown ready?",
                ctx => ctx.Blackboard.IsCooldownReady("undock_cooldown")),
            new ActionNode("Click undock", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                ctx.Blackboard.SetCooldown("undock_cooldown", TimeSpan.FromSeconds(15));
                return NodeStatus.Success;
            }));

    // ─── Final dock ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fires only when:
    ///   • Route complete (RouteJumpsRemaining == 0) AND was_in_space_with_route is true
    ///     — prevents false-triggering if route was never set.
    ///   • OR emergency_dock == true (shield drop).
    ///
    /// Navigation is exclusively via route-marker right-click — no overview interaction.
    ///
    /// dock_phase state machine:
    ///   ""                → right-click the final route marker
    ///   "marker_menu"     → Dock → "docking"; Warp to 0 → "warping_to_dock"; else retry
    ///   "warping_to_dock" → wait for warp to complete (IsWarping guard), then back to ""
    ///   "docking"         → wait for IsDocked (HandleArrived fires)
    /// </summary>
    private static IBehaviorNode HandleFinalDock() =>
        new SequenceNode("Final dock",
            new ConditionNode("Final dock needed?", ctx =>
                ctx.GameState.IsInSpace &&
                ((ctx.GameState.RouteJumpsRemaining == 0 &&
                  ctx.Blackboard.Get<bool>("was_in_space_with_route")) ||
                 ctx.Blackboard.Get<bool>("emergency_dock"))),
            new ActionNode("Dock state machine", ctx =>
            {
                // Always wait for any active warp — do not act mid-warp.
                if (ctx.GameState.IsWarping) return NodeStatus.Running;
                if (!ctx.Blackboard.IsCooldownReady("post_jump")) return NodeStatus.Running;

                var phase = ctx.Blackboard.Get<string>("dock_phase") ?? "";

                switch (phase)
                {
                    // ── Right-click the final route marker ────────────────────────────
                    case "":
                    {
                        var marker = ctx.GameState.ParsedUI.InfoPanelContainer?
                            .InfoPanelRoute?.RouteElementMarkers
                            .OrderBy(m => m.Region.X + m.Region.Y)
                            .FirstOrDefault();

                        if (marker == null)
                        {
                            ctx.Log("[Travel] Final dock: route has 0 jumps but no marker visible — waiting for UI to update");
                            return NodeStatus.Running;
                        }

                        ctx.Log("[Travel] Final dock: right-clicking destination route marker to open dock menu");
                        ctx.RightClick(marker);
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        ctx.Blackboard.SetCooldown("dock_menu_timeout", TimeSpan.FromSeconds(6));
                        ctx.Blackboard.Set("dock_phase", "marker_menu");
                        return NodeStatus.Running;
                    }

                    // ── Handle route marker context menu ───────────────────────────────
                    case "marker_menu":
                    {
                        // Collect entries from parsed ContextMenus first (fast path).
                        // Also scan the raw UI tree for MenuEntryView nodes — the route-marker
                        // context menu may not use the Python ContextMenu type, which means
                        // ParsedUI.ContextMenus is empty even though the menu is visible.
                        var parsedEntries = ctx.GameState.ParsedUI.ContextMenus
                            .SelectMany(m => m.Entries).ToList();

                        var treeEntries = (ctx.GameState.ParsedUI.UITree?
                            .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView") ?? [])
                            .ToList();

                        bool menuVisible = parsedEntries.Count > 0 || treeEntries.Count > 0;

                        if (!menuVisible)
                        {
                            if (!ctx.Blackboard.IsCooldownReady("dock_menu_timeout"))
                                return NodeStatus.Running;
                            ctx.Log("[Travel] Final dock: route marker menu never appeared — retrying right-click");
                            ctx.Blackboard.Set("dock_phase", "");
                            return NodeStatus.Running;
                        }

                        // ── Look for Dock ──────────────────────────────────────────────────
                        var dockEntry = parsedEntries.FirstOrDefault(e => EntryHasText(e, "Dock"));

                        UITreeNodeWithDisplayRegion? dockNode = null;
                        if (dockEntry == null)
                            dockNode = treeEntries.FirstOrDefault(n =>
                                n.GetAllContainedDisplayTexts().Any(t =>
                                    t.Trim().Equals("Dock", StringComparison.OrdinalIgnoreCase) ||
                                    t.Trim().StartsWith("Dock ", StringComparison.OrdinalIgnoreCase)));

                        if (dockEntry != null)
                        {
                            ctx.Log("[Travel] Final dock: station is in docking range — clicking Dock in route marker menu");
                            ctx.Click(dockEntry.UINode);
                            ctx.Blackboard.Set("dock_phase", "docking");
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                            return NodeStatus.Running;
                        }
                        if (dockNode != null)
                        {
                            ctx.Log("[Travel] Final dock: station is in docking range — clicking Dock (found via UI tree scan)");
                            ctx.Click(dockNode);
                            ctx.Blackboard.Set("dock_phase", "docking");
                            ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                            return NodeStatus.Running;
                        }

                        // ── Not in range — look for Warp to 0 ─────────────────────────────
                        var warpEntry =
                            parsedEntries.FirstOrDefault(e =>
                                EntryHasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ") || t.Contains("0 m") || t.EndsWith(" 0"))) ??
                            parsedEntries.FirstOrDefault(e => EntryHasText(e, "warp"));

                        UITreeNodeWithDisplayRegion? warpNode = null;
                        if (warpEntry == null)
                            warpNode = treeEntries
                                .Where(n => n.GetAllContainedDisplayTexts()
                                    .Any(t => t.Contains("Warp", StringComparison.OrdinalIgnoreCase)))
                                .MinBy(n => n.Region.Y);

                        if (warpEntry != null)
                        {
                            ctx.Log($"[Travel] Final dock: not in docking range — warping to 0 via route marker menu ('{warpEntry.Text}')");
                            ctx.Click(warpEntry.UINode);
                            ctx.Blackboard.Set("dock_phase", "warping_to_dock");
                            return NodeStatus.Running;
                        }
                        if (warpNode != null)
                        {
                            ctx.Log("[Travel] Final dock: not in docking range — warping to 0 (found via UI tree scan)");
                            ctx.Click(warpNode);
                            ctx.Blackboard.Set("dock_phase", "warping_to_dock");
                            return NodeStatus.Running;
                        }

                        // Menu visible but contains neither Dock nor Warp — wrong menu or gate.
                        ctx.Log($"[Travel] Final dock: marker menu has {parsedEntries.Count} parsed + {treeEntries.Count} tree entries but no Dock/Warp — dismissing and retrying");
                        ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("dock_phase", "");
                        return NodeStatus.Running;
                    }

                    // ── Wait for warp-to-0 to complete, then retry from "" ─────────────
                    case "warping_to_dock":
                    {
                        // IsWarping guard at the top already handles the in-warp wait.
                        // We reach here only when warp has stopped.
                        ctx.Log("[Travel] Final dock: warp-to-0 complete — re-opening route marker menu to dock");
                        ctx.Blackboard.Set("dock_phase", "");
                        return NodeStatus.Running;
                    }

                    case "docking":
                        return NodeStatus.Running;

                    default:
                        ctx.Blackboard.Set("dock_phase", "");
                        return NodeStatus.Running;
                }
            }));

    // ─── In-space navigation ──────────────────────────────────────────────────

    /// <summary>
    /// Hop-by-hop navigation while RouteJumpsRemaining > 0.
    /// Sets was_in_space_with_route on each tick so HandleFinalDock can fire
    /// correctly when the route is complete.
    /// NEVER clicks "Dock" — docking is exclusively HandleFinalDock's job.
    /// Watchdog: if no progress (warp/jump) for 15 s, resets state and retries marker click.
    /// </summary>
    private static IBehaviorNode HandleInSpace() =>
        new SequenceNode("Navigate in space",
            new ConditionNode("In space with route?", ctx =>
            {
                if (ctx.GameState.IsInSpace && ctx.GameState.RouteJumpsRemaining > 0)
                {
                    ctx.Blackboard.Set("was_in_space_with_route", true);
                    return true;
                }
                return false;
            }),
            new SelectorNode("Nav steps",

                // Warping or waiting to confirm jump via system-name change
                new SequenceNode("Wait for jump/warp",
                    new ConditionNode("In motion or awaiting jump?", ctx =>
                        ctx.GameState.IsWarping ||
                        IsJumping(ctx.GameState) ||
                        (ctx.Blackboard.Get<bool>("warp_just_stopped") &&
                         !ctx.Blackboard.IsCooldownReady("jump_retry"))),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Post-jump cloak / session-change window
                new SequenceNode("Post-jump cooldown",
                    new ConditionNode("Cooldown?",
                        ctx => !ctx.Blackboard.IsCooldownReady("post_jump")),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Dismiss a context menu we didn't open
                new SequenceNode("Dismiss stray menu",
                    new ConditionNode("Unexpected menu?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        !ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Escape", ctx =>
                    {
                        ctx.KeyPress(VirtualKey.Escape);
                        return NodeStatus.Success;
                    })),

                // Context menu we opened — click Jump or Warp to 0 ONLY.
                // "Dock" is intentionally excluded — that's HandleFinalDock's job.
                new SequenceNode("Handle nav menu",
                    new ConditionNode("Our menu open?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Click jump/warp", ctx =>
                    {
                        ctx.Blackboard.Set("nav_menu_expected", false);

                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null) return NodeStatus.Failure;

                        var entries = menu.Entries;
                        var chosen =
                            entries.FirstOrDefault(e => EntryHasText(e, "jump")) ??
                            entries.FirstOrDefault(e =>
                                EntryHasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ") || t.Contains("0 m") || t.EndsWith(" 0"))) ??
                            entries.FirstOrDefault(e => EntryHasText(e, "warp"));

                        if (chosen == null)
                        {
                            ctx.Log("[Travel] Nav: route marker menu has no Jump/Warp entry (unexpected menu?) — dismissing");
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        bool isJump = EntryHasText(chosen, "jump");
                        ctx.Log($"[Travel] Nav: {(isJump ? "jumping through gate" : "warping to next hop")} — clicking '{chosen.Text}'");
                        ctx.Click(chosen.UINode);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(isJump ? 5 : 3));
                        return NodeStatus.Success;
                    })),

                // ── Watchdog ─────────────────────────────────────────────────────────
                // If the ship has not warped, jumped, or changed system for 15 seconds
                // while route is still active, something is stuck. Reset nav state and
                // re-arm a marker right-click.
                new SequenceNode("Stuck watchdog",
                    new ConditionNode("No progress for 15 s?", ctx =>
                        !ctx.GameState.IsWarping &&
                        !IsJumping(ctx.GameState) &&
                        ctx.Blackboard.IsCooldownReady("post_jump") &&
                        !ctx.Blackboard.Get<bool>("warp_just_stopped") &&
                        ctx.Blackboard.IsCooldownReady("progress_heartbeat")),
                    new ActionNode("Retry navigation", ctx =>
                    {
                        ctx.Log("[Travel] Watchdog: ship has not warped or jumped for 15 s — dismissing any stray menu and re-clicking route marker");
                        if (ctx.GameState.HasContextMenu) ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("nav_menu_expected", false);
                        ctx.Blackboard.Set("warp_just_stopped", false);
                        // Snooze the watchdog for another 15 s
                        ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));
                        // marker_click cooldown is already expired after 15 s — no need to reset
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
                        ctx.Blackboard.Set("warp_just_stopped", false);
                        ctx.Log("[Travel] Nav: right-clicking next route marker to get Jump/Warp menu");
                        ctx.RightClick(marker);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    })),

                // Route panel not yet updated after jump — idle this tick
                new ActionNode("Wait for route panel", _ => NodeStatus.Success)));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void CloseSearchResultsWindow(BotContext ctx)
    {
        var closeBtn = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 120) return false;
            if (n.Region.Height < 15 || n.Region.Height > 50) return false;
            if (n.Region.Width < 30 || n.Region.Width > 200) return false;
            return string.Equals(OwnText(n), "Close", StringComparison.OrdinalIgnoreCase);
        });
        if (closeBtn != null) { ctx.Click(closeBtn); ctx.Wait(TimeSpan.FromMilliseconds(300)); }
    }

    private static void ClickSearchBar(BotContext ctx)
    {
        var bar = FindSearchBar(ctx.GameState.ParsedUI);
        if (bar != null) { ctx.Click(bar); ctx.Wait(TimeSpan.FromMilliseconds(400)); }
        else { ctx.KeyPress(VirtualKey.S, VirtualKey.Shift); ctx.Wait(TimeSpan.FromSeconds(1)); }
    }

    private static UITreeNodeWithDisplayRegion? FindSearchBar(ParsedUI ui) =>
        ui.UITree?.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
            n.Region.Y < 100 && n.Region.Height is > 8 and < 40 && n.Region.Width > 80 &&
            (n.Node.GetDictString("_hint")?.Contains("Search",   StringComparison.OrdinalIgnoreCase) == true ||
             n.Node.GetDictString("_hint")?.Contains("New Eden", StringComparison.OrdinalIgnoreCase) == true ||
             n.Node.GetDictString("_name")?.Contains("search",   StringComparison.OrdinalIgnoreCase) == true));

    private static UITreeNodeWithDisplayRegion? FindResultGroup(ParsedUI ui, string groupKeyword) =>
        ui.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 100 || n.Region.Height < 8 || n.Region.Height > 50) return false;
            var t = OwnText(n);
            return t.StartsWith(groupKeyword, StringComparison.OrdinalIgnoreCase) &&
                   t.Contains('(') && t.Length < 40;
        });

    private static UITreeNodeWithDisplayRegion? FindStationResultRow(ParsedUI ui, string dest)
    {
        UITreeNodeWithDisplayRegion? header = null;
        ui.UITree?.FindFirst(n =>
        {
            if (n.Region.Y < 100) return false;
            var t = OwnText(n);
            if (t.StartsWith("Station", StringComparison.OrdinalIgnoreCase) &&
                t.Contains('(') && t.Length < 40 && n.Region.Height < 50)
            { header = n; return true; }
            return false;
        });

        bool RowContainsDest(UITreeNodeWithDisplayRegion n) =>
            OwnText(n).Contains(dest, StringComparison.OrdinalIgnoreCase) ||
            n.GetAllContainedDisplayTexts().Any(t => t.Contains(dest, StringComparison.OrdinalIgnoreCase));

        bool IsResultRow(UITreeNodeWithDisplayRegion n) =>
            n.Region.Y >= 120 && n.Region.Height is >= 10 and <= 50 && n.Region.Width >= 60 &&
            !n.Node.PythonObjectTypeName.Contains("Edit", StringComparison.OrdinalIgnoreCase);

        if (header != null)
        {
            var row = ui.UITree?.FindFirst(n =>
                n.Region.Y > header.Region.Y && IsResultRow(n) && RowContainsDest(n));
            if (row != null) return row;
        }
        return ui.UITree?.FindFirst(n => IsResultRow(n) && RowContainsDest(n));
    }

    private static UITreeNodeWithDisplayRegion? FirstMarker(BotContext ctx) =>
        ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers
            .OrderBy(m => m.Region.X + m.Region.Y)
            .FirstOrDefault();

    private static bool IsJumping(GameStateSnapshot gs) =>
        gs.ParsedUI.ShipUI?.Indication?.ManeuverType?
            .Contains("jump", StringComparison.OrdinalIgnoreCase) ?? false;

    private static bool EntryHasText(ContextMenuEntry e, string keyword) =>
        e.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true ||
        e.UINode.GetAllContainedDisplayTexts()
                .Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string OwnText(UITreeNodeWithDisplayRegion n)
    {
        var s = EveTextUtil.StripTags(n.Node.GetDictString("_setText"));
        if (!string.IsNullOrEmpty(s)) return s;
        return EveTextUtil.StripTags(n.Node.GetDictString("_text")) ?? "";
    }
}
