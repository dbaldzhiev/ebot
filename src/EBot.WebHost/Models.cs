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
    // Cargo Hold (regular ship cargo)
    double? CargoUsedM3,
    double? CargoMaxM3,
    IReadOnlyList<CargoItemDto> CargoItems,
    // Ore / Mining Hold
    double? OreHoldUsedM3,
    double? OreHoldMaxM3,
    IReadOnlyList<CargoItemDto> OreHoldItems);

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
    bool? IsActive,
    bool IsHiliteVisible,
    bool IsBusy,
    bool IsOverloaded,
    bool IsOffline,
    int? RampRotationMilli);

public sealed record CargoItemDto(string? Name, int? Quantity);

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

// ─── Builder helpers ────────────────────────────────────────────────────────

public static class DtoMapper
{
    public static GameStateSummary ToDto(EBot.Core.DecisionEngine.BotContext ctx)
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
            .Select((m, i) => new ModuleButtonDto(i, m.IsActive, m.IsHiliteVisible, m.IsBusy, m.IsOverloaded, m.IsOffline, m.RampRotationMilli))
            .ToList();

        var loc = ui.InfoPanelContainer?.InfoPanelLocationInfo;
        var contextMenuEntries = ui.ContextMenus.FirstOrDefault()?.Entries
            .Select(e => e.Text ?? "")
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList() ?? [];

        // Inventory windows — distinguish cargo hold from ore/mining hold by title
        static bool IsOreHold(InventoryWindow w) =>
            w.SubCaptionLabelText?.Contains("Ore",    StringComparison.OrdinalIgnoreCase) == true ||
            w.SubCaptionLabelText?.Contains("Mining", StringComparison.OrdinalIgnoreCase) == true;

        static bool IsCargoHold(InventoryWindow w) =>
            w.SubCaptionLabelText?.Contains("Cargo",  StringComparison.OrdinalIgnoreCase) == true ||
            // If no title detected, treat as generic cargo (fallback)
            string.IsNullOrEmpty(w.SubCaptionLabelText);

        var oreWin   = ui.InventoryWindows.FirstOrDefault(IsOreHold);
        var cargoWin = ui.InventoryWindows.FirstOrDefault(w => !IsOreHold(w) && IsCargoHold(w))
                    ?? ui.InventoryWindows.FirstOrDefault(w => !IsOreHold(w));

        double? cargoUsed = cargoWin?.CapacityGauge?.Used;
        double? cargoMax  = cargoWin?.CapacityGauge?.Maximum;
        var cargoItems = (cargoWin?.Items ?? [])
            .Select(i => new CargoItemDto(i.Name, i.Quantity))
            .ToList();

        double? oreUsed = oreWin?.CapacityGauge?.Used;
        double? oreMax  = oreWin?.CapacityGauge?.Maximum;
        var oreItems = (oreWin?.Items ?? [])
            .Select(i => new CargoItemDto(i.Name, i.Quantity))
            .ToList();

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
            cargoUsed,
            cargoMax,
            cargoItems,
            oreUsed,
            oreMax,
            oreItems);
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1_000_000_000) return $"{meters / 1_000_000_000:F2} AU";
        if (meters >= 1_000) return $"{meters / 1_000:F1} km";
        return $"{meters:F0} m";
    }
}
