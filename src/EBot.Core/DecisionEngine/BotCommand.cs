using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.Core.DecisionEngine;

public enum CommandStatus
{
    Running,
    Success,
    Failure,
    Skipped
}

public interface IBotCommand
{
    string Name { get; }
    CommandStatus Execute(BotContext ctx);
}

public abstract class BotCommandBase : IBotCommand
{
    public abstract string Name { get; }
    public abstract CommandStatus Execute(BotContext ctx);
}

public sealed class ConditionalCommand : BotCommandBase
{
    private readonly Func<BotContext, bool> _condition;
    private readonly IBotCommand _command;

    public ConditionalCommand(Func<BotContext, bool> condition, IBotCommand command)
    {
        _condition = condition;
        _command = command;
    }

    public override string Name => $"IF (condition) THEN {_command.Name}";

    public override CommandStatus Execute(BotContext ctx)
    {
        if (_condition(ctx))
        {
            return _command.Execute(ctx);
        }
        return CommandStatus.Skipped;
    }
}

public sealed class WaitCommand : BotCommandBase
{
    private readonly TimeSpan _duration;
    private DateTime? _endTime;

    public WaitCommand(TimeSpan duration) => _duration = duration;

    public override string Name => $"WAIT {_duration.TotalSeconds}s";

    public override CommandStatus Execute(BotContext ctx)
    {
        _endTime ??= DateTime.UtcNow + _duration;
        return DateTime.UtcNow >= _endTime ? CommandStatus.Success : CommandStatus.Running;
    }
}

public sealed class WarpToCommand : BotCommandBase
{
    private readonly string _targetName;
    private bool _started;

    public WarpToCommand(string targetName) => _targetName = targetName;

    public override string Name => $"WARP_TO \"{_targetName}\"";

    public override CommandStatus Execute(BotContext ctx)
    {
        if (ctx.GameState.ParsedUI.ShipUI?.Indication?.ManeuverType?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true)
        {
            _started = true;
            return CommandStatus.Running;
        }

        if (_started) return CommandStatus.Success;

        // Implementation: Find target in overview and warp
        var entry = ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries)
            .FirstOrDefault(e => e.Name?.Contains(_targetName, StringComparison.OrdinalIgnoreCase) == true);
        
        if (entry != null)
        {
            ctx.RightClick(entry.UINode);
            ctx.ClickMenuEntry("Warp to Within 0 m");
            _started = true;
            return CommandStatus.Running;
        }

        return CommandStatus.Failure;
    }
}

public sealed class DockCommand : BotCommandBase
{
    public override string Name => "DOCK";
    public override CommandStatus Execute(BotContext ctx)
    {
        if (ctx.GameState.ParsedUI.StationWindow != null) return CommandStatus.Success;
        
        var station = ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries)
            .FirstOrDefault(e => e.ObjectType?.Contains("Station", StringComparison.OrdinalIgnoreCase) == true);

        if (station != null)
        {
            ctx.RightClick(station.UINode);
            ctx.ClickMenuEntry("Dock");
            return CommandStatus.Running;
        }

        return CommandStatus.Failure;
    }
}

public sealed class BookmarkDockCommand : BotCommandBase
{
    private readonly string _bookmarkName;
    private bool _warpingToBookmark;
    private bool _arrivedAtBookmark;

    public BookmarkDockCommand(string bookmarkName) => _bookmarkName = bookmarkName;

    public override string Name => $"BOOKMARK_DOCK \"{_bookmarkName}\"";

    public override CommandStatus Execute(BotContext ctx)
    {
        if (ctx.GameState.ParsedUI.StationWindow != null) return CommandStatus.Success;

        if (!_arrivedAtBookmark)
        {
            if (ctx.GameState.IsWarping)
            {
                _warpingToBookmark = true;
                return CommandStatus.Running;
            }

            if (_warpingToBookmark)
            {
                _arrivedAtBookmark = true;
                return CommandStatus.Running;
            }

            // Find bookmark in InfoPanel or People & Places
            // Heuristic: search all text nodes for bookmark name in the left side of the screen
            var bookmarkNode = ctx.GameState.ParsedUI.UITree?.FindAll(n => 
                n.Region.X < 400 && 
                n.GetAllContainedDisplayTexts().Any(t => t.Equals(_bookmarkName, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();

            if (bookmarkNode != null)
            {
                ctx.RightClick(bookmarkNode);
                ctx.ClickMenuEntry("Warp to Within 0 m");
                _warpingToBookmark = true;
                return CommandStatus.Running;
            }
            
            // Fallback: If no bookmark found, maybe we are already there or should just try to dock
            _arrivedAtBookmark = true;
        }

        // Standard dock logic
        var station = ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries)
            .FirstOrDefault(e => e.ObjectType?.Contains("Station", StringComparison.OrdinalIgnoreCase) == true ||
                                 e.ObjectType?.Contains("Structure", StringComparison.OrdinalIgnoreCase) == true ||
                                 e.ObjectType?.Contains("Citadel", StringComparison.OrdinalIgnoreCase) == true);

        if (station != null)
        {
            ctx.RightClick(station.UINode);
            ctx.ClickMenuEntry("Dock");
            return CommandStatus.Running;
        }

        return CommandStatus.Failure;
    }
}

public sealed class UndockCommand : BotCommandBase
{
    public override string Name => "UNDOCK";
    public override CommandStatus Execute(BotContext ctx)
    {
        if (ctx.GameState.ParsedUI.StationWindow == null) return CommandStatus.Success;
        var btn = ctx.GameState.ParsedUI.StationWindow.UndockButton;
        if (btn != null)
        {
            ctx.Click(btn);
            return CommandStatus.Running;
        }
        return CommandStatus.Failure;
    }
}

public sealed class MineAllCommand : BotCommandBase
{
    public override string Name => "MINE_ALL";
    public override CommandStatus Execute(BotContext ctx)
    {
        var targets = ctx.GameState.ParsedUI.Targets;
        if (targets.Count == 0) return CommandStatus.Failure;

        var inactiveModules = ctx.GameState.ParsedUI.ShipUI?.ModuleButtons
            .Where(b => b.Name?.Contains("Miner", StringComparison.OrdinalIgnoreCase) == true && b.IsActive != true)
            .ToList();

        if (inactiveModules == null || inactiveModules.Count == 0) return CommandStatus.Success;

        foreach (var mod in inactiveModules)
        {
            ctx.Click(mod.UINode);
        }

        return CommandStatus.Running;
    }
}

public sealed class LaunchDronesCommand : BotCommandBase
{
    public override string Name => "LAUNCH_DRONES";
    public override CommandStatus Execute(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInBay?.QuantityCurrent ?? 0) > 0)
        {
            ctx.KeyPress(VirtualKey.F, VirtualKey.Shift);
            return CommandStatus.Success;
        }
        return CommandStatus.Success;
    }
}

public sealed class RecallDronesCommand : BotCommandBase
{
    public override string Name => "RECALL_DRONES";
    public override CommandStatus Execute(BotContext ctx)
    {
        if ((ctx.GameState.ParsedUI.DronesWindow?.DronesInSpace?.QuantityCurrent ?? 0) > 0)
        {
            ctx.KeyPress(VirtualKey.R, VirtualKey.Shift);
            return CommandStatus.Running;
        }
        return CommandStatus.Success;
    }
}

public sealed class BreakCommand : BotCommandBase
{
    public override string Name => "BREAK";
    public override CommandStatus Execute(BotContext ctx) => CommandStatus.Success;
}

public sealed class AutopilotCommand : BotCommandBase
{
    private DateTime? _nextActionTime;
    private bool _wasInSpace;

    public override string Name => "AUTOPILOT";

    public override CommandStatus Execute(BotContext ctx)
    {
        if (DateTime.UtcNow < _nextActionTime) return CommandStatus.Running;

        var ui = ctx.GameState.ParsedUI;
        
        // 1. If docked, undock if there's a route
        if (ctx.GameState.IsDocked)
        {
            if (ctx.GameState.RouteJumpsRemaining > 0)
            {
                var undockBtn = ui.StationWindow?.UndockButton;
                if (undockBtn != null)
                {
                    ctx.Click(undockBtn);
                    _nextActionTime = DateTime.UtcNow.AddSeconds(10);
                    return CommandStatus.Running;
                }
            }
            else if (_wasInSpace)
            {
                return CommandStatus.Success; // Arrived and docked
            }
        }

        // 2. If in space, handle navigation
        if (ctx.GameState.IsInSpace)
        {
            _wasInSpace = true;

            // Wait if warping or jumping
            if (ctx.GameState.IsWarping || 
                ui.ShipUI?.Indication?.ManeuverType?.Contains("Jump", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CommandStatus.Running;
            }

            // Find next route marker
            var marker = ui.InfoPanelContainer?.InfoPanelRoute?.RouteElementMarkers.FirstOrDefault();
            if (marker == null)
            {
                if (ctx.GameState.RouteJumpsRemaining == 0) return CommandStatus.Success;
                return CommandStatus.Running;
            }

            // Right-click marker to see options
            if (!ctx.GameState.HasContextMenu)
            {
                ctx.RightClick(marker);
                _nextActionTime = DateTime.UtcNow.AddSeconds(2);
                return CommandStatus.Running;
            }

            // Handle menu: prioritize Dock, Jump, then Warp
            var menu = ui.ContextMenus.FirstOrDefault();
            if (menu != null)
            {
                var dock = menu.Entries.FirstOrDefault(e => e.Text?.Contains("Dock", StringComparison.OrdinalIgnoreCase) == true);
                var jump = menu.Entries.FirstOrDefault(e => e.Text?.Contains("Jump", StringComparison.OrdinalIgnoreCase) == true);
                var warp = menu.Entries.FirstOrDefault(e => e.Text?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true && e.Text?.Contains(" 0") == true);
                
                var chosen = dock ?? jump ?? warp ?? menu.Entries.FirstOrDefault(e => e.Text?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true);

                if (chosen != null)
                {
                    ctx.Click(chosen.UINode);
                    _nextActionTime = DateTime.UtcNow.AddSeconds(5);
                }
                else
                {
                    ctx.KeyPress(VirtualKey.Escape);
                    _nextActionTime = DateTime.UtcNow.AddSeconds(1);
                }
            }
        }

        return CommandStatus.Running;
    }
}

public sealed class UnloadOreCommand : BotCommandBase
{
    public override string Name => "UNLOAD_ORE";
    public override CommandStatus Execute(BotContext ctx)
    {
        if (ctx.GameState.ParsedUI.StationWindow == null) return CommandStatus.Failure;

        var inventory = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault();
        if (inventory == null)
        {
            ctx.KeyPress(VirtualKey.I, VirtualKey.Alt);
            return CommandStatus.Running;
        }

        // 1. Ensure Ore Hold is selected
        var oreHoldEntry = inventory.NavEntries.FirstOrDefault(e => e.HoldType == InventoryHoldType.Mining);
        if (oreHoldEntry != null && !oreHoldEntry.IsSelected)
        {
            ctx.Click(oreHoldEntry.UINode);
            return CommandStatus.Running;
        }

        // 2. Select all items and move to Item Hangar (via context menu on any item)
        var firstItem = inventory.Items.FirstOrDefault();
        if (firstItem != null)
        {
            ctx.RightClick(firstItem.UINode);
            ctx.ClickMenuEntry("Select All");
            // Next tick we would drag, but let's use "Move to Item Hangar" if it exists in menu
            // Or just "Select All" then drag to the NavEntry for Item Hangar
            
            var itemHangarEntry = inventory.NavEntries.FirstOrDefault(e => e.HoldType == InventoryHoldType.Item);
            if (itemHangarEntry != null)
            {
                // Simple implementation: drag first item to item hangar (EVE usually moves all if all selected)
                // But we don't have drag in BotContext yet. 
                // Let's assume we use context menu "Move to Item Hangar"
                ctx.ClickMenuEntry("Move to Item Hangar");
            }
            return CommandStatus.Success;
        }

        return CommandStatus.Success; // Already empty
    }
}
