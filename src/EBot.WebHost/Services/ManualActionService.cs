using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;

namespace EBot.WebHost.Services;

/// <summary>
/// Executes one-off manual game interactions (dock, undock, inventory operations)
/// driven by the web UI or REST API, independent of the bot decision loop.
/// </summary>
public sealed class ManualActionService
{
    private readonly LogSink _logSink;

    // Injected by BotOrchestrator after runner creation
    private InputSimulator? _input;
    private ActionExecutor? _executor;
    private Func<BotContext?>? _getContext;

    public ManualActionService(LogSink logSink)
    {
        _logSink = logSink;
    }

    public void Configure(InputSimulator input, ActionExecutor executor, Func<BotContext?> getContext)
    {
        _input    = input;
        _executor = executor;
        _getContext = getContext;
    }

    // ─── Inventory ──────────────────────────────────────────────────────────

    /// <summary>Opens the ship inventory (Alt+C).</summary>
    public async Task OpenCargoAsync()
    {
        await EnsureReadyAsync();
        var handle = GetEveHandle();
        var q = new ActionQueue();
        q.Enqueue(new KeyPressAction(VirtualKey.C, [VirtualKey.Alt]));
        await _executor!.ExecuteAllAsync(q, handle);
        _logSink.Add("Info", "Action", "Opened inventory (Alt+C)");
    }

    /// <summary>Clicks a hold entry in the inventory left panel.</summary>
    public async Task SwitchToHoldAsync(string holdType)
    {
        await EnsureReadyAsync();
        var handle = GetEveHandle();
        var ctx = _getContext!();

        var invWin = ctx?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        if (invWin == null)
        {
            await OpenCargoAsync();
            await Task.Delay(900);
            invWin = _getContext!()?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
            if (invWin == null)
            {
                _logSink.Add("Warn", "Action", "SwitchToHold: inventory not open");
                return;
            }
        }

        var entry = invWin.NavEntries.FirstOrDefault(e =>
            e.HoldType.ToString().Equals(holdType, StringComparison.OrdinalIgnoreCase) ||
            (e.Label?.Contains(holdType, StringComparison.OrdinalIgnoreCase) == true));

        if (entry == null)
        {
            _logSink.Add("Warn", "Action", $"SwitchToHold: hold '{holdType}' not found in nav panel");
            return;
        }

        var (cx, cy) = entry.UINode.Center;
        var q = new ActionQueue();
        q.Enqueue(new ClickAction(cx, cy));
        await _executor!.ExecuteAllAsync(q, handle);
        _logSink.Add("Info", "Action", $"Switched to hold: {entry.Label}");
    }

    /// <summary>Cycles through every nav entry so each hold's data is captured into the cache.</summary>
    public async Task ScanAllHoldsAsync()
    {
        await EnsureReadyAsync();
        var handle = GetEveHandle();
        var ctx = _getContext!();

        var invWin = ctx?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        if (invWin == null)
        {
            await OpenCargoAsync();
            await Task.Delay(1000);
            invWin = _getContext!()?.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        }
        if (invWin == null)
        {
            _logSink.Add("Warn", "Action", "ScanAllHolds: inventory not open after Alt+C");
            return;
        }
        if (invWin.NavEntries.Count == 0)
        {
            _logSink.Add("Info", "Action", "ScanAllHolds: no navigation entries found");
            return;
        }

        foreach (var entry in invWin.NavEntries)
        {
            var (cx, cy) = entry.UINode.Center;
            var q = new ActionQueue();
            q.Enqueue(new ClickAction(cx, cy));
            await _executor!.ExecuteAllAsync(q, handle);
            await Task.Delay(700);
            _logSink.Add("Info", "Action", $"Scanned hold: {entry.Label}");
        }
        _logSink.Add("Info", "Action", $"ScanAllHolds complete — {invWin.NavEntries.Count} holds scanned");
    }

    // ─── Navigation ─────────────────────────────────────────────────────────

    /// <summary>Clears the autopilot destination via right-click → Remove Waypoint.</summary>
    public async Task ClearDestinationAsync()
    {
        await EnsureReadyAsync();
        var handle = GetEveHandle();
        var ctx = _getContext!();

        var markers = ctx?.GameState.ParsedUI.InfoPanelContainer?
            .InfoPanelRoute?.RouteElementMarkers ?? [];
        if (markers.Count == 0) return;

        var (cx, cy) = markers[^1].Center;
        var q = new ActionQueue();
        q.Enqueue(new RightClickAction(cx, cy));
        await _executor!.ExecuteAllAsync(q, handle);

        ContextMenuEntry? removeEntry = null;
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(200);
            removeEntry = _getContext!()?.GameState.ParsedUI.ContextMenus
                .SelectMany(m => m.Entries)
                .FirstOrDefault(e =>
                    e.Text?.Contains("Remove",      StringComparison.OrdinalIgnoreCase) == true ||
                    e.Text?.Contains("Clear",       StringComparison.OrdinalIgnoreCase) == true ||
                    e.Text?.Contains("Destination", StringComparison.OrdinalIgnoreCase) == true);
            if (removeEntry != null) break;
        }

        if (removeEntry != null)
        {
            var (ex, ey) = removeEntry.UINode.Center;
            var q2 = new ActionQueue();
            q2.Enqueue(new ClickAction(ex, ey));
            await _executor!.ExecuteAllAsync(q2, handle);
            _logSink.Add("Info", "Action", "Destination cleared");
        }
        else
        {
            var q2 = new ActionQueue();
            q2.Enqueue(new KeyPressAction(VirtualKey.Escape, []));
            await _executor!.ExecuteAllAsync(q2, handle);
            _logSink.Add("Warn", "Action", "Clear destination: no Remove entry found in menu");
        }
    }

    // ─── Dock / Undock ──────────────────────────────────────────────────────

    /// <summary>Clicks the Undock button in the station window.</summary>
    public async Task UndockAsync()
    {
        var ui  = _getContext!()?.GameState.ParsedUI;
        var btn = ui?.StationWindow?.UndockButton;
        if (btn == null)
            throw new InvalidOperationException("Not docked or undock button not found in UI.");

        await EnsureReadyAsync();
        var handle = GetEveHandle();
        var cx = btn.Region.X + btn.Region.Width / 2;
        var cy = btn.Region.Y + btn.Region.Height / 2;
        var queue = new ActionQueue();
        queue.Enqueue(new ClickAction(cx, cy));
        await _executor!.ExecuteAllAsync(queue, handle);
        _logSink.Add("Info", "Action", $"Undock clicked ({cx},{cy})");
    }

    /// <summary>Docks to the nearest dockable object in the overview.</summary>
    public async Task DockAsync()
    {
        await EnsureReadyAsync();
        var handle = GetEveHandle();

        var target = FindDockableEntry();
        if (target == null)
        {
            var tabs = _getContext!()?.GameState.ParsedUI
                .OverviewWindows.FirstOrDefault()?.Tabs ?? [];
            foreach (var tab in tabs.Where(t => !t.IsActive))
            {
                _logSink.Add("Info", "Action", $"No station in current tab — switching to '{tab.Name}'");
                var (tx, ty) = tab.UINode.Center;
                var tq = new ActionQueue();
                tq.Enqueue(new ClickAction(tx, ty));
                await _executor!.ExecuteAllAsync(tq, handle);
                await Task.Delay(700);
                target = FindDockableEntry();
                if (target != null) break;
            }
        }

        if (target == null)
            throw new InvalidOperationException(
                "No dockable station/structure found in any overview tab.");

        var (cx, cy) = target.UINode.Center;
        _logSink.Add("Info", "Action", $"Dock: RightClick '{target.Name}' ({cx},{cy})");

        var q = new ActionQueue();
        q.Enqueue(new RightClickAction(cx, cy));
        await _executor!.ExecuteAllAsync(q, handle);

        ContextMenuEntry? dockEntry = null;
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(220);
            dockEntry = _getContext!()?.GameState.ParsedUI.ContextMenus
                .SelectMany(m => m.Entries)
                .FirstOrDefault(e => e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);
            if (dockEntry != null) break;
        }

        if (dockEntry != null)
        {
            var (ex, ey) = dockEntry.UINode.Center;
            var q2 = new ActionQueue();
            q2.Enqueue(new ClickAction(ex, ey));
            await _executor!.ExecuteAllAsync(q2, handle);
            _logSink.Add("Info", "Action", $"Dock menu entry clicked ({ex},{ey})");
        }
        else
        {
            _logSink.Add("Warn", "Action",
                "Dock context menu did not appear — right-click may have missed. Try again.");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private OverviewEntry? FindDockableEntry()
    {
        var overview = _getContext!()?.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
        if (overview == null) return null;

        static bool LooksLikeStation(OverviewEntry e)
        {
            var allTexts = new[] { e.ObjectType ?? "", e.Name ?? "" }
                .Concat(e.Texts).Concat(e.CellsTexts.Values);
            return allTexts.Any(t => EveConstants.StationKeywords.Any(k =>
                t.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }

        static bool LooksLikeNonDockable(OverviewEntry e)
        {
            var allTexts = new[] { e.ObjectType ?? "" }.Concat(e.CellsTexts.Values);
            return allTexts.Any(t => EveConstants.NonDockableKeywords.Any(k =>
                t.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }

        var byType = overview.Entries
            .Where(e => LooksLikeStation(e) && !LooksLikeNonDockable(e))
            .OrderBy(e => e.DistanceInMeters ?? double.MaxValue)
            .FirstOrDefault();
        if (byType != null) return byType;

        return overview.Entries
            .Where(e => e.DistanceInMeters.HasValue && !LooksLikeNonDockable(e))
            .OrderBy(e => e.DistanceInMeters!.Value)
            .FirstOrDefault();
    }

    private Task EnsureReadyAsync()
    {
        if (_executor == null || _input == null)
            throw new InvalidOperationException("ManualActionService not configured — call Configure() first.");
        return Task.CompletedTask;
    }

    private static nint GetEveHandle() =>
        EveProcessFinder.FindFirstClient()?.MainWindowHandle ?? 0;
}
