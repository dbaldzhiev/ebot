using EBot.Core.Bot;
using EBot.Core.DecisionEngine;

namespace EBot.ExampleBots.MiningBot;

public sealed class MiningBotScripted : IBot
{
    private readonly CommandBot _innerBot;

    public MiningBotScripted()
    {
        var script = @"
            UNDOCK
            WAIT 5s
            WARP_TO ""Asteroid Belt""
            WAIT 10s
            LAUNCH_DRONES
            LOOP
                MINE_ALL
                WAIT 30s
                IF ORE_HOLD_FULL THEN BREAK
            END
            RECALL_DRONES
            WAIT 5s
            WARP_TO ""Station""
            DOCK
            UNLOAD_ORE
        ";
        _innerBot = new CommandBot(script);
    }

    public string Name => "MiningBot (Scripted)";
    public string Description => "A mining bot implemented using the new natural language Command DSL.";

    public BotSettings GetDefaultSettings() => new();

    public IBehaviorNode BuildBehaviorTree()
    {
        return _innerBot.BuildBehaviorTree();
    }
}
