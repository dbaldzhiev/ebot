using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.AutopilotBot;

/// <summary>
/// Warp-to-0 travel bot with automatic final docking.
///
/// Core loop (every tick, priority order):
///   1. Close message boxes.
///   2. Set up route if a destination was given.
///   3. Arrived (docked + route empty + was traveling) → stop.
///   4. Undock if docked with route remaining.
///   5. In space with route markers → right-click next marker:
///        Dock   — available when already at destination in range → dock
///        Jump   — gate in menu → jump through
///        Warp   — warp to gate or station at 0
///
/// The same right-click logic handles every hop and the final dock — no
/// separate HandleFinalDock state machine is needed.
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

        ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Travel Root",
            TrackSystemChange(),
            HandleMessageBoxes(),
            SetupDestination(),
            HandleArrived(),
            HandleUndock(),
            HandleInSpace());

    // ─── System-change / heartbeat tracker ───────────────────────────────────
    // Always returns Failure — side-effects only (heartbeat, jump detection).

    private static IBehaviorNode TrackSystemChange() =>
        new ActionNode("Track system", ctx =>
        {
            var sys  = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.SystemName;
            var prev = ctx.Blackboard.Get<string>("travel_sys");

            bool jumped = sys != null && prev != null &&
                          !string.Equals(sys, prev, StringComparison.OrdinalIgnoreCase);
            if (jumped)
            {
                ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(2));
                ctx.Blackboard.Set("nav_menu_expected", false);
                ctx.Log($"[Travel] Jumped: {prev} → {sys}");
            }
            if (sys != null) ctx.Blackboard.Set("travel_sys", sys);

            var wasWarping = ctx.Blackboard.Get<bool>("was_warping");
            var isWarping  = ctx.GameState.IsWarping;
            if (wasWarping && !isWarping && !ctx.GameState.IsDocked)
            {
                ctx.Blackboard.SetCooldown("post_warp", TimeSpan.FromSeconds(4));
                ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));
            }
            ctx.Blackboard.Set("was_warping", isWarping);

            if (isWarping || jumped)
                ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));

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
            new ConditionNode("Docked, route empty, was traveling?", ctx =>
                ctx.GameState.IsDocked &&
                ctx.GameState.RouteJumpsRemaining == 0 &&
                ctx.Blackboard.Get<bool>("was_in_space_with_route")),
            new ActionNode("Stop", ctx =>
            {
                ctx.Blackboard.Set("was_in_space_with_route", false);
                ctx.Log("[Travel] Arrived and docked — stopping");
                ctx.RequestStop();
                return NodeStatus.Success;
            }));

    // ─── Undock ───────────────────────────────────────────────────────────────

    private static IBehaviorNode HandleUndock() =>
        new SequenceNode("Undock",
            new ConditionNode("Docked with route?", ctx =>
                ctx.GameState.IsDocked && ctx.GameState.RouteJumpsRemaining > 0),
            new ConditionNode("Undock cooldown?",
                ctx => ctx.Blackboard.IsCooldownReady("undock_cooldown")),
            new ActionNode("Click undock", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                ctx.Blackboard.SetCooldown("undock_cooldown", TimeSpan.FromSeconds(15));
                return NodeStatus.Success;
            }));

    // ─── In-space navigation ──────────────────────────────────────────────────
    //
    // Right-clicks the next route marker every tick. The menu may contain:
    //   • Dock   → we're at the destination station and in docking range → dock
    //   • Jump   → we're at a gate → jump through
    //   • Warp   → we need to warp to gate or station → warp to 0
    //
    // All three cases are handled by the same menu-click logic, including the
    // final docking step — no separate HandleFinalDock needed.

    private static IBehaviorNode HandleInSpace() =>
        new SequenceNode("Navigate in space",
            new ConditionNode("In space with markers?", ctx =>
            {
                if (!ctx.GameState.IsInSpace) return false;
                var hasMarkers = FirstMarker(ctx) != null;
                if (hasMarkers) ctx.Blackboard.Set("was_in_space_with_route", true);
                return hasMarkers;
            }),
            new SelectorNode("Nav steps",

                // Wait while warping or during post-warp settle
                new SequenceNode("Wait for motion",
                    new ConditionNode("In motion or settling?", ctx =>
                        ctx.GameState.IsWarping ||
                        IsJumping(ctx.GameState) ||
                        !ctx.Blackboard.IsCooldownReady("post_warp")),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                // Post-jump cloak / session-change window
                new SequenceNode("Post-jump cooldown",
                    new ConditionNode("Post-jump?",
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

                // Context menu we opened — click Dock, Jump, or Warp (in priority order)
                new SequenceNode("Handle nav menu",
                    new ConditionNode("Our menu open?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.Get<bool>("nav_menu_expected")),
                    new ActionNode("Click nav action", ctx =>
                    {
                        ctx.Blackboard.Set("nav_menu_expected", false);

                        // Gather entries from parsed menus and raw tree scan
                        var parsedEntries = ctx.GameState.ParsedUI.ContextMenus
                            .SelectMany(m => m.Entries).ToList();
                        var treeEntries = (ctx.GameState.ParsedUI.UITree?
                            .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView") ?? [])
                            .ToList();

                        bool menuVisible = parsedEntries.Count > 0 || treeEntries.Count > 0;
                        if (!menuVisible)
                        {
                            // Menu closed before we could read it — retry marker click next tick
                            return NodeStatus.Failure;
                        }

                        // Priority 1: Dock (at destination station in range)
                        NavEntry? dock =
                            AsEntry(parsedEntries.FirstOrDefault(e => EntryHasText(e, "Dock"))) ??
                            AsEntry(treeEntries.FirstOrDefault(n =>
                                n.GetAllContainedDisplayTexts().Any(t =>
                                    t.Trim().Equals("Dock", StringComparison.OrdinalIgnoreCase))));

                        // Priority 2: Jump (through gate)
                        NavEntry? jump =
                            AsEntry(parsedEntries.FirstOrDefault(e => EntryHasText(e, "jump"))) ??
                            AsEntry(treeEntries.FirstOrDefault(n =>
                                n.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains("Jump", StringComparison.OrdinalIgnoreCase))));

                        // Priority 3: Warp to 0 (approach gate or station)
                        NavEntry? warp =
                            AsEntry(parsedEntries.FirstOrDefault(e =>
                                EntryHasText(e, "warp") &&
                                e.UINode.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains(" 0 ") || t.Contains("0 m") || t.EndsWith(" 0")))) ??
                            AsEntry(parsedEntries.FirstOrDefault(e => EntryHasText(e, "warp"))) ??
                            AsEntry(treeEntries.FirstOrDefault(n =>
                                n.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains("Warp", StringComparison.OrdinalIgnoreCase))));

                        var chosen = dock ?? jump ?? warp;
                        if (chosen == null)
                        {
                            ctx.Log($"[Travel] Marker menu has {parsedEntries.Count + treeEntries.Count} entries but no Dock/Jump/Warp — dismissing");
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        string action = dock != null ? "docking" : jump != null ? "jumping" : "warping";
                        ctx.Log($"[Travel] {action} — clicking '{chosen.Text ?? "(tree node)"}'");
                        // Hover first so the mouse arrives at the entry, then wait for
                        // the pointer to settle before the actual button press fires.
                        ctx.Hover(chosen.Node);
                        ctx.Wait(TimeSpan.FromMilliseconds(180));
                        ctx.Click(chosen.Node);

                        int cooldownSec = dock != null ? 15 : jump != null ? 6 : 3;
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(cooldownSec));
                        if (dock != null) ctx.Blackboard.SetCooldown("post_jump", TimeSpan.FromSeconds(12));
                        return NodeStatus.Success;
                    })),

                // Watchdog: no progress for 15 s → dismiss stray menu, reset, retry
                new SequenceNode("Stuck watchdog",
                    new ConditionNode("No progress 15 s?", ctx =>
                        !ctx.GameState.IsWarping &&
                        !IsJumping(ctx.GameState) &&
                        ctx.Blackboard.IsCooldownReady("post_jump") &&
                        ctx.Blackboard.IsCooldownReady("post_warp") &&
                        ctx.Blackboard.IsCooldownReady("progress_heartbeat")),
                    new ActionNode("Reset and retry", ctx =>
                    {
                        ctx.Log("[Travel] Watchdog: no progress for 15 s — retrying");
                        if (ctx.GameState.HasContextMenu) ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("nav_menu_expected", false);
                        ctx.Blackboard.SetCooldown("progress_heartbeat", TimeSpan.FromSeconds(15));
                        return NodeStatus.Success;
                    })),

                // Right-click the next route hop marker
                new SequenceNode("Click route marker",
                    new ConditionNode("No menu + cooldown?", ctx =>
                        !ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.IsCooldownReady("marker_click")),
                    new ActionNode("Right-click marker", ctx =>
                    {
                        var marker = FirstMarker(ctx);
                        if (marker == null) return NodeStatus.Failure;
                        ctx.Blackboard.Set("nav_menu_expected", true);
                        ctx.RightClick(marker);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    })),

                // Idle: route panel updating after jump
                new ActionNode("Idle", _ => NodeStatus.Success)));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Thin wrapper that unifies a parsed ContextMenuEntry and a raw UITreeNode behind
    /// a single clickable Node, so the menu handler never branches on source type.
    /// </summary>
    private sealed record NavEntry(string? Text, UITreeNodeWithDisplayRegion Node);

    private static NavEntry? AsEntry(ContextMenuEntry? e) =>
        e == null ? null : new NavEntry(e.Text, e.UINode);

    private static NavEntry? AsEntry(UITreeNodeWithDisplayRegion? n) =>
        n == null ? null : new NavEntry(
            n.GetAllContainedDisplayTexts().FirstOrDefault(), n);

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
