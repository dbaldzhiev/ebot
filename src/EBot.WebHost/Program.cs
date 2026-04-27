using System.Text.Json;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;
using EBot.WebHost;
using EBot.WebHost.Hubs;
using EBot.WebHost.Mcp;
using EBot.WebHost.Services;
using EBot.WebHost.Terminal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

// ─── DPI Awareness ─────────────────────────────────────────────────────────
// Must be called before any Win32 calls so SetCursorPos uses physical pixels,
// matching EVE Online's _displayX/_displayY coordinate space.
InputSimulator.SetDpiAwareness();

// ─── Configuration ─────────────────────────────────────────────────────────

var port = args.Select((a, i) => (a, i))
    .FirstOrDefault(x => x.a == "--port").i is var pi && pi > 0 && pi < args.Length - 1
    && int.TryParse(args[pi + 1], out var p) ? p : 5000;

foreach (var arg in args)
{
    if (arg.StartsWith("--port=") && int.TryParse(arg[7..], out var pp))
        port = pp;
}

// ─── Builder ───────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Suppress ASP.NET Core banner and status messages
builder.WebHost.SuppressStatusMessages(true);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Logging: clear default providers; use our in-memory sink
var logSink = new LogSink();
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new LogSinkProvider(logSink));
builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);

// Core services
builder.Services.AddSingleton(logSink);
builder.Services.AddSingleton<BotOrchestrator>();
builder.Services.AddSingleton<ModuleTypeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModuleTypeService>());

// ESI HTTP client — base URL + sensible timeouts
builder.Services.AddHttpClient("esi", c =>
{
    c.BaseAddress = new Uri("https://esi.evetech.net/latest/");
    c.DefaultRequestHeaders.Add("User-Agent", "EBot/1.0 (github.com/ebot)");
    c.Timeout = TimeSpan.FromSeconds(10);
});

// AI chat backend — select via EBOT_AI_BACKEND env var ("anthropic" | "ollama")
var aiBackend = Environment.GetEnvironmentVariable("EBOT_AI_BACKEND") ?? "ollama";
if (aiBackend.Equals("ollama", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IChatService, OllamaChatService>();
else
    builder.Services.AddSingleton<IChatService, ChatService>();

// SignalR
builder.Services.AddSignalR();

// MCP server (SSE transport) — AI agents connect at /mcp
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<EveBotMcpTools>();

// CORS: allow any local client
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()));

// Terminal dashboard (hosted background service)
builder.Services.AddSingleton(sp =>
    new TerminalDashboard(
        sp.GetRequiredService<BotOrchestrator>(),
        sp.GetRequiredService<LogSink>(),
        port));
builder.Services.AddHostedService(sp => sp.GetRequiredService<TerminalDashboard>());

// Global Pause/Break hotkey — emergency stop even when EVE has keyboard focus
builder.Services.AddHostedService<GlobalHotKeyService>();

// Session file logger — writes all log entries to ebot/logs/session_YYYYMMDD_HHmmss.log
builder.Services.AddHostedService<SessionFileLogger>();

// SDE (Static Data Export) — downloads CCP's station data once per patch
builder.Services.AddSingleton<EBot.WebHost.Services.SdeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EBot.WebHost.Services.SdeService>());

// Discord webhook notifications (opt-in via DISCORD_WEBHOOK_URL env var)
builder.Services.AddSingleton<EBot.WebHost.Services.DiscordNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EBot.WebHost.Services.DiscordNotificationService>());

// ─── App ───────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// SignalR hub
app.MapHub<BotHub>("/botHub");

// MCP endpoint
app.MapMcp("/mcp");

// ─── REST API ──────────────────────────────────────────────────────────────

var api = app.MapGroup("/api");

// GET /api/status
api.MapGet("/status", (BotOrchestrator o) => Results.Ok(o.GetStatus(port)));

// GET /api/bots
api.MapGet("/bots", () => Results.Ok(
    BotOrchestrator.AvailableBots.Select(b => new { name = b.Name, description = b.Description })));

// GET /api/processes
api.MapGet("/processes", () =>
{
    var clients = EveProcessFinder.FindEveClients();
    return Results.Ok(clients.Select(c => new
    {
        pid = c.ProcessId,
        name = c.ProcessName,
        window_title = c.MainWindowTitle,
    }));
});

// POST /api/start  { "botName": "Mining Bot" }
api.MapPost("/start", async ([FromBody] StartRequest req, BotOrchestrator o) =>
{
    try
    {
        await o.StartAsync(req.BotName, req.Pid, req.ExePath, req.TickMs, req.Destination, req.Mining);
        return Results.Ok(new { success = true, message = $"Bot '{req.BotName}' started." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// POST /api/stop
api.MapPost("/stop", async (BotOrchestrator o) =>
{
    await o.StopAsync();
    return Results.Ok(new { success = true });
});

// POST /api/emergency-stop  — hard-stop: bot off + input released (same as Pause/Break key)
api.MapPost("/emergency-stop", async (BotOrchestrator o) =>
{
    await o.EmergencyStopAsync();
    return Results.Ok(new { success = true });
});

// POST /api/save-frame  — saves the current raw UI JSON to ebot/logs/frames/
api.MapPost("/save-frame", (BotOrchestrator orch) =>
{
    var json = orch.GetLastRawJson();
    if (json == null)
        return Results.BadRequest(new { success = false, message = "No frame available yet — wait for the first tick" });

    var framesDir = Path.Combine(SessionFileLogger.GetLogsDirectory(), "frames");
    Directory.CreateDirectory(framesDir);

    var ts       = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff");
    var fileName = $"frame_{ts}.json";
    var filePath = Path.Combine(framesDir, fileName);
    File.WriteAllText(filePath, json);

    return Results.Ok(new { success = true, file = filePath, size_bytes = json.Length, file_name = fileName });
});

// POST /api/kill  — gracefully shut down the EBot server process
api.MapPost("/kill", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(300); // allow response to be sent first
        lifetime.StopApplication();
    });
    return Results.Ok(new { success = true, message = "Server shutting down…" });
});

// POST /api/pause
api.MapPost("/pause", async (BotOrchestrator o) =>
{
    await o.PauseAsync();
    return Results.Ok(new { success = true });
});

// POST /api/resume
api.MapPost("/resume", async (BotOrchestrator o) =>
{
    await o.ResumeAsync();
    return Results.Ok(new { success = true });
});

// POST /api/debug/step  — executes a single tick if the bot is paused
api.MapPost("/debug/step", async (BotOrchestrator o) =>
{
    try
    {
        await o.StepAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// ─── Session Recording API ────────────────────────────────────────────────

// POST /api/debug/record/start
api.MapPost("/debug/record/start", (BotOrchestrator o) =>
{
    o.StartRecording();
    return Results.Ok(new { success = true });
});

// POST /api/debug/record/stop
api.MapPost("/debug/record/stop", (BotOrchestrator o) =>
{
    o.StopRecording();
    return Results.Ok(new { success = true });
});

// GET /api/debug/record/download
api.MapGet("/debug/record/download", (BotOrchestrator o) =>
{
    var json = o.GetRecordingJson();
    if (string.IsNullOrEmpty(json))
        return Results.BadRequest(new { success = false, message = "No recording available" });

    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    return Results.File(bytes, "application/json", $"ebot_session_{DateTime.Now:yyyyMMdd_HHmmss}.json");
});

// POST /api/survival  { "enabled": true }
api.MapPost("/survival", ([FromBody] SurvivalRequest req, BotOrchestrator o) =>
{
    o.SetSurvivalMode(req.Enabled);
    return Results.Ok(new { success = true, survival_enabled = o.SurvivalEnabled });
});

// POST /api/mining/settings  — update ore hold % and shield % at runtime
api.MapPost("/mining/settings", ([FromBody] UpdateMiningSettingsRequest req, BotOrchestrator o) =>
{
    o.UpdateMiningSettings(req);
    return Results.Ok(new { success = true });
});

// POST /api/travel/settings  — update travel bot options at runtime (no restart needed)
api.MapPost("/travel/settings", ([FromBody] UpdateTravelSettingsRequest req, BotOrchestrator o) =>
{
    o.UpdateTravelSettings(req.AbMwdTrick, req.HardenMode);
    return Results.Ok(new { success = true });
});

// GET /api/log
api.MapGet("/log", (BotOrchestrator o, [FromQuery] int count = 50) =>
    Results.Ok(o.GetRecentLogs(Math.Clamp(count, 1, 200))));

// GET /api/debug/state  — full JSON dump of bot blackboard, stack, and actions
api.MapGet("/debug/state", (BotOrchestrator o) =>
{
    var state = o.GetFullState();
    return state != null ? Results.Ok(state) : Results.NotFound("No bot state available (ensure bot is running)");
});

// GET /api/debug/inventory  — detailed breakdown of detected inventory windows
api.MapGet("/debug/inventory", (BotOrchestrator o) => Results.Ok(o.GetInventoryDebug()));

// GET /api/debug/hold-cache  — dump of the orchestrator's hold cache
api.MapGet("/debug/hold-cache", (BotOrchestrator o) => Results.Ok(o.GetHoldCacheDebug()));

// POST /api/debug/scan-holds — force a full scan of all holds (Alt+C -> cycle entries)
api.MapPost("/debug/scan-holds", async (BotOrchestrator o) =>
{
    try
    {
        await o.ScanAllHoldsAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// GET /api/ai-info  — returns active AI backend details for the UI
api.MapGet("/ai-info", () =>
{
    var backend  = Environment.GetEnvironmentVariable("EBOT_AI_BACKEND") ?? "ollama";
    var isOllama = backend.Equals("ollama", StringComparison.OrdinalIgnoreCase);
    var model    = isOllama ? OllamaChatService.CurrentModel : "claude-opus-4-6";
    var url      = isOllama ? OllamaChatService.BaseUrl : (string?)null;
    return Results.Ok(new { backend = isOllama ? "ollama" : "anthropic", model, url });
});

// Shared HttpClient for Ollama proxy calls
var ollamaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

// GET /api/ollama/models  — lists models available on the Ollama server
api.MapGet("/ollama/models", async () =>
{
    try
    {
        var json = await ollamaHttp.GetStringAsync(
            $"{OllamaChatService.BaseUrl}/api/tags");
        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .Where(n => n != null)
            .ToList();
        return Results.Ok(new { models, current = OllamaChatService.CurrentModel });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { models = Array.Empty<string>(), current = OllamaChatService.CurrentModel, error = ex.Message });
    }
});

// POST /api/ollama/model  { "model": "llama3.2" }
api.MapPost("/ollama/model", ([FromBody] OllamaModelRequest req, IChatService chat) =>
{
    if (string.IsNullOrWhiteSpace(req.Model))
        return Results.BadRequest(new { error = "model is required" });
    if (chat is OllamaChatService)
        OllamaChatService.SetModel(req.Model);
    return Results.Ok(new { model = OllamaChatService.CurrentModel });
});

// GET /api/dpi  — returns detected system DPI and current coordinate scale
api.MapGet("/dpi", () =>
{
    var systemDpi = InputSimulator.GetSystemDpi();
    return Results.Ok(new
    {
        system_dpi = systemDpi,
        scale_percent = (int)Math.Round(systemDpi / 96.0 * 100),
        coordinate_scale = InputSimulator.CoordinateScale,
    });
});

// POST /api/dpi/scale  { "scale": 1.0 }
api.MapPost("/dpi/scale", ([FromBody] DpiScaleRequest req) =>
{
    if (req.Scale is < 0.1f or > 4.0f)
        return Results.BadRequest(new { error = "scale must be between 0.1 and 4.0" });
    InputSimulator.CoordinateScale = req.Scale;
    return Results.Ok(new { coordinate_scale = InputSimulator.CoordinateScale });
});

// GET /api/debug/input  — verifies SendInput works; reports screen/window/cursor state
api.MapGet("/debug/input", (BotOrchestrator orch) =>
{
    var client = EveProcessFinder.FindFirstClient();
    var handle = client?.MainWindowHandle ?? 0;
    var report = InputDiagnostics.Run(handle);
    return Results.Ok(new { report = report.Summary(), passed = report.MoveOk });
});

// POST /api/open-cargo  — opens ship inventory (Alt+C)
api.MapPost("/open-cargo", async (BotOrchestrator o) =>
{
    try { await o.OpenCargoAsync(); return Results.Ok(new { success = true }); }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// POST /api/scan-holds  — cycles through all inventory hold nav entries to populate hold cache
api.MapPost("/scan-holds", async (BotOrchestrator o) =>
{
    try { await o.ScanAllHoldsAsync(); return Results.Ok(new { success = true }); }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// POST /api/switch-hold  — clicks a specific hold in the inventory nav panel
api.MapPost("/switch-hold", async (SwitchHoldRequest req, BotOrchestrator o) =>
{
    try { await o.SwitchToHoldAsync(req.HoldType); return Results.Ok(new { success = true }); }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// POST /api/clear-destination  — right-click last route marker → Remove Waypoint
api.MapPost("/clear-destination", async (BotOrchestrator o) =>
{
    try
    {
        await o.ClearDestinationAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// POST /api/undock  — click the undock button
api.MapPost("/undock", async (BotOrchestrator o) =>
{
    try
    {
        await o.UndockAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// POST /api/dock  — right-click nearest station → Dock
api.MapPost("/dock", async (BotOrchestrator o) =>
{
    try
    {
        await o.DockAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

// ─── SDE station search (local, no external API calls) ────────────────────

// GET /api/stations/search?q=jita  — instant local search using cached SDE
api.MapGet("/stations/search", ([FromQuery] string q, EBot.WebHost.Services.SdeService sde) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length < 3)
        return Results.BadRequest(new { error = "Query must be at least 3 characters" });
    if (!sde.IsReady)
        return Results.Ok(new { results = Array.Empty<object>(), status = sde.StatusMessage, ready = false });

    var hits = sde.Search(q, limit: 10);
    return Results.Ok(new { results = hits, status = sde.StatusMessage, ready = true });
});

// GET /api/stations/sde-status  — SDE download progress and readiness
api.MapGet("/stations/sde-status", (EBot.WebHost.Services.SdeService sde) =>
    Results.Ok(new
    {
        ready       = sde.IsReady,
        downloading = sde.IsDownloading,
        progress    = sde.DownloadProgress,
        status      = sde.StatusMessage,
    }));

// POST /api/stations/sde-refresh  — delete local checksum and force re-download
api.MapPost("/stations/sde-refresh", (EBot.WebHost.Services.SdeService sde) =>
{
    _ = Task.Run(() => sde.ForceRefreshAsync());
    return Results.Ok(new { success = true, message = "SDE re-download started" });
});

// GET /api/stations/sde-debug  — database info for diagnostics
api.MapGet("/stations/sde-debug", (EBot.WebHost.Services.SdeService sde) =>
{
    var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "eve_sde.db");
    return Results.Ok(new
    {
        status   = sde.StatusMessage,
        ready    = sde.IsReady,
        dbExists = File.Exists(dbPath),
        dbBytes  = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
        dbPath,
        hint     = "Run: python src/EBot.WebHost/setup_sde.py",
    });
});

// ─── Saved destinations ────────────────────────────────────────────────────

var destPath = Path.Combine(AppContext.BaseDirectory, "data", "destinations.json");

List<TravelDestination> LoadDestinations()
{
    try
    {
        if (File.Exists(destPath))
            return JsonSerializer.Deserialize<List<TravelDestination>>(
                File.ReadAllText(destPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
    catch { }
    return [];
}

void SaveDestinations(List<TravelDestination> dests)
{
    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
    File.WriteAllText(destPath, JsonSerializer.Serialize(dests,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

// GET /api/destinations
api.MapGet("/destinations", () => Results.Ok(LoadDestinations()));

// POST /api/destinations  { id, name, systemName, typeId, iconUrl }
api.MapPost("/destinations", ([FromBody] TravelDestination dest) =>
{
    if (string.IsNullOrWhiteSpace(dest.Id) || string.IsNullOrWhiteSpace(dest.Name))
        return Results.BadRequest(new { error = "id and name are required" });
    var list = LoadDestinations();
    list.RemoveAll(d => d.Id == dest.Id);
    list.Add(dest);
    SaveDestinations(list);
    return Results.Ok(list);
});

// DELETE /api/destinations/{id}
api.MapDelete("/destinations/{id}", (string id) =>
{
    var list = LoadDestinations();
    list.RemoveAll(d => d.Id == id);
    SaveDestinations(list);
    return Results.Ok(list);
});

// GET /api/debug/survey  — survey ISK cache + scored asteroid list for live diagnostics
api.MapGet("/debug/survey", (BotOrchestrator o) =>
{
    var bb  = o.LastContext?.Blackboard;
    var world = bb?.Get<EBot.ExampleBots.MiningBot.WorldState>("world");
    var cache = bb?.Get<Dictionary<string, double>>("survey_isk_cache");
    var phase = bb?.Get<string>("survey_phase") ?? "";
    var lastBelt = bb?.Get<int>("survey_last_belt", -1) ?? -1;
    var lastTick = bb?.Get<long>("survey_scan_tick", -1L) ?? -1L;

    var asteroids = (world?.Asteroids ?? [])
        .OrderByDescending(a => a.Score)
        .Select(a => new
        {
            name       = a.Name,
            distance   = a.DistanceText,
            isk_per_m3 = a.ValuePerM3.HasValue ? Math.Round(a.ValuePerM3.Value, 1) : (double?)null,
            source     = a.ValuePerM3.HasValue ? "survey" : "fallback",
            score      = Math.Round(a.Score, 1),
            locked     = a.IsLocked,
            pending    = a.IsLockPending,
            mining     = a.IsBeingMined,
        });

    return Results.Ok(new
    {
        survey_phase    = phase,
        last_belt       = lastBelt,
        last_scan_tick  = lastTick,
        cache_entries   = cache?.Count ?? 0,
        cache           = cache ?? [],
        laser_range_m   = world?.LaserRangeM ?? 0,
        asteroids,
    });
});

// GET /api/mining-stats  — current session mining statistics from the blackboard
api.MapGet("/mining-stats", (BotOrchestrator o) =>
{
    var bb = o.LastContext?.Blackboard;
    return Results.Ok(new
    {
        total_m3     = bb?.Get<double>("total_unloaded_m3") ?? 0,
        cycles       = bb?.Get<int>("unload_cycles")        ?? 0,
        phase        = bb?.Get<string>("unload_phase")      ?? "",
        return_phase = bb?.Get<string>("return_phase")      ?? "",
        needs_unload = bb?.Get<bool>("needs_unload")        ?? false,
        home_station = bb?.Get<string>("home_station")      ?? "",
    });
});

// GET /api/mining/belts  — returns the discovered belt registry for the active mining bot
api.MapGet("/mining/belts", (BotOrchestrator o) =>
{
    var bot = o.ActiveMiningBot;
    if (bot == null)
        return Results.Ok(new { count = 0, belts = Array.Empty<object>() });

    int count = bot.BeltCount;
    var belts = Enumerable.Range(0, Math.Max(count, bot.BeltNames.Count))
        .Select(i => (object)new
        {
            index    = i,
            name     = bot.BeltNames.TryGetValue(i, out var n) ? n : $"Belt {i + 1}",
            depleted = bot.BeltDepleted.TryGetValue(i, out var d) && d,
            excluded = bot.BeltExcluded.TryGetValue(i, out var e) && e,
        })
        .ToArray();
    return Results.Ok(new { count, belts });
});

// POST /api/mining/belts/{idx}/toggle  — toggle user-excluded status for a belt
api.MapPost("/mining/belts/{idx:int}/toggle", (int idx, BotOrchestrator o) =>
{
    o.ToggleBeltExcluded(idx);
    return Results.Ok(new { toggled = idx });
});

// GET /api/debug/modules  — dump module slots with parsed names and raw hint text
api.MapGet("/debug/modules", (BotOrchestrator orch) =>
{
    var ui = orch.LastContext?.GameState.ParsedUI;
    if (ui?.ShipUI == null) return Results.Ok("Ship UI not visible.");

    var rows = ui.ShipUI.ModuleButtonsRows;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Total module slots: {ui.ShipUI.ModuleButtons.Count}  (high={rows.Top.Count} mid={rows.Middle.Count} low={rows.Bottom.Count})");
    foreach (var (m, i) in ui.ShipUI.ModuleButtons.Select((m, i) => (m, i)))
    {
        var rawHint = EBot.Core.GameState.EveTextUtil.StripTags(m.UINode.Node.GetDictString("_hint"))
                   ?? EBot.Core.GameState.EveTextUtil.StripTags(m.SlotNode.Node.GetDictString("_hint"))
                   ?? "(no hint)";
        var slotName = m.UINode.Node.GetDictString("_name") ?? m.SlotNode.Node.GetDictString("_name") ?? "?";
        var row = rows.Top.Contains(m) ? "HIGH" : rows.Middle.Contains(m) ? "MID" : "LOW";
        sb.AppendLine($"[{i}] {row,-4} name=\"{m.Name ?? "(null)"}\"  active={m.IsActive} busy={m.IsBusy} overload={m.IsOverloaded}");
        sb.AppendLine($"       hint=\"{rawHint}\"  slotName={slotName}");
    }
    return Results.Ok(sb.ToString());
});

// GET /api/debug/travel  — live diagnostics for AB/MWD trick and Harden Mode
api.MapGet("/debug/travel", (BotOrchestrator orch, ModuleTypeService mtSvc) =>
{
    var ctx  = orch.LastContext;
    var bot  = orch.ActiveTravelBot;
    var ui   = ctx?.GameState.ParsedUI;

    EBot.Core.GameState.ITypeNameResolver.TypeEntry? Resolved(EBot.Core.GameState.ShipUIModuleButton m) =>
        m.TypeId.HasValue ? mtSvc.Resolve(m.TypeId.Value) : null;

    bool IsShieldHardener(EBot.Core.GameState.ShipUIModuleButton m)
    {
        if (m.Name != null && (
                m.Name.Contains("Shield Hardener", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Multispectrum",   StringComparison.OrdinalIgnoreCase)))
            return true;
        var e = Resolved(m);
        return e != null && (
            e.GroupName.Contains("Shield Hardener", StringComparison.OrdinalIgnoreCase) ||
            e.GroupName.Contains("Multispectrum",   StringComparison.OrdinalIgnoreCase));
    }

    bool IsPropModule(EBot.Core.GameState.ShipUIModuleButton m)
    {
        if (m.Name != null && (
                m.Name.Contains("Afterburner", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Microwarp",   StringComparison.OrdinalIgnoreCase)))
            return true;
        var e = Resolved(m);
        return e != null && (
            e.GroupName.Contains("Afterburner",    StringComparison.OrdinalIgnoreCase) ||
            e.GroupName.Contains("Microwarpdrive", StringComparison.OrdinalIgnoreCase));
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"=== Travel Bot Settings ===");
    sb.AppendLine($"  bot active    : {bot != null}");
    sb.AppendLine($"  AbMwdTrick    : {bot?.AbMwdTrick}");
    sb.AppendLine($"  HardenMode    : {bot?.HardenMode}");
    sb.AppendLine($"  TypeCache     : {mtSvc.CachedTypeCount} types / {mtSvc.CachedGroupCount} groups resolved");
    sb.AppendLine($"  overheat_queue: {ctx?.Blackboard.Get<System.Collections.Generic.List<int>>("overheat_queue")?.Count ?? 0} items");
    sb.AppendLine($"  overheat_cd   : ready={ctx?.Blackboard.IsCooldownReady("overheat_cooldown")}");
    sb.AppendLine();

    if (ui?.ShipUI == null)
    {
        sb.AppendLine("ShipUI not visible — undocked or UI not parsed.");
        return Results.Ok(sb.ToString());
    }

    bool attacked = ui.OverviewWindows.Any(w => w.Entries.Any(e => e.IsAttackingMe));
    sb.AppendLine($"=== Combat State ===");
    sb.AppendLine($"  IsBeingAttacked : {attacked}");
    sb.AppendLine();

    var rows = ui.ShipUI.ModuleButtonsRows;
    sb.AppendLine($"=== All Module Buttons ({ui.ShipUI.ModuleButtons.Count} total: high={rows.Top.Count} mid={rows.Middle.Count} low={rows.Bottom.Count}) ===");
    foreach (var (m, i) in ui.ShipUI.ModuleButtons.Select((m, i) => (m, i)))
    {
        var rawHint = EBot.Core.GameState.EveTextUtil.StripTags(m.UINode.Node.GetDictString("_hint"))
                   ?? EBot.Core.GameState.EveTextUtil.StripTags(m.SlotNode.Node.GetDictString("_hint"))
                   ?? "(no hint)";
        var resolved  = Resolved(m);
        var esiName   = resolved != null ? $"{resolved.TypeName} (grp: {resolved.GroupName}, cat: {resolved.CategoryId})" : "(not yet resolved)";
        var rowLabel  = rows.Top.Contains(m) ? "HIGH" : rows.Middle.Contains(m) ? "MID" : "LOW";
        var propTag   = IsPropModule(m)      ? " [PROP]"    : "";
        var hardenTag = IsShieldHardener(m)  ? " [HARDENER]": "";
        sb.AppendLine($"  [{i}] {rowLabel,-4} typeId={m.TypeId?.ToString() ?? "?"}{propTag}{hardenTag}");
        sb.AppendLine($"        hint-name : \"{m.Name ?? "(null)"}\"");
        sb.AppendLine($"        esi-name  : {esiName}");
        sb.AppendLine($"        hint-raw  : \"{rawHint}\"");
        sb.AppendLine($"        active={m.IsActive} busy={m.IsBusy} overload={m.IsOverloaded}");
    }

    sb.AppendLine();
    sb.AppendLine($"=== AB/MWD Trick: would click ===");
    var allMods = ui.ShipUI.ModuleButtons.ToList();
    var propMod = allMods.FirstOrDefault(IsPropModule);
    sb.AppendLine(propMod != null
        ? $"  Module [{allMods.IndexOf(propMod)}] typeId={propMod.TypeId} \"{propMod.Name ?? Resolved(propMod)?.TypeName ?? "(unknown)"}\""
        : "  (none found — keywords 'Afterburner'/'Microwarp' and ESI group not matched)");

    sb.AppendLine();
    sb.AppendLine($"=== Harden Mode: inactive hardeners to click ===");
    var hardeners = allMods.Where(m => IsShieldHardener(m) && m.IsActive != true && !m.IsBusy).ToList();
    if (hardeners.Count == 0) sb.AppendLine("  (none — either all active/unknown, no hardeners found, or not yet resolved)");
    else foreach (var m in hardeners) sb.AppendLine($"  Module [{allMods.IndexOf(m)}] typeId={m.TypeId} \"{m.Name ?? Resolved(m)?.TypeName ?? "(unknown)"}\"");

    sb.AppendLine();
    sb.AppendLine($"=== Mid-rack for overheat ===");
    foreach (var (m, i) in rows.Middle.Select((m, i) => (m, i)))
        sb.AppendLine($"  [mid-{i}] typeId={m.TypeId} \"{m.Name ?? Resolved(m)?.TypeName ?? "(null)"}\"  active={m.IsActive} overloaded={m.IsOverloaded}");
    if (rows.Middle.Count == 0) sb.AppendLine("  (empty — mid-rack classification may have failed; check /api/debug/modules)");

    return Results.Ok(sb.ToString());
});

// GET /api/debug/reader  — diagnostic using the already-running runner's last JSON
api.MapGet("/debug/reader", (BotOrchestrator orch, ILoggerFactory lf) =>
{
    var result = new System.Text.StringBuilder();

    // 1. EVE process
    var client = EveProcessFinder.FindFirstClient();
    result.AppendLine($"EVE process: {(client != null ? client.ToString() : "NOT FOUND")}");

    // 2. Get JSON from already-running runner (no new memory read needed)
    var json = orch.GetLastRawJsonSample(4000);
    if (json == null)
    {
        result.AppendLine("LastRawJson: null — runner not started yet or no tick completed");
        return Results.Ok(new { report = result.ToString(), json_preview = (string?)null });
    }

    var fullLen = orch.GetLastRawJsonSample(int.MaxValue)?.Length ?? 0;
    result.AppendLine($"JSON length: {fullLen:N0} chars");
    result.AppendLine($"JSON preview length: {json.Length} chars");

    // 3. Parse the sample to inspect structure
    var parser = new EBot.Core.GameState.UITreeParser(lf.CreateLogger<EBot.Core.GameState.UITreeParser>());
    EBot.Core.GameState.ParsedUI parsed;
    try
    {
        var fullJson = orch.GetLastRawJsonSample(int.MaxValue)!;
        parsed = parser.Parse(fullJson);
    }
    catch (Exception ex)
    {
        result.AppendLine($"Parser threw: {ex.GetType().Name}: {ex.Message}");
        return Results.Ok(new { report = result.ToString(), json_preview = json });
    }

    result.AppendLine($"UITree root type: \"{parsed.UITree?.Node.PythonObjectTypeName ?? "null"}\"");
    result.AppendLine($"UITree children: {parsed.UITree?.Children.Count ?? 0}");
    result.AppendLine($"ShipUI: {(parsed.ShipUI != null ? "FOUND" : "not found")}");
    if (parsed.ShipUI != null)
    {
        result.AppendLine($"  Capacitor: {(parsed.ShipUI.Capacitor != null ? $"{parsed.ShipUI.Capacitor.LevelPercent}%" : "null")}");
        result.AppendLine($"  Shield: {parsed.ShipUI.HitpointsPercent?.Shield}%  Armor: {parsed.ShipUI.HitpointsPercent?.Armor}%");
    }
    result.AppendLine($"InfoPanelContainer: {(parsed.InfoPanelContainer != null ? "FOUND" : "not found")}");
    if (parsed.InfoPanelContainer?.InfoPanelLocationInfo != null)
        result.AppendLine($"  System: {parsed.InfoPanelContainer.InfoPanelLocationInfo.SystemName}");
    result.AppendLine($"OverviewWindows: {parsed.OverviewWindows.Count}");
    result.AppendLine($"Targets: {parsed.Targets.Count}");
    result.AppendLine($"ContextMenus: {parsed.ContextMenus.Count}");

    // Dump root-level child type names to see what's actually in the tree
    if (parsed.UITree != null)
    {
        result.AppendLine("Root children:");
        foreach (var child in parsed.UITree.Children.Take(30))
            result.AppendLine($"  \"{child.Node.PythonObjectTypeName}\"");
    }

    return Results.Ok(new { report = result.ToString(), json_preview = json });
});

// GET /api/debug/infopanel  — dumps InfoPanelLocationInfo text structure for diagnostics
api.MapGet("/debug/infopanel", (BotOrchestrator orch, ILoggerFactory lf) =>
{
    var json = orch.GetLastRawJsonSample(int.MaxValue);
    if (json == null) return Results.Ok("No JSON yet");

    var parser = new EBot.Core.GameState.UITreeParser(lf.CreateLogger<EBot.Core.GameState.UITreeParser>());
    var parsed = parser.Parse(json);
    var panel = parsed.InfoPanelContainer?.InfoPanelLocationInfo?.UINode;
    if (panel == null) return Results.Ok("InfoPanelLocationInfo not found");

    var sb = new System.Text.StringBuilder();
    DumpNode(panel, sb, 0);

    static void DumpNode(EBot.Core.GameState.UITreeNodeWithDisplayRegion n, System.Text.StringBuilder sb, int depth)
    {
        if (depth > 4) return;
        var indent = new string(' ', depth * 2);
        var name = n.Node.GetDictString("_name") ?? "";
        var setText = n.Node.GetDictString("_setText") ?? "";
        var text = n.Node.GetDictString("_text") ?? "";
        var hint = n.Node.GetDictString("_hint") ?? "";
        sb.AppendLine($"{indent}[{n.Node.PythonObjectTypeName}] name={name} _setText={setText} _text={text} _hint={hint[..Math.Min(40,hint.Length)]}");
        foreach (var child in n.Children)
            DumpNode(child, sb, depth + 1);
    }

    return Results.Ok(sb.ToString());
});


// ─── Discord API ──────────────────────────────────────────────────────────

// GET /api/discord/status  — current settings (url, enabled, interval)
api.MapGet("/discord/status", (EBot.WebHost.Services.DiscordNotificationService discord) =>
    Results.Ok(discord.GetStatus()));

// POST /api/discord/settings  — full settings object; any missing field uses server default
api.MapPost("/discord/settings", async ([FromBody] EBot.WebHost.Services.DiscordSettings req,
    EBot.WebHost.Services.DiscordNotificationService discord) =>
{
    await discord.SaveSettingsAsync(req);
    return Results.Ok(discord.GetStatus());
});

// POST /api/discord/test  — sends a test message to confirm the webhook works
api.MapPost("/discord/test", async (EBot.WebHost.Services.DiscordNotificationService discord) =>
{
    var ok = await discord.SendTestAsync();
    return ok
        ? Results.Ok(new { success = true, message = "Test message sent to Discord." })
        : Results.BadRequest(new { success = false, message = "Webhook delivery failed — check the URL." });
});

// ─── Auto-start monitor ────────────────────────────────────────────────────

// Monitor starts automatically; it always runs when no bot is active.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var orch = app.Services.GetRequiredService<BotOrchestrator>();
    _ = Task.Run(async () =>
    {
        try { await orch.EnsureMonitorAsync(); }
        catch { /* No EVE client yet — silently ignore */ }
    });
});

// ─── Run ───────────────────────────────────────────────────────────────────

app.Run();
