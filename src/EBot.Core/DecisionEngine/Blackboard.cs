namespace EBot.Core.DecisionEngine;

/// <summary>
/// A key-value store for sharing data between behavior tree nodes.
/// Acts as the "memory" of the bot between ticks.
/// </summary>
public sealed class Blackboard
{
    private readonly Dictionary<string, object> _data = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new();

    /// <summary>
    /// Gets a typed value from the blackboard, or default if not found.
    /// </summary>
    public T? Get<T>(string key)
    {
        if (_data.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>
    /// Sets a typed value on the blackboard.
    /// </summary>
    public void Set<T>(string key, T value) where T : notnull
    {
        _data[key] = value;
    }

    /// <summary>
    /// Checks if a key exists on the blackboard.
    /// </summary>
    public bool Has(string key) => _data.ContainsKey(key);

    /// <summary>
    /// Removes a key from the blackboard.
    /// </summary>
    public bool Remove(string key) => _data.Remove(key);

    /// <summary>
    /// Clears all data from the blackboard.
    /// </summary>
    public void Clear()
    {
        _data.Clear();
        _cooldowns.Clear();
    }

    // ─── Cooldown System ───────────────────────────────────────────────

    /// <summary>
    /// Sets a cooldown that expires at the specified time.
    /// </summary>
    public void SetCooldown(string key, TimeSpan duration)
    {
        _cooldowns[key] = DateTimeOffset.UtcNow + duration;
    }

    /// <summary>
    /// Checks whether a cooldown has expired.
    /// </summary>
    public bool IsCooldownReady(string key)
    {
        if (!_cooldowns.TryGetValue(key, out var expiry))
            return true;
        if (DateTimeOffset.UtcNow >= expiry)
        {
            _cooldowns.Remove(key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Remaining time on a cooldown, or TimeSpan.Zero if ready.
    /// </summary>
    public TimeSpan GetCooldownRemaining(string key)
    {
        if (!_cooldowns.TryGetValue(key, out var expiry))
            return TimeSpan.Zero;
        var remaining = expiry - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    // ─── Counter Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Increments an integer counter and returns the new value.
    /// </summary>
    public int Increment(string key)
    {
        var val = Get<int>(key) + 1;
        Set(key, val);
        return val;
    }

    /// <summary>
    /// Gets all keys currently stored in the blackboard.
    /// </summary>
    public IEnumerable<string> Keys => _data.Keys;
}
