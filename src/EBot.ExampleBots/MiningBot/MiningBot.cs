using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

/// <summary>
/// Example mining bot demonstrating how to use the EBot framework.
/// 
/// Behavior:
/// 1. Handle message boxes (close them)
/// 2. If docked → undock
/// 3. If in space:
///    a. If ore hold is full → warp to station, dock, unload, repeat
///    b. If no asteroid locked → lock nearest asteroid from overview
///    c. Activate mining lasers on locked target
///    d. Wait while mining
/// </summary>
public sealed class MiningBot : IBot
{
    public string Name => "Mining Bot";
    public string Description => "Mines asteroids, unloads at station when ore hold is full.";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs = 2000,
        MinActionDelayMs = 80,
        MaxActionDelayMs = 250,
        CoordinateJitter = 4,
    };

    public IBehaviorNode BuildBehaviorTree()
    {
        return new SelectorNode("Root",
            // Priority 1: Handle message boxes
            HandleMessageBoxes(),

            // Priority 2: Handle docked state
            HandleDocked(),

            // Priority 3: In-space mining logic
            HandleInSpace()
        );
    }

    // ─── Sub-Trees ─────────────────────────────────────────────────────

    private static IBehaviorNode HandleMessageBoxes()
    {
        return new SequenceNode("Handle Message Boxes",
            new ConditionNode("Has Message Box?", ctx => ctx.GameState.HasMessageBox),
            new ActionNode("Close Message Box", ctx =>
            {
                var msgBox = ctx.GameState.ParsedUI.MessageBoxes[0];
                // Click the first button (usually "OK" or "Yes")
                var button = msgBox.Buttons.FirstOrDefault();
                if (button != null)
                {
                    ctx.Click(button);
                    return NodeStatus.Success;
                }
                return NodeStatus.Failure;
            })
        );
    }

    private static IBehaviorNode HandleDocked()
    {
        return new SequenceNode("Handle Docked",
            new ConditionNode("Is Docked?", ctx => ctx.GameState.IsDocked),
            new SelectorNode("Docked Actions",
                // If we need to unload (came back with full hold)
                new SequenceNode("Unload Ore",
                    new ConditionNode("Needs Unload?", ctx =>
                        ctx.Blackboard.Get<bool>("needs_unload")),
                    new ActionNode("Open Inventory", ctx =>
                    {
                        // Open inventory with Alt+C shortcut
                        ctx.KeyPress(VirtualKey.C, VirtualKey.Alt);
                        ctx.Wait(TimeSpan.FromSeconds(1));
                        ctx.Blackboard.Set("needs_unload", false);
                        return NodeStatus.Success;
                    })
                ),
                // Otherwise, undock
                new ActionNode("Undock", ctx =>
                {
                    var undockBtn = ctx.GameState.ParsedUI.StationWindow?.UndockButton;
                    if (undockBtn != null)
                    {
                        ctx.Click(undockBtn);
                        ctx.Wait(TimeSpan.FromSeconds(10)); // Wait for undock
                        return NodeStatus.Success;
                    }
                    return NodeStatus.Failure;
                })
            )
        );
    }

    private static IBehaviorNode HandleInSpace()
    {
        return new SequenceNode("Handle In Space",
            new ConditionNode("Is In Space?", ctx => ctx.GameState.IsInSpace),
            new SelectorNode("In-Space Actions",
                // If warping, just wait
                new SequenceNode("Wait While Warping",
                    new ConditionNode("Is Warping?", ctx => ctx.GameState.IsWarping),
                    new ActionNode("Wait", _ => NodeStatus.Success)
                ),

                // If capacitor too low, wait
                new SequenceNode("Wait Cap Regen",
                    new ConditionNode("Cap Low?", ctx =>
                        ctx.GameState.CapacitorPercent.HasValue &&
                        ctx.GameState.CapacitorPercent < 20),
                    new ActionNode("Wait", _ => NodeStatus.Success)
                ),

                // Check ore hold — if full, go unload
                CheckOreHoldFull(),

                // Lock an asteroid if none locked
                LockAsteroid(),

                // Activate mining modules
                ActivateMiningModules(),

                // Default: wait (mining in progress)
                new ActionNode("Idle", _ => NodeStatus.Success)
            )
        );
    }

    private static IBehaviorNode CheckOreHoldFull()
    {
        return new SequenceNode("Check Ore Hold Full",
            new ConditionNode("Ore Hold Full?", ctx =>
            {
                var oreHold = ctx.GameState.ParsedUI.InventoryWindows
                    .FirstOrDefault(w => w.SubCaptionLabelText?.Contains("ore", StringComparison.OrdinalIgnoreCase) == true);
                return oreHold?.CapacityGauge?.FillPercent >= 95;
            }),
            new ActionNode("Set Unload Flag & Dock", ctx =>
            {
                ctx.Blackboard.Set("needs_unload", true);

                // Right-click the info panel route to warp to station
                // In practice, the bot would use the route or bookmarks
                var route = ctx.GameState.ParsedUI.InfoPanelContainer?.InfoPanelRoute;
                if (route != null && route.RouteElementMarkers.Count > 0)
                {
                    ctx.Click(route.RouteElementMarkers[0]);
                    ctx.Wait(TimeSpan.FromSeconds(2));
                }
                return NodeStatus.Success;
            })
        );
    }

    private static IBehaviorNode LockAsteroid()
    {
        return new SequenceNode("Lock Asteroid",
            new ConditionNode("No Target Locked?", ctx => !ctx.GameState.HasTargets),
            new ConditionNode("Cooldown Ready?", ctx =>
                ctx.Blackboard.IsCooldownReady("lock_target")),
            new ActionNode("Lock Nearest Asteroid", ctx =>
            {
                var overview = ctx.GameState.ParsedUI.OverviewWindows.FirstOrDefault();
                if (overview == null) return NodeStatus.Failure;

                // Find asteroid entries (typically contain "Asteroid" or "Veldspar" etc.)
                var asteroid = overview.Entries
                    .Where(e => IsAsteroid(e))
                    .OrderBy(e => e.DistanceInMeters ?? double.MaxValue)
                    .FirstOrDefault();

                if (asteroid == null) return NodeStatus.Failure;

                // Click to select, then Ctrl+click to lock
                ctx.Click(asteroid.UINode);
                ctx.Wait(TimeSpan.FromMilliseconds(500));
                ctx.KeyPress(VirtualKey.Control); // Lock target shortcut: Ctrl+click or custom
                ctx.Blackboard.SetCooldown("lock_target", TimeSpan.FromSeconds(5));

                return NodeStatus.Success;
            })
        );
    }

    private static IBehaviorNode ActivateMiningModules()
    {
        return new SequenceNode("Activate Mining Modules",
            new ConditionNode("Has Targets?", ctx => ctx.GameState.HasTargets),
            new ConditionNode("Cooldown Ready?", ctx =>
                ctx.Blackboard.IsCooldownReady("activate_modules")),
            new ActionNode("Activate Modules", ctx =>
            {
                var shipUI = ctx.GameState.ParsedUI.ShipUI;
                if (shipUI == null) return NodeStatus.Failure;

                // Activate modules in the top row (where mining lasers typically are)
                var inactiveModules = shipUI.ModuleButtonsRows.Top
                    .Where(m => m.IsActive != true)
                    .ToList();

                foreach (var module in inactiveModules)
                {
                    ctx.Click(module.UINode);
                    ctx.Wait(TimeSpan.FromMilliseconds(300));
                }

                ctx.Blackboard.SetCooldown("activate_modules", TimeSpan.FromSeconds(3));
                return inactiveModules.Count > 0 ? NodeStatus.Success : NodeStatus.Failure;
            })
        );
    }

    private static bool IsAsteroid(OverviewEntry entry)
    {
        var texts = entry.Texts.Select(t => t.ToLowerInvariant()).ToList();
        var asteroidKeywords = new[]
        {
            "asteroid", "veldspar", "scordite", "pyroxeres", "plagioclase",
            "omber", "kernite", "jaspet", "hemorphite", "hedbergite",
            "spodumain", "crokite", "bistot", "arkonor", "mercoxit",
            "dark ochre", "gneiss",
        };

        return texts.Any(t => asteroidKeywords.Any(k => t.Contains(k)));
    }
}
