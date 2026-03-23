using EBot.Core.DecisionEngine;
using EBot.Core.GameState;

namespace EBot.ExampleBots;

/// <summary>
/// Pre-built survival behavior tree that can wrap any bot.
///
/// Priority (first branch that succeeds wins each tick):
///   1. Dismiss any blocking message box
///   2. If under attack with low shields → deactivate weapons, activate tank
///   3. If structure critical → attempt to stop and stabilise
///   4. Fall through to the wrapped bot's own tree
///
/// Usage:
///   var tree = SurvivalNodes.Wrap(myBot.BuildBehaviorTree());
/// </summary>
public static class SurvivalNodes
{
    private const int ShieldEmergencyPct = 30;
    private const int StructureCriticalPct = 25;

    /// <summary>
    /// Returns a SelectorNode that runs survival checks before delegating
    /// to <paramref name="botTree"/>.
    /// </summary>
    public static IBehaviorNode Wrap(IBehaviorNode botTree) =>
        new SelectorNode("SafeRoot",
            DismissMessageBoxes(),
            EmergencyTank(),
            StructureCritical(),
            botTree);

    // ─── Dismiss message boxes ──────────────────────────────────────────────

    private static IBehaviorNode DismissMessageBoxes() =>
        new SequenceNode("DismissMessageBox",
            new ConditionNode("HasMessageBox",
                ctx => ctx.GameState.ParsedUI.MessageBoxes.Count > 0),
            new ActionNode("ClickMessageBoxButton", ctx =>
            {
                var btn = ctx.GameState.ParsedUI.MessageBoxes
                    .SelectMany(mb => mb.Buttons)
                    .FirstOrDefault();

                if (btn == null) return NodeStatus.Failure;
                ctx.Click(btn);
                return NodeStatus.Success;
            }));

    // ─── Emergency tank (activate hardeners, deactivate weapons) ───────────

    private static IBehaviorNode EmergencyTank() =>
        new SequenceNode("EmergencyTank",
            new ConditionNode("UnderAttackLowShields", ctx =>
            {
                var ui = ctx.GameState.ParsedUI;
                var hp = ui.ShipUI?.HitpointsPercent;
                if (hp == null) return false;

                bool underAttack = ui.OverviewWindows
                    .SelectMany(w => w.Entries)
                    .Any(e => e.IsAttackingMe);

                return underAttack && hp.Shield < ShieldEmergencyPct;
            }),
            new ActionNode("ActivateTankModules", ctx =>
            {
                var rows = ctx.GameState.ParsedUI.ShipUI?.ModuleButtonsRows;
                if (rows == null) return NodeStatus.Failure;

                // Deactivate top-row modules (weapons / miners)
                foreach (var mod in rows.Top.Where(m => m.IsActive == true))
                    ctx.Click(mod.UINode);

                // Activate middle-row modules (hardeners / shield boosters)
                foreach (var mod in rows.Middle.Where(m => m.IsActive == false))
                    ctx.Click(mod.UINode);

                return NodeStatus.Success;
            }));

    // ─── Structure critical — stop ship movement ────────────────────────────

    private static IBehaviorNode StructureCritical() =>
        new SequenceNode("StructureCritical",
            new ConditionNode("StructureVeryLow", ctx =>
            {
                var hp = ctx.GameState.ParsedUI.ShipUI?.HitpointsPercent;
                return hp != null && hp.Structure < StructureCriticalPct;
            }),
            new ActionNode("StopShip", ctx =>
            {
                var stopBtn = ctx.GameState.ParsedUI.ShipUI?.StopButton;
                if (stopBtn == null) return NodeStatus.Failure;
                ctx.Click(stopBtn);
                return NodeStatus.Success;
            }));
}
