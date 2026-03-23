using EBot.Core.Bot;
using EBot.Core.DecisionEngine;

namespace EBot.ExampleBots;

/// <summary>
/// A no-op bot used for "Monitor" mode.
/// It reads game state every tick but takes no actions, allowing the
/// terminal dashboard and web UI to display live EVE Online data.
/// </summary>
public sealed class IdleBot : IBot
{
    public string Name => "Monitor";
    public string Description => "Read-only mode. Displays live EVE Online data without taking any actions.";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs = 1000,
    };

    /// <summary>Always succeeds instantly — no actions taken.</summary>
    public IBehaviorNode BuildBehaviorTree() =>
        new ActionNode("Observe", _ => NodeStatus.Success);
}
