using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    // ─── 7b. Return to station ───────────────────────────────────────────────

    private IBehaviorNode ReturnToStation() =>
        new SequenceNode("Return to station",
            new ConditionNode("Needs return?", ctx =>
                ctx.Blackboard.Get<bool>("needs_unload") || IsOreHoldFull(ctx)),
            new ActionNode("Return state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("return_phase") ?? "";
                switch (phase)
                {
                    case "":
                        ctx.Blackboard.Set("needs_unload", true);
                        StopAllModules(ctx);
                        RecallDrones(ctx);
                        // Reset per-cycle menu-type probing (home_menu_type persists across cycles once learned)
                        ctx.Blackboard.Set("return_tried_stations",   false);
                        ctx.Blackboard.Set("return_tried_structures",  false);
                        ctx.Blackboard.Set("return_current_menu",     "");
                        ctx.Blackboard.Set("return_phase", "await_drones");
                        ctx.Blackboard.SetCooldown("return_drone_timeout", TimeSpan.FromSeconds(25));
                        return NodeStatus.Running;

                    case "await_drones":
                    {
                        bool dronesBack = (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) == 0;
                        bool timedOut   = ctx.Blackboard.IsCooldownReady("return_drone_timeout");
                        if (!dronesBack && !timedOut) return NodeStatus.Running;
                        if (!dronesBack) ctx.Log("[Mining] Drone recall timed out — warping to station without all drones");
                        ctx.Blackboard.Set("return_phase", "find_station");
                        return NodeStatus.Running;
                    }

                    case "find_station":
                    {
                        if (ctx.GameState.IsDocked) { ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Success; }
                        
                        ctx.Log("[Mining] Initiating menu-based docking. Right-clicking in space.");
                        ctx.Blackboard.Set("menu_expected", true);
                        RightClickInSpace(ctx);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        ctx.Blackboard.Set("return_phase", "space_menu_dock");
                        ctx.Blackboard.Set("return_tick", 0);
                        return NodeStatus.Running;
                    }

                    case "space_menu_dock":
                    {
                        if (ctx.GameState.IsDocked) { ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Success; }
                        int tick = ctx.Blackboard.Get<int>("return_tick") + 1;
                        ctx.Blackboard.Set("return_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 5) { ctx.Blackboard.Set("return_phase", "find_station"); }
                            return NodeStatus.Running;
                        }

                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();

                        // Determine which submenu category to hover.
                        // If we already learned where the home station lives, go straight there.
                        // Otherwise probe: try "Stations" first, then "Structures" if Stations failed.
                        var homeMenuType     = ctx.Blackboard.Get<string>("home_menu_type") ?? "";
                        bool triedStations   = ctx.Blackboard.Get<bool>("return_tried_stations");
                        bool triedStructures = ctx.Blackboard.Get<bool>("return_tried_structures");
                        var homeName         = ctx.Blackboard.Get<string>("home_station") ?? "";

                        // Heuristic: player structures often have " - " but NPC stations follow 
                        // "System X - Moon Y - Corporation". If it has "Moon" or "Corporation", it's likely a Station.
                        bool looksLikeNpcStation = homeName.Contains(" Moon ", StringComparison.OrdinalIgnoreCase) || 
                                                 homeName.Contains(" Corporation ", StringComparison.OrdinalIgnoreCase) ||
                                                 homeName.Contains(" University ", StringComparison.OrdinalIgnoreCase) ||
                                                 homeName.Contains(" Institute ", StringComparison.OrdinalIgnoreCase) ||
                                                 homeName.Contains(" School ", StringComparison.OrdinalIgnoreCase);

                        bool preferStructures = homeName.Contains(" - ") && !looksLikeNpcStation && !triedStructures && !triedStations;

                        ContextMenuEntry? entry = null;
                        if (!string.IsNullOrEmpty(homeMenuType))
                        {
                            // Remembered from a previous dock this session — use it directly
                            entry = menu?.Entries.FirstOrDefault(e =>
                                string.Equals(e.Text?.Trim(), homeMenuType, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (preferStructures)
                        {
                            entry = menu?.Entries.FirstOrDefault(e =>
                                e.Text?.Contains("Structures", StringComparison.OrdinalIgnoreCase) == true);
                        }
                        else if (!triedStations)
                        {
                            entry = menu?.Entries.FirstOrDefault(e =>
                                e.Text?.Contains("Stations", StringComparison.OrdinalIgnoreCase) == true &&
                                e.Text?.Contains("Structures", StringComparison.OrdinalIgnoreCase) != true);
                        }
                        else if (!triedStructures)
                        {
                            entry = menu?.Entries.FirstOrDefault(e =>
                                e.Text?.Contains("Structures", StringComparison.OrdinalIgnoreCase) == true);
                        }

                        if (entry != null)
                        {
                            ctx.Log($"[Mining] Found '{entry.Text}' in menu. Hovering.");
                            ctx.Blackboard.Set("return_current_menu", entry.Text?.Trim() ?? "");
                            ctx.Blackboard.Set("return_parent_x", entry.UINode.Region.X + entry.UINode.Region.Width);
                            HoverAndSlide(ctx, entry.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("return_phase", "station_submenu");
                            ctx.Blackboard.Set("return_tick", 0);
                        }
                        else
                        {
                            // Fallback to route panel if neither Stations nor Structures is in the menu
                            var route = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers.FirstOrDefault();
                            if (route != null && tick > 3)
                            {
                                ctx.Log("[Mining] Station/Structure category not in menu, using route panel fallback.");
                                ctx.RightClick(route);
                                ctx.Wait(TimeSpan.FromMilliseconds(500));
                                ctx.Blackboard.Set("return_phase", "warp_menu");
                            }
                            else if (tick > 6)
                            {
                                ctx.KeyPress(VirtualKey.Escape);
                                ctx.Blackboard.Set("return_phase", "find_station");
                            }
                        }
                        return NodeStatus.Running;
                    }

                    case "station_submenu":
                    {
                        if (ctx.GameState.IsDocked) { ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Success; }
                        int tick = ctx.Blackboard.Get<int>("return_tick") + 1;
                        ctx.Blackboard.Set("return_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 5) { ctx.Blackboard.Set("return_phase", "find_station"); }
                            return NodeStatus.Running;
                        }

                        var allMenus = ctx.GameState.ParsedUI.ContextMenus;

                        // The station submenu is any menu to the right of the leftmost (main) menu.
                        // Avoid the X-coordinate-based filter which breaks when menu origin shifts.
                        var mainMenuX = allMenus.Count > 0 ? allMenus.Min(m => m.UINode.Region.X) : 0;
                        var subMenu = allMenus.FirstOrDefault(m => m.UINode.Region.X > mainMenuX && m.Entries.Count > 0);

                        // Fallback: original X-offset approach in case all menus share the same X
                        if (subMenu == null)
                        {
                            int parentX = ctx.Blackboard.Get<int>("return_parent_x");
                            subMenu = allMenus.FirstOrDefault(m => m.UINode.Region.X > parentX - 10);
                        }

                        if (subMenu == null || subMenu.Entries.Count == 0)
                        {
                            // Don't wait indefinitely for a submenu that never appears
                            if (tick > 8)
                            {
                                MarkCurrentMenuTried(ctx);
                                ctx.KeyPress(VirtualKey.Escape);
                                ctx.Blackboard.Set("return_phase", "find_station");
                                ctx.Blackboard.Set("return_tick", 0);
                            }
                            return NodeStatus.Running;
                        }

                        var homeName = ctx.Blackboard.Get<string>("home_station");
                        
                        // 1. Try exact match first (case-insensitive)
                        var target = subMenu.Entries.FirstOrDefault(e => 
                            !string.IsNullOrEmpty(homeName) && 
                            string.Equals(e.Text?.Trim(), homeName.Trim(), StringComparison.OrdinalIgnoreCase));

                        // 2. Fallback to contains match
                        target ??= subMenu.Entries.FirstOrDefault(e => 
                            !string.IsNullOrEmpty(homeName) && 
                            e.Text?.Contains(homeName, StringComparison.OrdinalIgnoreCase) == true);
                        
                        // 3. Last resort: first entry ONLY if we don't have a home station set
                        if (target == null && string.IsNullOrEmpty(homeName))
                        {
                            target = subMenu.Entries.FirstOrDefault();
                        }

                        if (target != null)
                        {
                            ctx.Log($"[Mining] Target station '{target.Text}' found. Hovering.");
                            ctx.Blackboard.Set("return_parent_x", target.UINode.Region.X + target.UINode.Region.Width);
                            HoverAndSlide(ctx, target.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("return_phase", "station_action_menu");
                            ctx.Blackboard.Set("return_tick", 0);
                        }
                        else if (tick > 8)
                        {
                            MarkCurrentMenuTried(ctx);
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("return_phase", "find_station");
                            ctx.Blackboard.Set("return_tick", 0);
                        }
                        return NodeStatus.Running;
                    }

                    case "station_action_menu":
                    {
                        if (ctx.GameState.IsDocked) { ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Success; }
                        int tick = ctx.Blackboard.Get<int>("return_tick") + 1;
                        ctx.Blackboard.Set("return_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 5) { ctx.Blackboard.Set("return_phase", "find_station"); }
                            return NodeStatus.Running;
                        }

                        var allMenus = ctx.GameState.ParsedUI.ContextMenus;
                        int parentX = ctx.Blackboard.Get<int>("return_parent_x");
                        var actionMenu = allMenus.OrderByDescending(m => m.UINode.Region.X).FirstOrDefault();

                        var dockEntry = actionMenu?.Entries.FirstOrDefault(e => 
                            e.Text?.Equals("Dock", StringComparison.OrdinalIgnoreCase) == true);

                        if (dockEntry != null)
                        {
                            ctx.Log("[Mining] 'Dock' command found. Clicking.");
                            ctx.Hover(dockEntry.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(300));
                            ctx.Click(dockEntry.UINode);
                            ctx.Blackboard.Set("menu_expected", false);
                            ctx.Blackboard.Set("return_phase", "waiting_to_dock");
                            ctx.Blackboard.Set("return_tick", 0);
                            ctx.Wait(TimeSpan.FromSeconds(5));
                        }
                        else if (tick > 6)
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("return_phase", "find_station");
                        }
                        return NodeStatus.Running;
                    }

                    case "warp_menu":
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        if (!ctx.GameState.HasContextMenu)
                        { ctx.Blackboard.Set("return_phase", "find_station"); return NodeStatus.Running; }
                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var dock = menu?.Entries.FirstOrDefault(e =>
                            string.Equals(e.Text?.Trim(), "Dock", StringComparison.OrdinalIgnoreCase));
                        if (dock != null)
                        { ctx.Click(dock.UINode); ctx.Blackboard.Set("return_phase", "waiting_to_dock"); ctx.Blackboard.Set("return_tick", 0); return NodeStatus.Running; }
                        var warp = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true &&
                            (e.Text.Contains(" 0") || e.Text.Contains("0 m")));
                        warp ??= menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true);
                        if (warp != null)
                        { ctx.Click(warp.UINode); ctx.Blackboard.Set("return_phase", "at_station"); }
                        else
                        { ctx.KeyPress(VirtualKey.Escape); ctx.Blackboard.Set("return_phase", "find_station"); }
                        return NodeStatus.Running;
                    }

                    case "at_station":
                    {
                        if (ctx.GameState.IsDocked) { ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Success; }
                        var station = FindStationInOverview(ctx);
                        if (station != null)
                        {
                            ctx.Blackboard.Set("menu_expected", true);
                            ctx.RightClick(station.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(500));
                            ctx.Blackboard.Set("return_phase", "dock_menu");
                        }
                        return NodeStatus.Running;
                    }

                    case "dock_menu":
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        if (!ctx.GameState.HasContextMenu)
                        { ctx.Blackboard.Set("return_phase", "at_station"); return NodeStatus.Running; }
                        var menu = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var dock = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);
                        if (dock != null)
                        { ctx.Click(dock.UINode); ctx.Blackboard.Set("return_phase", "waiting_to_dock"); ctx.Blackboard.Set("return_tick", 0); }
                        else
                        { ctx.KeyPress(VirtualKey.Escape); ctx.Blackboard.Set("return_phase", "at_station"); }
                        return NodeStatus.Running;
                    }

                    case "waiting_to_dock":
                    {
                        if (ctx.GameState.IsDocked)
                        {
                            // Memorise which top-level menu category led to a successful dock
                            var wonMenu = ctx.Blackboard.Get<string>("return_current_menu") ?? "";
                            if (!string.IsNullOrEmpty(wonMenu))
                            {
                                ctx.Blackboard.Set("home_menu_type", wonMenu);
                                ctx.Log($"[Mining] Home station is under '{wonMenu}' — remembered for this session.");
                            }
                            ctx.Blackboard.Set("return_phase", "");
                            return NodeStatus.Success;
                        }
                        int tick = ctx.Blackboard.Get<int>("return_tick") + 1;
                        ctx.Blackboard.Set("return_tick", tick);
                        if (tick > 60) { ctx.Blackboard.Set("return_phase", "find_station"); ctx.Blackboard.Set("return_tick", 0); }
                        return NodeStatus.Running;
                    }

                    default:
                        ctx.Blackboard.Set("return_phase", "");
                        return NodeStatus.Running;
                }
            }));

    // Marks whichever top-level menu category ("Stations"/"Structures") was tried and failed,
    // so the next space_menu_dock iteration tries the other one.
    private static void MarkCurrentMenuTried(BotContext ctx)
    {
        var current = ctx.Blackboard.Get<string>("return_current_menu") ?? "";
        if (current.Contains("Station", StringComparison.OrdinalIgnoreCase))
            ctx.Blackboard.Set("return_tried_stations", true);
        else if (current.Contains("Structure", StringComparison.OrdinalIgnoreCase))
            ctx.Blackboard.Set("return_tried_structures", true);
        ctx.Log($"[Mining] '{current}' submenu did not contain home station — will try the other category.");
    }

    // ─── 7d-pre. Proactive belt discovery ────────────────────────────────────

    private IBehaviorNode DiscoverBeltsOnce() =>
        new SequenceNode("Discover belt list",
            new ConditionNode("Belt list unknown?", ctx =>
                !_beltsDiscoveryDone &&
                _beltCount == 0 &&
                !ctx.GameState.IsWarping &&
                ctx.Blackboard.IsCooldownReady("belt_discover_cd")),
            new ActionNode("Discovery state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("discover_phase") ?? "";

                switch (phase)
                {
                    case "":
                        ctx.Blackboard.Set("menu_expected", true);
                        RightClickInSpace(ctx);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        ctx.Blackboard.Set("discover_phase", "space_menu");
                        ctx.Blackboard.Set("discover_tick", 0);
                        return NodeStatus.Running;

                    case "space_menu":
                    {
                        int tick = ctx.Blackboard.Get<int>("discover_tick") + 1;
                        ctx.Blackboard.Set("discover_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 5) { Abort(ctx, 30); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }
                        ctx.Blackboard.Set("discover_tick", 0);

                        ContextMenuEntry? beltsEntry = null;
                        foreach (var m in ctx.GameState.ParsedUI.ContextMenus)
                        {
                            beltsEntry = m.Entries.FirstOrDefault(e =>
                                e.Text != null && e.Text.Trim().Length < 20 &&
                                e.Text.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase));
                            if (beltsEntry != null) break;
                        }

                        if (beltsEntry == null)
                        {
                            ctx.Log("[Mining] No Asteroid Belts entry in space menu — system has none");
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("menu_expected", false);
                            _beltsDiscoveryDone = true;
                            Abort(ctx, 600);
                            return NodeStatus.Failure;
                        }

                        ctx.Blackboard.Set("discover_ref_x",
                            beltsEntry.UINode.Region.X + beltsEntry.UINode.Region.Width);

                        HoverAndSlide(ctx, beltsEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(600));
                        ctx.Blackboard.Set("discover_phase", "belt_submenu");
                        return NodeStatus.Running;
                    }

                    case "belt_submenu":
                    {
                        int tick = ctx.Blackboard.Get<int>("discover_tick") + 1;
                        ctx.Blackboard.Set("discover_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 8) { Abort(ctx, 20); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();

                        static bool BeltText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        int discoverRefX = ctx.Blackboard.Get<int>("discover_ref_x");

                        var beltEntries = allEntries
                            .Where(e => e.UINode.Region.X > discoverRefX && BeltText(e, t =>
                                (t.Length > 14 && t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)) ||
                                (t.Length > 8  && (t.Contains("Ore Deposit",  StringComparison.OrdinalIgnoreCase) ||
                                                   t.Contains("Cluster",      StringComparison.OrdinalIgnoreCase)))))
                            .OrderBy(e => e.UINode.Region.Y)
                            .ToList();

                        if (beltEntries.Count == 0 && allMenus.Count >= 2)
                        {
                            var minX   = allMenus.Min(m => m.UINode.Region.X);
                            var subMnu = allMenus.Where(m => m.UINode.Region.X > minX + 10)
                                                 .MaxBy(m => m.UINode.Region.X);
                            if (subMnu?.Entries.Count > 0)
                                beltEntries = subMnu.Entries.OrderBy(e => e.UINode.Region.Y).ToList();
                        }

                        if (beltEntries.Count == 0)
                        {
                            var treeNodes = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > discoverRefX &&
                                    n.Region.Height > 3 &&
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Length > 14 &&
                                        t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)))
                                ?? []).OrderBy(n => n.Region.Y).ToList();

                            if (treeNodes.Count > 0)
                            {
                                _beltCount = treeNodes.Count;
                                for (int i = 0; i < treeNodes.Count; i++)
                                {
                                    var txt = treeNodes[i].GetAllContainedDisplayTexts()
                                        .Where(t => t.Length > 3).OrderByDescending(t => t.Length)
                                        .FirstOrDefault() ?? $"Belt {i + 1}";
                                    _beltNames[i] = txt.Trim();
                                }
                            }
                            else if (tick > 8)
                            {
                                Abort(ctx, 15);
                                return NodeStatus.Failure;
                            }
                            else
                            {
                                return NodeStatus.Running;
                            }
                        }
                        else
                        {
                            _beltCount = beltEntries.Count;
                            for (int i = 0; i < beltEntries.Count; i++)
                            {
                                var txt = beltEntries[i].UINode.GetAllContainedDisplayTexts()
                                    .Where(t => t.Length > 3).OrderByDescending(t => t.Length)
                                    .FirstOrDefault() ?? beltEntries[i].Text ?? $"Belt {i + 1}";
                                _beltNames[i] = txt.Trim();
                            }
                        }

                        ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.Set("menu_expected", false);
                        ctx.Blackboard.Set("discover_phase", "");
                        ctx.Blackboard.Set("discover_tick", 0);
                        _beltsDiscoveryDone = true;

                        if (_beltCount > 0)
                            ctx.Log($"[Mining] Discovered {_beltCount} asteroid belts");

                        return NodeStatus.Failure;
                    }

                    default:
                        ctx.Blackboard.Set("discover_phase", "");
                        return NodeStatus.Failure;
                }

                void Abort(BotContext c, int cooldownSec)
                {
                    c.Blackboard.Set("discover_phase", "");
                    c.Blackboard.Set("discover_tick", 0);
                    c.Blackboard.Set("menu_expected", false);
                    c.Blackboard.SetCooldown("belt_discover_cd", TimeSpan.FromSeconds(cooldownSec));
                }
            }));

    // ─── 7e. Warp to next asteroid belt ─────────────────────────────────────

    private IBehaviorNode WarpToBelt() =>
        new SequenceNode("Warp to belt",
            new ConditionNode("No asteroids + cooldown + not warping?", ctx =>
                !AnyAsteroidsInOverview(ctx) &&
                !ctx.GameState.IsWarping &&
                ctx.Blackboard.IsCooldownReady("warp_belt")),
            new ActionNode("Belt navigation state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("belt_phase") ?? "";
                int ticks = ctx.Blackboard.Get<int>("belt_phase_ticks");

                void Progress(string nextPhase)
                {
                    ctx.Blackboard.Set("belt_phase", nextPhase);
                    ctx.Blackboard.Set("belt_phase_ticks", 0);
                }

                void Reset(int cooldownSec = 10)
                {
                    ctx.Blackboard.Set("belt_phase", "");
                    ctx.Blackboard.Set("belt_phase_ticks", 0);
                    ctx.Blackboard.Set("menu_expected", false);
                    ctx.Blackboard.Set("cascade_ref_x", 0);
                    ctx.Blackboard.SetCooldown("warp_belt", TimeSpan.FromSeconds(cooldownSec));
                }

                bool TimedOut(int maxTicks = 8)
                {
                    ticks++;
                    ctx.Blackboard.Set("belt_phase_ticks", ticks);
                    return ticks > maxTicks;
                }

                switch (phase)
                {
                    case "":
                    {
                        StopAllModules(ctx);
                        RecallDrones(ctx);

                        int lastBelt = ctx.Blackboard.Get<int>("last_belt_target");
                        if (lastBelt >= 0 && _beltCount > 0)
                        {
                            int depletedNorm = lastBelt % _beltCount;
                            _beltDepleted[depletedNorm] = true;
                            ctx.Log($"[Mining] Belt {depletedNorm} marked depleted");
                        }

                        int curIdx = ctx.Blackboard.Get<int>("belt_index");
                        if (_beltCount > 0)
                        {
                            int attempts = 0;
                            int norm = curIdx % _beltCount;
                            while (attempts < _beltCount &&
                                   (_beltDepleted.GetValueOrDefault(norm) || _beltExcluded.GetValueOrDefault(norm)))
                            {
                                curIdx++;
                                norm = curIdx % _beltCount;
                                attempts++;
                            }
                            if (attempts >= _beltCount)
                            {
                                ctx.Log("[Mining] All belts depleted or excluded — resetting belt depletion and retrying");
                                _beltDepleted.Clear();
                                curIdx = 0;
                            }
                        }

                        ctx.Blackboard.Set("belt_target", curIdx);
                        ctx.Blackboard.Set("belt_index",  curIdx + 1);
                        ctx.Blackboard.Set("last_belt_target", curIdx);
                        int displayIdx = _beltCount > 0 ? curIdx % _beltCount : curIdx;
                        string beltName = _beltNames.TryGetValue(displayIdx, out var n) ? n : $"belt {displayIdx}";
                        ctx.Log($"[Mining] No asteroids — moving to {beltName} (index {displayIdx})");

                        ctx.Blackboard.SetCooldown("belt_drone_recall", TimeSpan.FromSeconds(15));
                        Progress("await_drones");
                        return NodeStatus.Running;
                    }

                    case "await_drones":
                    {
                        bool dronesBack = (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) == 0;
                        bool timedOut   = ctx.Blackboard.IsCooldownReady("belt_drone_recall");
                        if (!dronesBack && !timedOut) return NodeStatus.Running;
                        if (!dronesBack) ctx.Log("[Mining] Belt-hop drone recall timed out — warping anyway");

                        ctx.Blackboard.Set("menu_expected", true);
                        RightClickInSpace(ctx);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        Progress("await_space_menu");
                        return NodeStatus.Running;
                    }

                    case "await_space_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut()) { Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        ContextMenuEntry? beltsEntry = null;
                        foreach (var m in ctx.GameState.ParsedUI.ContextMenus)
                        {
                            beltsEntry = m.Entries.FirstOrDefault(e =>
                                e.Text != null &&
                                e.Text.Trim().Length < 20 &&
                                e.Text.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase));
                            if (beltsEntry != null) break;
                        }

                        if (beltsEntry == null)
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            Reset(20);
                            return NodeStatus.Failure;
                        }

                        ctx.Blackboard.Set("cascade_ref_x",
                            beltsEntry.UINode.Region.X + beltsEntry.UINode.Region.Width);

                        HoverAndSlide(ctx, beltsEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_belt_list");
                        return NodeStatus.Running;
                    }

                    case "await_belt_list":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();

                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");
                        int beltTarget  = ctx.Blackboard.Get<int>("belt_target");

                        var allBeltEntries = allEntries
                            .Where(e => e.UINode.Region.X > cascadeRefX && EntryHasText(e, t =>
                                (t.Length > 14 && t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)) ||
                                (t.Length > 8  && (t.Contains("Ore Deposit",  StringComparison.OrdinalIgnoreCase) ||
                                                   t.Contains("Cluster",      StringComparison.OrdinalIgnoreCase)))))
                            .OrderBy(e => e.UINode.Region.Y)
                            .ToList();

                        if (allBeltEntries.Count > 0)
                        {
                            _beltCount = allBeltEntries.Count;
                            for (int i = 0; i < allBeltEntries.Count; i++)
                            {
                                var txt = allBeltEntries[i].UINode.GetAllContainedDisplayTexts()
                                    .Where(t => t.Length > 3)
                                    .OrderByDescending(t => t.Length)
                                    .FirstOrDefault()
                                    ?? allBeltEntries[i].Text ?? $"Belt {i + 1}";
                                _beltNames[i] = txt.Trim();
                            }
                        }
                        else if (allMenus.Count >= 2)
                        {
                            var spaceMenuX = allMenus.Min(m => m.UINode.Region.X);
                            var subMenu    = allMenus
                                .Where(m => m.UINode.Region.X > spaceMenuX + 10)
                                .MaxBy(m => m.UINode.Region.X);
                            if (subMenu != null && subMenu.Entries.Count > 0)
                            {
                                _beltCount = subMenu.Entries.Count;
                                for (int i = 0; i < subMenu.Entries.Count; i++)
                                {
                                    var txt = subMenu.Entries[i].UINode.GetAllContainedDisplayTexts()
                                        .Where(t => t.Length > 3)
                                        .OrderByDescending(t => t.Length)
                                        .FirstOrDefault()
                                        ?? subMenu.Entries[i].Text ?? $"Belt {i + 1}";
                                    _beltNames[i] = txt.Trim();
                                }
                            }
                        }

                        ContextMenuEntry? beltEntry = allBeltEntries.Count > 0
                            ? allBeltEntries[beltTarget % allBeltEntries.Count]
                            : null;

                        if (beltEntry == null && allMenus.Count >= 2)
                        {
                            var spaceMenuX = allMenus.Min(m => m.UINode.Region.X);
                            var subMenu = allMenus
                                .Where(m => m.UINode.Region.X > spaceMenuX + 10)
                                .MaxBy(m => m.UINode.Region.X);
                            if (subMenu != null)
                            {
                                var subEntries = subMenu.Entries.OrderBy(e => e.UINode.Region.Y).ToList();
                                beltEntry = subEntries.Count > 0
                                    ? subEntries[beltTarget % subEntries.Count]
                                    : null;
                            }
                        }

                        UITreeNodeWithDisplayRegion? beltNodeFromTree = null;
                        if (beltEntry == null)
                        {
                            var treeNodes = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&
                                    n.Region.Height > 3 &&
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Length > 14 &&
                                        t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .OrderBy(n => n.Region.Y)
                                .ToList();
                            if (treeNodes.Count > 0)
                            {
                                _beltCount = treeNodes.Count;
                                for (int i = 0; i < treeNodes.Count; i++)
                                {
                                    var tn = treeNodes[i].GetAllContainedDisplayTexts()
                                        .Where(t => t.Length > 3).OrderByDescending(t => t.Length)
                                        .FirstOrDefault() ?? $"Belt {i + 1}";
                                    _beltNames[i] = tn.Trim();
                                }
                                beltNodeFromTree = treeNodes[beltTarget % treeNodes.Count];
                            }
                        }

                        if (beltEntry == null && beltNodeFromTree == null)
                        {
                            var beltsHeader = allEntries.FirstOrDefault(e =>
                                EntryHasText(e, t => t.Length < 20 &&
                                    t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)));
                            if (beltsHeader != null)
                            {
                                HoverAndSlide(ctx, beltsHeader.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(500));
                            }
                            if (TimedOut(16)) { ctx.KeyPress(VirtualKey.Escape); Reset(20); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var chosenBeltNode = beltEntry?.UINode ?? beltNodeFromTree;
                        if (beltEntry != null)
                        {
                            HoverAndSlide(ctx, beltEntry.UINode);
                        }
                        else if (beltNodeFromTree != null)
                        {
                            HoverAndSlide(ctx, beltNodeFromTree);
                        }
                        if (chosenBeltNode != null)
                            ctx.Blackboard.Set("cascade_ref_x",
                                chosenBeltNode.Region.X + chosenBeltNode.Region.Width);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_actions_menu");
                        return NodeStatus.Running;
                    }

                    case "await_actions_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();

                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");

                        var warpEntry = allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Warp",   StringComparison.OrdinalIgnoreCase)
                                              && t.Contains("Within",  StringComparison.OrdinalIgnoreCase)));
                        warpEntry ??= allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Warp", StringComparison.OrdinalIgnoreCase)));

                        UITreeNodeWithDisplayRegion? warpNodeFromTree = null;
                        if (warpEntry == null)
                        {
                            warpNodeFromTree = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&
                                    n.Region.Height > 3 &&
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Contains("Warp", StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .MinBy(n => n.Region.Y);
                        }

                        if (warpEntry == null && warpNodeFromTree == null)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); }
                            return NodeStatus.Running;
                        }

                        var chosenWarpNode = warpEntry?.UINode ?? warpNodeFromTree;
                        var warpText = warpEntry?.UINode.GetAllContainedDisplayTexts()
                                           .FirstOrDefault(t => t.Contains("Warp", StringComparison.OrdinalIgnoreCase))
                                       ?? warpNodeFromTree?.GetAllContainedDisplayTexts()
                                           .FirstOrDefault(t => t.Contains("Warp", StringComparison.OrdinalIgnoreCase))
                                       ?? "?";

                        bool isDirectWarpAction = warpText.Contains("0 m",  StringComparison.OrdinalIgnoreCase)
                                               || warpText.Contains("0m",   StringComparison.OrdinalIgnoreCase)
                                               || System.Text.RegularExpressions.Regex.IsMatch(warpText, @"\d+\s*(m|km)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        ctx.Hover(chosenWarpNode!);
                        ctx.Wait(TimeSpan.FromMilliseconds(200));
                        ctx.Click(chosenWarpNode!);

                        if (isDirectWarpAction)
                        {
                            Reset(30);
                            return NodeStatus.Success;
                        }

                        ctx.Blackboard.Set("cascade_ref_x",
                            chosenWarpNode!.Region.X + chosenWarpNode.Region.Width);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_warp_distances");
                        return NodeStatus.Running;
                    }

                    case "await_warp_distances":
                    {
                        if (ctx.GameState.IsWarping)
                        {
                            Reset(30);
                            return NodeStatus.Success;
                        }

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();

                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");

                        var within0 = allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Within 0", StringComparison.OrdinalIgnoreCase)));
                        within0 ??= allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t =>
                                (t.Equals("0 m", StringComparison.OrdinalIgnoreCase) ||
                                 t.Equals("0m",  StringComparison.OrdinalIgnoreCase))));

                        UITreeNodeWithDisplayRegion? within0Node = null;
                        if (within0 == null)
                        {
                            within0Node = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&
                                    n.Region.Height > 3 &&
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Contains("Within 0", StringComparison.OrdinalIgnoreCase) ||
                                        t.Equals("0 m", StringComparison.OrdinalIgnoreCase) ||
                                        t.Equals("0m",  StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .MinBy(n => n.Region.Y);
                        }

                        if (within0 != null || within0Node != null)
                        {
                            var clickNode = within0?.UINode ?? within0Node!;
                            ctx.Hover(clickNode);
                            ctx.Wait(TimeSpan.FromMilliseconds(200));
                            ctx.Click(clickNode);
                            Reset(30);
                            return NodeStatus.Success;
                        }

                        if (TimedOut(12)) { Reset(8); return NodeStatus.Failure; }
                        return NodeStatus.Running;
                    }

                    default:
                        ctx.Blackboard.Set("belt_phase", "");
                        return NodeStatus.Running;
                }
            }));
}
