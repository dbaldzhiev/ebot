#pragma warning disable CA1416 // Windows only

using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Imaging;

namespace EBot.Core.Bot;

/// <summary>
/// The bot runner orchestrates the core loop: Read → Parse → Decide → Act.
/// </summary>
public sealed class BotRunner : IDisposable
{
    // Shared singleton no-op bot used for monitor mode self-stops
    private static readonly IBot _monitorBot = new NoOpBot();

    private IBot _bot;
    private bool _isMonitorMode;
    private readonly BotSettings _settings;
    private readonly IEveMemoryReader _reader;
    private readonly UITreeParser _parser;
    private readonly InputSimulator _input;
    private readonly ActionExecutor _executor;
    private readonly ILogger<BotRunner> _logger;

    public SessionRecorder Recorder { get; } = new();

    private readonly BotContext _context = new();
    private IBehaviorNode? _behaviorTree;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _snapshotSequence;

    private volatile bool _stepRequested;

    // Pending hot-swap — applied at the top of the next tick so the read cycle is never interrupted
    private sealed class SwapRequest(IBot bot, bool isMonitorMode)
    {
        public readonly IBot Bot = bot;
        public readonly bool IsMonitorMode = isMonitorMode;
    }
    private volatile SwapRequest? _pendingSwap;

    // TPM: sliding window of tick completion timestamps (last 60 s)
    private readonly Queue<long> _tickTimestampsMs = new();
    private readonly object _tpmLock = new();

    public BotRunnerState State { get; private set; } = BotRunnerState.Idle;

    /// <summary>Last raw JSON from the memory reader (for diagnostics).</summary>
    public string? LastRawJson { get; private set; }

    /// <summary>Approximate ticks per minute over the last 60 seconds.</summary>
    public double TicksPerMinute
    {
        get
        {
            lock (_tpmLock)
            {
                var nowMs = Environment.TickCount64;
                var cutoffMs = nowMs - 60_000;
                // Drop old entries
                while (_tickTimestampsMs.Count > 0 && _tickTimestampsMs.Peek() < cutoffMs)
                    _tickTimestampsMs.Dequeue();
                if (_tickTimestampsMs.Count < 2) return 0;
                var windowMs = nowMs - _tickTimestampsMs.Peek();
                return windowMs > 0 ? _tickTimestampsMs.Count / (windowMs / 60_000.0) : 0;
            }
        }
    }

    /// <summary>Fired on each completed tick with the current context.</summary>
    public event Action<BotContext, IBot>? OnTick;

    /// <summary>Fired when the bot state changes.</summary>
    public event Action<BotRunnerState>? OnStateChanged;

    /// <summary>Fired when an error occurs during execution.</summary>
    public event Action<Exception>? OnError;

    /// <summary>Fired when a bot is swapped in/out (not fired for the initial bot).</summary>
    public event Action<bool>? OnBotSwapped;   // bool = isMonitorMode

    /// <summary>
    /// Hot-swaps the active bot without interrupting the read-parse-broadcast cycle.
    /// The swap takes effect at the start of the next tick.
    /// </summary>
    public void SwapBot(IBot newBot, bool isMonitorMode) =>
        _pendingSwap = new SwapRequest(newBot, isMonitorMode);

    public BotRunner(
        IBot bot,
        BotSettings settings,
        IEveMemoryReader reader,
        UITreeParser parser,
        InputSimulator input,
        ActionExecutor executor,
        ILogger<BotRunner> logger)
    {
        _bot = bot;
        _isMonitorMode = false;
        _settings = settings;
        _reader = reader;
        _parser = parser;
        _input = input;
        _executor = executor;
        _logger = logger;

        // Apply humanization settings
        _input.MinDelayMs = settings.MinActionDelayMs;
        _input.MaxDelayMs = settings.MaxActionDelayMs;
        _input.CoordinateJitter = settings.CoordinateJitter;
    }

    /// <summary>
    /// Starts the bot. Returns immediately; the bot runs on a background task.
    /// </summary>
    public void Start(bool isMonitorMode = false)
    {
        _isMonitorMode = isMonitorMode;
        if (State == BotRunnerState.Running)
        {
            _logger.LogWarning("Bot is already running");
            return;
        }

        _cts = new CancellationTokenSource();
        _behaviorTree = _bot.BuildBehaviorTree();
        _context.StartTime = DateTimeOffset.UtcNow;
        _context.TickCount = 0;
        _context.Blackboard.Clear();

        _bot.OnStart(_context);

        SetState(BotRunnerState.Running);
        _logger.LogInformation("Starting bot: {BotName}", _bot.Name);

        _runTask = Task.Run(() => RunLoop(_cts.Token));
    }

    /// <summary>
    /// Pauses the bot (stops ticking but preserves state).
    /// </summary>
    public void Pause()
    {
        if (State != BotRunnerState.Running) return;
        SetState(BotRunnerState.Paused);
        _logger.LogInformation("Bot paused");
    }

    /// <summary>
    /// Resumes a paused bot.
    /// </summary>
    public void Resume()
    {
        if (State != BotRunnerState.Paused) return;
        SetState(BotRunnerState.Running);
        _logger.LogInformation("Bot resumed");
    }

    /// <summary>
    /// Executes a single tick if the bot is currently paused.
    /// </summary>
    public void Step()
    {
        if (State != BotRunnerState.Paused) return;
        _stepRequested = true;
        _logger.LogInformation("Step requested");
    }

    /// <summary>
    /// Stops the bot and waits for it to shut down.
    /// </summary>
    public async Task StopAsync()
    {
        if (State == BotRunnerState.Idle) return;

        _logger.LogInformation("Stopping bot...");
        _cts?.Cancel();

        if (_runTask != null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }

        _bot.OnStop(_context);
        SetState(BotRunnerState.Idle);
        _logger.LogInformation("Bot stopped. Total ticks: {Ticks}, Runtime: {Runtime}",
            _context.TickCount, _context.RunDuration);
    }

    // ─── Main Loop ─────────────────────────────────────────────────────

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Apply pending bot swap (set by SwapBot — does not interrupt the read cycle)
                var swap = System.Threading.Interlocked.Exchange(ref _pendingSwap, null);
                if (swap != null)
                {
                    _bot.OnStop(_context);
                    _bot = swap.Bot;
                    _isMonitorMode = swap.IsMonitorMode;
                    _behaviorTree = _bot.BuildBehaviorTree();
                    _context.Blackboard.Clear();
                    _context.TickCount = 0;
                    _bot.OnStart(_context);
                    OnBotSwapped?.Invoke(_isMonitorMode);
                }

                // Check max runtime
                if (_settings.MaxRuntime > TimeSpan.Zero && _context.RunDuration >= _settings.MaxRuntime)
                {
                    _logger.LogInformation("Maximum runtime reached ({Runtime})", _settings.MaxRuntime);
                    break;
                }

                // Skip tick if paused (unless a single step is requested)
                if (State == BotRunnerState.Paused && !_stepRequested)
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                _stepRequested = false;

                // STEP 1: Read game state
                var readResult = await _reader.ReadMemoryAsync(ct);
                if (!readResult.IsSuccess)
                {
                    _logger.LogWarning("Memory reading failed: {Error}", readResult.ErrorMessage);
                    await Task.Delay(_settings.TickIntervalMs, ct);
                    continue;
                }

                // STEP 2: Parse the UI tree
                LastRawJson = readResult.Json;
                var parsedUI = _parser.Parse(readResult.Json!);

                // Optionally log the raw JSON and screenshot
                if (_settings.LogMemoryReadings)
                {
                    await LogMemoryReading(readResult.Json!, ct);
                    CaptureScreenshot();
                }

                // STEP 3: Create snapshot
                var snapshot = new GameStateSnapshot
                {
                    ParsedUI = parsedUI,
                    Timestamp = DateTimeOffset.UtcNow,
                    ProcessId = _settings.Sanderling.ProcessId,
                    ReadDuration = readResult.Elapsed,
                    SequenceNumber = Interlocked.Increment(ref _snapshotSequence),
                };

                _context.GameState = snapshot;
                _context.TickCount++;

                // RECORDING: Capture state before BT tick
                IReadOnlyDictionary<string, object>? blackboardBefore = null;
                if (Recorder.IsRecording)
                {
                    blackboardBefore = new Dictionary<string, object>(_context.Blackboard.GetData());
                }

                // STEP 4: Run the behavior tree with a watchdog
                _context.Actions.Clear();
                _context.ActiveNodes.Clear();
                
                try 
                {
                    // Note: BT is currently synchronous, so this CTS is for documentation/future async use.
                    // To truly break a sync hang, we'd need a separate thread or async BT nodes.
                    _behaviorTree?.Tick(_context);
                }
                catch (Exception ex)
                {
                    var trace = string.Join(" -> ", _context.ActiveNodes.Reverse());
                    _logger.LogError(ex, "Behavior tree crashed during tick {Tick}. Trace: {Trace}", _context.TickCount, trace);
                    EmergencyDiagnosticDump($"NodeCrash_{_context.ActiveNodes.Peek()}");
                    throw;
                }

                // STEP 5: Execute queued actions
                if (!_context.Actions.IsEmpty)
                {
                    var windowHandle = GetEveWindowHandle();
                    await _executor.ExecuteAllAsync(_context.Actions, windowHandle, ct);
                }

                // STEP 5b: Forward any diagnostic log messages from behavior tree nodes
                foreach (var msg in _context.DrainDiagMessages())
                    _logger.LogDebug("{DiagMsg}", msg);

                // STEP 6: Handle self-stop request (bot signals task complete)
                if (_context.StopRequested && !_isMonitorMode)
                {
                    _context.ConsumeStopRequest();
                    // Queue a swap to the no-op monitor bot — processed at top of next tick
                    _pendingSwap = new SwapRequest(_monitorBot, isMonitorMode: true);
                    _logger.LogInformation("Bot requested self-stop — returning to monitor mode");
                }

                // Record tick timestamp for TPM
                lock (_tpmLock)
                    _tickTimestampsMs.Enqueue(Environment.TickCount64);

                if (Recorder.IsRecording)
                {
                    Recorder.Record(new RecordedTick
                    {
                        TickCount = _context.TickCount,
                        Timestamp = DateTimeOffset.UtcNow,
                        FrameJson = LastRawJson,
                        BlackboardBefore = blackboardBefore,
                        Actions = _context.Actions.GetDescriptions()
                    });
                }

                OnTick?.Invoke(_context, _bot);

                // Wait before next tick
                await Task.Delay(_settings.TickIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in bot tick {Tick}", _context.TickCount);
                OnError?.Invoke(ex);
                await Task.Delay(_settings.TickIntervalMs * 2, ct); // Back off on error
            }
        }
    }

    /// <summary>
    /// Manually trigger an emergency diagnostic dump (screenshot + frame).
    /// </summary>
    public void TriggerEmergencyDump(string reason) => EmergencyDiagnosticDump(reason);

    private void EmergencyDiagnosticDump(string reason)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            Directory.CreateDirectory(_settings.LogDirectory);

            if (LastRawJson != null)
            {
                var jsonPath = Path.Combine(_settings.LogDirectory, $"emergency_{reason}_{timestamp}.json");
                File.WriteAllText(jsonPath, LastRawJson);
            }

            CaptureScreenshot(); // This uses internal naming, but it's enough
            _logger.LogInformation("Emergency diagnostic dump completed (Reason: {Reason})", reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to perform emergency dump: {Msg}", ex.Message);
        }
    }

    private nint GetEveWindowHandle()
    {
        if (_settings.Sanderling.ProcessId <= 0)
        {
            var client = EveProcessFinder.FindFirstClient();
            if (client != null)
                _settings.Sanderling.ProcessId = client.ProcessId;
        }

        var clients = EveProcessFinder.FindEveClients();
        var target = _settings.Sanderling.ProcessId > 0
            ? clients.FirstOrDefault(c => c.ProcessId == _settings.Sanderling.ProcessId)
            : clients.FirstOrDefault();

        return target?.MainWindowHandle ?? 0;
    }

    private async Task LogMemoryReading(string json, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_settings.LogDirectory);
            var filename = Path.Combine(_settings.LogDirectory,
                $"reading_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{_snapshotSequence}.json");
            await File.WriteAllTextAsync(filename, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log memory reading");
        }
    }

    private void CaptureScreenshot()
    {
        try
        {
            var hWnd = GetEveWindowHandle();
            if (hWnd == 0) return;

            if (!NativeMethods.GetWindowRect(hWnd, out var rect)) return;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdcDest = g.GetHdc();
                var hdcSrc = NativeMethods.GetWindowDC(hWnd);
                try
                {
                    NativeMethods.BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, 0, 0, NativeMethods.SRCCOPY);
                }
                finally
                {
                    g.ReleaseHdc(hdcDest);
                    NativeMethods.ReleaseDC(hWnd, hdcSrc);
                }
            }

            Directory.CreateDirectory(_settings.LogDirectory);
            var filename = Path.Combine(_settings.LogDirectory,
                $"screenshot_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{_snapshotSequence}.png");
            bmp.Save(filename, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to capture screenshot: {Msg}", ex.Message);
        }
    }

    private void SetState(BotRunnerState newState)
    {
        State = newState;
        OnStateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _reader.Dispose();
    }
}

/// <summary>
/// Minimal no-op bot used as the monitor-mode placeholder inside BotRunner.
/// Keeps EBot.Core self-contained (no dependency on EBot.ExampleBots).
/// </summary>
internal sealed class NoOpBot : IBot
{
    public string Name        => "Monitor";
    public string Description => "Monitoring — no bot active";
    public BotSettings GetDefaultSettings() => new();
    public IBehaviorNode BuildBehaviorTree() =>
        new EBot.Core.DecisionEngine.ActionNode("Idle", _ =>
            EBot.Core.DecisionEngine.NodeStatus.Failure);
}

/// <summary>
/// Current state of the bot runner.
/// </summary>
public enum BotRunnerState
{
    Idle,
    Running,
    Paused,
}
