using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EBot.Core.Bot;
using EBot.Core.GameState;

namespace EBot.Core.DecisionEngine;

public sealed class BotScriptEngine
{
    public static List<IBotCommand> Parse(string script)
    {
        var commands = new List<IBotCommand>();
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            if (line.StartsWith("LOOP", StringComparison.OrdinalIgnoreCase))
            {
                // Find matching END
                int loopStart = i + 1;
                int loopEnd = -1;
                int depth = 1;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].StartsWith("LOOP", StringComparison.OrdinalIgnoreCase)) depth++;
                    if (lines[j].StartsWith("END", StringComparison.OrdinalIgnoreCase)) depth--;
                    if (depth == 0) { loopEnd = j; break; }
                }

                if (loopEnd != -1)
                {
                    var loopBody = string.Join("\n", lines[loopStart..loopEnd]);
                    commands.Add(new LoopCommand(Parse(loopBody)));
                    i = loopEnd;
                    continue;
                }
            }

            var cmd = ParseLine(line);
            if (cmd != null) commands.Add(cmd);
        }

        return commands;
    }

    private static IBotCommand? ParseLine(string line)
    {
        if (line.StartsWith("IF ", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(line, @"IF\s+(.+)\s+THEN\s+(.+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var condStr = match.Groups[1].Value.Trim();
                var cmdStr = match.Groups[2].Value.Trim();
                var innerCmd = ParseLine(cmdStr);
                if (innerCmd != null)
                {
                    return new ConditionalCommand(ctx => CheckCondition(ctx, condStr), innerCmd);
                }
            }
        }

        if (line.StartsWith("WAIT ", StringComparison.OrdinalIgnoreCase))
        {
            var val = line[5..].Trim();
            if (val.EndsWith('s')) val = val[..^1];
            if (double.TryParse(val, out var seconds))
                return new WaitCommand(TimeSpan.FromSeconds(seconds));
        }
        else if (line.StartsWith("WARP_TO ", StringComparison.OrdinalIgnoreCase))
        {
            var target = line[8..].Trim('"', ' ');
            return new WarpToCommand(target);
        }
        else if (line.Equals("DOCK", StringComparison.OrdinalIgnoreCase))
        {
            return new DockCommand();
        }
        else if (line.Equals("UNDOCK", StringComparison.OrdinalIgnoreCase))
        {
            return new UndockCommand();
        }
        else if (line.Equals("MINE_ALL", StringComparison.OrdinalIgnoreCase))
        {
            return new MineAllCommand();
        }
        else if (line.Equals("LAUNCH_DRONES", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchDronesCommand();
        }
        else if (line.Equals("RECALL_DRONES", StringComparison.OrdinalIgnoreCase))
        {
            return new RecallDronesCommand();
        }
        else if (line.Equals("UNLOAD_ORE", StringComparison.OrdinalIgnoreCase))
        {
            return new UnloadOreCommand();
        }
        else if (line.Equals("BREAK", StringComparison.OrdinalIgnoreCase))
        {
            return new BreakCommand();
        }
        else if (line.StartsWith("BOOKMARK_DOCK ", StringComparison.OrdinalIgnoreCase))
        {
            var bookmark = line[14..].Trim('"', ' ');
            return new BookmarkDockCommand(bookmark);
        }
        else if (line.Equals("AUTOPILOT", StringComparison.OrdinalIgnoreCase))
        {
            return new AutopilotCommand();
        }

        return null;
    }

    private static bool CheckCondition(BotContext ctx, string condition)
    {
        if (condition.Equals("ORE_HOLD_FULL", StringComparison.OrdinalIgnoreCase))
        {
            var inv = ctx.GameState.ParsedUI.InventoryWindows.FirstOrDefault(w => w.HoldType == InventoryHoldType.Mining);
            return (inv?.CapacityGauge?.FillPercent ?? 0) > 95;
        }
        if (condition.Equals("UNDER_ATTACK", StringComparison.OrdinalIgnoreCase))
        {
            return ctx.GameState.ParsedUI.OverviewWindows.SelectMany(w => w.Entries).Any(e => e.IsAttackingMe);
        }
        return false;
    }
}

public sealed class LoopCommand : BotCommandBase
{
    private readonly List<IBotCommand> _body;
    private int _currentIndex = 0;

    public LoopCommand(List<IBotCommand> body) => _body = body;

    public override string Name => "LOOP";

    public override CommandStatus Execute(BotContext ctx)
    {
        if (_body.Count == 0) return CommandStatus.Success;

        var cmd = _body[_currentIndex];
        var status = cmd.Execute(ctx);

        if (status == CommandStatus.Success)
        {
            // If the command that just succeeded was a BREAK, or a conditional BREAK that ran
            if (cmd is BreakCommand || (cmd is ConditionalCommand && cmd.Name.Contains("BREAK")))
            {
                return CommandStatus.Success; // Exit loop
            }

            _currentIndex = (_currentIndex + 1) % _body.Count;
        }
        else if (status == CommandStatus.Skipped)
        {
            _currentIndex = (_currentIndex + 1) % _body.Count;
        }
        else if (status == CommandStatus.Failure)
        {
            return CommandStatus.Failure;
        }

        return CommandStatus.Running;
    }
}

public sealed class CommandBot : IBot
{
    private readonly List<IBotCommand> _commands;
    private int _currentIndex = 0;

    public CommandBot(string script)
    {
        _commands = BotScriptEngine.Parse(script);
    }

    public string Name => "CommandBot";
    public string Description => "Executes a sequence of high-level commands.";

    public BotSettings GetDefaultSettings() => new();

    public IBehaviorNode BuildBehaviorTree()
    {
        return new ActionNode("CommandBotSequence", ctx =>
        {
            if (_currentIndex >= _commands.Count) return NodeStatus.Success;

            var current = _commands[_currentIndex];
            var status = current.Execute(ctx);

            if (status == CommandStatus.Success || status == CommandStatus.Skipped)
            {
                _currentIndex++;
                return NodeStatus.Running;
            }

            if (status == CommandStatus.Failure) return NodeStatus.Failure;

            return NodeStatus.Running;
        });
    }
}
