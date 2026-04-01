using System.Collections.Concurrent;
using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;
using EBot.ExampleBots;
using EBot.ExampleBots.AutopilotBot;
using EBot.ExampleBots.MiningBot;
using EBot.WebHost.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EBot.WebHost;

/// <summary>
/// Central singleton that owns ONE perpetual BotRunner.
///
/// Architecture:
///   • The runner is created on first EnsureMonitorAsync() and NEVER torn down
///     while the application is alive. The read-parse-broadcast cycle runs
///     continuously, so the web UI always has live game state.
///
///   • When a bot is started, SwapBot() replaces the decision tree inside the
///     existing runner. The memory-read loop is never interrupted.
///
///   • When a bot is stopped, SwapBot() puts IdleBot back in. Monitoring
///     continues without any gap.
///
///   • IsMonitoring = true whenever IdleBot is the active bot.
///   • State (Running/Paused/Idle) reflects only whether a real bot is active.
/// </summary>
public sealed class BotOrchestrator : IDisposable
{
    private readonly IHubContext<BotHub> _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LogSink _logSink;

    // Single perpetual runner — created once, never destroyed during the app lifetime
    private BotRunner? _runner;

    // True when the runner is executing IdleBot (monitor-only)
    private bool _isMonitorMode = true;

    // The real bot currently swapped in (null when idling)
    private IBot? _activeBot;

    private BotContext? _lastContext;

    // ─── Available bots registry ────────────────────────────────────────────

    public static readonly IReadOnlyList<IBot> AvailableBots =
    [
        new MiningBot(),
        new AutopilotBot(),
    ];

    // ─── State ──────────────────────────────────────────────────────────────

    /// <summary>Bot runner state — Idle when no real bot is active.</summary>
    public BotRunnerState State => (_runner != null && !_isMonitorMode)
        ? _runner.State
        : BotRunnerState.Idle;

    public string? CurrentBotName => _isMonitorMode ? null : _activeBot?.Name;

    public BotContext? LastContext => _lastContext;

    public double TicksPerMinute => _runner?.TicksPerMinute ?? 0;

    public bool IsMonitoring => _isMonitorMode;

    public bool SurvivalEnabled { get; private set; } = true;

    public BotOrchestrator(
        IHubContext<BotHub> hub,
        ILoggerFactory loggerFactory,
        LogSink logSink)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _logSink = logSink;

        _logSink.EntryAdded += entry =>
            _ = _hub.Clients.All.SendAsync("LogEntry", entry);
    }

    // ─── Monitor ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the perpetual runner with IdleBot if it hasn't been started yet.
    /// No-op if the runner is already running.
    /// </summary>
    public async Task EnsureMonitorAsync(string? exePath = null)
    {
        if (_runner != null) return;   // already running — no-op

        _isMonitorMode = true;
        _activeBot = null;

        _runner = CreateRunner(new IdleBot(), exePath, isMonitorMode: true);

        _runner.OnBotSwapped += isMonitor =>
        {
            _isMonitorMode = isMonitor;
            if (!isMonitor)
                _ = _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Running.ToString());
            else
                _ = _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Idle.ToString());
        };

        _runner.Start(isMonitorMode: true);
        _logSink.Add("Info", "Orchestrator", "Monitor started — reading game state continuously");
        await Task.CompletedTask;
    }

    // ─── Bot commands ───────────────────────────────────────────────────────

    /// <summary>
    /// Swaps in a named bot. Monitoring continues uninterrupted.
    /// </summary>
    public async Task StartAsync(
        string botName,
        int pid = 0,
        string? exePath = null,
        int tickMs = 0)
    {
        if (_runner == null)
            await EnsureMonitorAsync(exePath);

        if (!_isMonitorMode)
            throw new InvalidOperationException("A bot is already running. Stop it first.");

        var bot = AvailableBots.FirstOrDefault(b =>
            b.Name.Equals(botName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unknown bot: '{botName}'. Available: {string.Join(", ", AvailableBots.Select(b => b.Name))}");

        _activeBot = bot;
        _isMonitorMode = false;

        IBot effectiveBot = SurvivalEnabled ? new SurvivalWrappedBot(bot) : bot;
        _runner!.SwapBot(effectiveBot, isMonitorMode: false);

        _logSink.Add("Info", "Orchestrator", $"Bot started: {bot.Name}");
        await _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Running.ToString());
    }

    /// <summary>
    /// Swaps in an AutopilotBot with the given destination pre-loaded.
    /// Monitoring continues uninterrupted.
    /// </summary>
    public async Task TravelToAsync(string destination, string? exePath = null)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Destination cannot be empty.", nameof(destination));

        if (_runner == null)
            await EnsureMonitorAsync(exePath);

        var bot = new AutopilotBot { Destination = destination };
        _activeBot = bot;
        _isMonitorMode = false;

        IBot effectiveBot = SurvivalEnabled ? new SurvivalWrappedBot(bot) : bot;
        _runner!.SwapBot(effectiveBot, isMonitorMode: false);

        _logSink.Add("Info", "Orchestrator", $"Autopilot started → {destination}");
        await _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Running.ToString());
    }

    /// <summary>
    /// Stops the active bot and returns to monitor mode. No gap in monitoring.
    /// </summary>
    public async Task StopAsync()
    {
        if (_isMonitorMode || _runner == null) return;

        _activeBot = null;
        _isMonitorMode = true;

        _runner.SwapBot(new IdleBot(), isMonitorMode: true);

        _logSink.Add("Info", "Orchestrator", "Bot stopped — back to monitor mode");
        await _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Idle.ToString());
    }

    /// <summary>Pause the running bot (no effect if monitoring).</summary>
    public async Task PauseAsync()
    {
        if (_isMonitorMode) return;
        _runner?.Pause();
        _logSink.Add("Info", "Orchestrator", "Bot paused");
        await _hub.Clients.All.SendAsync("StateChanged", State.ToString());
    }

    /// <summary>Resume a paused bot.</summary>
    public async Task ResumeAsync()
    {
        if (_isMonitorMode) return;
        _runner?.Resume();
        _logSink.Add("Info", "Orchestrator", "Bot resumed");
        await _hub.Clients.All.SendAsync("StateChanged", State.ToString());
    }

    /// <summary>
    /// Emergency hard-stop: swap in IdleBot immediately, release all held input,
    /// and notify all connected clients. Triggered by the Pause/Break global hotkey
    /// or the web UI Nuke button.
    /// </summary>
    public async Task EmergencyStopAsync()
    {
        _logSink.Add("Warn", "EMERGENCY", "Emergency stop triggered (Pause/Break)");

        // Swap bot out to IdleBot regardless of current state
        _activeBot = null;
        _isMonitorMode = true;
        _runner?.SwapBot(new IdleBot(), isMonitorMode: true);

        // Release any stuck mouse buttons / modifier keys immediately
        InputSimulator.ReleaseAllInput();

        _logSink.Add("Info", "EMERGENCY", "Input released — system returned to idle");

        await _hub.Clients.All.SendAsync("StateChanged", BotRunnerState.Idle.ToString());
        await _hub.Clients.All.SendAsync("EmergencyStop");
    }

    public void SetSurvivalMode(bool enabled)
    {
        SurvivalEnabled = enabled;
        _logSink.Add("Info", "Orchestrator", $"Survival mode {(enabled ? "enabled" : "disabled")}");
        _ = _hub.Clients.All.SendAsync("SurvivalChanged", enabled);
    }

    // ─── Internal ───────────────────────────────────────────────────────────

    private BotRunner CreateRunner(IBot bot, string? exePath, bool isMonitorMode)
    {
        var settings = bot.GetDefaultSettings();

        var client = EveProcessFinder.FindFirstClient();
        var pid    = client?.ProcessId ?? 0;

        if (client != null)
            settings.Sanderling.ProcessId = pid;

        // Prefer direct in-process reading (no file I/O, no child process).
        // Fall back to SanderlingReader (HTTP or CLI) if needed.
        IEveMemoryReader reader;
        try
        {
            reader = new DirectMemoryReader(_loggerFactory.CreateLogger<DirectMemoryReader>(), pid);
            _logSink.Add("Info", "Orchestrator", "Using direct in-process memory reader (no file I/O)");
        }
        catch (Exception ex)
        {
            _logSink.Add("Warn", "Orchestrator",
                $"Direct reader unavailable ({ex.Message}), falling back to SanderlingReader");
            if (!string.IsNullOrEmpty(exePath))
                settings.Sanderling.ExecutablePath = exePath;
            reader = new SanderlingReader(settings.Sanderling,
                         _loggerFactory.CreateLogger<SanderlingReader>());
        }

        var parser   = new UITreeParser(_loggerFactory.CreateLogger<UITreeParser>());
        var input    = new InputSimulator(_loggerFactory.CreateLogger<InputSimulator>());
        var executor = new ActionExecutor(input, _loggerFactory.CreateLogger<ActionExecutor>());

        var runner = new BotRunner(bot, settings, reader, parser, input, executor,
            _loggerFactory.CreateLogger<BotRunner>());

        runner.OnTick += ctx =>
        {
            _lastContext = ctx;
            _ = _hub.Clients.All.SendAsync("TickUpdate", DtoMapper.ToDto(ctx));
        };

        runner.OnError += ex =>
            _logSink.Add("Error", "BotRunner", ex.Message);

        executor.ActionPerformed += desc =>
            _ = _hub.Clients.All.SendAsync("ActionLog", new
            {
                time = DateTimeOffset.UtcNow,
                description = desc,
            });

        return runner;
    }

    // ─── Query ──────────────────────────────────────────────────────────────

    public BotStatusResponse GetStatus(int port) => new(
        State.ToString(),
        CurrentBotName,
        _lastContext != null ? DtoMapper.ToDto(_lastContext) : null,
        port,
        SurvivalEnabled);

    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 50) =>
        _logSink.GetRecent(count);

    /// <summary>Returns a sample of the last raw JSON read (first N chars) for diagnostics.</summary>
    public string? GetLastRawJsonSample(int maxChars = 2000) =>
        _runner?.LastRawJson is { } j ? j[..Math.Min(maxChars, j.Length)] : null;

    public void Dispose() => _runner?.Dispose();

    // ─── Survival wrapper ────────────────────────────────────────────────────

    private sealed class SurvivalWrappedBot(IBot inner) : IBot
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public BotSettings GetDefaultSettings() => inner.GetDefaultSettings();
        public void OnStart(BotContext ctx) => inner.OnStart(ctx);
        public void OnStop(BotContext ctx) => inner.OnStop(ctx);
        public IBehaviorNode BuildBehaviorTree() =>
            SurvivalNodes.Wrap(inner.BuildBehaviorTree());
    }
}
