using EBot.Core.Bot;
using EBot.Core.DecisionEngine;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;
using EBot.ExampleBots;
using EBot.ExampleBots.AutopilotBot;
using EBot.ExampleBots.MiningBot;
using EBot.WebHost.Hubs;
using EBot.WebHost.Services;
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
    private readonly ManualActionService _manualActions;

    // Single perpetual runner — created once, never destroyed during the app lifetime
    private BotRunner? _runner;

    // True when the runner is executing IdleBot (monitor-only)
    private bool _isMonitorMode = true;

    // The real bot currently swapped in (null when idling)
    private IBot? _activeBot;

    private BotContext? _lastContext;

    // Multi-tick hold cache: only one hold is visible at a time, so we cache across ticks
    private readonly Dictionary<string, HoldInfoDto> _holdCache = new();

    // ─── Available bots registry ────────────────────────────────────────────

    public static readonly IReadOnlyList<IBot> AvailableBots =
    [
        new MiningBot(),
        new TravelBot(),
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

    public bool SurvivalEnabled { get; private set; } = false;

    /// <summary>Fires after each tick completes. Subscribers must not block.</summary>
    public event Action<BotContext>? TickCompleted;

    /// <summary>Access to the underlying runner for diagnostic tools.</summary>
    public BotRunner? Runner => _runner;

    /// <summary>Manually trigger a diagnostic dump via the runner.</summary>
    public void TriggerEmergencyDump(string reason) => _runner?.TriggerEmergencyDump(reason);

    /// <summary>Returns the active MiningBot instance if one is running, otherwise null.</summary>
    public MiningBot? ActiveMiningBot
    {
        get
        {
            if (_activeBot is MiningBot mb) return mb;
            if (_activeBot is SurvivalWrappedBot swb && swb.Inner is MiningBot smb) return smb;
            return null;
        }
    }

    /// <summary>Toggle a belt's user-excluded status on the active mining bot.</summary>
    public void ToggleBeltExcluded(int idx) => ActiveMiningBot?.ToggleBeltExcluded(idx);

    /// <summary>Updates mining parameters on the active bot instance without stopping it.</summary>
    public void UpdateMiningSettings(int oreHoldPct, int shieldPct)
    {
        var bot = ActiveMiningBot;
        if (bot != null)
        {
            bot.OreHoldFullPercent = oreHoldPct;
            bot.ShieldEscapePercent = shieldPct;
            _logSink.Add("Info", "Orchestrator", $"Updated mining settings: OreHold={oreHoldPct}%, ShieldEscape={shieldPct}%");
        }
    }

    public void StartRecording()
    {
        _runner?.Recorder.Start();
        _logSink.Add("Info", "Orchestrator", "Session recording started");
    }

    public void StopRecording()
    {
        _runner?.Recorder.Stop();
        _logSink.Add("Info", "Orchestrator", "Session recording stopped");
    }

    public string? GetRecordingJson() => _runner?.Recorder.ExportJson();

    public BotOrchestrator(
        IHubContext<BotHub> hub,
        ILoggerFactory loggerFactory,
        LogSink logSink)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _logSink = logSink;
        _manualActions = new ManualActionService(logSink);

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

        // Quick non-invasive input environment summary (no cursor movement)
        _ = Task.Run(() =>
        {
            try
            {
                var eveClient = EveProcessFinder.FindFirstClient();
                var diag = InputDiagnostics.Run(eveClient?.MainWindowHandle ?? 0);
                _logSink.Add("Info", "Input",
                    $"Screen {diag.ScreenWidth}×{diag.ScreenHeight}  DPI={diag.SystemDpi}  Scale={diag.CoordScale:F2}");
                if (diag.EveValid)
                {
                    bool fullscreen = diag.EveClientOrigin.X == 0 && diag.EveClientOrigin.Y == 0
                                      && diag.EveClientSize.W == diag.ScreenWidth
                                      && diag.EveClientSize.H == diag.ScreenHeight;
                    _logSink.Add("Info", "Input",
                        $"EVE window: {(fullscreen ? "fullscreen" : "windowed")}  " +
                        $"origin=({diag.EveClientOrigin.X},{diag.EveClientOrigin.Y})  " +
                        $"size={diag.EveClientSize.W}×{diag.EveClientSize.H}");
                }
                else
                {
                    _logSink.Add("Warn", "Input", "EVE window not found — start EVE before running a bot");
                }
            }
            catch { /* non-critical */ }
        });

        await Task.CompletedTask;
    }

    // ─── Bot commands ───────────────────────────────────────────────────────

    /// <summary>
    /// Swaps in a named bot. Monitoring continues uninterrupted.
    /// For "Travel Bot", an optional destination may be supplied; when provided the bot
    /// sets the in-game route automatically before navigating.
    /// </summary>
    public async Task StartAsync(
        string botName,
        int pid = 0,
        string? exePath = null,
        int tickMs = 0,
        string? destination = null,
        MiningBotConfig? mining = null)
    {
        if (_runner == null)
            await EnsureMonitorAsync(exePath);

        if (!_isMonitorMode)
            throw new InvalidOperationException("A bot is already running. Stop it first.");

        IBot bot;
        if (botName.Equals("Travel Bot", StringComparison.OrdinalIgnoreCase))
        {
            bot = new TravelBot { Destination = string.IsNullOrWhiteSpace(destination) ? null : destination };
        }
        else if (botName.Equals("Mining Bot", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = mining ?? new MiningBotConfig();
            bot = new MiningBot
            {
                HomeStationOverride = string.IsNullOrWhiteSpace(cfg.DockingBookmark) ? null : cfg.DockingBookmark,
                OreHoldFullPercent  = cfg.OreHoldFull,
                ShieldEscapePercent = cfg.ShieldEscape,
            };
        }
        else
        {
            bot = AvailableBots.FirstOrDefault(b =>
                b.Name.Equals(botName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"Unknown bot: '{botName}'. Available: {string.Join(", ", AvailableBots.Select(b => b.Name))}");
        }

        _activeBot = bot;
        _isMonitorMode = false;

        IBot effectiveBot = SurvivalEnabled ? new SurvivalWrappedBot(bot) : bot;
        _runner!.SwapBot(effectiveBot, isMonitorMode: false);

        _logSink.Add("Info", "Orchestrator", $"Bot started: {bot.Name}");
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

    /// <summary>Executes exactly one tick while paused.</summary>
    public async Task StepAsync()
    {
        if (_isMonitorMode || _runner == null) return;
        if (State != BotRunnerState.Paused)
            throw new InvalidOperationException("Bot must be paused to step.");
        
        _runner.Step();
        _logSink.Add("Info", "Orchestrator", "Single tick executed (Step)");
        await Task.CompletedTask;
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

    // ─── Manual actions (undock, dock) — delegated to ManualActionService ────

    public Task OpenCargoAsync()          => _manualActions.OpenCargoAsync();
    public Task SwitchToHoldAsync(string h) => _manualActions.SwitchToHoldAsync(h);
    public Task ScanAllHoldsAsync()       => _manualActions.ScanAllHoldsAsync();
    public Task ClearDestinationAsync()   => _manualActions.ClearDestinationAsync();
    public Task UndockAsync()             => _manualActions.UndockAsync();
    public Task DockAsync()               => _manualActions.DockAsync();

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

        // Re-read screen metrics on each runner creation (resolution may change).
        InputSimulator.InvalidateScreenMetrics();

        var input    = new InputSimulator(_loggerFactory.CreateLogger<InputSimulator>());
        var executor = new ActionExecutor(input, _loggerFactory.CreateLogger<ActionExecutor>());

        _manualActions.Configure(input, executor, () => _lastContext);

        var runner = new BotRunner(bot, settings, reader, parser, input, executor,
            _loggerFactory.CreateLogger<BotRunner>());

        runner.OnTick += (ctx, bot) =>
        {
            _lastContext = ctx;
            // Update hold cache from whatever hold is currently visible in the inventory window
            var ui = ctx.GameState.ParsedUI;
            foreach (var w in ui.InventoryWindows)
            {
                if (w.CapacityGauge == null) continue;
                var key = w.HoldType != InventoryHoldType.Unknown
                          ? w.HoldType.ToString()
                          : (w.SubCaptionLabelText ?? "Unknown");
                var name = DtoMapper.FriendlyHoldName(w.SubCaptionLabelText, w.HoldType);
                _holdCache[key] = new HoldInfoDto(
                    name, key,
                    w.CapacityGauge.Used, w.CapacityGauge.Maximum,
                    (w.Items ?? []).Select(i => new CargoItemDto(i.Name, i.Quantity)).ToList());
            }

            var miningBot = bot as MiningBot;
            if (miningBot == null && bot is SurvivalWrappedBot swb && swb.Inner is MiningBot mb)
            {
                miningBot = mb;
            }

            _ = _hub.Clients.All.SendAsync("TickUpdate", DtoMapper.ToDto(ctx, _holdCache, TicksPerMinute, miningBot));
            TickCompleted?.Invoke(ctx);
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

    public BotStatusResponse GetStatus(int port)
    {
        var miningBot = _activeBot as MiningBot;
        if (miningBot == null && _activeBot is SurvivalWrappedBot swb && swb.Inner is MiningBot mb)
        {
            miningBot = mb;
        }

        return new BotStatusResponse(
            State.ToString(),
            CurrentBotName,
            _activeBot?.Description,
            _lastContext != null ? DtoMapper.ToDto(_lastContext, _holdCache, TicksPerMinute, miningBot) : null,
            port,
            SurvivalEnabled);
    }

    public BotStateDto? GetFullState() => 
        _lastContext != null ? DtoMapper.ToBotStateDto(_lastContext) : null;

    public object GetInventoryDebug()
    {
        var ui = _lastContext?.GameState.ParsedUI;
        if (ui == null) return new { error = "No game state available" };

        return new
        {
            tick = _lastContext!.TickCount,
            windowCount = ui.InventoryWindows.Count,
            windows = ui.InventoryWindows.Select(w => new
            {
                type = w.UINode.Node.PythonObjectTypeName,
                name = w.UINode.Node.GetDictString("_name"),
                title = w.SubCaptionLabelText,
                holdType = w.HoldType.ToString(),
                gauge = w.CapacityGauge == null ? null : new { w.CapacityGauge.Used, w.CapacityGauge.Maximum, w.CapacityGauge.FillPercent },
                itemCount = w.Items.Count,
                navEntryCount = w.NavEntries.Count,
                navEntries = w.NavEntries.Select(e => new { e.Label, type = e.HoldType.ToString(), e.IsSelected })
            })
        };
    }

    public object GetHoldCacheDebug()
    {
        return new
        {
            count = _holdCache.Count,
            entries = _holdCache.Select(kv => new
            {
                key = kv.Key,
                name = kv.Value.Name,
                type = kv.Value.HoldType,
                used = kv.Value.UsedM3,
                max = kv.Value.MaxM3,
                itemCount = kv.Value.Items.Count
            })
        };
    }

    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 50) =>
        _logSink.GetRecent(count);

    /// <summary>Returns a sample of the last raw JSON read (first N chars) for diagnostics.</summary>
    public string? GetLastRawJsonSample(int maxChars = 2000) =>
        _runner?.LastRawJson is { } j ? j[..Math.Min(maxChars, j.Length)] : null;

    /// <summary>Returns the full last raw JSON read (may be several MB). Used for frame saving.</summary>
    public string? GetLastRawJson() => _runner?.LastRawJson;

    public void Dispose() => _runner?.Dispose();

}
