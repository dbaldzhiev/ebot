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
        await o.StartAsync(req.BotName, req.Pid, req.ExePath, req.TickMs);
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

// POST /api/survival  { "enabled": true }
api.MapPost("/survival", ([FromBody] SurvivalRequest req, BotOrchestrator o) =>
{
    o.SetSurvivalMode(req.Enabled);
    return Results.Ok(new { success = true, survival_enabled = o.SurvivalEnabled });
});

// GET /api/log
api.MapGet("/log", (BotOrchestrator o, [FromQuery] int count = 50) =>
    Results.Ok(o.GetRecentLogs(Math.Clamp(count, 1, 200))));

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

// ─── Quick Travel config ───────────────────────────────────────────────────

var quickTravelPath = Path.Combine(AppContext.BaseDirectory, "data", "quick-travel.json");

List<string> LoadQuickTravel()
{
    try
    {
        if (File.Exists(quickTravelPath))
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(quickTravelPath)) ?? [];
    }
    catch { }
    // Seed defaults on first run
    var defaults = new List<string> { "Jita 4-4" };
    SaveQuickTravel(defaults);
    return defaults;
}

void SaveQuickTravel(List<string> stations)
{
    Directory.CreateDirectory(Path.GetDirectoryName(quickTravelPath)!);
    File.WriteAllText(quickTravelPath, JsonSerializer.Serialize(stations, new JsonSerializerOptions { WriteIndented = true }));
}

// GET /api/quick-travel  — list saved stations
api.MapGet("/quick-travel", () => Results.Ok(LoadQuickTravel()));

// ─── Station aliases ───────────────────────────────────────────────────────

var aliasPath = Path.Combine(AppContext.BaseDirectory, "data", "station-aliases.json");

// Default aliases shipped with the bot
var defaultAliases = new List<StationAlias>
{
    new("Jita 4-4", "Jita", "Jita IV - Moon 4 - Caldari Navy Assembly Plant"),
};

Dictionary<string, StationAlias> LoadAliases()
{
    try
    {
        if (File.Exists(aliasPath))
        {
            var list = JsonSerializer.Deserialize<List<StationAlias>>(
                File.ReadAllText(aliasPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return list.ToDictionary(a => a.Alias, StringComparer.OrdinalIgnoreCase);
        }
    }
    catch { }
    // First run: seed defaults
    SaveAliases(defaultAliases);
    return defaultAliases.ToDictionary(a => a.Alias, StringComparer.OrdinalIgnoreCase);
}

void SaveAliases(IEnumerable<StationAlias> aliases)
{
    Directory.CreateDirectory(Path.GetDirectoryName(aliasPath)!);
    File.WriteAllText(aliasPath, JsonSerializer.Serialize(aliases.ToList(),
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

// GET /api/station-aliases  — returns {alias: {alias, system, bookmark}, …}
api.MapGet("/station-aliases", () =>
{
    var d = LoadAliases();
    // Return as a plain object keyed by alias for easy JS lookup
    return Results.Ok(d.ToDictionary(kv => kv.Key, kv => new { kv.Value.System, kv.Value.Bookmark }));
});

// POST /api/station-aliases  { "alias": "Jita 4-4", "system": "Jita", "bookmark": "Jita IV - Moon 4 - Caldari Navy Assembly Plant" }
api.MapPost("/station-aliases", ([FromBody] StationAliasRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Alias) || string.IsNullOrWhiteSpace(req.System))
        return Results.BadRequest(new { error = "alias and system are required" });
    var d = LoadAliases();
    d[req.Alias] = new StationAlias(req.Alias, req.System, req.Bookmark);
    SaveAliases(d.Values);
    return Results.Ok(d.ToDictionary(kv => kv.Key, kv => new { kv.Value.System, kv.Value.Bookmark }));
});

// DELETE /api/station-aliases/{alias}
api.MapDelete("/station-aliases/{alias}", (string alias) =>
{
    var d = LoadAliases();
    d.Remove(alias);
    SaveAliases(d.Values);
    return Results.Ok(d.ToDictionary(kv => kv.Key, kv => new { kv.Value.System, kv.Value.Bookmark }));
});

// POST /api/quick-travel  { "station": "Jita" }
api.MapPost("/quick-travel", ([FromBody] QuickTravelRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Station))
        return Results.BadRequest(new { error = "station name required" });
    var list = LoadQuickTravel();
    var name = req.Station.Trim();
    if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
    {
        list.Add(name);
        SaveQuickTravel(list);
    }
    return Results.Ok(list);
});

// DELETE /api/quick-travel/{station}
api.MapDelete("/quick-travel/{station}", (string station) =>
{
    var list = LoadQuickTravel();
    var removed = list.RemoveAll(s => s.Equals(station, StringComparison.OrdinalIgnoreCase));
    if (removed > 0) SaveQuickTravel(list);
    return Results.Ok(list);
});

// POST /api/quick-travel/{station}/go  — start autopilot to that station
api.MapPost("/quick-travel/{station}/go", async (string station, BotOrchestrator o) =>
{
    try
    {
        await o.TravelToAsync(station);
        return Results.Ok(new { success = true, destination = station });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
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

// GET /api/debug/modules  — dump raw dict keys for each module slot (diagnostic)
api.MapGet("/debug/modules", (BotOrchestrator orch) =>
{
    var ui = orch.LastContext?.GameState.ParsedUI;
    if (ui?.ShipUI == null) return Results.Ok("Ship UI not visible.");

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Total module slots: {ui.ShipUI.ModuleButtons.Count}");
    foreach (var (m, i) in ui.ShipUI.ModuleButtons.Select((m, i) => (m, i)))
    {
        var slotType = m.SlotNode.Node.PythonObjectTypeName;
        var btnType  = m.UINode.Node.PythonObjectTypeName;
        var dictKeys = string.Join(", ", (IEnumerable<string>?)m.UINode.Node.DictEntriesOfInterest?.Keys ?? []);
        var slotKeys = string.Join(", ", (IEnumerable<string>?)m.SlotNode.Node.DictEntriesOfInterest?.Keys ?? []);
        sb.AppendLine($"[{i}] slot={slotType} btn={btnType}");
        sb.AppendLine($"     isActive={m.IsActive} isBusy={m.IsBusy} isOverloaded={m.IsOverloaded} isOffline={m.IsOffline}");
        sb.AppendLine($"     btn-keys: {dictKeys}");
        sb.AppendLine($"     slot-keys: {slotKeys}");
    }
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
