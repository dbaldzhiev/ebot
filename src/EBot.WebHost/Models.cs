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
    int RouteJumpsRemaining,
    bool HasContextMenu,
    IReadOnlyList<string> ContextMenuEntries,
    // All detected inventory holds (currently-visible + orchestrator hold cache)
    IReadOnlyList<HoldInfoDto> Holds,
    // Nav entries (clickable holds in the left panel)
    IReadOnlyList<HoldNavEntryDto> HoldNavEntries);

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
    int TickMs = 0);

public sealed record BotInfo(string Name, string Description);

public sealed record EveProcessDto(int ProcessId, string Name, string WindowTitle);

public sealed record SurvivalRequest(bool Enabled);

public sealed record OllamaModelRequest(string Model);

public sealed record DpiScaleRequest(float Scale);

public sealed record QuickTravelRequest(string Station);

/// <summary>
/// Maps a user-facing alias (e.g. "Jita 4-4") to:
///   System   — the solar system name for the autopilot route (e.g. "Jita")
///   Bookmark — the exact in-game bookmark name to use for the final warp+dock
///              (e.g. "Jita IV - Moon 4 - Caldari Navy Assembly Plant")
/// When Bookmark is null the bot docks via the overview instead.
/// </summary>
public sealed record StationAlias(string Alias, string System, string? Bookmark);

public sealed record StationAliasRequest(string Alias, string System, string? Bookmark);

public sealed record SwitchHoldRequest(string HoldType);

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
        IReadOnlyDictionary<string, HoldInfoDto>? holdCache = null)
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
            gs.RouteJumpsRemaining,
            gs.HasContextMenu,
            contextMenuEntries,
            holds,
            navEntries);
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1_000_000_000) return $"{meters / 1_000_000_000:F2} AU";
        if (meters >= 1_000) return $"{meters / 1_000:F1} km";
        return $"{meters:F0} m";
    }
}
