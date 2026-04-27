namespace EBot.Core.DecisionEngine;

/// <summary>
/// Interface for bot implementations. Each bot provides a behavior tree
/// defining its logic and default settings.
/// </summary>
public interface IBot
{
    /// <summary>Display name of the bot.</summary>
    string Name { get; }

    /// <summary>Description of what this bot does.</summary>
    string Description { get; }

    /// <summary>
    /// Builds the behavior tree that defines this bot's decision logic.
    /// Called once when the bot starts.
    /// </summary>
    IBehaviorNode BuildBehaviorTree();

    /// <summary>
    /// Returns the default settings for this bot.
    /// </summary>
    BotSettings GetDefaultSettings();

    /// <summary>
    /// Called once when the bot starts, for any initialization.
    /// </summary>
    void OnStart(BotContext context) { }

    /// <summary>
    /// Called once when the bot stops, for any cleanup.
    /// </summary>
    void OnStop(BotContext context) { }
}
