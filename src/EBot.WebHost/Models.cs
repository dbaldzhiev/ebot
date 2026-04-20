using EBot.Core.GameState;

namespace EBot.WebHost;

// ─── DTOs for Web / MCP layer ──────────────────────────────────────────────

public sealed record LogEntry(
    DateTimeOffset Time,
    string Level,
    string Category,
    string Message);

public sealed record GameStateSummary(
    bool IsInSpace,
    bool IsDocked,
    bool IsWarping,
    int? CapacitorPercent,
    int? ShieldPercent,
    int? ArmorPercent,
    int? StructurePercent,
    string? SpeedText,
    int TargetCount,
    long TickCount,
    string Runtime,
    IReadOnlyList<TargetDto> Targets,
    IReadOnlyList<OverviewEntryDto> Overview,
    IReadOnlyList<ModuleButtonDto> ModuleButtons,
    // Location
    string? SystemName,
    string? SecurityStatus,
    string? CurrentStation,
    int RouteJumpsRemaining,
    bool HasContextMenu,
    IReadOnlyList<string> ContextMenuEntries,
    // Mining specific
    IReadOnlyList<AsteroidDto> TopAsteroids,
    IReadOnlyList<BeltDto> DiscoveredBelts,
    // All detected inventory holds (currently-visible + orchestrator hold cache)
    IReadOnlyList<HoldInfoDto> Holds,
    // Nav entries (clickable holds in the left panel)
    IReadOnlyList<HoldNavEntryDto> HoldNavEntries,
    // Decision / Engine
    string? ThoughtProcess,
    double EngineRpm,
    // Behavior Tree Visualization
    IReadOnlyList<string> ActiveNodes,
    // Expanded UI
    DroneGroupDto? DronesInBay,
    DroneGroupDto? DronesInSpace,
    SelectedItemDto? SelectedItem,
    // Current Bot Settings
    int? MiningOreHoldPct,
    int? MiningShieldPct);

public sealed record AsteroidDto(
    string Name,
    string DistanceText,
    double Score,
    bool IsLocked,
    bool IsBeingMined);

public sealed record BeltDto(
    int Index,
    string Name,
    bool Depleted,
    bool Excluded);

public sealed record TargetDto(
    string? Name,
    bool IsActive,
    int? ShieldPercent,
    int? ArmorPercent,
    int? StructurePercent,
    string? DistanceText);

public sealed record OverviewEntryDto(
    string? Name,
    string? ObjectType,
    string? Distance,
    bool IsAttackingMe,
    IReadOnlyDictionary<string, string> CellsTexts);

public sealed record ModuleButtonDto(
    int SlotIndex,
    string? Name,
    bool? IsActive,
    bool IsHiliteVisible,
    bool IsBusy,
    bool IsOverloaded,
    bool IsOffline,
    int? RampRotationMilli);

public sealed record CargoItemDto(string? Name, int? Quantity);

public sealed record DroneDto(
    string? Name,
    int? Shield,
    int? Armor,
    int? Structure);

public sealed record DroneGroupDto(
    string? Header,
    int? Current,
    int? Max,
    IReadOnlyList<DroneDto> Drones);

public sealed record SelectedItemDto(
    string? Name,
    IReadOnlyList<string> Actions);

/// <summary>Inventory hold data (capacity + items) for one hold type.</summary>
public sealed record HoldInfoDto(
    string Name,
    string HoldType,
    double? UsedM3,
    double? MaxM3,
    IReadOnlyList<CargoItemDto> Items);

/// <summary>A clickable hold navigation entry in the left panel of the EVE inventory window.</summary>
public sealed record HoldNavEntryDto(
    string Label,
    string HoldType,
    bool IsSelected);

public sealed record BotStatusResponse(
    string State,
    string? BotName,
    string? BotDescription,
    GameStateSummary? GameState,
    int Port,
    bool SurvivalEnabled);

public sealed record StartRequest(
    string BotName,
    int Pid = 0,
    string? ExePath = null,
    int TickMs = 0,
    string? Destination = null,
    MiningBotConfig? Mining = null);

/// <summary>Settings for the Mining Bot, passed from the UI at start time.</summary>
public sealed record MiningBotConfig(
    string? DockingBookmark = null,
    int     OreHoldFull     = 95,
    int     ShieldEscape    = 25);

public sealed record BotInfo(string Name, string Description);

public sealed record EveProcessDto(int ProcessId, string Name, string WindowTitle);

public sealed record SurvivalRequest(bool Enabled);

public sealed record OllamaModelRequest(string Model);

public sealed record DpiScaleRequest(float Scale);

public sealed record UpdateMiningSettingsRequest(int OreHoldFull, int ShieldEscape);

public sealed record SwitchHoldRequest(string HoldType);

// ─── Debug & Simulation DTOs ───────────────────────────────────────────────

public sealed record BotStateDto(
    long TickCount,
    string Runtime,
    IReadOnlyList<string> ActiveNodes,
    IReadOnlyDictionary<string, object> Blackboard,
    IReadOnlyList<string> QueuedActions);

public sealed record RecordedTickDto(
    long TickCount,
    DateTimeOffset Timestamp,
    string FrameJson,
    IReadOnlyDictionary<string, object> BlackboardBefore,
    IReadOnlyList<string> Actions);

/// <summary>A saved autopilot destination (loaded from or stored via ESI).</summary>
public sealed record TravelDestination(
    string Id,
    string Name,
    string? SystemName,
    int? TypeId,
    string? IconUrl);

// ─── Builder helpers ────────────────────────────────────────────────────────

public static class DtoMapper
{
    /// <summary>
    /// Converts a BotContext to a GameStateSummary DTO.
    /// <paramref name="holdCache"/> is the orchestrator's multi-tick hold data cache;
    /// currently-visible holds override it for freshness.
    /// </summary>
    public static GameStateSummary ToDto(
        EBot.Core.DecisionEngine.BotContext ctx,
        IReadOnlyDictionary<string, HoldInfoDto>? holdCache = null,
        double engineRpm = 0,
        EBot.ExampleBots.MiningBot.MiningBot? miningBot = null)
    {
        var gs = ctx.GameState;
        var ui = gs.ParsedUI;

        var targets = ui.Targets.Select(t => new TargetDto(
            t.TextLabel,
            t.IsActiveTarget,
            t.HitpointsPercent?.Shield,
            t.HitpointsPercent?.Armor,
            t.HitpointsPercent?.Structure,
            t.DistanceInMeters.HasValue ? FormatDistance(t.DistanceInMeters.Value) : null
        )).ToList();

        var overview = ui.OverviewWindows.FirstOrDefault()?.Entries
            .Select(e => new OverviewEntryDto(
                e.Name, e.ObjectType, e.DistanceText, e.IsAttackingMe,
                (IReadOnlyDictionary<string, string>)e.CellsTexts))
            .ToList() ?? [];

        var modules = (ui.ShipUI?.ModuleButtons ?? [])
            .Select((m, i) => new ModuleButtonDto(i, m.Name, m.IsActive, m.IsHiliteVisible, m.IsBusy, m.IsOverloaded, m.IsOffline, m.RampRotationMilli))
            .ToList();

        var loc = ui.InfoPanelContainer?.InfoPanelLocationInfo;
        var contextMenuEntries = ui.ContextMenus.FirstOrDefault()?.Entries
            .Select(e => e.Text ?? "")
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList() ?? [];

        // ── Mining Specific ─────────────────────────────────────────────────
        var world = ctx.Blackboard.Get<EBot.ExampleBots.MiningBot.WorldState>("world");
        var topAsteroids = (world?.Asteroids ?? [])
            .OrderByDescending(a => a.Score)
            .Take(5)
            .Select(a => new AsteroidDto(a.Name, a.DistanceText, Math.Round(a.Score, 1), a.IsLocked, a.IsBeingMined))
            .ToList();

        var belts = new List<BeltDto>();
        if (miningBot != null)
        {
            for (int i = 0; i < miningBot.BeltCount; i++)
            {
                belts.Add(new BeltDto(
                    i,
                    miningBot.BeltNames.TryGetValue(i, out var n) ? n : $"Belt {i + 1}",
                    miningBot.BeltDepleted.TryGetValue(i, out var d) && d,
                    miningBot.BeltExcluded.TryGetValue(i, out var e) && e
                ));
            }
        }

        // ── Inventory holds ──────────────────────────────────────────────────
        // Start with cached holds from previous ticks, then overlay the currently-visible window.
        var holdsDict = holdCache?.ToDictionary(kv => kv.Key, kv => kv.Value)
                        ?? new Dictionary<string, HoldInfoDto>();

        foreach (var w in ui.InventoryWindows)
        {
            if (w.CapacityGauge == null) continue;  // no gauge = skip
            var key  = w.HoldType != InventoryHoldType.Unknown
                       ? w.HoldType.ToString()
                       : (w.SubCaptionLabelText ?? "Unknown");
            var name = w.SubCaptionLabelText ?? (w.HoldType != InventoryHoldType.Unknown
                       ? w.HoldType.ToString() : "Hold");
            holdsDict[key] = new HoldInfoDto(
                name, key,
                w.CapacityGauge.Used, w.CapacityGauge.Maximum,
                (w.Items ?? []).Select(i => new CargoItemDto(i.Name, i.Quantity)).ToList());
        }

        // Sort: Cargo first, then by hold type name
        var holdOrder = new Dictionary<string, int>
        {
            ["Cargo"]           = 0,
            ["Mining"]          = 1,
            ["Infrastructure"]  = 2,
            ["ShipMaintenance"] = 3,
            ["Fleet"]           = 4,
            ["Fuel"]            = 5,
            ["Item"]            = 6,
            ["Unknown"]         = 99,
        };
        var holds = holdsDict.Values
            .OrderBy(h => holdOrder.GetValueOrDefault(h.HoldType, 50))
            .ToList();

        // Nav entries from the currently-open inventory window
        var navEntries = ui.InventoryWindows.FirstOrDefault()?.NavEntries
            .Select(e => new HoldNavEntryDto(e.Label ?? e.HoldType.ToString(), e.HoldType.ToString(), e.IsSelected))
            .ToList() ?? [];

        // ── Expanded UI ──────────────────────────────────────────────────────
        
        static DroneGroupDto? MapDroneGroup(DronesGroup? g) => g == null ? null : new DroneGroupDto(
            g.HeaderText, g.QuantityCurrent, g.QuantityMaximum,
            g.Drones.Select(d => new DroneDto(d.Name, d.HitpointsPercent?.Shield, d.HitpointsPercent?.Armor, d.HitpointsPercent?.Structure)).ToList());

        var selectedItem = ui.SelectedItemWindow == null ? null : new SelectedItemDto(
            ui.SelectedItemWindow.UINode.GetAllContainedDisplayTexts().FirstOrDefault(),
            ui.SelectedItemWindow.ActionButtons.Select(b => b.Node.GetDictString("_hint") ?? b.Node.PythonObjectTypeName).ToList());

        var stationName = ui.StationWindow?.UINode.GetAllContainedDisplayTexts().FirstOrDefault(t => t.Length > 3);

        return new GameStateSummary(
            gs.IsInSpace,
            gs.IsDocked,
            gs.IsWarping,
            gs.CapacitorPercent,
            ui.ShipUI?.HitpointsPercent?.Shield,
            ui.ShipUI?.HitpointsPercent?.Armor,
            ui.ShipUI?.HitpointsPercent?.Structure,
            ui.ShipUI?.SpeedText,
            gs.TargetCount,
            ctx.TickCount,
            ctx.RunDuration.ToString(@"hh\:mm\:ss"),
            targets,
            overview,
            modules,
            loc?.SystemName,
            loc?.SecurityStatusText,
            stationName,
            gs.RouteJumpsRemaining,
            gs.HasContextMenu,
            contextMenuEntries,
            topAsteroids,
            belts,
            holds,
            navEntries,
            string.Join(" > ", ctx.ActivePathSnapshot.Reverse()),
            engineRpm,
            ctx.ActivePathSnapshot.ToList(),
            MapDroneGroup(ui.DronesWindow?.DronesInBay),
            MapDroneGroup(ui.DronesWindow?.DronesInSpace),
            selectedItem,
            miningBot?.OreHoldFullPercent,
            miningBot?.ShieldEscapePercent);
    }

    public static BotStateDto ToBotStateDto(EBot.Core.DecisionEngine.BotContext ctx)
    {
        return new BotStateDto(
            ctx.TickCount,
            ctx.RunDuration.ToString(@"hh\:mm\:ss"),
            ctx.ActivePathSnapshot.ToList(),
            ctx.Blackboard.GetData(),
            ctx.Actions.GetDescriptions());
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1_000_000_000) return $"{meters / 1_000_000_000:F2} AU";
        if (meters >= 1_000) return $"{meters / 1_000:F1} km";
        return $"{meters:F0} m";
    }
}

