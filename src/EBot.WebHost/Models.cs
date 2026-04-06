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
    // Cargo
    double? CargoUsedM3,
    double? CargoMaxM3,
    IReadOnlyList<CargoItemDto> CargoItems);

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

        // Cargo: first inventory window that looks like ship cargo
        var cargoWin = ui.InventoryWindows.FirstOrDefault();
        double? cargoUsed = cargoWin?.CapacityGauge?.Used;
        double? cargoMax  = cargoWin?.CapacityGauge?.Maximum;
        var cargoItems = (cargoWin?.Items ?? [])
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
            cargoItems);
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1_000_000_000) return $"{meters / 1_000_000_000:F2} AU";
        if (meters >= 1_000) return $"{meters / 1_000:F1} km";
        return $"{meters:F0} m";
    }
}
