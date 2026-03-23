using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EBot.WebHost.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace EBot.WebHost.Services;

/// <summary>
/// AI chat backend powered by a local Ollama instance.
/// Uses Ollama's native <c>/api/chat</c> endpoint with tool calling.
///
/// Configuration (environment variables):
///   EBOT_OLLAMA_URL   — Base URL of the Ollama server (default: http://192.168.1.40:11434)
///   EBOT_OLLAMA_MODEL — Model to use, must support tool calling  (default: llama3.2)
/// </summary>
public sealed class OllamaChatService(
    BotOrchestrator orchestrator,
    IHubContext<BotHub> hub,
    ILogger<OllamaChatService> logger) : IChatService
{
    // ── Configuration ──────────────────────────────────────────────────────

    public static readonly string BaseUrl =
        (Environment.GetEnvironmentVariable("EBOT_OLLAMA_URL") ?? "http://192.168.1.40:11434")
        .TrimEnd('/');

    private static string _model =
        Environment.GetEnvironmentVariable("EBOT_OLLAMA_MODEL") ?? "llama3.2";

    /// <summary>Change the active Ollama model at runtime.</summary>
    public static void SetModel(string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
            _model = model.Trim();
    }

    public static string CurrentModel => _model;

    private static readonly string _systemPrompt =
        "You are the Command & Control AI for an EVE Online bot framework. " +
        "You can control bots, query game state, and advise the pilot. " +
        "Be concise and action-oriented. When the user asks you to do something, " +
        "call the appropriate tool immediately. " +
        "After executing tools, summarise what happened in 1-2 sentences.";

    // Single shared HttpClient — service is singleton
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    // JSON options: snake_case naming + omit nulls for clean Ollama payloads
    private static readonly JsonSerializerOptions _jsonSend = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // JSON options: case-insensitive for reading Ollama responses
    private static readonly JsonSerializerOptions _jsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // JSON for serializing tool results / orchestrator data
    private static readonly JsonSerializerOptions _jsonData = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Tool schema builder uses default options (property names are already lowercase literals)
    private static readonly JsonSerializerOptions _jsonSchema = new() { WriteIndented = false };

    // ── Tool definitions ───────────────────────────────────────────────────

    private static readonly IReadOnlyList<OllamaTool> _tools = BuildTools();

    private static List<OllamaTool> BuildTools() =>
    [
        MakeTool("get_status",
            "Get current bot state and live EVE Online game state (location, HP, capacitor, targets)."),

        MakeTool("list_bots",
            "List all bot types available to start."),

        MakeTool("start_bot",
            "Start a bot. Auto-detects the EVE Online process.",
            ("bot_name", "string", "Bot name, e.g. 'Mining Bot'", true)),

        MakeTool("stop_bot",
            "Stop the currently running bot."),

        MakeTool("pause_bot",
            "Pause the running bot (preserves state)."),

        MakeTool("resume_bot",
            "Resume a paused bot."),

        MakeTool("set_survival_mode",
            "Enable or disable Survival Mode (auto-tank, dismiss popups).",
            ("enabled", "boolean", "true to enable, false to disable", true)),

        MakeTool("get_overview",
            "Get the current overview window entries (asteroids, ships, gates, etc.)."),

        MakeTool("get_targets",
            "Get currently locked targets and their hit points."),

        MakeTool("get_log",
            "Get recent log entries from the bot framework.",
            ("count", "integer", "Number of log lines (default 20, max 50)", false)),

        MakeTool("travel_to",
            "Open People & Places in-game, search for the given system, set it as destination, " +
            "then start the Autopilot Bot (warp-to-0 travel). E.g. 'travel to Jita'.",
            ("destination", "string", "Target solar system name, e.g. 'Jita'", true)),
    ];

    private static OllamaTool MakeTool(string name, string description,
        params (string name, string type, string desc, bool required)[] parameters)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var (pName, pType, pDesc, pRequired) in parameters)
        {
            props[pName] = new { type = pType, description = pDesc };
            if (pRequired) required.Add(pName);
        }

        var schema = required.Count > 0
            ? (object)new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return new OllamaTool
        {
            Type = "function",
            Function = new OllamaToolFunction
            {
                Name = name,
                Description = description,
                Parameters = JsonSerializer.SerializeToElement(schema, _jsonSchema),
            },
        };
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task ProcessAsync(string userMessage, string connectionId,
        CancellationToken cancellationToken = default)
    {
        var conn = hub.Clients.Client(connectionId);
        try
        {
            var messages = new List<OllamaMessage>
            {
                new() { Role = "system", Content = _systemPrompt },
                new() { Role = "user",   Content = userMessage },
            };

            // ── Agentic tool-use loop ──────────────────────────────────────

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestBody = new
                {
                    model = _model,
                    messages,
                    tools = _tools,
                    stream = false,
                };

                var json = JsonSerializer.Serialize(requestBody, _jsonSend);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http.PostAsync(
                    $"{BaseUrl}/api/chat", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Ollama returned {(int)response.StatusCode}: {body}");
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var ollamaResp = JsonSerializer.Deserialize<OllamaResponse>(responseJson, _jsonRead)
                    ?? throw new InvalidOperationException("Null response from Ollama");

                var assistantMsg = ollamaResp.Message
                    ?? throw new InvalidOperationException("Ollama response missing message");

                // Preserve assistant message for context
                messages.Add(assistantMsg);

                var toolCalls = assistantMsg.ToolCalls ?? [];
                if (toolCalls.Count == 0)
                {
                    // No more tools — send final text
                    var finalText = assistantMsg.Content ?? "Done.";
                    if (string.IsNullOrWhiteSpace(finalText)) finalText = "Done.";
                    await conn.SendAsync("ChatMessage", new { text = finalText }, cancellationToken);
                    break;
                }

                // Execute each tool call
                foreach (var toolCall in toolCalls)
                {
                    var name = toolCall.Function?.Name ?? "unknown";
                    var argsElement = toolCall.Function?.Arguments ?? default;

                    await conn.SendAsync("ChatToolCall", new { name }, cancellationToken);

                    string toolResult;
                    try
                    {
                        var inputDict = argsElement.ValueKind == JsonValueKind.Object
                            ? argsElement.EnumerateObject()
                                .ToDictionary(p => p.Name, p => p.Value)
                            : new Dictionary<string, JsonElement>();

                        toolResult = await ExecuteToolAsync(name, inputDict, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        toolResult = $"Error: {ex.Message}";
                        logger.LogWarning(ex, "Tool {Tool} failed", name);
                    }

                    await conn.SendAsync("ChatToolResult",
                        new { name, result = toolResult }, cancellationToken);

                    // Append tool result for next iteration
                    messages.Add(new OllamaMessage
                    {
                        Role    = "tool",
                        Name    = name,
                        Content = toolResult,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — ignore
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OllamaChatService error");
            try
            {
                await conn.SendAsync("ChatError", new { error = ex.Message }, cancellationToken);
            }
            catch { /* connection may be gone */ }
        }
    }

    // ── Tool dispatch (mirrors ChatService.ExecuteToolAsync) ───────────────

    private async Task<string> ExecuteToolAsync(string name,
        IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        switch (name)
        {
            case "get_status":
                return Serialize(orchestrator.GetStatus(0));

            case "list_bots":
                return Serialize(BotOrchestrator.AvailableBots
                    .Select(b => new { b.Name, b.Description }));

            case "start_bot":
            {
                var botName = Str(input, "bot_name") ?? "Mining Bot";
                await orchestrator.StartAsync(botName, tickMs: 0);
                return $"Bot '{botName}' started.";
            }

            case "stop_bot":
                await orchestrator.StopAsync();
                return "Bot stopped.";

            case "pause_bot":
                await orchestrator.PauseAsync();
                return "Bot paused.";

            case "resume_bot":
                await orchestrator.ResumeAsync();
                return "Bot resumed.";

            case "set_survival_mode":
            {
                var enabled = Bool(input, "enabled") ?? true;
                orchestrator.SetSurvivalMode(enabled);
                return $"Survival mode {(enabled ? "enabled" : "disabled")}.";
            }

            case "get_overview":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state — start monitor or a bot first.";
                var entries = ctx.GameState.ParsedUI.OverviewWindows
                    .FirstOrDefault()?.Entries ?? [];
                if (entries.Count == 0) return "Overview is empty.";
                return Serialize(entries.Select(e => new
                {
                    name     = e.Name,
                    type     = e.ObjectType,
                    distance = e.DistanceText,
                    attacking = e.IsAttackingMe,
                }));
            }

            case "get_targets":
            {
                var ctx = orchestrator.LastContext;
                if (ctx == null) return "No game state.";
                var targets = ctx.GameState.ParsedUI.Targets;
                if (targets.Count == 0) return "No targets locked.";
                return Serialize(targets.Select(t => new
                {
                    name      = t.TextLabel,
                    active    = t.IsActiveTarget,
                    shield    = t.HitpointsPercent?.Shield,
                    armor     = t.HitpointsPercent?.Armor,
                    structure = t.HitpointsPercent?.Structure,
                }));
            }

            case "get_log":
            {
                var count = Math.Clamp(Int(input, "count") ?? 20, 1, 50);
                var logs = orchestrator.GetRecentLogs(count);
                return Serialize(logs.Select(l => new
                {
                    time    = l.Time.ToString("HH:mm:ss"),
                    level   = l.Level,
                    message = l.Message,
                }));
            }

            case "travel_to":
            {
                var destination = Str(input, "destination");
                if (string.IsNullOrWhiteSpace(destination))
                    return "Error: destination is required.";
                await orchestrator.TravelToAsync(destination);
                return $"Autopilot Bot started. Navigating to '{destination}' using warp-to-0.";
            }

            default:
                return $"Unknown tool: {name}";
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string Serialize(object obj) => JsonSerializer.Serialize(obj, _jsonData);

    private static string? Str(IReadOnlyDictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(IReadOnlyDictionary<string, JsonElement> d, string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        return v.ValueKind == JsonValueKind.True ? true
             : v.ValueKind == JsonValueKind.False ? false
             : null;
    }

    private static int? Int(IReadOnlyDictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.TryGetInt32(out var i) ? i : null;

    // ── Ollama API DTOs ────────────────────────────────────────────────────

    private sealed class OllamaResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private sealed class OllamaMessage
    {
        public string Role { get; set; } = "";
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }

        // Optional: some models use the tool name in results
        public string? Name { get; set; }
    }

    private sealed class OllamaToolCall
    {
        public OllamaToolCallFunction? Function { get; set; }
    }

    private sealed class OllamaToolCallFunction
    {
        public string Name { get; set; } = "";
        public JsonElement Arguments { get; set; }
    }

    private sealed class OllamaTool
    {
        public string Type { get; set; } = "function";
        public OllamaToolFunction Function { get; set; } = new();
    }

    private sealed class OllamaToolFunction
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonElement Parameters { get; set; }
    }
}
