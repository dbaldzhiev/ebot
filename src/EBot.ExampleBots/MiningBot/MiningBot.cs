using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
public sealed partial class MiningBot : IBot
{
    // ─── Configurable settings (set at construction time via init) ─────────

    /// <summary>Name of the home station to return to. Auto-detected from station window if null.</summary>
    public string? HomeStationOverride { get; init; }

    /// <summary>Ore hold fill % at which the bot returns to unload. Default 95.</summary>
    public int OreHoldFullPercent { get; init; } = 95;

    /// <summary>Shield % below which the bot emergency-warps to station. Default 25.</summary>
    public int ShieldEscapePercent { get; init; } = 25;

    // ─── Internal constants ────────────────────────────────────────────────

    /// <summary>Approach threshold: if nearest asteroid is farther than this, approach it. Covers most mining lasers.</summary>
    private const double DefaultLaserRangeM = 15_000;

    /// <summary>Capacitor % below which the bot waits before acting.</summary>
    private const int MinCapPct = 15;

    // ─── Session statistics ──────────────────────────────────────────────────

    private double         _totalUnloadedM3;
    private int            _unloadCycles;
    private DateTimeOffset _sessionStart;

    // ─── Belt registry (thread-safe: bot tick thread + API thread) ───────────

    private readonly ConcurrentDictionary<int, string> _beltNames    = new();
    private readonly ConcurrentDictionary<int, bool>   _beltDepleted = new();
    private readonly ConcurrentDictionary<int, bool>   _beltExcluded = new();
    private int _beltCount = 0;
    private bool _beltsDiscoveryDone;

    public IReadOnlyDictionary<int, string> BeltNames    => _beltNames;
    public IReadOnlyDictionary<int, bool>   BeltDepleted => _beltDepleted;
    public IReadOnlyDictionary<int, bool>   BeltExcluded => _beltExcluded;
    public int BeltCount => _beltCount;

    public void ToggleBeltExcluded(int idx) =>
        _beltExcluded.AddOrUpdate(idx, true, (_, v) => !v);

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
        _beltDepleted.Clear();
        _beltExcluded.Clear();
        _beltNames.Clear();
        _beltCount = 0;
        _beltsDiscoveryDone = false;
        ctx.Blackboard.Set("last_belt_target", -1);
        // Pre-seed home station if provided so the bot doesn't need to auto-detect
        if (!string.IsNullOrWhiteSpace(HomeStationOverride))
        {
            ctx.Blackboard.Set("home_station",     HomeStationOverride);
            ctx.Blackboard.Set("home_station_set", true);
        }
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
        ctx.Blackboard.Set("belt_index",        0);   // restart belt cycle counter after unload
        ctx.Blackboard.Set("last_belt_target", -1);  // no current belt after station run
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
                // Logging probe: always fails → selector continues to real actions
                new ActionNode("Log space state", ctx =>
                {
                    var ovWindows  = ctx.GameState.ParsedUI.OverviewWindows.Count;
                    var ov         = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
                    var ovEntries  = ov?.Entries.Count ?? 0;
                    var ovHeaders  = ov != null ? string.Join(",", ov.ColumnHeaders) : "?";
                    var asteroids  = AsteroidsInOverview(ctx).Count();
                    var targets    = ctx.GameState.TargetCount;
                    var capPct     = ctx.GameState.CapacitorPercent?.ToString("F0") ?? "?";
                    var warping    = ctx.GameState.IsWarping;
                    var beltPhase  = ctx.Blackboard.Get<string>("belt_phase") ?? "";
                    var warpReady  = ctx.Blackboard.IsCooldownReady("warp_belt");
                    var shipUI     = ctx.GameState.ParsedUI.ShipUI;
                    var topMods    = shipUI?.ModuleButtonsRows.Top.Count ?? 0;
                    var midMods    = shipUI?.ModuleButtonsRows.Middle.Count ?? 0;
                    var botMods    = shipUI?.ModuleButtonsRows.Bottom.Count ?? 0;
                    var miningMods = shipUI != null ? GetMiningModules(shipUI).Count() : 0;
                    ctx.Log($"[Space] ov={ovWindows}w/{ovEntries}e/{asteroids}ast targets={targets} " +
                            $"cap={capPct}% warping={warping} belt_phase='{beltPhase}' warpReady={warpReady} " +
                            $"mods=T{topMods}/M{midMods}/B{botMods} mining={miningMods} hdrs=[{ovHeaders}]");
                    return NodeStatus.Failure;   // fail → SelectorNode tries next child
                }),
                WaitCapRegen(),
                ReturnToStation(),
                DroneDefense(),
                NavigateToMiningHold(),
                DiscoverBeltsOnce(),
                MineAtBelt(),
                WarpToBelt()));

    // ─── 7a. Cap regen ───────────────────────────────────────────────────────

    private static IBehaviorNode WaitCapRegen() =>
        new SequenceNode("Cap regen",
            new ConditionNode("Cap critical?", ctx =>
                ctx.GameState.CapacitorPercent.HasValue &&
                ctx.GameState.CapacitorPercent < MinCapPct),
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
    //  Sequence:
    //    0. Deploy drones
    //    1. Acquire laser range (hover module → tooltip)
    //    2. Mine cycle state machine:
    //         - Build candidates (non-targeted in-range asteroids, sorted by value)
    //         - If N targets < N lasers AND candidates exist: lock → click target → fire laser
    //         - If idle lasers exist AND targets exist: click target → fire laser
    //         - If no candidates in range: approach nearest out-of-range asteroid
    //    3. Propulsion management
    //    4. Idle (mining in progress)

    private IBehaviorNode MineAtBelt() =>
        new SequenceNode("Mine at belt",
            new ConditionNode("Asteroids visible?", ctx =>
            {
                var result = AnyAsteroidsInOverview(ctx);
                if (!result)
                {
                    var ov = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
                    ctx.Log($"[Mine] No asteroids. ov_windows={ctx.GameState.ParsedUI.OverviewWindows.Count} " +
                            $"ov_entries={ov?.Entries.Count ?? 0} " +
                            $"sample=[{string.Join("|", (ov?.Entries.Take(3) ?? []).Select(e => $"\"{e.Name}\"/{e.ObjectType}"))}]");
                }
                return result;
            }),
            new SelectorNode("Mining actions",

                // Priority 0: Deploy drones on arrival
                new SequenceNode("Deploy drones at belt",
                    new ConditionNode("Drones in bay, none in space?", ctx =>
                        (ctx.GameState.ParsedUI.DronesWindow?.DronesInBay?.QuantityCurrent ?? 0) > 0 &&
                        (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) == 0),
                    new ConditionNode("Deploy cooldown?",
                        ctx => ctx.Blackboard.IsCooldownReady("drone_deploy")),
                    new ActionNode("Launch drones", ctx =>
                    {
                        ctx.Log("[Mining] Deploying drones at asteroid belt");
                        ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
                        ctx.Blackboard.SetCooldown("drone_deploy", TimeSpan.FromSeconds(12));
                        return NodeStatus.Success;
                    })),

                // Priority 1: Acquire laser range via module tooltip (once per session)
                new SequenceNode("Acquire laser range",
                    new ConditionNode("Range unknown + cooldown?", ctx =>
                        !ctx.Blackboard.Has("laser_range_m") &&
                        ctx.Blackboard.IsCooldownReady("range_fetch")),
                    new ActionNode("Hover laser for tooltip", ctx =>
                    {
                        var shipUI = ctx.GameState.ParsedUI.ShipUI;
                        var laser  = shipUI != null ? GetMiningModules(shipUI).FirstOrDefault() : null;
                        if (laser == null) return NodeStatus.Failure;
                        ctx.Hover(laser.UINode);
                        ctx.Blackboard.SetCooldown("range_fetch", TimeSpan.FromSeconds(3));
                        ctx.Blackboard.Set("range_hover_pending", true);
                        return NodeStatus.Success;
                    })),

                // Priority 1b: Read the tooltip that appeared after hover
                new SequenceNode("Read laser range tooltip",
                    new ConditionNode("Hover pending + tooltip visible?", ctx =>
                        ctx.Blackboard.Get<bool>("range_hover_pending") &&
                        ctx.GameState.ParsedUI.ModuleButtonTooltip != null),
                    new ActionNode("Cache laser range", ctx =>
                    {
                        ctx.Blackboard.Set("range_hover_pending", false);
                        var tt = ctx.GameState.ParsedUI.ModuleButtonTooltip!;
                        var meters = tt.OptimalRangeMeters.HasValue
                            ? (double)tt.OptimalRangeMeters.Value
                            : (tt.OptimalRangeText != null ? ParseDistanceM(tt.OptimalRangeText) : null);
                        if (meters.HasValue)
                        {
                            ctx.Blackboard.Set("laser_range_m", meters.Value);
                            ctx.Log($"[Mining] Laser range: {meters.Value / 1000:F0} km  (from '{tt.OptimalRangeText}')");
                        }
                        return NodeStatus.Failure; // always fail — selector continues
                    })),

                // Priority 2: Main mine cycle — lock → click target → fire laser
                new ActionNode("Mine cycle", ctx =>
                {
                    var shipUI     = ctx.GameState.ParsedUI.ShipUI;
                    if (shipUI == null) return NodeStatus.Failure;

                    var allLasers  = GetMiningModules(shipUI).ToList();
                    if (allLasers.Count == 0) return NodeStatus.Failure;

                    var idleLasers = allLasers.Where(m => m.IsActive != true && !m.IsBusy && !m.IsOffline).ToList();
                    var targets    = ctx.GameState.ParsedUI.Targets.ToList();
                    var laserRange = ctx.Blackboard.Has("laser_range_m")
                        ? ctx.Blackboard.Get<double>("laser_range_m")
                        : DefaultLaserRangeM;

                    // Candidates: asteroids that are NOT already locked, within laser range
                    var lockedNames = targets
                        .Select(t => t.TextLabel ?? "")
                        .Where(n => n.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var candidates = AsteroidsInOverview(ctx)
                        .Where(a => !lockedNames.Contains(a.Name ?? ""))
                        .Where(a => a.DistanceInMeters.HasValue && a.DistanceInMeters.Value <= laserRange)
                        .OrderByDescending(OreValueOf)
                        .ToList();

                    var phase = ctx.Blackboard.Get<string>("mc_phase") ?? "";

                    // ── Phase: awaiting context menu after right-click ────────────
                    if (phase == "menu")
                    {
                        ctx.Blackboard.Set("menu_expected", false);
                        ctx.Blackboard.Set("mc_phase", "");

                        if (!ctx.GameState.HasContextMenu) return NodeStatus.Failure;

                        var menu      = ctx.GameState.ParsedUI.ContextMenus.FirstOrDefault();
                        var lockEntry = menu?.Entries.FirstOrDefault(e =>
                            e.Text?.Contains("Lock", StringComparison.OrdinalIgnoreCase) == true &&
                            e.Text?.Contains("Unlock", StringComparison.OrdinalIgnoreCase) != true);

                        lockEntry ??= (ctx.GameState.ParsedUI.UITree?
                            .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                n.GetAllContainedDisplayTexts().Any(t =>
                                    t.Contains("Lock", StringComparison.OrdinalIgnoreCase) &&
                                    !t.Contains("Unlock", StringComparison.OrdinalIgnoreCase)))
                            ?? []).Select(n => new ContextMenuEntry { UINode = n }).FirstOrDefault();

                        if (lockEntry == null)
                        {
                            ctx.KeyPress(VirtualKey.Escape);
                            return NodeStatus.Failure;
                        }

                        ctx.Click(lockEntry.UINode);
                        ctx.Blackboard.Set("mc_prev_target_count", targets.Count);
                        ctx.Blackboard.Set("mc_phase", "await_target");
                        ctx.Blackboard.SetCooldown("mc_lock_timeout", TimeSpan.FromSeconds(30));
                        return NodeStatus.Running;
                    }

                    // ── Phase: waiting for new TargetInBar after Lock click ──────
                    if (phase == "await_target")
                    {
                        var prev = ctx.Blackboard.Get<int>("mc_prev_target_count");
                        if (targets.Count > prev)
                        {
                            // New target appeared — record its index (newest = last in bar)
                            ctx.Blackboard.Set("mc_new_target_idx", prev); // index of newly added target
                            ctx.Blackboard.Set("mc_phase", "click_fire");
                            return NodeStatus.Running;
                        }
                        if (!ctx.Blackboard.IsCooldownReady("mc_lock_timeout")) return NodeStatus.Running; // still waiting
                        ctx.Log("[Mining] Lock timed out — resetting");
                        ctx.Blackboard.Set("mc_phase", "");
                        return NodeStatus.Failure;
                    }

                    // ── Phase: click the new target then fire an idle laser ───────
                    if (phase == "click_fire")
                    {
                        ctx.Blackboard.Set("mc_phase", "");
                        if (!ctx.Blackboard.IsCooldownReady("activate_modules")) return NodeStatus.Running;

                        var newIdx  = ctx.Blackboard.Get<int>("mc_new_target_idx");
                        var target  = targets.Count > newIdx ? targets[newIdx] : targets.LastOrDefault();
                        var laser   = idleLasers.FirstOrDefault();

                        if (target == null || laser == null) return NodeStatus.Failure;

                        ctx.Click(target.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(400));
                        ctx.Click(laser.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(400));
                        ctx.Log($"[Mining] Fired laser on '{target.TextLabel}' (target idx {newIdx})");
                        ctx.Blackboard.SetCooldown("activate_modules", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    }

                    // ── Idle evaluation ──────────────────────────────────────────
                    // Fire idle lasers on existing targets (no new lock needed)
                    if (idleLasers.Count > 0 && targets.Count > 0 &&
                        ctx.Blackboard.IsCooldownReady("activate_modules"))
                    {
                        var laser  = idleLasers[0];
                        var lIdx   = allLasers.IndexOf(laser);
                        var target = targets[Math.Min(lIdx, targets.Count - 1)];

                        ctx.Click(target.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(400));
                        ctx.Click(laser.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(400));
                        ctx.Log($"[Mining] Laser {lIdx} → '{target.TextLabel}' (idle fire)");
                        ctx.Blackboard.SetCooldown("activate_modules", TimeSpan.FromSeconds(2));
                        return NodeStatus.Success;
                    }

                    // Lock another target if below laser count and candidates available
                    if (targets.Count < allLasers.Count &&
                        candidates.Count > 0 &&
                        ctx.Blackboard.IsCooldownReady("lock_target"))
                    {
                        var asteroid = candidates[0];
                        ctx.Log($"[Mining] Locking '{asteroid.Name}' (target {targets.Count + 1}/{allLasers.Count})");
                        ctx.Blackboard.Set("menu_expected", true);
                        ctx.RightClick(asteroid.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(600));
                        ctx.Blackboard.Set("mc_phase", "menu");
                        ctx.Blackboard.SetCooldown("lock_target", TimeSpan.FromSeconds(8));
                        return NodeStatus.Running;
                    }

                    return NodeStatus.Failure;
                }),

                // Priority 3: Approach nearest out-of-range asteroid (when no candidates in range)
                new SequenceNode("Approach out-of-range asteroid",
                    new ConditionNode("No in-range candidates + cooldown?", ctx =>
                    {
                        if (!ctx.Blackboard.IsCooldownReady("approach")) return false;
                        var laserRange = ctx.Blackboard.Has("laser_range_m")
                            ? ctx.Blackboard.Get<double>("laser_range_m")
                            : DefaultLaserRangeM;
                        var lockedNames = ctx.GameState.ParsedUI.Targets
                            .Select(t => t.TextLabel ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                        // Need to approach only if there is NO in-range unlocked asteroid
                        return !AsteroidsInOverview(ctx)
                            .Where(a => !lockedNames.Contains(a.Name ?? ""))
                            .Any(a => a.DistanceInMeters.HasValue && a.DistanceInMeters.Value <=
                                (ctx.Blackboard.Has("laser_range_m")
                                    ? ctx.Blackboard.Get<double>("laser_range_m")
                                    : DefaultLaserRangeM));
                    }),
                    new ActionNode("Approach nearest asteroid", ctx =>
                    {
                        var laserRange = ctx.Blackboard.Has("laser_range_m")
                            ? ctx.Blackboard.Get<double>("laser_range_m")
                            : DefaultLaserRangeM;

                        // Pick nearest asteroid outside range to approach
                        var target = AsteroidsInOverview(ctx)
                            .Where(a => a.DistanceInMeters.HasValue && a.DistanceInMeters.Value > laserRange)
                            .OrderBy(a => a.DistanceInMeters!.Value)
                            .FirstOrDefault()
                            ?? AsteroidsInOverview(ctx).FirstOrDefault(); // fallback: any asteroid

                        if (target == null) return NodeStatus.Failure;

                        var ap = ctx.Blackboard.Get<string>("approach_phase") ?? "";
                        if (ap == "")
                        {
                            ctx.Blackboard.Set("menu_expected", true);
                            ctx.RightClick(target.UINode);
                            ctx.Wait(TimeSpan.FromMilliseconds(600));
                            ctx.Blackboard.Set("approach_phase", "menu");
                            ctx.Blackboard.SetCooldown("approach", TimeSpan.FromSeconds(20));
                            return NodeStatus.Running;
                        }

                        ctx.Blackboard.Set("menu_expected", false);
                        ctx.Blackboard.Set("approach_phase", "");
                        if (!ctx.GameState.HasContextMenu) return NodeStatus.Failure;

                        var approachEntry = ctx.GameState.ParsedUI.ContextMenus
                            .SelectMany(m => m.Entries)
                            .FirstOrDefault(e =>
                                e.Text?.Contains("Approach", StringComparison.OrdinalIgnoreCase) == true);

                        approachEntry ??= (ctx.GameState.ParsedUI.UITree?
                            .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                n.GetAllContainedDisplayTexts()
                                    .Any(t => t.Contains("Approach", StringComparison.OrdinalIgnoreCase)))
                            ?? []).Select(n => new ContextMenuEntry { UINode = n }).FirstOrDefault();

                        if (approachEntry == null) { ctx.KeyPress(VirtualKey.Escape); return NodeStatus.Failure; }

                        ctx.Hover(approachEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(150));
                        ctx.Click(approachEntry.UINode);
                        ctx.Log($"[Mining] Approaching '{target.Name}' (out of {laserRange / 1000:F0} km range)");
                        return NodeStatus.Success;
                    })),

                // Priority 4: Propulsion on when moving to out-of-range asteroid
                new SequenceNode("Activate propulsion",
                    new ConditionNode("Too far + prop idle?", ctx =>
                    {
                        var laserRange = ctx.Blackboard.Has("laser_range_m")
                            ? ctx.Blackboard.Get<double>("laser_range_m") : DefaultLaserRangeM;
                        var nearest = AsteroidsInOverview(ctx)
                            .Where(e => e.DistanceInMeters.HasValue)
                            .OrderBy(e => e.DistanceInMeters!.Value)
                            .FirstOrDefault();
                        if (nearest?.DistanceInMeters == null || nearest.DistanceInMeters <= laserRange) return false;
                        var shipUI = ctx.GameState.ParsedUI.ShipUI;
                        if (shipUI == null) return false;
                        var prop = FindPropulsionModule(shipUI);
                        return prop != null && prop.IsActive != true && !prop.IsBusy && !prop.IsOffline
                               && ctx.Blackboard.IsCooldownReady("activate_ab");
                    }),
                    new ActionNode("Turn on prop", ctx =>
                    {
                        var prop = FindPropulsionModule(ctx.GameState.ParsedUI.ShipUI!);
                        if (prop == null) return NodeStatus.Failure;
                        ctx.Click(prop.UINode);
                        ctx.Log("[Mining] Propulsion on");
                        ctx.Blackboard.SetCooldown("activate_ab", TimeSpan.FromSeconds(8));
                        return NodeStatus.Success;
                    })),

                // Priority 5: Propulsion off when in range
                new SequenceNode("Deactivate propulsion",
                    new ConditionNode("In range + prop active?", ctx =>
                    {
                        var laserRange = ctx.Blackboard.Has("laser_range_m")
                            ? ctx.Blackboard.Get<double>("laser_range_m") : DefaultLaserRangeM;
                        var nearest = AsteroidsInOverview(ctx)
                            .Where(e => e.DistanceInMeters.HasValue)
                            .OrderBy(e => e.DistanceInMeters!.Value)
                            .FirstOrDefault();
                        if (nearest?.DistanceInMeters == null || nearest.DistanceInMeters > laserRange) return false;
                        var shipUI = ctx.GameState.ParsedUI.ShipUI;
                        return shipUI != null && FindPropulsionModule(shipUI)?.IsActive == true;
                    }),
                    new ActionNode("Turn off prop", ctx =>
                    {
                        var prop = FindPropulsionModule(ctx.GameState.ParsedUI.ShipUI!);
                        if (prop == null || prop.IsActive != true) return NodeStatus.Failure;
                        ctx.Click(prop.UINode);
                        ctx.Log("[Mining] Propulsion off — in range");
                        return NodeStatus.Success;
                    })),

                // Priority 6: Idle (lasers cycling, waiting for next lock or range close)
                new ActionNode("Mining in progress", _ => NodeStatus.Success)));

    // ─── 7d-pre. Proactive belt discovery ────────────────────────────────────
    //
    //  Fires once per session when _beltCount == 0 (list not yet known).
    //  Opens the space context menu, hovers "Asteroid Belts ▶", reads the list,
    //  then closes the menu.  Done in ~3 ticks so mining is barely interrupted.

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
                    // Phase 1: open the space context menu
                    case "":
                        ctx.Blackboard.Set("menu_expected", true);
                        RightClickInSpace(ctx);
                        ctx.Wait(TimeSpan.FromMilliseconds(800));
                        ctx.Blackboard.Set("discover_phase", "space_menu");
                        ctx.Blackboard.Set("discover_tick", 0);
                        return NodeStatus.Running;

                    // Phase 2: hover "Asteroid Belts ▶" to expand submenu
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
                            // No belts in this system
                            ctx.Log("[Mining] No Asteroid Belts entry in space menu — system has none");
                            ctx.KeyPress(VirtualKey.Escape);
                            ctx.Blackboard.Set("menu_expected", false);
                            _beltsDiscoveryDone = true;
                            Abort(ctx, 600);
                            return NodeStatus.Failure;
                        }

                        // Store right edge of space menu for X-filtering the belt list
                        ctx.Blackboard.Set("discover_ref_x",
                            beltsEntry.UINode.Region.X + beltsEntry.UINode.Region.Width);

                        HoverAndSlide(ctx, beltsEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(600));
                        ctx.Blackboard.Set("discover_phase", "belt_submenu");
                        return NodeStatus.Running;
                    }

                    // Phase 3: capture belt names then close
                    case "belt_submenu":
                    {
                        int tick = ctx.Blackboard.Get<int>("discover_tick") + 1;
                        ctx.Blackboard.Set("discover_tick", tick);

                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (tick > 8) { Abort(ctx, 20); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        // Try parsed menus first
                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();

                        static bool BeltText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        // Only accept belt entries strictly to the right of the space-menu panel
                        int discoverRefX = ctx.Blackboard.Get<int>("discover_ref_x");

                        var beltEntries = allEntries
                            .Where(e => e.UINode.Region.X > discoverRefX && BeltText(e, t =>
                                (t.Length > 14 && t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)) ||
                                (t.Length > 8  && (t.Contains("Ore Deposit",  StringComparison.OrdinalIgnoreCase) ||
                                                   t.Contains("Cluster",      StringComparison.OrdinalIgnoreCase)))))
                            .OrderBy(e => e.UINode.Region.Y)
                            .ToList();

                        // Positional fallback: rightmost submenu
                        if (beltEntries.Count == 0 && allMenus.Count >= 2)
                        {
                            var minX   = allMenus.Min(m => m.UINode.Region.X);
                            var subMnu = allMenus.Where(m => m.UINode.Region.X > minX + 10)
                                                 .MaxBy(m => m.UINode.Region.X);
                            if (subMnu?.Entries.Count > 0)
                                beltEntries = subMnu.Entries.OrderBy(e => e.UINode.Region.Y).ToList();
                        }

                        // Tree-scan fallback — only nodes to the right of space-menu panel
                        if (beltEntries.Count == 0)
                        {
                            var treeNodes = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > discoverRefX &&
                                    n.Region.Height > 3 &&       // height=0 means hidden/collapsed panel
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
                                // Submenu never appeared — re-hover and retry a bit
                                Abort(ctx, 15);
                                return NodeStatus.Failure;
                            }
                            else
                            {
                                return NodeStatus.Running; // wait another tick
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

                        // Return Failure so the SelectorNode continues to MineAtBelt this tick
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

    private IBehaviorNode WarpToBelt() =>
        new SequenceNode("Warp to belt",
            new ConditionNode("No asteroids + cooldown + not warping?", ctx =>
                !AnyAsteroidsInOverview(ctx) &&
                !ctx.GameState.IsWarping &&
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
                    ctx.Blackboard.Set("cascade_ref_x", 0); // clear so next attempt starts fresh
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
                    // Phase 1: stop lasers, recall drones, pick next belt index
                    // ─────────────────────────────────────────────────────────
                    case "":
                    {
                        StopAllModules(ctx);
                        RecallDrones(ctx);

                        // Mark the belt we just left as depleted (no asteroids found there)
                        int lastBelt = ctx.Blackboard.Get<int>("last_belt_target");
                        if (lastBelt >= 0 && _beltCount > 0)
                        {
                            int depletedNorm = lastBelt % _beltCount;
                            _beltDepleted[depletedNorm] = true;
                            ctx.Log($"[Mining] Belt {depletedNorm} marked depleted");
                        }

                        // Advance to the next belt, skipping depleted or user-excluded ones
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
                                // All belts depleted/excluded — reset depletion and start over
                                ctx.Log("[Mining] All belts depleted or excluded — resetting belt depletion and retrying");
                                _beltDepleted.Clear();
                                curIdx = ctx.Blackboard.Get<int>("belt_index");
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

                    // ─────────────────────────────────────────────────────────
                    // Phase 1b: wait for drones to return before warping
                    // ─────────────────────────────────────────────────────────
                    case "await_drones":
                    {
                        bool dronesBack = (ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) == 0;
                        bool timedOut   = ctx.Blackboard.IsCooldownReady("belt_drone_recall");
                        if (!dronesBack && !timedOut) return NodeStatus.Running;
                        if (!dronesBack) ctx.Log("[Mining] Belt-hop drone recall timed out — warping anyway");

                        // Mark menu as expected so HandleStrayContextMenu does not dismiss
                        // the space menu or any of the cascade submenus.
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
                            if (TimedOut()) { Reset(8); return NodeStatus.Failure; }
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
                            return NodeStatus.Failure;
                        }

                        // Record the right edge of this menu panel.  Every subsequent cascade level
                        // must appear strictly to the right of this X — used to filter out stale
                        // tree nodes that linger from previous cascade interactions at the same coords.
                        ctx.Blackboard.Set("cascade_ref_x",
                            beltsEntry.UINode.Region.X + beltsEntry.UINode.Region.Width);

                        // L-shaped hover: center → slide right, keeping Y row so bezier
                        // curve stays on the same entry and enters the next panel cleanly
                        HoverAndSlide(ctx, beltsEntry.UINode);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_belt_list");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 3: belt list visible — hover a belt entry
                    //
                    //  After hovering "Asteroid Belts" the belt submenu appears.
                    //  Strategy:
                    //  1. Search ALL entries across ALL menus by UINode full texts
                    //     (more reliable than ContextMenuEntry.Text which may still
                    //     return a glyph for some entries).
                    //  2. Positional fallback: if ≥2 menus, hover first entry of the
                    //     rightmost menu (the submenu, not the space menu).
                    //  3. Re-hover "Asteroid Belts" on every tick where the submenu
                    //     is not yet detected — keeps the submenu open while waiting
                    //     for the UI tree to capture it.
                    // ─────────────────────────────────────────────────────────
                    case "await_belt_list":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();
                        ctx.Log($"[WarpToBelt] belt_list t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(6).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(20, (e.Text ?? "").Length))}'"))}]");

                        // Helper: scan all UINode texts (not just parsed Text field)
                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        // Only accept entries to the right of the space-menu panel.
                        // This rejects stale ghost nodes from previous cascade sessions
                        // that happen to sit at the same screen coordinates.
                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");
                        int beltTarget  = ctx.Blackboard.Get<int>("belt_target");

                        var allBeltEntries = allEntries
                            .Where(e => e.UINode.Region.X > cascadeRefX && EntryHasText(e, t =>
                                (t.Length > 14 && t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)) ||
                                (t.Length > 8  && (t.Contains("Ore Deposit",  StringComparison.OrdinalIgnoreCase) ||
                                                   t.Contains("Cluster",      StringComparison.OrdinalIgnoreCase)))))
                            .OrderBy(e => e.UINode.Region.Y)
                            .ToList();

                        // Capture belt names from the belt submenu entries (primary source)
                        if (allBeltEntries.Count > 0)
                        {
                            _beltCount = allBeltEntries.Count;
                            for (int i = 0; i < allBeltEntries.Count; i++)
                            {
                                // Take the longest meaningful text from the entry node
                                var txt = allBeltEntries[i].UINode.GetAllContainedDisplayTexts()
                                    .Where(t => t.Length > 3)
                                    .OrderByDescending(t => t.Length)
                                    .FirstOrDefault()
                                    ?? allBeltEntries[i].Text ?? $"Belt {i + 1}";
                                _beltNames[i] = txt.Trim();
                            }
                        }
                        // Positional fallback: if filter missed entries but we can see a submenu, capture from it
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

                        // Positional fallback: rightmost separate menu's entries sorted by Y
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
                                if (beltEntry != null)
                                    ctx.Log($"[WarpToBelt] positional belt fallback: index {beltTarget % subEntries.Count} of {subEntries.Count}");
                            }
                        }

                        // Full UI-tree scan fallback: the belt submenu container may not use
                        // Python type "ContextMenu" — search all MenuEntryView nodes in the tree.
                        UITreeNodeWithDisplayRegion? beltNodeFromTree = null;
                        if (beltEntry == null)
                        {
                            var treeNodes = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&  // must be right of space-menu panel
                                    n.Region.Height > 3 &&       // height=0 means hidden/collapsed panel
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Length > 14 &&
                                        t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .OrderBy(n => n.Region.Y)
                                .ToList();
                            if (treeNodes.Count > 0)
                            {
                                // Also save names so the WebUI belt list populates
                                _beltCount = treeNodes.Count;
                                for (int i = 0; i < treeNodes.Count; i++)
                                {
                                    var tn = treeNodes[i].GetAllContainedDisplayTexts()
                                        .Where(t => t.Length > 3).OrderByDescending(t => t.Length)
                                        .FirstOrDefault() ?? $"Belt {i + 1}";
                                    _beltNames[i] = tn.Trim();
                                }
                                beltNodeFromTree = treeNodes[beltTarget % treeNodes.Count];
                                var txt = beltNodeFromTree.GetAllContainedDisplayTexts()
                                    .FirstOrDefault(t => t.Contains("Asteroid Belt")) ?? "?";
                                ctx.Log($"[WarpToBelt] Tree-scan belt index {beltTarget % treeNodes.Count}/{treeNodes.Count}: '{txt}'");
                            }
                        }

                        if (beltEntry == null && beltNodeFromTree == null)
                        {
                            // Submenu not yet detected — re-hover "Asteroid Belts" to keep it open.
                            var beltsHeader = allEntries.FirstOrDefault(e =>
                                EntryHasText(e, t => t.Length < 20 &&
                                    t.Contains("Asteroid Belt", StringComparison.OrdinalIgnoreCase)));
                            if (beltsHeader != null)
                            {
                                ctx.Log("[WarpToBelt] Re-hovering 'Asteroid Belts' — submenu not yet visible");
                                HoverAndSlide(ctx, beltsHeader.UINode);
                                ctx.Wait(TimeSpan.FromMilliseconds(500));
                            }
                            if (TimedOut(16)) { ctx.KeyPress(VirtualKey.Escape); Reset(20); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var chosenBeltNode = beltEntry?.UINode ?? beltNodeFromTree;
                        if (beltEntry != null)
                        {
                            ctx.Log($"[WarpToBelt] Hovering belt (menu entry): '{beltEntry.Text}'");
                            HoverAndSlide(ctx, beltEntry.UINode);
                        }
                        else if (beltNodeFromTree != null)
                        {
                            ctx.Log($"[WarpToBelt] Hovering belt (tree node)");
                            HoverAndSlide(ctx, beltNodeFromTree);
                        }
                        // Advance the reference X — next level (belt actions) must appear to the right of this panel
                        if (chosenBeltNode != null)
                            ctx.Blackboard.Set("cascade_ref_x",
                                chosenBeltNode.Region.X + chosenBeltNode.Region.Width);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_actions_menu");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 4: hover "Warp to Within (0 m) ▶"
                    //  Search ALL entries across ALL menus by UINode full texts.
                    //  Belt names never contain "Warp", space menu never contains "Warp".
                    // ─────────────────────────────────────────────────────────
                    case "await_actions_menu":
                    {
                        if (!ctx.GameState.HasContextMenu)
                        {
                            if (TimedOut(12)) { ctx.KeyPress(VirtualKey.Escape); Reset(8); return NodeStatus.Failure; }
                            return NodeStatus.Running;
                        }

                        var allMenus   = ctx.GameState.ParsedUI.ContextMenus;
                        var allEntries = allMenus.SelectMany(m => m.Entries).ToList();
                        ctx.Log($"[WarpToBelt] actions t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(6).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(20, (e.Text ?? "").Length))}'"))}]");

                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        // Only accept entries to the right of the belt-list panel
                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");

                        // "Warp to Within (0 m)" — prefer match with both Warp+Within
                        var warpEntry = allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Warp",   StringComparison.OrdinalIgnoreCase)
                                              && t.Contains("Within",  StringComparison.OrdinalIgnoreCase)));
                        warpEntry ??= allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Warp", StringComparison.OrdinalIgnoreCase)));

                        // Full UI-tree scan fallback for warp action
                        UITreeNodeWithDisplayRegion? warpNodeFromTree = null;
                        if (warpEntry == null)
                        {
                            warpNodeFromTree = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&
                                    n.Region.Height > 3 &&       // height=0 means hidden/collapsed panel
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Contains("Warp", StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .MinBy(n => n.Region.Y);
                            if (warpNodeFromTree != null)
                            {
                                var txt = warpNodeFromTree.GetAllContainedDisplayTexts()
                                    .FirstOrDefault(t => t.Contains("Warp")) ?? "?";
                                ctx.Log($"[WarpToBelt] Tree-scan warp: '{txt}'");
                            }
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

                        // Detect whether this entry already includes a specific distance (e.g. "Warp to Within (0 m)").
                        // In current EVE the belt sub-menu lists each distance as a direct click action — no further submenu.
                        // We CLICK it directly. If it were actually a sub-menu opener the click still activates it.
                        bool isDirectWarpAction = warpText.Contains("0 m",  StringComparison.OrdinalIgnoreCase)
                                               || warpText.Contains("0m",   StringComparison.OrdinalIgnoreCase)
                                               || System.Text.RegularExpressions.Regex.IsMatch(warpText, @"\d+\s*(m|km)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        ctx.Log($"[WarpToBelt] Warp entry: '{warpText}' direct={isDirectWarpAction} " +
                                $"client({chosenWarpNode?.Region.X + chosenWarpNode?.Region.Width / 2}," +
                                $"{chosenWarpNode?.Region.Y + chosenWarpNode?.Region.Height / 2})");

                        ctx.Hover(chosenWarpNode!);
                        ctx.Wait(TimeSpan.FromMilliseconds(200));
                        ctx.Click(chosenWarpNode!);

                        if (isDirectWarpAction)
                        {
                            // The click IS the warp command — no further menu needed.
                            ctx.Log("[WarpToBelt] Direct warp click fired — resetting cascade");
                            Reset(30);
                            return NodeStatus.Success;
                        }

                        // Submenu opener: advance cascadeRefX and wait for the distance picker
                        ctx.Blackboard.Set("cascade_ref_x",
                            chosenWarpNode!.Region.X + chosenWarpNode.Region.Width);
                        ctx.Wait(TimeSpan.FromMilliseconds(500));
                        Progress("await_warp_distances");
                        return NodeStatus.Running;
                    }

                    // ─────────────────────────────────────────────────────────
                    // Phase 5: CLICK "Within 0 m"
                    //  Search ALL entries by UINode full texts — "Within 0" is unique.
                    // ─────────────────────────────────────────────────────────
                    case "await_warp_distances":
                    {
                        // If previous click was a direct warp action it may have already started the warp
                        if (ctx.GameState.IsWarping)
                        {
                            ctx.Log("[WarpToBelt] Warp started — cascade complete");
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
                        ctx.Log($"[WarpToBelt] distances t={ticks+1} menus={allMenus.Count} entries={allEntries.Count} " +
                                $"[{string.Join("|", allEntries.Take(6).Select(e => $"'{(e.Text ?? "").Substring(0, Math.Min(20, (e.Text ?? "").Length))}'"))}]");

                        static bool EntryHasText(ContextMenuEntry e, Func<string, bool> pred)
                            => e.UINode.GetAllContainedDisplayTexts().Any(pred);

                        // Only accept distance entries to the right of the belt-actions panel
                        int cascadeRefX = ctx.Blackboard.Get<int>("cascade_ref_x");

                        var within0 = allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t => t.Contains("Within 0", StringComparison.OrdinalIgnoreCase)));
                        // Fallback: any entry with just "0 m" or "0m" (the 0-distance option)
                        within0 ??= allEntries.FirstOrDefault(e =>
                            e.UINode.Region.X > cascadeRefX &&
                            EntryHasText(e, t =>
                                (t.Equals("0 m", StringComparison.OrdinalIgnoreCase) ||
                                 t.Equals("0m",  StringComparison.OrdinalIgnoreCase))));

                        // Full UI-tree scan fallback for distance entry
                        UITreeNodeWithDisplayRegion? within0Node = null;
                        if (within0 == null)
                        {
                            within0Node = (ctx.GameState.ParsedUI.UITree?
                                .FindAll(n => n.Node.PythonObjectTypeName == "MenuEntryView" &&
                                    n.Region.X > cascadeRefX &&
                                    n.Region.Height > 3 &&       // height=0 means hidden/collapsed panel
                                    n.GetAllContainedDisplayTexts().Any(t =>
                                        t.Contains("Within 0", StringComparison.OrdinalIgnoreCase) ||
                                        t.Equals("0 m", StringComparison.OrdinalIgnoreCase) ||
                                        t.Equals("0m",  StringComparison.OrdinalIgnoreCase)))
                                ?? [])
                                .MinBy(n => n.Region.Y);
                            if (within0Node != null)
                                ctx.Log($"[WarpToBelt] Tree-scan distance found");
                        }

                        if (within0 != null || within0Node != null)
                        {
                            var clickNode = within0?.UINode ?? within0Node!;
                            var distText  = clickNode.GetAllContainedDisplayTexts().FirstOrDefault() ?? "?";
                            ctx.Log($"[WarpToBelt] Clicking distance '{distText}' " +
                                    $"client({clickNode.Region.X + clickNode.Region.Width / 2}," +
                                    $"{clickNode.Region.Y + clickNode.Region.Height / 2}) " +
                                    $"src={(within0 != null ? "menu" : "tree")}");
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

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L-shaped hover for cascading menus: move to center of the entry, pause briefly,
    /// then slide horizontally to the right edge at the same Y row. This prevents the
    /// bezier curve from drifting over adjacent entries while entering the next panel.
    /// </summary>
    private static void HoverAndSlide(BotContext ctx, UITreeNodeWithDisplayRegion node)
    {
        var r    = node.Region;
        int midY = r.Y + r.Height / 2;
        ctx.Actions.Enqueue(new MoveMouseAction(r.X + r.Width / 2, midY)); // center
        ctx.Wait(TimeSpan.FromMilliseconds(350));
        ctx.Actions.Enqueue(new MoveMouseAction(r.X + r.Width - 2, midY)); // right edge
    }

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

    /// <summary>
    /// If an inventory window is open but NOT showing the Mining Hold,
    /// click the Mining Hold nav entry to switch to it.
    /// Returns Failure always (probe node) — actual hold reading happens next tick.
    /// </summary>
    private IBehaviorNode NavigateToMiningHold() =>
        new ActionNode("Navigate to mining hold", ctx =>
        {
            if (!ctx.Blackboard.IsCooldownReady("nav_mining_hold"))
                return NodeStatus.Failure;

            var inv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
            if (inv == null) return NodeStatus.Failure;

            // Already showing the mining hold — nothing to do
            if (inv.HoldType == InventoryHoldType.Mining) return NodeStatus.Failure;

            // Find the Mining Hold nav entry and click it
            var miningNav = inv.NavEntries.FirstOrDefault(e =>
                e.HoldType == InventoryHoldType.Mining ||
                (e.UINode.GetAllContainedDisplayTexts()
                    .Any(t => t.Contains("Mining Hold", StringComparison.OrdinalIgnoreCase) ||
                              t.Contains("Ore Hold",    StringComparison.OrdinalIgnoreCase))));

            if (miningNav == null) return NodeStatus.Failure;

            ctx.Click(miningNav.UINode);
            ctx.Log("[Inv] Switched inventory to Mining Hold");
            ctx.Blackboard.SetCooldown("nav_mining_hold", TimeSpan.FromSeconds(10));
            return NodeStatus.Failure; // always fail → selector continues to mining
        });

    private static InventoryWindow? FindOreHoldWindow(BotContext ctx) =>
        ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w =>
            w.HoldType == InventoryHoldType.Mining) ??
        // Fallback: text match in case title extraction returns Unknown
        ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w =>
            w.SubCaptionLabelText?.Contains("ore",   StringComparison.OrdinalIgnoreCase) == true ||
            w.SubCaptionLabelText?.Contains("mining", StringComparison.OrdinalIgnoreCase) == true);

    private bool IsOreHoldFull(BotContext ctx)
    {
        var w = FindOreHoldWindow(ctx);
        return w?.CapacityGauge?.FillPercent >= OreHoldFullPercent;
    }

    private static bool AnyAsteroidsInOverview(BotContext ctx)
        => AsteroidsInOverview(ctx).Any();

    /// <summary>
    /// Returns all asteroid entries from the overview. Tries parsed overview first;
    /// if that yields nothing, falls back to a raw UITree scan for OverviewScrollEntry
    /// nodes. Also logs diagnostics when nothing is found so failures are visible.
    /// </summary>
    private static IEnumerable<OverviewEntry> AsteroidsInOverview(BotContext ctx)
    {
        var ov    = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        var count = ov?.Entries.Count ?? 0;
        var parsed = (ov?.Entries.Where(IsAsteroid) ?? []).ToList();

        if (parsed.Count > 0) return parsed;

        // ── Diagnostic log (throttled via blackboard to avoid spam) ──────────
        var lastLog = ctx.Blackboard.Get<long>("ov_diag_tick");
        var thisTick = ctx.Blackboard.Get<long>("tick_counter") + 1;
        ctx.Blackboard.Set("tick_counter", thisTick);
        if (thisTick - lastLog >= 5)   // log every ~10 s
        {
            ctx.Blackboard.Set("ov_diag_tick", thisTick);
            var ovWindows = ctx.GameState.ParsedUI.OverviewWindows.Count;
            if (ov == null)
                ctx.Log($"[Mining] No overview window found (windows={ovWindows}). Check Python type name.");
            else
            {
                var sample = string.Join(" | ", ov.Entries.Take(4)
                    .Select(e => $"N={e.Name ?? "?"} T={e.ObjectType ?? "?"} " +
                                 $"texts=[{string.Join(",", e.Texts.Take(3).Select(t => $"\"{t}\""))}]"));
                ctx.Log($"[Mining] Overview: windows={ovWindows} entries={count} asteroids=0. " +
                        $"Sample: {(count > 0 ? sample : "none")}");
            }
        }

        // ── UITree fallback ───────────────────────────────────────────────────
        var treeFallback = GetAsteroidsFromTreeScan(ctx);
        if (treeFallback.Count > 0)
            ctx.Log($"[Mining] UITree fallback found {treeFallback.Count} asteroid scroll entries");
        return treeFallback;
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

    /// <summary>
    /// Returns modules identified as mining lasers.
    /// Search order: named modules in Top → named modules in any row → all Top modules.
    /// This is robust to ClassifyModuleRows misclassifying rows due to HUD layout variations.
    /// </summary>
    private static IEnumerable<ShipUIModuleButton> GetMiningModules(ShipUI shipUI)
    {
        static bool IsMiningName(ShipUIModuleButton m) =>
            m.Name != null && (
                m.Name.Contains("Mining",    StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Strip",     StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Laser",     StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Harvester", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Modulated", StringComparison.OrdinalIgnoreCase));

        var top = shipUI.ModuleButtonsRows.Top.Where(m => !m.IsOffline).ToList();

        // 1. Named mining modules in top row
        var namedTop = top.Where(IsMiningName).ToList();
        if (namedTop.Count > 0) return namedTop;

        // 2. Named mining modules anywhere (fallback when row classification is off)
        var allButtons = shipUI.ModuleButtons.Where(m => !m.IsOffline).ToList();
        var namedAny = allButtons.Where(IsMiningName).ToList();
        if (namedAny.Count > 0) return namedAny;

        // 3. Last resort: all top-row modules (names not yet populated from tooltip)
        return top;
    }

    private static int GetMiningModuleCount(BotContext ctx)
    {
        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null) return 1;
        return Math.Max(1, GetMiningModules(shipUI).Count());
    }

    /// <summary>
    /// Returns the first mid-row module identified as a propulsion module (AB or MWD).
    /// Falls back to first mid-row module if none are named.
    /// </summary>
    private static ShipUIModuleButton? FindPropulsionModule(ShipUI shipUI)
    {
        var mid = shipUI.ModuleButtonsRows.Middle.Where(m => !m.IsOffline).ToList();
        // Name-based identification
        var prop = mid.FirstOrDefault(m => m.Name != null && (
            m.Name.Contains("Afterburner",    StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Microwarpdrive", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("MWD",            StringComparison.OrdinalIgnoreCase)));
        return prop ?? mid.FirstOrDefault(); // fallback: first mid-row module
    }

    private static void StopAllModules(BotContext ctx)
    {
        var shipUI = ctx.GameState.ParsedUI.ShipUI;
        if (shipUI == null) return;
        // Stop only identified mining modules — don't touch shields, prop, or other mid/low slots
        foreach (var mod in GetMiningModules(shipUI).Where(m => m.IsActive == true))
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
        // "Asteroid" ObjectType is definitive — but "Asteroid Belt" is a celestial, not a rock.
        if (!string.IsNullOrEmpty(e.ObjectType) &&
            e.ObjectType.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) &&
            !e.ObjectType.Contains("Belt",    StringComparison.OrdinalIgnoreCase))
            return true;

        // Scan ALL text from the UINode directly, but exclude any text that identifies
        // the entry as an "Asteroid Belt" celestial (belt names contain "asteroid belt").
        var texts = e.UINode.GetAllContainedDisplayTexts()
            .Select(t => t.ToLowerInvariant())
            .Where(t => !t.Contains("asteroid belt"));   // belt names are NOT minable asteroids
        return texts.Any(t => _asteroidKeywords.Any(k => t.Contains(k)));
    }

    /// <summary>
    /// Ore value ranking (higher = more valuable). Used for target prioritisation.
    /// Range always beats value: in-range asteroids are preferred over out-of-range ones,
    /// then within each group the highest value wins.
    /// </summary>
    private static readonly Dictionary<string, int> _oreValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "scordite",   1 },
            { "plagioclase",2 },
            { "veldspar",   3 },
            { "pyroxeres",  4 },
            { "bistot",     5 },
            { "omber",      6 },
            { "jaspet",     7 },
            { "hedbergite", 8 },
            { "crokite",    9 },
            { "hemorphite", 10 },
            { "kernite",    11 },
            { "arkonor",    12 },
            { "gneiss",     13 },
            { "dark ochre", 14 },
            { "ochre",      14 },
            { "mercoxit",   15 },
            { "spodumain",  16 },
        };

    private static int OreValueOf(OverviewEntry e)
    {
        var texts = e.UINode.GetAllContainedDisplayTexts()
            .Select(t => t.ToLowerInvariant()).ToList();
        foreach (var (ore, val) in _oreValue)
            if (texts.Any(t => t.Contains(ore)))
                return val;
        return 0;
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

    [GeneratedRegex(@"([\d.,]+)\s*(m|km|au)", RegexOptions.IgnoreCase)]
    private static partial Regex DistanceRegex();

    /// <summary>
    /// Picks the best asteroid to target using range-first, value-second priority:
    ///  • If any asteroid is within laser range → pick the most valuable one in-range.
    ///  • Otherwise → pick the most valuable asteroid of all (will need approach first).
    /// Excludes names already locked (pass lockedNames to avoid re-locking).
    /// </summary>
    private static OverviewEntry? SelectBestAsteroid(
        BotContext ctx,
        HashSet<string>? lockedNames = null)
    {
        var candidates = AsteroidsInOverview(ctx)
            .Where(e => lockedNames == null || !lockedNames.Contains(e.Name ?? ""))
            .ToList();
        if (candidates.Count == 0) return null;

        var inRange = candidates
            .Where(e => e.DistanceInMeters.HasValue && e.DistanceInMeters <= DefaultLaserRangeM)
            .ToList();

        if (inRange.Count > 0)
            return inRange.MaxBy(OreValueOf);   // most valuable that is already in range

        // Nothing in range — pick most valuable overall (approach will close the gap)
        return candidates.MaxBy(OreValueOf);
    }

    /// <summary>
    /// Parses a distance string like "12.4 km", "850 m", "1.2 AU" into metres.
    /// </summary>
    private static double? ParseDistanceM(string text)
    {
        var m = DistanceRegex().Match(text);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", "."),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val)) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "km" => val * 1_000,
            "au" => val * 149_597_870_700.0,
            _    => val,   // metres
        };
    }

    /// <summary>
    /// Scans the full UITree for OverviewScrollEntry nodes that look like asteroids
    /// and creates synthetic OverviewEntry objects for them. Used as a fallback when
    /// the parsed overview window misses entries (e.g. Python type-name mismatch).
    /// </summary>
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
                             .Where(t => !t.Contains("asteroid belt"))  // belt celestials are not minable
                             .ToList();
            if (!lower.Any(t => _asteroidKeywords.Any(k => t.Contains(k))))
                continue;

            // Extract distance from any text that looks like a distance
            double? dist = texts.Select(t => ParseDistanceM(t))
                .FirstOrDefault(d => d.HasValue);

            // Best name candidate: longest non-distance text
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
}
