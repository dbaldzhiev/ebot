namespace EBot.Core.DecisionEngine;

/// <summary>
/// Status returned by a behavior tree node after a tick.
/// </summary>
public enum NodeStatus
{
    /// <summary>Node completed successfully.</summary>
    Success,

    /// <summary>Node failed.</summary>
    Failure,

    /// <summary>Node is still running and needs more ticks.</summary>
    Running,
}

/// <summary>
/// A node in a behavior tree. All behavior tree nodes implement this interface.
/// </summary>
public interface IBehaviorNode
{
    /// <summary>Display name for logging and debugging.</summary>
    string Name { get; }

    /// <summary>
    /// Executes one tick of this node's logic.
    /// </summary>
    NodeStatus Tick(BotContext context);

    /// <summary>
    /// Resets the node to its initial state (called when the tree restarts).
    /// </summary>
    void Reset() { }
}

// ─── Composite Nodes ───────────────────────────────────────────────────────

/// <summary>
/// Runs children in sequence. Succeeds only if ALL children succeed.
/// Fails immediately when any child fails.
/// </summary>
public sealed class SequenceNode : IBehaviorNode
{
    private readonly List<IBehaviorNode> _children;
    private int _currentIndex;

    public string Name { get; }

    public SequenceNode(string name, params IBehaviorNode[] children)
    {
        Name = name;
        _children = [.. children];
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            while (_currentIndex < _children.Count)
            {
                var status = _children[_currentIndex].Tick(context);
                switch (status)
                {
                    case NodeStatus.Failure:
                        _currentIndex = 0;
                        return NodeStatus.Failure;
                    case NodeStatus.Running:
                        return NodeStatus.Running;
                    case NodeStatus.Success:
                        _currentIndex++;
                        break;
                }
            }

            _currentIndex = 0;
            return NodeStatus.Success;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }

    public void Reset()
    {
        _currentIndex = 0;
        foreach (var child in _children) child.Reset();
    }
}

/// <summary>
/// Runs children in order. Succeeds on the FIRST child that succeeds.
/// Fails only when ALL children fail.
/// </summary>
public sealed class SelectorNode : IBehaviorNode
{
    private readonly List<IBehaviorNode> _children;
    private int _currentIndex;

    public string Name { get; }

    public SelectorNode(string name, params IBehaviorNode[] children)
    {
        Name = name;
        _children = [.. children];
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            while (_currentIndex < _children.Count)
            {
                var status = _children[_currentIndex].Tick(context);
                switch (status)
                {
                    case NodeStatus.Success:
                        _currentIndex = 0;
                        return NodeStatus.Success;
                    case NodeStatus.Running:
                        return NodeStatus.Running;
                    case NodeStatus.Failure:
                        _currentIndex++;
                        break;
                }
            }

            _currentIndex = 0;
            return NodeStatus.Failure;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }

    public void Reset()
    {
        _currentIndex = 0;
        foreach (var child in _children) child.Reset();
    }
}

/// <summary>
/// A non-stateful selector. Restarts from the first child every tick.
/// Useful for root-level nodes that must always execute (like state synthesis).
/// </summary>
public sealed class StatelessSelectorNode : IBehaviorNode
{
    private readonly List<IBehaviorNode> _children;

    public string Name { get; }

    public StatelessSelectorNode(string name, params IBehaviorNode[] children)
    {
        Name = name;
        _children = [.. children];
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            foreach (var child in _children)
            {
                var status = child.Tick(context);
                if (status != NodeStatus.Failure) return status;
            }
            return NodeStatus.Failure;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }
}

/// <summary>
/// Evaluates a predicate against the current game state.
/// Returns Success if true, Failure if false.
/// </summary>
public sealed class ConditionNode : IBehaviorNode
{
    private readonly Func<BotContext, bool> _predicate;

    public string Name { get; }

    public ConditionNode(string name, Func<BotContext, bool> predicate)
    {
        Name = name;
        _predicate = predicate;
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            return _predicate(context) ? NodeStatus.Success : NodeStatus.Failure;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }
}

/// <summary>
/// Executes a bot action. The action function returns a NodeStatus
/// to indicate completion, failure, or ongoing work.
/// </summary>
public sealed class ActionNode : IBehaviorNode
{
    private readonly Func<BotContext, NodeStatus> _action;

    public string Name { get; }

    public ActionNode(string name, Func<BotContext, NodeStatus> action)
    {
        Name = name;
        _action = action;
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            return _action(context);
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }
}

/// <summary>
/// Inverts the result of a child node (Success ↔ Failure, Running stays Running).
/// </summary>
public sealed class InverterNode : IBehaviorNode
{
    private readonly IBehaviorNode _child;

    public string Name { get; }

    public InverterNode(string name, IBehaviorNode child)
    {
        Name = name;
        _child = child;
    }

    public NodeStatus Tick(BotContext context) => _child.Tick(context) switch
    {
        NodeStatus.Success => NodeStatus.Failure,
        NodeStatus.Failure => NodeStatus.Success,
        _ => NodeStatus.Running,
    };

    public void Reset() => _child.Reset();
}

/// <summary>
/// Repeats a child node a specified number of times (or indefinitely if count is -1).
/// </summary>
public sealed class RepeatNode : IBehaviorNode
{
    private readonly IBehaviorNode _child;
    private readonly int _maxCount;
    private int _currentCount;

    public string Name { get; }

    public RepeatNode(string name, IBehaviorNode child, int count = -1)
    {
        Name = name;
        _child = child;
        _maxCount = count;
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            if (_maxCount > 0 && _currentCount >= _maxCount)
            {
                _currentCount = 0;
                return NodeStatus.Success;
            }

            var status = _child.Tick(context);
            if (status == NodeStatus.Running) return NodeStatus.Running;

            _currentCount++;
            if (status == NodeStatus.Failure)
            {
                _currentCount = 0;
                return NodeStatus.Failure;
            }

            // Still repeating
            if (_maxCount < 0 || _currentCount < _maxCount)
                return NodeStatus.Running;

            _currentCount = 0;
            return NodeStatus.Success;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }

    public void Reset()
    {
        _currentCount = 0;
        _child.Reset();
    }
}

/// <summary>
/// Waits for a specified duration before returning Success.
/// </summary>
public sealed class WaitNode : IBehaviorNode
{
    private readonly TimeSpan _duration;
    private DateTimeOffset? _startTime;

    public string Name { get; }

    public WaitNode(string name, TimeSpan duration)
    {
        Name = name;
        _duration = duration;
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            _startTime ??= DateTimeOffset.UtcNow;

            if (DateTimeOffset.UtcNow - _startTime >= _duration)
            {
                _startTime = null;
                return NodeStatus.Success;
            }

            return NodeStatus.Running;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }

    public void Reset()
    {
        _startTime = null;
    }
}

/// <summary>
/// Always succeeds regardless of child result (wraps a child so it can't fail the parent).
/// </summary>
public sealed class AlwaysSucceedNode : IBehaviorNode
{
    private readonly IBehaviorNode _child;

    public string Name { get; }

    public AlwaysSucceedNode(string name, IBehaviorNode child)
    {
        Name = name;
        _child = child;
    }

    public NodeStatus Tick(BotContext context)
    {
        context.ActiveNodes.Push(Name);
        context.SnapshotActivePath();
        try
        {
            var status = _child.Tick(context);
            return status == NodeStatus.Running ? NodeStatus.Running : NodeStatus.Success;
        }
        finally
        {
            context.ActiveNodes.Pop();
        }
    }

    public void Reset() => _child.Reset();
}
