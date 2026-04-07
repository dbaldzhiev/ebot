using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

/// <summary>
/// Production mining bot — state-aware, self-recovering.
///
/// Priority order (evaluated every tick):
///  1. Close modal message boxes.
///  2. Close stray context menus that would block clicks.
///  3. Emergency warp to station when shield &lt; 25%.
///  4. Docked: transfer ore hold → item hangar, then undock.
///  5. Wait while warping.
///  6. Ensure the "Mining" (or "Asteroid") overview tab is active.
///  7. In space:
///       a. Wait for capacitor (&lt; 15%).
///       b. Ore hold ≥ 95%: stop lasers, recall drones, return to station.
///       c. Drone defence: launch on attack, recall when safe.
///       d. Asteroids visible: lock targets then activate top-row mining modules.
///       e. No asteroids: right-click space → Asteroid Belts → select belt → warp to 0.
/// </summary>
public sealed class MiningBot : IBot
{
    // ─── Constants ──────────────────────────────────────────────────────────

    private const int    ShieldEscapePercent = 25;
    private const double OreHoldFullPercent  = 95.0;
    private const int    MaxLockedAsteroids  = 2;
    private const int    MinCapPercent       = 15;
    private const double LaserRangeM         = 14_500; // approach if farther than this

    // ─── Session statistics ──────────────────────────────────────────────────

    private double         _totalUnloadedM3;
    private int            _unloadCycles;
    private DateTimeOffset _sessionStart;

    // ─── IBot ───────────────────────────────────────────────────────────────

    public string Name => "Mining Bot";

    public string Description
    {
        get
        {
            if (_sessionStart == default) return "Mining Bot — idle";
            var elapsed = DateTimeOffset.UtcNow - _sessionStart;
            var rate    = elapsed.TotalHours > 0.01
                ? $"{_totalUnloadedM3 / elapsed.TotalHours:F0} m³/hr" : "—";
            return $"Mining Bot — {_totalUnloadedM3:F0} m³ unloaded " +
                   $"({_unloadCycles} cycles) | ~{rate}";
        }
    }

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs   = 2000,
        MinActionDelayMs = 80,
        MaxActionDelayMs = 250,
        CoordinateJitter = 4,
    };

    public void OnStart(BotContext ctx)
    {
        _totalUnloadedM3 = 0;
        _unloadCycles    = 0;
        _sessionStart    = DateTimeOffset.UtcNow;
        SyncStats(ctx);
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Mining Root",
            HandleMessageBoxes(),
            HandleStrayContextMenu(),
            HandleShieldEmergency(),
            HandleDocked(),
            HandleWarping(),
            EnsureMiningTab(),      // ← switch overview to Mining tab before any space logic
            HandleInSpace());

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Message boxes
    // ═══════════════════════════════════════════════════════════════════════

    private static IBehaviorNode HandleMessageBoxes() =>
        new SequenceNode("Close message box",
            new ConditionNode("Has message box?",
                ctx => ctx.GameState.HasMessageBox),
            new ActionNode("Click first button", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.MessageBoxes[0].Buttons.FirstOrDefault();
                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                return NodeStatus.Success;
            }));

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Stray context menus
    // ═══════════════════════════════════════════════════════════════════════

    private static IBehaviorNode HandleStrayContextMenu() =>
        new SequenceNode("Close stray context menu",
            new ConditionNode("Unexpected menu?", ctx =>
                ctx.GameState.HasContextMenu &&
                !ctx.Blackboard.Get<bool>("menu_expected")),
            new ActionNode("Press Escape", ctx =>
            {
                ctx.KeyPress(VirtualKey.Escape);
                return NodeStatus.Success;
            }));

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Shield emergency
    // ═══════════════════════════════════════════════════════════════════════

    private IBehaviorNode HandleShieldEmergency() =>
        new SequenceNode("Shield emergency",
            new ConditionNode("In space + critical shield?", ctx =>
                ctx.GameState.IsInSpace &&
                !ctx.GameState.IsWarping &&
                (ctx.GameState.ParsedUI.ShipUI?.HitpointsPercent?.Shield ?? 100) < ShieldEscapePercent),
            new ActionNode("Emergency return", ctx =>
            {
                StopAllModules(ctx);
                RecallDrones(ctx);
                ctx.Blackboard.Set("needs_unload", true);
                ctx.Blackboard.Set("return_phase", "find_station");
                return NodeStatus.Running;
            }));

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Docked
    // ═══════════════════════════════════════════════════════════════════════

    private IBehaviorNode HandleDocked() =>
        new SequenceNode("Docked",
            new ConditionNode("Is docked?", ctx => ctx.GameState.IsDocked),
            new SelectorNode("Docked actions",
                PerformUnload(),
                RememberStationAndUndock()));

    // ─── Unload state machine ────────────────────────────────────────────────

    private IBehaviorNode PerformUnload() =>
        new SequenceNode("Unload ore",
            new ConditionNode("Needs unload?",
                ctx => ctx.Blackboard.Get<bool>("needs_unload")),
            new ActionNode("Unload state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("unload_phase") ?? "";
                switch (phase)
                {
                    case "":
                        ctx.KeyPress(VirtualKey.C, VirtualKey.Alt);
                        ctx.Wait(TimeSpan.FromSeconds(1.5));
                        ctx.Blackboard.Set("unload_phase", "find_orehold");
                        return NodeStatus.Running;

                    case "find_orehold":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null)
                        {
                            var link = ctx.GameState.ParsedUI.UITree?.FindFirst(n =>
                                n.GetAllContainedDisplayTexts()
                                 .Any(t => t.Contains("Ore Hold",
                                     StringComparison.OrdinalIgnoreCase))
                                && n.Region.Width > 10 && n.Region.Height > 6);
                            if (link != null) { ctx.Click(link); ctx.Wait(TimeSpan.FromMilliseconds(700)); }
                            return NodeStatus.Running;
                        }
                        if (oreHold.Items.Count == 0) { FinishUnload(ctx, 0); return NodeStatus.Success; }
                        ctx.Blackboard.Set("unload_phase", "stack_all");
                        return NodeStatus.Running;
                    }

                    case "stack_all":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold?.ButtonToStackAll != null)
                        {
                            ctx.Click(oreHold.ButtonToStackAll);
                            ctx.Wait(TimeSpan.FromMilliseconds(500));
                        }
                        ctx.Blackboard.Set("unload_phase", "select_all");
                        return NodeStatus.Running;
                    }

                    case "select_all":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null || oreHold.Items.Count == 0)
                        { FinishUnload(ctx, 0); return NodeStatus.Success; }
                        ctx.Blackboard.Set("unload_vol_before", oreHold.CapacityGauge?.Used ?? 0.0);
                        ctx.Click(oreHold.Items[0].UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(200));
                        ctx.KeyPress(VirtualKey.A, VirtualKey.Control);
                        ctx.Wait(TimeSpan.FromMilliseconds(300));
                        ctx.Blackboard.Set("unload_phase", "open_menu");
                        return NodeStatus.Running;
                    }

                    case "open_menu":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold == null || oreHold.Items.Count == 0)
                        { FinishUnload(ctx, ctx.Blackboard.Get<double>("unload_vol_before")); return NodeStatus.Success; }
                        ctx.Blackboard.Set("menu_expected", true);
                        ctx.RightClick(oreHold.Items[0].UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        ctx.Blackboard.Set("unload_phase", "click_move");
                        return NodeStatus.Running;
                    }

                    case "click_move":
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        if (!ctx.GameState.HasContextMenu)
                        { ctx.Blackboard.Set("unload_phase", "open_menu"); return NodeStatus.Running; }
                        var menu  = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var entry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("hangar",   StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Move To",  StringComparison.OrdinalIgnoreCase) == true ||
                            e.Text?.Contains("Move All", StringComparison.OrdinalIgnoreCase) == true);
                        if (entry != null)
                        {
                            ctx.Click(entry.UINode);
                            ctx.Wait(TimeSpan.FromSeconds(1));
                            ctx.Blackboard.Set("unload_phase", "verify");
                        }
                        else
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("unload_phase", "open_menu");
                        }
                        return NodeStatus.Running;
                    }

                    case "verify":
                    {
                        var oreHold = FindOreHoldWindow(ctx);
                        if (oreHold?.Items.Count > 0)
                        { ctx.Blackboard.Set("unload_phase", "select_all"); return NodeStatus.Running; }
                        FinishUnload(ctx, ctx.Blackboard.Get<double>("unload_vol_before"));
                        return NodeStatus.Success;
                    }

                    default:
                        ctx.Blackboard.Set("unload_phase", "");
                        return NodeStatus.Running;
                }
            }));

    private void FinishUnload(BotContext ctx, double volume)
    {
        _totalUnloadedM3 += volume;
        _unloadCycles++;
        SyncStats(ctx);
        ctx.Blackboard.Set("needs_unload",       false);
        ctx.Blackboard.Set("unload_phase",        "");
        ctx.Blackboard.Set("unload_vol_before",   0.0);
    }

    private void SyncStats(BotContext ctx)
    {
        ctx.Blackboard.Set("total_unloaded_m3", _totalUnloadedM3);
        ctx.Blackboard.Set("unload_cycles",     _unloadCycles);
    }

    // ─── Remember station and undock ────────────────────────────────────────

    private static IBehaviorNode RememberStationAndUndock() =>
        new ActionNode("Remember home + undock", ctx =>
        {
            if (!ctx.Blackboard.Get<bool>("home_station_set"))
            {
                var name = ctx.GameState.ParsedUI.StationWindow?.UINode
                    .GetAllContainedDisplayTexts()
                    .Where(t => t.Length > 5 && !t.All(char.IsDigit) &&
                                !t.Equals("Undock", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(name))
                {
                    ctx.Blackboard.Set("home_station",     name);
                    ctx.Blackboard.Set("home_station_set", true);
                }
                var sys = ctx.GameState.ParsedUI.InfoPanelContainer?
                    .InfoPanelLocationInfo?.SystemName ?? "";
                if (!string.IsNullOrEmpty(sys))
                    ctx.Blackboard.Set("home_system", sys);
            }
            var btn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
            if (btn == null) return NodeStatus.Failure;
            ctx.Click(btn);
            ctx.Wait(TimeSpan.FromSeconds(10));
            return NodeStatus.Success;
        });

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Warping
    // ═══════════════════════════════════════════════════════════════════════

    private static IBehaviorNode HandleWarping() =>
        new SequenceNode("Wait while warping",
            new ConditionNode("Is warping?", ctx => ctx.GameState.IsWarping),
            new ActionNode("Wait", _ => NodeStatus.Success));

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Ensure Mining overview tab is active
    //    Fires every tick the tab needs switching; succeeds → stops selector
    //    for that tick (1-tick delay), then the tab is active next tick.
    // ═══════════════════════════════════════════════════════════════════════

    private static IBehaviorNode EnsureMiningTab() =>
        new SequenceNode("Ensure Mining tab",
            new ConditionNode("In space, Mining tab not active?", ctx =>
            {
                if (!ctx.GameState.IsInSpace) return false;
                var tabs = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Tabs ?? [];
                if (tabs.Count == 0) return false;
                var miningTab = FindMiningTab(tabs);
                return miningTab != null && !miningTab.IsActive;
            }),
            new ActionNode("Click Mining tab", ctx =>
            {
                var tabs     = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Tabs ?? [];
                var miningTab = FindMiningTab(tabs);
                if (miningTab == null) return NodeStatus.Failure;
                ctx.Click(miningTab.UINode);
                return NodeStatus.Success;
            }));

    private static OverviewTab? FindMiningTab(IReadOnlyList<OverviewTab> tabs) =>
        tabs.FirstOrDefault(t =>
            t.Name?.Contains("Mining",   StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Mine",     StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Ore",      StringComparison.OrdinalIgnoreCase) == true);

    // ═══════════════════════════════════════════════════════════════════════
    // 7. In-space mining loop
    // ═══════════════════════════════════════════════════════════════════════

    private IBehaviorNode HandleInSpace() =>
        new SequenceNode("In space",
            new ConditionNode("Is in space?", ctx => ctx.GameState.IsInSpace),
            new SelectorNode("Space actions",
                WaitCapRegen(),
                ReturnToStation(),
                DroneDefense(),
                MineAtBelt(),
                WarpToBelt()));

    // ─── 7a. Cap regen ───────────────────────────────────────────────────────

    private static IBehaviorNode WaitCapRegen() =>
        new SequenceNode("Cap regen",
            new ConditionNode("Cap critical?", ctx =>
                ctx.GameState.CapacitorPercent.HasValue &&
                ctx.GameState.CapacitorPercent < MinCapPercent),
            new ActionNode("Wait", _ => NodeStatus.Success));

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
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        ctx.Blackboard.Set("return_phase", "find_station");
                        return NodeStatus.Running;

                    case "find_station":
                    {
                        var station = FindStationInOverview(ctx);
                        if (station != null)
                        {
                            ctx.Blackboard.Set("menu_expected", true);
                            ctx.RightClick(station.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(500));
                            ctx.Blackboard.Set("return_phase", "warp_menu");
                        }
                        else
                        {
                            // Try route panel as fallback
                            var route = ctx.GameState.ParsedUI.InfoPanelContainer?
                                .InfoPanelRoute?.RouteElementMarkers.FirstOrDefault();
                            if (route != null)
                            {
                                ctx.Blackboard.Set("menu_expected", true);
                                ctx.RightClick(route);
                                ctx.Wait(TimeSpan.FromMilliseconds(500));
                                ctx.Blackboard.Set("return_phase", "warp_menu");
                            }
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
                        { ctx.Click(dock.UINode); ctx.Blackboard.Set("return_phase", ""); return NodeStatus.Running; }
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
                        { ctx.Click(dock.UINode); ctx.Blackboard.Set("return_phase", ""); }
                        else
                        { ctx.KeyPress(VirtualKey.Escape); ctx.Blackboard.Set("return_phase", "at_station"); }
                        return NodeStatus.Running;
                    }

                    default:
                        ctx.Blackboard.Set("return_phase", "");
                        return NodeStatus.Running;
                }
            }));

    // ─── 7c. Drone defence ───────────────────────────────────────────────────

    private static IBehaviorNode DroneDefense() =>
        new SelectorNode("Drone defence",
            new SequenceNode("Launch vs rats",
                new ConditionNode("Hostiles + drones in bay?", ctx =>
                    (ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Entries
                        .Any(e => e.IsAttackingMe) ?? false) &&
                    (ctx.GameState.ParsedUI.DronesWindow?.DronesInBay?.QuantityCurrent ?? 0) > 0),
                new ConditionNode("Launch cooldown?",
                    ctx => ctx.Blackboard.IsCooldownReady("drone_launch")),
                new ActionNode("Launch all drones", ctx =>
                {
                    ctx.KeyPress(VirtualKey.F, VirtualKey.Shift); // Shift+F = Launch Drones
                    ctx.Blackboard.SetCooldown("drone_launch", TimeSpan.FromSeconds(10));
                    return NodeStatus.Success;
                })),
            new SequenceNode("Recall when safe",
                new ConditionNode("Drones out, no hostiles?", ctx =>
                    (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0 &&
                    !(ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Entries
                        .Any(e => e.IsAttackingMe) ?? false)),
                new ConditionNode("Recall cooldown?",
                    ctx => ctx.Blackboard.IsCooldownReady("drone_recall")),
                new ActionNode("Recall drones", ctx =>
                {
                    ctx.KeyPress(VirtualKey.R, VirtualKey.Shift); // Shift+R = Recall All Drones
                    ctx.Blackboard.SetCooldown("drone_recall", TimeSpan.FromSeconds(15));
                    return NodeStatus.Success;
                })));

    // ─── 7d. Mine at belt ────────────────────────────────────────────────────
    //
    //  Fires only when asteroids are visible in the current overview tab.
    //  Inner selector priority:
    //    1. Lock up to MaxLockedAsteroids targets
    //    2. Activate inactive TOP-ROW modules (mining lasers are in high slots)
    //    3. Approach if nearest locked target is beyond laser range
    //    4. Idle (mining in progress)

    private static IBehaviorNode MineAtBelt() =>
        new SequenceNode("Mine at belt",
            new ConditionNode("Asteroids visible?", ctx => AnyAsteroidsInOverview(ctx)),
            new SelectorNode("Mining actions",

                // Priority 1: Lock more asteroids (maintain up to MaxLockedAsteroids)
                new SequenceNode("Lock asteroid",
                    new ConditionNode("Under max locks + cooldown?", ctx =>
                        ctx.GameState.TargetCount < MaxLockedAsteroids &&
                        ctx.Blackboard.IsCooldownReady("lock_target")),
                    new ActionNode("Lock state machine", ctx =>
                    {
                        var lockPhase = ctx.Blackboard.Get<string>("lock_phase") ?? "";

                        if (lockPhase == "")
                        {
                            // Find an asteroid that isn't already locked
                            var lockedNames = ctx.GameState.ParsedUI.Targets
                                .Select(t => t.TextLabel ?? "")
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            var asteroid = ctx.GameState.ParsedUI.OverviewWindows
                                .FirstOrDefault()?.Entries
                                .Where(e => IsAsteroid(e) && !lockedNames.Contains(e.Name ?? ""))
                                .OrderBy(e => e.DistanceInMeters ?? double.MaxValue)
                                .FirstOrDefault();

                            if (asteroid == null) return NodeStatus.Failure;

                            ctx.Blackboard.Set("menu_expected", true);
                            ctx.RightClick(asteroid.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("lock_phase", "menu");
                            return NodeStatus.Running;
                        }

                        // lock_phase == "menu": context menu should be open
                        ctx.Blackboard.Set("menu_expected", false);
                        ctx.Blackboard.Set("lock_phase", "");

                        if (!ctx.GameState.HasContextMenu)
                        {
                            ctx.Blackboard.SetCooldown("lock_target", TimeSpan.FromSeconds(3));
                            return NodeStatus.Failure;
                        }

                        var menu      = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var lockEntry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Lock", StringComparison.OrdinalIgnoreCase) == true);

                        if (lockEntry != null)
                        {
                            ctx.Click(lockEntry.UINode);
                            ctx.Blackboard.SetCooldown("lock_target", TimeSpan.FromSeconds(6));
                            return NodeStatus.Success;
                        }

                        ctx.KeyPress(VirtualKey.Escape);
                        ctx.Blackboard.SetCooldown("lock_target", TimeSpan.FromSeconds(3));
                        return NodeStatus.Failure;
                    })),

                // Priority 2: Activate idle mining lasers (TOP ROW only = high slots)
                new SequenceNode("Activate mining lasers",
                    new ConditionNode("Has target + idle top-row modules?", ctx =>
                        ctx.GameState.HasTargets &&
                        (ctx.GameState.ParsedUI.ShipUI?.ModuleButtonsRows.Top
                            .Any(m => m.IsActive != true && !m.IsBusy && !m.IsOffline) ?? false)),
                    new ConditionNode("Activate cooldown?",
                        ctx => ctx.Blackboard.IsCooldownReady("activate_modules")),
                    new ActionNode("Click idle top-row modules", ctx =>
                    {
                        var shipUI = ctx.GameState.ParsedUI.ShipUI;
                        if (shipUI == null) return NodeStatus.Failure;
                        bool any = false;
                        // Top row = high slots: mining lasers, survey scanner
                        foreach (var mod in shipUI.ModuleButtonsRows.Top
                            .Where(m => m.IsActive != true && !m.IsBusy && !m.IsOffline))
                        {
                            ctx.Click(mod.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(300));
                            any = true;
                        }
                        ctx.Blackboard.SetCooldown("activate_modules", TimeSpan.FromSeconds(5));
                        return any ? NodeStatus.Success : NodeStatus.Failure;
                    })),

                // Priority 3: Approach if locked target is beyond laser range
                new SequenceNode("Approach asteroid",
                    new ConditionNode("Target too far?", ctx =>
                    {
                        var t = ctx.GameState.ParsedUI.Targets.FirstOrDefault();
                        return t?.DistanceInMeters > LaserRangeM;
                    }),
                    new ConditionNode("Approach cooldown?",
                        ctx => ctx.Blackboard.IsCooldownReady("approach")),
                    new ActionNode("Right-click target → Approach", ctx =>
                    {
                        var target = ctx.GameState.ParsedUI.Targets.FirstOrDefault();
                        if (target == null) return NodeStatus.Failure;
                        ctx.Blackboard.Set("menu_expected", true);
                        ctx.RightClick(target.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        ctx.Blackboard.Set("approach_phase", "menu");
                        ctx.Blackboard.SetCooldown("approach", TimeSpan.FromSeconds(10));
                        return NodeStatus.Running;
                    })),

                // Approach follow-up: click "Approach" in the context menu
                new SequenceNode("Approach menu click",
                    new ConditionNode("Approach menu open?", ctx =>
                        ctx.GameState.HasContextMenu &&
                        ctx.Blackboard.Get<string>("approach_phase") == "menu"),
                    new ActionNode("Click Approach", ctx =>
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        ctx.Blackboard.Set("approach_phase", "");
                        var approach = ctx.GameState.ParsedUI.ContextMenus
                            .SelectMany(m => m.Entries)
                            .FirstOrDefault(e =>
                                e.Text?.Contains("Approach", StringComparison.OrdinalIgnoreCase) == true);
                        if (approach != null) { ctx.Click(approach.UINode); return NodeStatus.Success; }
                        ctx.KeyPress(VirtualKey.Escape);
                        return NodeStatus.Failure;
                    })),

                // Priority 4: Idle (lasers cycling, waiting for next cycle or lock)
                new ActionNode("Mining in progress", _ => NodeStatus.Success)));

    // ─── 7e. Warp to next asteroid belt ─────────────────────────────────────
    //
    //  Right-click in empty 3D space → 4-level hover/click chain:
    //
    //    Level 1 — space menu:    "Asteroid Belts ▶"          → HOVER to expand
    //    Level 2 — belt list:     "Gergish X - Asteroid Belt N ▶" → HOVER to expand
    //    Level 3 — belt actions:  "Warp to Within (0 m) ▶"    → HOVER to expand
    //    Level 4 — warp distances:"Within 0 m"                → CLICK
    //
    //  Every entry with a ▶ arrow must be hovered, not clicked.
    //  Only the final leaf ("Within 0 m") is clicked.

    private static IBehaviorNode WarpToBelt() =>
        new SequenceNode("Warp to belt",
            new ConditionNode("No asteroids + cooldown?", ctx =>
                !AnyAsteroidsInOverview(ctx) &&
                ctx.Blackboard.IsCooldownReady("warp_belt")),
            new ActionNode("Belt navigation state machine", ctx =>
            {
                var phase = ctx.Blackboard.Get<string>("belt_phase") ?? "";

                // Timeout counter — incremented each tick we're waiting; reset on progress
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
                    // ─────────────────────────────────────────────────────────
                    // Phase 1: right-click in empty space
                    // ─────────────────────────────────────────────────────────
                    case "":
                    {
                        // Mark menu as expected NOW so HandleStrayContextMenu (which
                        // runs at higher priority) does not dismiss the space menu or
                        // any of the cascade submenus that appear during navigation.
                        ctx.Blackboard.Set("menu_expected", true);
                        RightClickInSpace(ctx);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        Progress("await_space_menu");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 2: space context menu appeared — hover "Asteroid Belts ▶"
                    // ─────────────────────────────────────────────────────────
                    case "await_space_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut()) Reset(8);
                            return NodeStatus.Running;
                        }

                        // Search all open menus for the short "Asteroid Belts" header.
                        // Length < 20 distinguishes "Asteroid Belts" (15) from belt names
                        // like "Gergish X - Asteroid Belt 1" (28+).
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
                            // Menu appeared but has no Asteroid Belts entry — this system has none
                            ctx.KeyPress(VirtualKey.Escape);
                            Reset(20);
                            return NodeStatus.Running;
                        }

                        // HOVER — submenus open on mouse-over, not click
                        ctx.Hover(beltsEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        Progress("await_belt_list");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 3: belt list visible — hover a belt entry
                    //
                    //  EVE may render submenus as:
                    //  (a) separate ContextMenu nodes → allMenus.Count grows
                    //  (b) MenuEntryView nodes nested inside the first ContextMenu
                    //      → allMenus.Count stays 1, but entries contain belt names
                    //
                    //  Strategy: search ALL entries across ALL menus by text.
                    //  Positional fallback: if 2+ menus, hover first entry of rightmost.
                    // ─────────────────────────────────────────────────────────
                    case "await_belt_list":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();
                        ctx.Log($"[WarpToBelt] belt_list t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(5).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(18, (e.Text ?? "").Length))}'"))}]");

                        // Text match: belt names like "Gergish II - Asteroid Belt 1"
                        var beltEntry = allEntries.FirstOrDefault(e =>
                            e.Text != null && e.Text.Length > 14 &&
                            e.Text.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase));
                        beltEntry ??= allEntries.FirstOrDefault(e =>
                            e.Text != null && e.Text.Length > 8 &&
                            (e.Text.Contains("Ore Deposit", StringComparison.OrdinalIgnoreCase) ||
                             e.Text.Contains("Cluster",     StringComparison.OrdinalIgnoreCase)));

                        // Positional fallback: rightmost separate menu's first entry
                        if (beltEntry == null && allMenus.Count >= 2)
                        {
                            var rightmost = allMenus.MaxBy(m => m.UINode.Region.X);
                            beltEntry = rightmost?.Entries.FirstOrDefault();
                            if (beltEntry != null)
                                ctx.Log($"[WarpToBelt] belt positional fallback: '{beltEntry.Text}'");
                        }

                        if (beltEntry == null)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(20); }
                            return NodeStatus.Running;
                        }

                        ctx.Log($"[WarpToBelt] Hovering belt: '{beltEntry.Text}'");
                        ctx.Hover(beltEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        Progress("await_actions_menu");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 4: hover "Warp to Within (0 m) ▶"
                    //  No count requirement — search ALL entries for "Warp"+"Within".
                    //  Belt names never contain "Warp", space menu never contains "Warp".
                    // ─────────────────────────────────────────────────────────
                    case "await_actions_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();
                        ctx.Log($"[WarpToBelt] actions t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(5).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(18, (e.Text ?? "").Length))}'"))}]");

                        // "Warp to Within (0 m)" — prefer match with both Warp+Within
                        var warpEntry = allEntries.FirstOrDefault(e =>
                            e.Text?.Contains("Warp",   StringComparison.OrdinalIgnoreCase) == true &&
                            e.Text.Contains("Within",  StringComparison.OrdinalIgnoreCase));
                        warpEntry ??= allEntries.FirstOrDefault(e =>
                            e.Text?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true);

                        if (warpEntry == null)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); }
                            return NodeStatus.Running;
                        }

                        ctx.Log($"[WarpToBelt] Hovering warp: '{warpEntry.Text}'");
                        ctx.Hover(warpEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        Progress("await_warp_distances");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 5: CLICK "Within 0 m"
                    //  Search ALL entries — "Within 0" is unique to the distance menu.
                    // ─────────────────────────────────────────────────────────
                    case "await_warp_distances":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();
                        ctx.Log($"[WarpToBelt] distances t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(6).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(18, (e.Text ?? "").Length))}'"))}]");

                        var within0 = allEntries.FirstOrDefault(e =>
                            e.Text?.Contains("Within 0", StringComparison.OrdinalIgnoreCase) == true);

                        if (within0 != null)
                        {
                            ctx.Log($"[WarpToBelt] Clicking: '{within0.Text}'");
                            ctx.Click(within0.UINode);
                            Reset();
                            ctx.Blackboard.SetCooldown("warp_belt", TimeSpan.FromSeconds(30));
                            return NodeStatus.Running;
                        }

                        if (TimedOut(12)) Reset(8);
                        return NodeStatus.Running;
                    }

                    default:
                        ctx.Blackboard.Set("belt_phase", "");
                        return NodeStatus.Running;
                }
            }));

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Right-clicks in the 3D game viewport — the area to the left of and above
    /// the overview, away from the ship HUD at the bottom.
    /// </summary>
    private static void RightClickInSpace(BotContext ctx)
    {
        var root = ctx.GameState.ParsedUI.UITree;
        if (root == null)
        {
            // Last resort: hardcoded safe position
            ctx.Actions.Enqueue(new RightClickAction(400, 300));
            return;
        }

        var cw = root.Region.Width;
        var ch = root.Region.Height;
        var cx = root.Region.X;
        var cy = root.Region.Y;

        // Default: upper-left quadrant (away from overview at right, HUD at bottom)
        var targetX = cx + cw / 3;
        var targetY = cy + ch / 4;

        // If overview is on the left side, click right-center instead
        var overview = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        if (overview != null)
        {
            var ovCenterX = overview.UINode.Region.X + overview.UINode.Region.Width / 2;
            if (ovCenterX < cw / 2)
                targetX = cx + cw * 2 / 3;
        }

        ctx.Actions.Enqueue(new RightClickAction(
            Math.Max(cx + 50, targetX),
            Math.Max(cy + 50, targetY)));
    }

    private static InventoryWindow? FindOreHoldWindow(BotContext ctx) =>
        ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w =>
            w.HoldType == InventoryHoldType.Mining) ??
        // Fallback: text match in case title extraction returns Unknown
        ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w =>
            w.SubCaptionLabelText?.Contains("ore",   StringComparison.OrdinalIgnoreCase) == true ||
            w.SubCaptionLabelText?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsOreHoldFull(BotContext ctx)
    {
        var w = FindOreHoldWindow(ctx);
        return w?.CapacityGauge?.FillPercent >= OreHoldFullPercent;
    }

    private static bool AnyAsteroidsInOverview(BotContext ctx)
    {
        var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        return ov?.Entries.Any(IsAsteroid) ?? false;
    }

    private static OverviewEntry? FindStationInOverview(BotContext ctx)
    {
        var homeStation = ctx.Blackboard.Get<string>("home_station");
        var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        if (ov == null) return null;

        // Exact match against remembered home station first
        if (!string.IsNullOrEmpty(homeStation))
        {
            var match = ov.Entries.FirstOrDefault(e =>
            {
                var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
                    .Concat(e.Texts).Concat(e.CellsTexts.Values);
                return texts.Any(t => t.Contains(homeStation,
                    StringComparison.OrdinalIgnoreCase));
            });
            if (match != null) return match;
        }

        // Fallback: any dockable structure
        return ov.Entries.FirstOrDefault(e =>
        {
            var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
                .Concat(e.Texts).Concat(e.CellsTexts.Values);
            return texts.Any(t =>
                t.Contains("Station",  StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Outpost",  StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Astrahus", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Fortizar", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Keepstar", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Raitaru",  StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Azbel",    StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Sotiyo",   StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Athanor",  StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Tatara",   StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void StopAllModules(BotContext ctx)
    {
        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null) return;
        // Only stop top-row (high slot) modules — don't toggle shields/propulsion
        foreach (var mod in shipUI.ModuleButtonsRows.Top.Where(m => m.IsActive == true))
        {
            ctx.Click(mod.UINode);
            ctx.Wait(TimeSpan.FromMilliseconds(200));
        }
    }

    private static void RecallDrones(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift); // Shift+R = Recall All Drones
    }

    private static bool IsAsteroid(OverviewEntry e)
    {
        var texts = new[] { e.Name ?? "", e.ObjectType ?? "" }
            .Concat(e.Texts)
            .Select(t => t.ToLowerInvariant());
        return texts.Any(t => _asteroidKeywords.Any(k => t.Contains(k)));
    }

    private static readonly string[] _asteroidKeywords =
    [
        "asteroid",
        "veldspar", "scordite", "pyroxeres", "plagioclase",
        "omber", "kernite", "jaspet", "hemorphite", "hedbergite",
        "spodumain", "crokite", "bistot", "arkonor", "mercoxit",
        "gneiss", "dark ochre", "ochre",
        "cobaltite", "euxenite", "scheelite", "titanite",
        "chromite", "otavite", "sperrylite", "vanadinite",
        "carnotite", "zircon", "loparite", "monazite",
        "bezdnacine", "rakovene", "ducinium", "eifyrium", "talassonite",
    ];
}
