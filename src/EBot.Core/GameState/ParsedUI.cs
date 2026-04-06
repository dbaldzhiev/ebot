namespace EBot.Core.GameState;

/// <summary>
/// The parsed EVE Online user interface — typed, easy-to-use representations
/// of all major UI elements extracted from the raw UI tree.
/// </summary>
public sealed class ParsedUI
{
    /// <summary>Root of the raw UI tree with computed display regions.</summary>
    public UITreeNodeWithDisplayRegion? UITree { get; init; }

    /// <summary>Right-click context menus currently visible.</summary>
    public IReadOnlyList<ContextMenu> ContextMenus { get; init; } = [];

    /// <summary>Ship UI showing modules, capacitor, hit points, speed.</summary>
    public ShipUI? ShipUI { get; init; }

    /// <summary>Currently locked targets.</summary>
    public IReadOnlyList<Target> Targets { get; init; } = [];

    /// <summary>The info panel on the left side (system info, route, etc.).</summary>
    public InfoPanelContainer? InfoPanelContainer { get; init; }

    /// <summary>Overview windows showing objects in space.</summary>
    public IReadOnlyList<OverviewWindow> OverviewWindows { get; init; } = [];

    /// <summary>The selected item window (shows when object selected in space).</summary>
    public SelectedItemWindow? SelectedItemWindow { get; init; }

    /// <summary>Drones window.</summary>
    public DronesWindow? DronesWindow { get; init; }

    /// <summary>Open inventory windows (cargo, ore hold, etc.).</summary>
    public IReadOnlyList<InventoryWindow> InventoryWindows { get; init; } = [];

    /// <summary>Chat window stacks (local, corp, fleet, etc.).</summary>
    public IReadOnlyList<ChatWindowStack> ChatWindowStacks { get; init; } = [];

    /// <summary>Module button tooltip (visible when hovering a module).</summary>
    public ModuleButtonTooltip? ModuleButtonTooltip { get; init; }

    /// <summary>Neocom sidebar.</summary>
    public Neocom? Neocom { get; init; }

    /// <summary>Modal message boxes.</summary>
    public IReadOnlyList<MessageBox> MessageBoxes { get; init; } = [];

    /// <summary>Station/structure window (when docked).</summary>
    public StationWindow? StationWindow { get; init; }

    /// <summary>Probe scanner window.</summary>
    public ProbeScannerWindow? ProbeScannerWindow { get; init; }

    /// <summary>Fleet window.</summary>
    public FleetWindow? FleetWindow { get; init; }
}

// ─── Ship UI ───────────────────────────────────────────────────────────────

public sealed class ShipUI
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public ShipUICapacitor? Capacitor { get; init; }
    public HitpointsPercent? HitpointsPercent { get; init; }
    public ShipUIIndication? Indication { get; init; }
    public IReadOnlyList<ShipUIModuleButton> ModuleButtons { get; init; } = [];
    public ShipUIModuleButtonRows ModuleButtonsRows { get; init; } = new();
    public UITreeNodeWithDisplayRegion? StopButton { get; init; }
    public UITreeNodeWithDisplayRegion? MaxSpeedButton { get; init; }
    /// <summary>Current speed text as displayed in the UI (e.g. "324 m/s").</summary>
    public string? SpeedText { get; init; }
}

public sealed class ShipUICapacitor
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public int? LevelPercent { get; init; }
}

public sealed class HitpointsPercent
{
    public int Shield { get; init; }
    public int Armor { get; init; }
    public int Structure { get; init; }
}

public sealed class ShipUIIndication
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? ManeuverType { get; init; }
}

public sealed class ShipUIModuleButton
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    /// <summary>Slot node (parent of UINode); used for sprite lookups.</summary>
    public UITreeNodeWithDisplayRegion SlotNode { get; init; } = null!;
    /// <summary>True when the module is actively cycling (ramp_active == true or ramp sprite visible).</summary>
    public bool? IsActive { get; init; }
    /// <summary>True when the selection highlight sprite is visible on the slot.</summary>
    public bool IsHiliteVisible { get; init; }
    /// <summary>True when the module is in a busy/transitioning state.</summary>
    public bool IsBusy { get; init; }
    /// <summary>True when the module is overloaded (overheating).</summary>
    public bool IsOverloaded { get; init; }
    /// <summary>True when the module is offline.</summary>
    public bool IsOffline { get; init; }
    /// <summary>Ramp cycle progress 0–1000 ms equivalent (derived from ramp sprite rotation).</summary>
    public int? RampRotationMilli { get; init; }
}

public sealed class ShipUIModuleButtonRows
{
    public IReadOnlyList<ShipUIModuleButton> Top { get; init; } = [];
    public IReadOnlyList<ShipUIModuleButton> Middle { get; init; } = [];
    public IReadOnlyList<ShipUIModuleButton> Bottom { get; init; } = [];
}

// ─── Targets ───────────────────────────────────────────────────────────────

public sealed class Target
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? TextLabel { get; init; }
    public HitpointsPercent? HitpointsPercent { get; init; }
    public bool IsActiveTarget { get; init; }
    public double? DistanceInMeters { get; init; }
}

// ─── Overview ──────────────────────────────────────────────────────────────

public sealed class OverviewWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    /// <summary>Column header labels extracted from the header row, in left-to-right order.</summary>
    public IReadOnlyList<string> ColumnHeaders { get; init; } = [];
    public IReadOnlyList<OverviewEntry> Entries { get; init; } = [];
    /// <summary>Overview filter tabs (tab group at the top of the overview window).</summary>
    public IReadOnlyList<OverviewTab> Tabs { get; init; } = [];
}

public sealed class OverviewTab
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? Name { get; init; }
    public bool IsActive { get; init; }
}

public sealed class OverviewEntry
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    /// <summary>Column header → cell text mapping built from X-position alignment.</summary>
    public IReadOnlyDictionary<string, string> CellsTexts { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Object name (from the "Name" column or leftmost text).</summary>
    public string? Name { get; init; }
    /// <summary>Object type/category (from the "Type" column).</summary>
    public string? ObjectType { get; init; }
    public string? DistanceText { get; init; }
    public double? DistanceInMeters { get; init; }
    public bool IsAttackingMe { get; init; }
    /// <summary>All display texts found in the entry, for fallback access.</summary>
    public IReadOnlyList<string> Texts { get; init; } = [];
}

// ─── Inventory ─────────────────────────────────────────────────────────────

public sealed class InventoryWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? SubCaptionLabelText { get; init; }
    public InventoryCapacityGauge? CapacityGauge { get; init; }
    public IReadOnlyList<InventoryItem> Items { get; init; } = [];
    public UITreeNodeWithDisplayRegion? ButtonToStackAll { get; init; }
}

public sealed class InventoryCapacityGauge
{
    public double Used { get; init; }
    public double Maximum { get; init; }
    public double FillPercent => Maximum > 0 ? (Used / Maximum) * 100 : 0;
}

public sealed class InventoryItem
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? Name { get; init; }
    public int? Quantity { get; init; }
}

// ─── Drones ────────────────────────────────────────────────────────────────

public sealed class DronesWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public DronesGroup? DronesInBay { get; init; }
    public DronesGroup? DronesInSpace { get; init; }
}

public sealed class DronesGroup
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? HeaderText { get; init; }
    public int? QuantityCurrent { get; init; }
    public int? QuantityMaximum { get; init; }
    public IReadOnlyList<DroneEntry> Drones { get; init; } = [];
}

public sealed class DroneEntry
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? Name { get; init; }
    public HitpointsPercent? HitpointsPercent { get; init; }
}

// ─── Context Menu ──────────────────────────────────────────────────────────

public sealed class ContextMenu
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public IReadOnlyList<ContextMenuEntry> Entries { get; init; } = [];
}

public sealed class ContextMenuEntry
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? Text { get; init; }
    public bool IsHighlighted { get; init; }
}

// ─── Info Panel ────────────────────────────────────────────────────────────

public sealed class InfoPanelContainer
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public InfoPanelRoute? InfoPanelRoute { get; init; }
    public InfoPanelLocationInfo? InfoPanelLocationInfo { get; init; }
}

public sealed class InfoPanelRoute
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public IReadOnlyList<UITreeNodeWithDisplayRegion> RouteElementMarkers { get; init; } = [];
    public int NextSystemsCount => RouteElementMarkers.Count;
}

public sealed class InfoPanelLocationInfo
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? SecurityStatusText { get; init; }
    public string? SystemName { get; init; }
}

// ─── Selected Item ─────────────────────────────────────────────────────────

public sealed class SelectedItemWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public IReadOnlyList<UITreeNodeWithDisplayRegion> ActionButtons { get; init; } = [];
}

// ─── Chat ──────────────────────────────────────────────────────────────────

public sealed class ChatWindowStack
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public IReadOnlyList<ChatWindow> ChatWindows { get; init; } = [];
}

public sealed class ChatWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? Name { get; init; }
    public int? UserCount { get; init; }
}

// ─── Module Button Tooltip ─────────────────────────────────────────────────

public sealed class ModuleButtonTooltip
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public string? ShortcutText { get; init; }
    public string? OptimalRangeText { get; init; }
    public int? OptimalRangeMeters { get; init; }
}

// ─── Station ───────────────────────────────────────────────────────────────

public sealed class StationWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public UITreeNodeWithDisplayRegion? UndockButton { get; init; }
}

// ─── Misc ──────────────────────────────────────────────────────────────────

public sealed class Neocom
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
}

public sealed class MessageBox
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
    public IReadOnlyList<UITreeNodeWithDisplayRegion> Buttons { get; init; } = [];
    public IReadOnlyList<string> Texts { get; init; } = [];
}

public sealed class ProbeScannerWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
}

public sealed class FleetWindow
{
    public UITreeNodeWithDisplayRegion UINode { get; init; } = null!;
}
