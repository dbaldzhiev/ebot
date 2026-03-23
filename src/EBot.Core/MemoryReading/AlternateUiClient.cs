using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EBot.Core.MemoryReading;

/// <summary>
/// HTTP client for the alternate-ui server (pine / ElmTime runtime).
///
/// Protocol summary (POST /api for every call):
///   1. ListGameClientProcessesRequest  → get windowId for the PID (once per session)
///   2. SearchUIRootAddress { processId } → find UI root address (once; polls until done)
///   3. ReadFromWindow { windowId, uiRootAddress } → return memory JSON (every tick)
///
/// References:
///   sanderling-arcitectus/implement/alternate-ui/source/src/EveOnline/VolatileProcessInterface.elm
///   sanderling-arcitectus/implement/alternate-ui/source/src/InterfaceToFrontendClient.elm
/// </summary>
public sealed class AlternateUiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly ILogger _logger;

    // Cached per session — invalidated when the read fails with ProcessNotFound
    private string? _windowId;
    private string? _uiRootAddress;
    private int _cachedPid;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AlternateUiClient(string baseUrl, int timeoutMs, ILogger logger)
    {
        _apiUrl = baseUrl.TrimEnd('/') + "/api";
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
    }

    /// <summary>
    /// Reads the EVE UI tree JSON for the given PID.
    /// Returns <c>null</c> on failure (logged internally).
    /// </summary>
    public async Task<string?> ReadMemoryJsonAsync(int pid, CancellationToken ct)
    {
        // Step 1 — resolve windowId (cached)
        if (_windowId == null || _cachedPid != pid)
        {
            _windowId = await GetWindowIdAsync(pid, ct);
            if (_windowId == null) return null;
            _cachedPid = pid;
            _uiRootAddress = null; // force re-search when pid changes
        }

        // Step 2 — resolve uiRootAddress (cached; SearchUIRootAddress polls until ready)
        if (_uiRootAddress == null)
        {
            _uiRootAddress = await SearchUIRootAddressAsync(pid, ct);
            if (_uiRootAddress == null) return null;
        }

        // Step 3 — read from window every tick
        var json = await ReadFromWindowAsync(_windowId, _uiRootAddress, ct);
        if (json == null)
        {
            // Process disappeared — clear cache so we rediscover next tick
            _windowId = null;
            _uiRootAddress = null;
        }
        return json;
    }

    // ─── Step 1: List processes → windowId ──────────────────────────────────

    private async Task<string?> GetWindowIdAsync(int pid, CancellationToken ct)
    {
        var returnValue = await PostVolatileRequestAsync(
            """{"ListGameClientProcessesRequest":{}}""", ct);
        if (returnValue == null) return null;

        using var doc = JsonDocument.Parse(returnValue);
        if (!doc.RootElement.TryGetProperty("ListGameClientProcessesResponse", out var arr))
        {
            _logger.LogWarning("AlternateUi: expected ListGameClientProcessesResponse");
            return null;
        }

        foreach (var proc in arr.EnumerateArray())
        {
            if (proc.TryGetProperty("processId", out var pidEl) && pidEl.GetInt32() == pid
                && proc.TryGetProperty("mainWindowId", out var winEl))
            {
                var id = winEl.GetString();
                _logger.LogInformation("AlternateUi: resolved windowId={Id} for PID {Pid}", id, pid);
                return id;
            }
        }

        _logger.LogWarning("AlternateUi: PID {Pid} not found in process list", pid);
        return null;
    }

    // ─── Step 2: SearchUIRootAddress → uiRootAddress ────────────────────────

    private async Task<string?> SearchUIRootAddressAsync(int pid, CancellationToken ct)
    {
        var requestJson = $$$"""{"SearchUIRootAddress":{"processId":{{{pid}}}}}""";

        // The volatile script runs the search in a background task — poll until complete
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var returnValue = await PostVolatileRequestAsync(requestJson, ct);
            if (returnValue == null) return null;

            using var doc = JsonDocument.Parse(returnValue);
            if (!doc.RootElement.TryGetProperty("SearchUIRootAddressResponse", out var resp))
            {
                _logger.LogWarning("AlternateUi: unexpected SearchUIRootAddress response");
                return null;
            }

            if (!resp.TryGetProperty("stage", out var stage)) continue;

            if (stage.TryGetProperty("SearchUIRootAddressCompleted", out var completed))
            {
                if (completed.TryGetProperty("uiRootAddress", out var addrEl)
                    && addrEl.ValueKind == JsonValueKind.String)
                {
                    var addr = addrEl.GetString();
                    _logger.LogInformation("AlternateUi: UI root address = {Addr}", addr);
                    return addr;
                }

                _logger.LogWarning("AlternateUi: SearchUIRootAddressCompleted but no address for PID {Pid}", pid);
                return null;
            }

            // SearchUIRootAddressInProgress — wait and retry
            _logger.LogDebug("AlternateUi: UI root search in progress (attempt {N})", attempt + 1);
            await Task.Delay(1000, ct);
        }

        _logger.LogWarning("AlternateUi: UI root search timed out for PID {Pid}", pid);
        return null;
    }

    // ─── Step 3: ReadFromWindow → memoryReadingSerialRepresentationJson ──────

    private async Task<string?> ReadFromWindowAsync(
        string windowId, string uiRootAddress, CancellationToken ct)
    {
        var requestJson =
            $$$"""{"ReadFromWindow":{"windowId":"{{{windowId}}}","uiRootAddress":"{{{uiRootAddress}}}"}}""";

        var returnValue = await PostVolatileRequestAsync(requestJson, ct);
        if (returnValue == null) return null;

        using var doc = JsonDocument.Parse(returnValue);
        if (!doc.RootElement.TryGetProperty("ReadFromWindowResult", out var result)) return null;

        if (result.TryGetProperty("ProcessNotFound", out _))
        {
            _logger.LogWarning("AlternateUi: ProcessNotFound — EVE client may have closed");
            return null;
        }

        if (result.TryGetProperty("Completed", out var completed)
            && completed.TryGetProperty("memoryReadingSerialRepresentationJson", out var jsonEl)
            && jsonEl.ValueKind == JsonValueKind.String)
        {
            return jsonEl.GetString();
        }

        _logger.LogWarning("AlternateUi: unexpected ReadFromWindowResult structure");
        return null;
    }

    // ─── Transport: POST /api ────────────────────────────────────────────────

    /// <summary>
    /// Posts a volatile process request and returns the <c>returnValueToString</c>
    /// from the response, or <c>null</c> on error.
    ///
    /// Request envelope:  {"RunInVolatileProcessRequest": <RequestToVolatileHost>}
    /// Response envelope: {"RunInVolatileProcessCompleteResponse": {"returnValueToString": "...", ...}}
    /// </summary>
    private async Task<string?> PostVolatileRequestAsync(string volatileRequestJson, CancellationToken ct)
    {
        var envelope = $$$"""{"RunInVolatileProcessRequest":{{{volatileRequestJson}}}}""";

        using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_apiUrl, content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("AlternateUi: POST /api → {Code}", resp.StatusCode);
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);

        // Unwrap outer envelope
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("SetupNotCompleteResponse", out var setup))
        {
            _logger.LogDebug("AlternateUi: setup not complete — {Msg}", setup.GetString());
            return null;
        }

        if (!doc.RootElement.TryGetProperty("RunInVolatileProcessCompleteResponse", out var complete))
        {
            _logger.LogWarning("AlternateUi: unexpected response: {Body}", body[..Math.Min(200, body.Length)]);
            return null;
        }

        if (complete.TryGetProperty("exceptionToString", out var ex)
            && ex.ValueKind == JsonValueKind.String)
        {
            _logger.LogWarning("AlternateUi: volatile process exception: {Ex}", ex.GetString());
            return null;
        }

        if (complete.TryGetProperty("returnValueToString", out var ret)
            && ret.ValueKind == JsonValueKind.String)
        {
            return ret.GetString();
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
