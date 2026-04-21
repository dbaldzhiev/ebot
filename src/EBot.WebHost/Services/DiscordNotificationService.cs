using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EBot.Core.DecisionEngine;
using EBot.ExampleBots.MiningBot;

namespace EBot.WebHost.Services;

/// <summary>
/// Posts EBot events to a Discord channel via an incoming webhook.
/// All settings are persisted to data/discord-settings.json and survive restarts/rebuilds.
/// The default webhook URL is baked in so it works out of the box.
/// </summary>
public sealed class DiscordNotificationService : IHostedService, IDisposable
{
    // ─── Defaults ────────────────────────────────────────────────────────────

    public const string DefaultWebhookUrl =
        "https://discord.com/api/webhooks/1496255696386265139/6b0BfJtMTfXRgfr_MT7hMnZ2rE32tEOADoCc4kVdrnwtXeInopjYpMR7Spexti7cqeEQ";

    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "data", "discord-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ─── Dependencies ─────────────────────────────────────────────────────────

    private readonly BotOrchestrator _orchestrator;
    private readonly LogSink _logSink;
    private readonly HttpClient _http = new();

    // ─── Live settings (loaded from file, writable via API) ───────────────────

    private DiscordSettings _cfg = new();

    // ─── Runtime state ────────────────────────────────────────────────────────

    // Session-scoped mining trackers
    private int      _lastUnloadCycles;
    private double   _lastTotalM3;
    private DateTime _sessionStart;
    private DateTime _lastSummaryTime = DateTime.MinValue;

    // State-change detectors (previous-tick values)
    private string? _lastBotName;
    private int     _lastBeltIndex = -2;        // -2 = uninitialized
    private bool    _lastIsDocked  = false;
    private readonly Dictionary<int, bool> _knownDepletedBelts = new();

    // Dedup guard for emergency bursts
    private DateTime _lastEmergencyNotified = DateTime.MinValue;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public DiscordNotificationService(BotOrchestrator orchestrator, LogSink logSink)
    {
        _orchestrator = orchestrator;
        _logSink = logSink;
    }

    // ─── IHostedService ──────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        await LoadSettingsAsync();
        _orchestrator.TickCompleted += OnTick;
        _logSink.EntryAdded += OnLogEntry;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _orchestrator.TickCompleted -= OnTick;
        _logSink.EntryAdded -= OnLogEntry;
        return Task.CompletedTask;
    }

    // ─── Tick handler ─────────────────────────────────────────────────────────

    private void OnTick(BotContext ctx)
    {
        DetectBotStartStop();
        DetectDockUndock(ctx);

        if (_orchestrator.ActiveMiningBot is { } bot)
        {
            DetectUnloadCycle(ctx, bot);
            DetectBeltChange(ctx, bot);
            DetectBeltDepleted(bot);
        }

        MaybeSendPeriodicSummary(ctx);
    }

    // ─── Event detectors ──────────────────────────────────────────────────────

    private void DetectBotStartStop()
    {
        if (!_cfg.NotifyBotStartStop) return;

        var botName = _orchestrator.CurrentBotName;
        if (botName == _lastBotName) return;

        if (botName != null)
        {
            _sessionStart = DateTime.UtcNow;
            _lastUnloadCycles = 0;
            _lastTotalM3 = 0;
            _lastSummaryTime = DateTime.UtcNow;
            _lastBeltIndex = -2;
            _knownDepletedBelts.Clear();
            _ = PostAsync($":green_circle: **{botName}** started");
        }
        else if (_lastBotName != null)
        {
            _ = PostAsync($":stop_sign: **{_lastBotName}** stopped");
        }

        _lastBotName = botName;
    }

    private void DetectDockUndock(BotContext ctx)
    {
        if (!_cfg.NotifyDockUndock) return;

        var docked = ctx.GameState.IsDocked;
        if (docked == _lastIsDocked) { _lastIsDocked = docked; return; }

        if (_lastIsDocked != docked)
        {
            var home = _orchestrator.LastContext?.Blackboard.Get<string>("home_station") ?? "";
            if (docked)
                _ = PostAsync($":anchor: **Docked**{(home.Length > 0 ? $" @ {home}" : "")}");
            else
                _ = PostAsync($":rocket: **Undocked**{(home.Length > 0 ? $" from {home}" : "")}");
            _lastIsDocked = docked;
        }
    }

    private void DetectUnloadCycle(BotContext ctx, MiningBot bot)
    {
        if (!_cfg.NotifyUnloadCycle) return;

        var cycles  = ctx.Blackboard.Get<int>("unload_cycles");
        var totalM3 = ctx.Blackboard.Get<double>("total_unloaded_m3");
        if (cycles <= _lastUnloadCycles) return;

        var parts = new List<string> { $":package: **Unload #{cycles} complete**" };

        if (_cfg.CycleShowM3Gained)   parts.Add($"+{totalM3 - _lastTotalM3:F0} m³ this cycle");
        if (_cfg.CycleShowTotalM3)    parts.Add($"Total: **{totalM3:F0} m³**");
        if (_cfg.CycleShowCycleCount) parts.Add($"Cycle **#{cycles}**");
        if (_cfg.CycleShowRate && bot.SessionRateM3Hr > 0)
            parts.Add($"Rate: **{bot.SessionRateM3Hr:F0} m³/hr**");
        if (_cfg.CycleShowHomeStation)
        {
            var home = ctx.Blackboard.Get<string>("home_station") ?? "";
            if (home.Length > 0) parts.Add($"Station: {home}");
        }

        _ = PostAsync(string.Join(" | ", parts));
        _lastUnloadCycles = cycles;
        _lastTotalM3 = totalM3;
    }

    private void DetectBeltChange(BotContext ctx, MiningBot bot)
    {
        if (!_cfg.NotifyBeltChange) return;

        var beltIdx = ctx.Blackboard.Get<int>("last_belt_target", -1);
        if (beltIdx < 0 || beltIdx == _lastBeltIndex) { _lastBeltIndex = beltIdx; return; }

        // Only fire when transitioning to a real belt (not -1 reset)
        if (_lastBeltIndex >= 0 || beltIdx >= 0)
        {
            var name = bot.BeltNames.TryGetValue(beltIdx, out var n) ? n : $"Belt {beltIdx + 1}";
            _ = PostAsync($":milky_way: Warping to **{name}** (#{beltIdx + 1})");
        }

        _lastBeltIndex = beltIdx;
    }

    private void DetectBeltDepleted(MiningBot bot)
    {
        if (!_cfg.NotifyBeltDepleted) return;

        foreach (var (idx, depleted) in bot.BeltDepleted)
        {
            if (!depleted) continue;
            if (_knownDepletedBelts.TryGetValue(idx, out var was) && was) continue;

            var name = bot.BeltNames.TryGetValue(idx, out var n) ? n : $"Belt {idx + 1}";
            _ = PostAsync($":rock: **{name}** depleted — moving on");
            _knownDepletedBelts[idx] = true;
        }
    }

    private void MaybeSendPeriodicSummary(BotContext ctx)
    {
        if (!_cfg.NotifyPeriodicSummary) return;
        if (_lastSummaryTime == DateTime.MinValue) return;
        if (DateTime.UtcNow - _lastSummaryTime < TimeSpan.FromMinutes(_cfg.SummaryIntervalMinutes)) return;
        _lastSummaryTime = DateTime.UtcNow;

        var bot = _orchestrator.ActiveMiningBot;
        var bb  = ctx.Blackboard;

        var lines = new List<string> { ":bar_chart: **Mining Summary**" };

        if (_cfg.SummaryShowElapsed)
        {
            var elapsed = DateTime.UtcNow - _sessionStart;
            lines.Add($"Elapsed: **{elapsed:hh\\:mm}**");
        }
        if (_cfg.SummaryShowCycles)
            lines.Add($"Cycles: **{bb.Get<int>("unload_cycles")}**");
        if (_cfg.SummaryShowTotalM3)
            lines.Add($"Mined: **{bb.Get<double>("total_unloaded_m3"):F0} m³**");
        if (_cfg.SummaryShowRate && bot != null && bot.SessionRateM3Hr > 0)
            lines.Add($"Rate: **{bot.SessionRateM3Hr:F0} m³/hr**");
        if (_cfg.SummaryShowStatus)
        {
            var status = ctx.GameState.IsDocked   ? "docked"
                       : ctx.GameState.IsWarping   ? "warping"
                       : "in space";
            lines.Add($"Status: **{status}**");
        }
        if (_cfg.SummaryShowSystemName)
        {
            var sys = ctx.GameState.ParsedUI.InfoPanelContainer?
                .InfoPanelLocationInfo?.SystemName ?? "";
            if (sys.Length > 0) lines.Add($"System: {sys}");
        }
        if (_cfg.SummaryShowBeltInfo && bot != null)
        {
            var beltIdx = bb.Get<int>("last_belt_target", -1);
            if (beltIdx >= 0 && bot.BeltNames.TryGetValue(beltIdx, out var beltName))
                lines.Add($"Belt: {beltName}");

            var world = bb.Get<WorldState>("world");
            if (world?.Asteroids.Count > 0)
            {
                var active = world.Asteroids.Count(a => a.IsBeingMined);
                lines.Add($"Asteroids: {world.Asteroids.Count} ({active} mining)");
            }
        }

        _ = PostAsync(string.Join("\n", lines));
    }

    // ─── Log entry handler ────────────────────────────────────────────────────

    private void OnLogEntry(LogEntry entry)
    {
        if (_cfg.NotifyEmergencyStop && entry.Category == "EMERGENCY")
        {
            if (DateTime.UtcNow - _lastEmergencyNotified < TimeSpan.FromSeconds(5)) return;
            _lastEmergencyNotified = DateTime.UtcNow;
            var mention = string.IsNullOrWhiteSpace(_cfg.EmergencyMention)
                ? "" : $" {_cfg.EmergencyMention}";
            _ = PostAsync($":rotating_light: **EMERGENCY STOP** — {entry.Message}{mention}");
            return;
        }

        if (_cfg.NotifyShieldEscape &&
            entry.Level is "Warn" or "Error" &&
            entry.Message.Contains("shield", StringComparison.OrdinalIgnoreCase) &&
            entry.Message.Contains("escap", StringComparison.OrdinalIgnoreCase))
        {
            _ = PostAsync($":warning: **Shield alert** — {entry.Message}");
        }
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public DiscordSettings GetStatus() => _cfg;

    public async Task SaveSettingsAsync(DiscordSettings incoming)
    {
        _cfg = incoming;
        await PersistSettingsAsync();
    }

    public async Task<bool> SendTestAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WebhookUrl)) return false;
        try
        {
            var body = JsonSerializer.Serialize(
                new { content = ":ping_pong: EBot test — Discord notifications are working!" }, JsonOpts);
            using var req = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_cfg.WebhookUrl, req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ─── Settings persistence ─────────────────────────────────────────────────

    private async Task LoadSettingsAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                await PersistSettingsAsync(); // write defaults on first run
                return;
            }

            var json = await File.ReadAllTextAsync(SettingsPath);
            _cfg = JsonSerializer.Deserialize<DiscordSettings>(json, JsonOpts) ?? new DiscordSettings();
        }
        catch
        {
            _cfg = new DiscordSettings(); // corrupt file → use defaults
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(_cfg, JsonOpts));
        }
        catch { /* non-critical */ }
    }

    // ─── Webhook delivery ─────────────────────────────────────────────────────

    private async Task PostAsync(string content)
    {
        if (!_cfg.Enabled || string.IsNullOrWhiteSpace(_cfg.WebhookUrl)) return;

        var text = string.IsNullOrWhiteSpace(_cfg.MessagePrefix)
            ? content
            : $"{_cfg.MessagePrefix} {content}";

        try
        {
            var body = JsonSerializer.Serialize(new { content = text }, JsonOpts);
            using var req = new StringContent(body, Encoding.UTF8, "application/json");
            await _http.PostAsync(_cfg.WebhookUrl, req);
        }
        catch { /* never crash the bot */ }
    }

    public void Dispose() => _http.Dispose();
}
