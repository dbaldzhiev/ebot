using System.Collections.Concurrent;
using System.Text.Json;

namespace EBot.Core.Bot;

/// <summary>
/// Represents all data captured during a single bot tick.
/// </summary>
public sealed class RecordedTick
{
    public long TickCount { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? FrameJson { get; set; }
    public IReadOnlyDictionary<string, object>? BlackboardBefore { get; set; }
    public IReadOnlyList<string>? Actions { get; set; }
}

/// <summary>
/// Manages the recording of bot sessions (sequences of ticks).
/// </summary>
public sealed class SessionRecorder
{
    private readonly ConcurrentQueue<RecordedTick> _ticks = new();
    private bool _isRecording;

    public bool IsRecording => _isRecording;
    public int TickCount => _ticks.Count;

    public void Start()
    {
        _ticks.Clear();
        _isRecording = true;
    }

    public void Stop()
    {
        _isRecording = false;
    }

    public void Record(RecordedTick tick)
    {
        if (!_isRecording) return;
        _ticks.Enqueue(tick);
    }

    public string ExportJson()
    {
        return JsonSerializer.Serialize(_ticks.ToList(), new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public void Clear()
    {
        _ticks.Clear();
    }
}
