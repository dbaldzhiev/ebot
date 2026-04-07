using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.AutopilotBot;

/// <summary>
/// Extends AutopilotBot with a bookmark-based final docking step.
///
/// After the autopilot route is completed (system reached), this bot:
///   1. Opens the in-game People &amp; Places / Bookmarks window (L key).
///   2. Searches for the configured bookmark name.
///   3. Right-clicks it → "Warp to Location" (at 0 m).
///   4. After landing, right-clicks the station in overview → Dock.
///
/// This is necessary for stations like Jita 4-4 (Caldari Navy Assembly Plant)
/// where the autopilot route ends at the system but doesn't guarantee you land
/// at the exact station.
/// </summary>
public sealed class BookmarkDockBot : IBot
{
    public string Destination { get; init; } = "";
    /// <summary>The solar system the autopilot should travel to.</summary>
    public string SystemName  { get; init; } = "";
    /// <summary>The exact in-game bookmark name to warp to for docking.</summary>
    public string BookmarkName { get; init; } = "";

    public string Name => "Autopilot + Bookmark Dock";
    public string Description =>
        $"Travel to {SystemName} via autopilot, then warp to bookmark '{BookmarkName}' and dock.";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs = 2000,
        MinActionDelayMs = 100,
        MaxActionDelayMs = 300,
        CoordinateJitter = 3,
    };

    public void OnStart(BotContext ctx)
    {
        ctx.Blackboard.Set("autopilot_destination", SystemName);
        ctx.Blackboard.Set("bm_dock_bookmark",      BookmarkName);
        ctx.Blackboard.Set("bm_dock_phase",         "");
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("BookmarkDock Root",
            HandleMessageBoxes(),
            // Reuse autopilot setup + navigation (but not HandleDocked — we manage that)
            SetupDestination(),
            HandleDockedAtWrong(),
            HandleInSpace(),
            HandleBookmarkDock());

    // ─── Reused from AutopilotBot (cannot inherit — copy the static nodes) ───

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
                    // ── Step 0: clear any existing destination first ──────
                    case "":
                    {
                        var markers = ctx.GameState.ParsedUI.InfoPanelContainer?
                            .InfoPanelRoute?.RouteElementMarkers ?? [];
                        if (markers.Count > 0)
                        {
                            // Right-click the LAST marker (= current destination square)
                            ctx.Blackboard.Set("menu_expected", true);
                            ctx.RightClick(markers[^1]);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("dest_phase", "clear_menu");
                        }
                        else
                        {
                            // Nothing to clear — open search immediately
                            ctx.KeyPress(VirtualKey.S, VirtualKey.Shift);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            ctx.Blackboard.Set("dest_phase", "typing");
                        }
                        return NodeStatus.Running;
                    }

                    case "clear_menu":
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        if (ctx.GameState.HasContextMenu)
                        {
                            var menu   = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                            var remove = menu?.Entries.FirstOrDefault(e =>
                                e.Text?.Contains("Remove", StringComparison.OrdinalIgnoreCase) == true ||
                                e.Text?.Contains("Clear",  StringComparison.OrdinalIgnoreCase) == true);
                            if (remove != null)
                            {
                                ctx.Click(remove.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(600));
                            }
                            else
                            {
                                ctx.KeyPress(VirtualKey.Escape);
                            }
                        }
                        // Whether we cleared or not, proceed to open search
                        ctx.KeyPress(VirtualKey.S, VirtualKey.Shift);
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        ctx.Blackboard.Set("dest_phase", "typing");
                        return NodeStatus.Running;
                    }

                    case "typing":
                    {
                        var field = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                            n.Node.PythonObjectTypeName.Contains("EditText", StringComparison.OrdinalIgnoreCase) ||
                            n.Node.PythonObjectTypeName.Contains("SingleLineEditText", StringComparison.OrdinalIgnoreCase));
                        if (field != null) ctx.Click(field);
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);
                        ctx.TypeText(ctx.Blackboard.Get<string>("autopilot_destination")!);
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
                                .FirstOrDefault(e => e.Text?.Contains("destination", StringComparison.OrdinalIgnoreCase) == true);
                            if (setDest != null)
                            {
                                ctx.Click(setDest.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(400));
                                ctx.Blackboard.Set("destination_set", true);
                                ctx.Blackboard.Set("dest_phase", "close_search");
                                return NodeStatus.Running;
                            }
                        }
                        var dest = ctx.Blackboard.Get<string>("autopilot_destination")!;

                        // We must check only the node's OWN text (not children).
                        // GetAllContainedDisplayTexts() is recursive, so a parent container
                        // (e.g. "Stations (1)" group) would also match, and FindFirst
                        // (pre-order DFS) would return the container before the actual row.
                        // By reading _setText/_text directly we skip group headers whose own
                        // text is something like "Stations (1)" — not the destination name.
                        static string OwnText(UITreeNodeWithDisplayRegion n)
                        {
                            var s = EveTextUtil.StripTags(n.Node.GetDictString("_setText"));
                            if (!string.IsNullOrEmpty(s)) return s;
                            return EveTextUtil.StripTags(n.Node.GetDictString("_text")) ?? "";
                        }

                        // Search result rows: own text contains the destination,
                        // sized like a list row (height 10–40 px), not an edit field.
                        var result = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                        {
                            if (n.Region.Width < 60 || n.Region.Height < 10 || n.Region.Height > 45) return false;
                            if (n.Node.PythonObjectTypeName.Contains("Edit",
                                StringComparison.OrdinalIgnoreCase)) return false;
                            var own = OwnText(n);
                            return own.Contains(dest, StringComparison.OrdinalIgnoreCase);
                        });
                        if (result != null) { ctx.RightClick(result); return NodeStatus.Running; }
                        return NodeStatus.Running;
                    }

                    // Close the Search Results dialog via its "Close" button
                    case "close_search":
                    {
                        var closeBtn = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                        {
                            var own = EveTextUtil.StripTags(n.Node.GetDictString("_setText"))
                                   ?? EveTextUtil.StripTags(n.Node.GetDictString("_text"))
                                   ?? "";
                            return own.Equals("Close", StringComparison.OrdinalIgnoreCase)
                                && n.Region.Width > 30 && n.Region.Height > 10;
                        });
                        if (closeBtn != null)
                            ctx.Click(closeBtn);
                        ctx.Blackboard.Set("dest_phase", "done");
                        return NodeStatus.Success;
                    }

                    default:
                        return NodeStatus.Success;
                }
            }));

    /// <summary>If we're docked somewhere other than the target, undock first.</summary>
    private static IBehaviorNode HandleDockedAtWrong() =>
        new SequenceNode("Undock if not yet arrived",
            new ConditionNode("Docked but bookmark phase not started?", ctx =>
                ctx.GameState.IsDocked &&
                string.IsNullOrEmpty(ctx.Blackboard.Get<string>("bm_dock_phase"))),
            new ActionNode("Click undock", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                ctx.Wait(TimeSpan.FromSeconds(8));
                return NodeStatus.Success;
            }));

    private static IBehaviorNode HandleInSpace() =>
        new SequenceNode("Navigate to system",
            new ConditionNode("In space AND route remaining?", ctx =>
                ctx.GameState.IsInSpace && ctx.GameState.RouteJumpsRemaining > 0),
            new SelectorNode("Navigation",

                new SequenceNode("Wait while warping",
                    new ConditionNode("Warping?", ctx =>
                        ctx.GameState.IsWarping ||
                        (ctx.GameState.ParsedUI.ShipUI?.Indication?.ManeuverType?
                            .Contains("jump", StringComparison.OrdinalIgnoreCase) ?? false)),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                new SequenceNode("Cooldown",
                    new ConditionNode("Cooldown active?", ctx => !ctx.Blackboard.IsCooldownReady("post_jump")),
                    new ActionNode("Wait", _ => NodeStatus.Success)),

                new SequenceNode("Handle menu",
                    new ConditionNode("Menu visible?", ctx => ctx.GameState.HasContextMenu),
                    new ActionNode("Click jump/warp", ctx =>
                    {
                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        if (menu == null) return NodeStatus.Failure;
                        var entries = menu.Entries;
                        var chosen =
                            entries.FirstOrDefault(e => e.Text?.Contains("jump", StringComparison.OrdinalIgnoreCase) == true) ??
                            entries.FirstOrDefault(e => e.Text?.Contains("warp", StringComparison.OrdinalIgnoreCase) == true &&
                                (e.Text.Contains(" 0 ") || e.Text.Contains("0 m") || e.Text.EndsWith(" 0"))) ??
                            entries.FirstOrDefault(e => e.Text?.Contains("warp", StringComparison.OrdinalIgnoreCase) == true);
                        if (chosen == null) { ctx.KeyPress(VirtualKey.Escape); return NodeStatus.Failure; }
                        ctx.Click(chosen.UINode);
                        if (chosen.Text?.Contains("jump", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // Post-jump: gate session change takes ~10-12 s
                            ctx.Blackboard.SetCooldown("post_jump",    TimeSpan.FromSeconds(12));
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(14));
                        }
                        else
                        {
                            // Post-warp command: ship needs 1-3 s to enter warp — don't re-click
                            ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(5));
                        }
                        return NodeStatus.Success;
                    })),

                new SequenceNode("Right-click route marker",
                    new ConditionNode("Has markers?", ctx =>
                        ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers
                            .OrderBy(m => m.Region.X + m.Region.Y).FirstOrDefault() != null),
                    new ConditionNode("Cooldown ready?", ctx => ctx.Blackboard.IsCooldownReady("marker_click")),
                    new ActionNode("Right-click marker", ctx =>
                    {
                        var marker = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers
                            .OrderBy(m => m.Region.X + m.Region.Y).FirstOrDefault();
                        if (marker == null) return NodeStatus.Failure;
                        ctx.RightClick(marker);
                        ctx.Blackboard.SetCooldown("marker_click", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    })),

                new ActionNode("Wait for route panel", _ => NodeStatus.Success)));

    // ─── Bookmark dock phase ─────────────────────────────────────────────────

    private static IBehaviorNode HandleBookmarkDock() =>
        new SequenceNode("Bookmark dock",
            new ConditionNode("In system, route done?", ctx =>
                ctx.GameState.IsInSpace && ctx.GameState.RouteJumpsRemaining == 0),
            new ActionNode("Bookmark dock state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("bm_dock_phase") ?? "";
                var bm    = ctx.Blackboard.Get<string>("bm_dock_bookmark") ?? "";

                switch (phase)
                {
                    // Step 1: open bookmarks window with L key (or skip straight to docking if no bookmark)
                    case "":
                        if (string.IsNullOrWhiteSpace(bm))
                        {
                            // No bookmark configured — dock via overview directly
                            ctx.Blackboard.Set("bm_dock_phase", "docking");
                            return NodeStatus.Running;
                        }
                        ctx.KeyPress(VirtualKey.L);
                        ctx.Wait(TimeSpan.FromSeconds(1.5));
                        ctx.Blackboard.Set("bm_dock_phase", "finding");
                        return NodeStatus.Running;

                    // Step 2: find the bookmark in the open window and right-click it
                    case "finding":
                    {
                        // Bookmarks window: look for a list entry whose text matches the bookmark name
                        var bmNode = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                        {
                            var texts = n.GetAllContainedDisplayTexts().ToList();
                            return texts.Any(t => t.Contains(bm, StringComparison.OrdinalIgnoreCase))
                                && n.Region.Width > 20 && n.Region.Height > 8;
                        });

                        if (bmNode == null)
                        {
                            // Window may still be opening or search needed — try typing in any search field
                            var search = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                                n.Node.PythonObjectTypeName.Contains("EditText", StringComparison.OrdinalIgnoreCase));
                            if (search != null)
                            {
                                ctx.Click(search);
                                ctx.TypeText(bm);
                            }
                            return NodeStatus.Running;
                        }

                        ctx.RightClick(bmNode);
                        ctx.Wait(TimeSpan.FromSeconds(0.5));
                        ctx.Blackboard.Set("bm_dock_phase", "warping");
                        return NodeStatus.Running;
                    }

                    // Step 3: context menu appeared — click "Warp to Location"
                    case "warping":
                    {
                        if (!ctx.GameState.HasContextMenu) return NodeStatus.Running;
                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var warpEntry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("warp", StringComparison.OrdinalIgnoreCase) == true);
                        if (warpEntry == null) return NodeStatus.Running;
                        ctx.Click(warpEntry.UINode);
                        ctx.Blackboard.Set("bm_dock_phase", "wait_warp");
                        ctx.Wait(TimeSpan.FromSeconds(2));
                        return NodeStatus.Running;
                    }

                    // Step 4: wait for warp to complete
                    case "wait_warp":
                    {
                        if (ctx.GameState.IsWarping) return NodeStatus.Running;
                        // Warp done — now dock via overview
                        ctx.Blackboard.Set("bm_dock_phase", "docking");
                        return NodeStatus.Running;
                    }

                    // Step 5: right-click station in overview → Dock
                    case "docking":
                    {
                        if (ctx.GameState.HasContextMenu)
                        {
                            var dock = ctx.GameState.ParsedUI.ContextMenus.SelectMany(m => m.Entries)
                                .FirstOrDefault(e => e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);
                            if (dock != null)
                            {
                                ctx.Click(dock.UINode);
                                ctx.Blackboard.Set("bm_dock_phase", "done");
                                return NodeStatus.Success;
                            }
                        }

                        // No menu yet — right-click the nearest station-like overview entry
                        var overview = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
                        var station = overview?.Entries
                            .Where(e => e.Texts.Any(t =>
                                t.Contains("Station", StringComparison.OrdinalIgnoreCase) ||
                                t.Contains("Assembly", StringComparison.OrdinalIgnoreCase) ||
                                t.Contains("Plant", StringComparison.OrdinalIgnoreCase) ||
                                t.Contains("Structure", StringComparison.OrdinalIgnoreCase) ||
                                t.Contains("Citadel", StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(e => e.DistanceInMeters ?? double.MaxValue)
                            .FirstOrDefault()
                            ?? overview?.Entries.OrderBy(e => e.DistanceInMeters ?? double.MaxValue).FirstOrDefault();

                        if (station != null)
                        {
                            ctx.RightClick(station.UINode);
                            return NodeStatus.Running;
                        }
                        return NodeStatus.Running;
                    }

                    case "done":
                        // Task complete — tell the runner to return to monitor/idle mode.
                        // This ensures autopilot never bleeds into mining or any other bot.
                        ctx.RequestStop();
                        return NodeStatus.Success;

                    default:
                        return NodeStatus.Running;
                }
            }));
}
