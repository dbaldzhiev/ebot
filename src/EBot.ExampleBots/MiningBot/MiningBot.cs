using System.Collections.Concurrent;
using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

/// <summary>
/// Production mining bot — state-aware, self-recovering.
/// </summary>
public sealed partial class MiningBot : IBot
{
    // ─── Configurable settings ──────────────────────────────────────────────
    public string? HomeStationOverride { get; init; }
    public int OreHoldFullPercent { get; set; } = 95;
    public int ShieldEscapePercent { get; set; } = 25;

    // ─── Session statistics ──────────────────────────────────────────────────
    private double         _totalUnloadedM3;
    private int            _unloadCycles;
    private DateTimeOffset _sessionStart;

    // ─── Belt registry ───────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<int, string> _beltNames    = new();
    private readonly ConcurrentDictionary<int, bool>   _beltDepleted = new();
    private readonly ConcurrentDictionary<int, bool>   _beltExcluded = new();
    private int _beltCount = 0;

    public IReadOnlyDictionary<int, string> BeltNames    => _beltNames;
    public IReadOnlyDictionary<int, bool>   BeltDepleted => _beltDepleted;
    public IReadOnlyDictionary<int, bool>   BeltExcluded => _beltExcluded;
    public int BeltCount => _beltCount;

    public double TotalUnloadedM3 => _totalUnloadedM3;
    public int    UnloadCycles    => _unloadCycles;
    public double SessionRateM3Hr =>
        _sessionStart != default && (DateTimeOffset.UtcNow - _sessionStart).TotalHours > 0.01
        ? _totalUnloadedM3 / (DateTimeOffset.UtcNow - _sessionStart).TotalHours : 0;

    public void ToggleBeltExcluded(int idx) =>
        _beltExcluded.AddOrUpdate(idx, true, (_, v) => !v);

    // ─── IBot Implementation ────────────────────────────────────────────────
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
        ctx.Blackboard.Set("last_belt_target", -1);

        ctx.Blackboard.Set("return_phase",            "");
        ctx.Blackboard.Set("return_tick",             0);
        ctx.Blackboard.Set("home_menu_type",          "");
        ctx.Blackboard.Set("return_tried_stations",   false);
        ctx.Blackboard.Set("return_tried_structures", false);
        ctx.Blackboard.Set("return_current_menu",     "");
        ctx.Blackboard.Set("unload_phase",   "");
        ctx.Blackboard.Set("unload_ticks",   0);
        // Always attempt unload before the first undock. The state machine exits
        // immediately if the ore hold turns out to be empty, so this is safe.
        ctx.Blackboard.Set("needs_unload",   true);
        ctx.Blackboard.Set("belt_phase",     "");
        ctx.Blackboard.Set("belt_phase_ticks", 0);
        ctx.Blackboard.Set("discover_phase", "");
        ctx.Blackboard.Set("discover_tick",  0);
        ctx.Blackboard.Set("menu_expected",  false);
        ctx.Blackboard.Set("belt_prop_started", false);
        ctx.Blackboard.Set(SurveyLastTickKey, -1L); // force initial scan

        if (!string.IsNullOrWhiteSpace(HomeStationOverride))
        {
            ctx.Blackboard.Set("home_station",     HomeStationOverride);
            ctx.Blackboard.Set("home_station_set", true);
        }
        else if (ctx.GameState.IsDocked)
        {
            var name = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelLocationInfo?.NearestLocationName;
            if (string.IsNullOrEmpty(name))
            {
                name = ctx.GameState.ParsedUI.StationWindow?.UINode
                    .GetAllContainedDisplayTexts()
                    .Where(t => t.Length is > 5 and < 60 && !t.All(char.IsDigit) &&
                                !t.Equals("Undock", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Length)
                    .FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(name))
            {
                ctx.Blackboard.Set("home_station",     name);
                ctx.Blackboard.Set("home_station_set", true);
                ctx.Log($"[Mining] Bot started while docked. Set home station to: '{name}'");
            }
        }
        SyncStats(ctx);
    }

    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new StatelessSelectorNode("Mining Root",
            new ActionNode("Trace Start", ctx => {
                if (ctx.Blackboard.IsCooldownReady("bt_trace")) {
                    ctx.Log($"[Mining] BT Tick #{ctx.TickCount} | Docked={ctx.GameState.IsDocked} InSpace={ctx.GameState.IsInSpace} Warping={ctx.GameState.IsWarping}");
                    ctx.Blackboard.SetCooldown("bt_trace", TimeSpan.FromSeconds(5));
                }
                return NodeStatus.Failure;
            }),
            new ActionNode("World State Synthesis", ctx => {
                SynthesizeWorldState(ctx);
                return NodeStatus.Failure;
            }),
            HandleMessageBoxes(),
            HandleStrayContextMenu(),
            HandleShieldEmergency(),
            HandleDocked(),
            HandleWarping(),
            HandleInSpace());

    // ─── Core infrastructure behaviors ──────────────────────────────────────

    private IBehaviorNode HandleInSpace() =>
        new StatelessSelectorNode("Space actions",
            new ConditionNode("Wait: not in space", ctx => !ctx.GameState.IsInSpace),
            new ActionNode("Space state cleanup", ctx => {
                // Recovery: needs_unload=true but hold is provably empty — abort the return machine.
                if (ctx.Blackboard.Get<bool>("needs_unload") && !ctx.GameState.IsDocked)
                {
                    var oreHold = FindOreHoldWindow(ctx);
                    if (oreHold != null && oreHold.Items.Count == 0 && (oreHold.CapacityGauge?.FillPercent ?? 100) < 5)
                    {
                        ctx.Log("[Navigation] Ore hold empty but needs_unload=true — clearing stale flag to resume mining.");
                        ctx.Blackboard.Set("needs_unload", false);
                        ctx.Blackboard.Set("return_phase", "");
                        ctx.Blackboard.Set("return_tick", 0);
                        if (ctx.GameState.HasContextMenu) ctx.KeyPress(VirtualKey.Escape);
                    }
                }

                // Cleanup: not unloading and hold not full — ensure return machine is fully reset.
                if (!ctx.Blackboard.Get<bool>("needs_unload") && !IsOreHoldFull(ctx))
                {
                    if (ctx.Blackboard.Get<string>("return_phase") != "")
                    {
                        ctx.Log("[Navigation] Hold is empty — clearing stale return states to enable warp.");
                        ctx.Blackboard.Set("return_phase", "");
                        ctx.Blackboard.Set("return_tick", 0);
                    }

                    if (!AnyAsteroidsInOverview(ctx) && ctx.Blackboard.Get<string>("mining_phase") != "")
                    {
                        ctx.Blackboard.Remove("mining_phase");
                        ctx.Blackboard.Set("mining_tick", 0);
                        ctx.Blackboard.Remove("assumed_locked");
                    }
                }
                return NodeStatus.Failure;
            }),
            WaitCapRegen(),
            ReturnToStation(),
            BT_DroneSecurity(),
            EnsureMiningTab(),
            DiscoverBeltsOnce(),
            WarpToBelt(),
            NavigateToMiningHold(),
            EnsureSurveyScanned(),
            BT_MineAtBelt(),
            // THE HEARTBEAT
            new ActionNode("Waiting for cooldowns", _ => NodeStatus.Running));

    private static IBehaviorNode WaitCapRegen() =>
        new SequenceNode("Capacitor regen",
            new ConditionNode("Capacitor low?", ctx =>
                (ctx.GameState.ParsedUI.ShipUI?.Capacitor?.LevelPercent ?? 100) < MinCapPct),
            new ActionNode("Wait for regen", _ => NodeStatus.Running));

    private static IBehaviorNode EnsureMiningTab() =>
        new SequenceNode("Ensure Mining tab",
            new ConditionNode("Need Mining tab?", ctx =>
            {
                if (ctx.GameState.IsDocked || !ctx.GameState.IsInSpace) return false;
                
                // If under attack, don't force mining tab
                var hostiles = ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries).Any(e => e.IsHostile || e.IsAttackingMe);
                if (hostiles) return false;

                var ov = ctx.GameState.ParsedUI.OverviewWindows
                    .FirstOrDefault(w => FindMiningTab(w.Tabs) != null)
                    ?? ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault(w => w.Entries.Any(IsAsteroid))
                    ?? ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
                if (ov == null) return false;

                var miningTab = FindMiningTab(ov.Tabs);
                if (miningTab == null || miningTab.IsActive) return false;

                if (!ctx.Blackboard.IsCooldownReady("click_mining_tab")) return false;

                ctx.Log($"[Mining] Mining tab '{miningTab.Name}' is NOT active.");
                return true;
            }),
            new ActionNode("Click Mining tab", ctx =>
            {
                var tabs = (ctx.GameState.ParsedUI.OverviewWindows
                    .FirstOrDefault(w => FindMiningTab(w.Tabs) != null)
                    ?? ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault())?.Tabs ?? [];
                var miningTab = FindMiningTab(tabs);
                if (miningTab == null) return NodeStatus.Failure;
                
                ctx.Log($"[Mining] Clicking mining overview tab: '{miningTab.Name}'");
                ctx.Click(miningTab.UINode);
                ctx.Blackboard.SetCooldown("click_mining_tab", TimeSpan.FromSeconds(5));
                return NodeStatus.Success;
            }));

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
                ctx.Blackboard.Set("return_phase", "await_drones");
                ctx.Blackboard.SetCooldown("return_drone_timeout", TimeSpan.FromSeconds(15));
                return NodeStatus.Running;
            }));

    private static IBehaviorNode HandleWarping() =>
        new SequenceNode("Wait while warping",
            new ConditionNode("Is warping?", ctx => ctx.GameState.IsWarping),
            new ActionNode("Wait", _ => NodeStatus.Success));

    private static OverviewTab? FindMiningTab(IReadOnlyList<OverviewTab> tabs) =>
        tabs.FirstOrDefault(t =>
            t.Name?.Contains("Mining",   StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Mine",     StringComparison.OrdinalIgnoreCase) == true ||
            t.Name?.Contains("Ore",      StringComparison.OrdinalIgnoreCase) == true);
}
