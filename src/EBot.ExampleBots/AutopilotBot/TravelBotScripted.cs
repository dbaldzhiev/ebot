using EBot.Core.Bot;
using EBot.Core.DecisionEngine;

namespace EBot.ExampleBots.AutopilotBot;

public sealed class TravelBotScripted : IBot
{
    private readonly CommandBot _innerBot;

    public TravelBotScripted()
    {
        var script = @"
            AUTOPILOT
        ";
        _innerBot = new CommandBot(script);
    }

    public string Name => "Travel Bot (Scripted)";
    public string Description => "Warp-to-0 autopilot following the in-game route.";

    public BotSettings GetDefaultSettings() => new()
    {
        TickIntervalMs = 2000
    };

    public IBehaviorNode BuildBehaviorTree()
    {
        return _innerBot.BuildBehaviorTree();
    }
}
