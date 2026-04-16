using System.Collections.Concurrent;
using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

/// <summary>
/// Production mining bot — state-aware, self-recovering.
///
/// Logic is split across multiple partial files:
///  - MiningBot.Constants.cs: Strings, keywords, and numeric constants.
///  - MiningBot.Unload.cs: Docked unloading and station management.
///  - MiningBot.Navigation.cs: Belt discovery, warping, and station return.
///  - MiningBot.Space.cs: Asteroid mining, drone defense, and sensing.
///  - MiningBot.Utils.cs: Shared UI and calculation helpers.
/// </summary>
public sealed partial class MiningBot : IBot
{
    // ─── Configurable settings ──────────────────────────────────────────────

    public string? HomeStationOverride { get; init; }
    public int OreHoldFullPercent { get; init; } = 95;
    public int ShieldEscapePercent { get; init; } = 25;

    // ─── Session statistics ──────────────────────────────────────────────────

    private double         _totalUnloadedM3;
    private int            _unloadCycles;
    private DateTimeOffset _sessionStart;

    // ─── Belt registry ───────────────────────────────────────────────────────

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
        _beltsDiscoveryDone = false;
        ctx.Blackboard.Set("last_belt_target", -1);
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
            new ActionNode("World State Synthesis", ctx => {
                SynthesizeWorldState(ctx);
                return NodeStatus.Failure; // Always continue to real nodes
            }),
            HandleMessageBoxes(),
            HandleStrayContextMenu(),
            HandleShieldEmergency(),
            HandleDocked(),
            HandleWarping(),
            EnsureMiningTab(),
            HandleInSpace());

    // ─── Core infrastructure behaviors ──────────────────────────────────────

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
                ctx.Blackboard.Set("return_phase", "find_station");
                return NodeStatus.Running;
            }));

    private static IBehaviorNode HandleWarping() =>
        new SequenceNode("Wait while warping",
            new ConditionNode("Is warping?", ctx => ctx.GameState.IsWarping),
            new ActionNode("Wait", _ => NodeStatus.Success));

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
}
