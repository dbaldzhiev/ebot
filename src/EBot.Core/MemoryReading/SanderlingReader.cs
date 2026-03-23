using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EBot.Core.MemoryReading;

/// <summary>
/// Reads the EVE Online UI tree from memory.
///
/// Two modes:
///   • HTTP  — Talks to a running alternate-ui HTTP server (<see cref="SanderlingConfig.HttpServerUrl"/>)
///             via the proper multi-step POST /api protocol. The server keeps a volatile process
///             alive between reads, so there is no cold-start cost per tick.
///   • CLI   — Spawns read-memory-64-bit.exe every tick. Each invocation starts a fresh .NET
///             runtime, so the first read is slow (~60 s). Subsequent reads pass --root-address
///             (cached from the previous successful read) which skips the full memory scan and
///             also avoids a Sanderling bug where 0 trees → NullReferenceException.
/// </summary>
public sealed class SanderlingReader : IEveMemoryReader
{
    private readonly SanderlingConfig _config;
    private readonly ILogger<SanderlingReader> _logger;
    private readonly AlternateUiClient? _altUi;
    private bool _disposed;

    // CLI-mode cache: UI root address found in the previous successful read
    private string? _cachedRootAddress;
    private int _cachedRootPid;

    /// <summary>Path to the last JSON output file produced by the CLI tool (CLI mode only).</summary>
    public string? LastOutputFilePath { get; private set; }

    public SanderlingReader(SanderlingConfig config, ILogger<SanderlingReader> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (config.HttpServerUrl != null)
            _altUi = new AlternateUiClient(config.HttpServerUrl, config.ReadTimeoutMs, logger);
    }

    /// <summary>
    /// Performs a single memory reading of the EVE Online client.
    /// Returns the raw JSON string.
    /// </summary>
    public async Task<MemoryReadingResult> ReadMemoryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pid = _config.ProcessId;
        if (pid <= 0)
        {
            var client = EveProcessFinder.FindFirstClient();
            if (client == null)
                return MemoryReadingResult.Failure("No EVE Online client process found.");
            pid = client.ProcessId;
            _logger.LogInformation("Auto-detected EVE client PID: {Pid} ({Title})", pid, client.MainWindowTitle);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            string? json;
            if (_altUi != null)
            {
                json = await _altUi.ReadMemoryJsonAsync(pid, ct);
                if (json == null)
                    return MemoryReadingResult.Failure("AlternateUi returned no data.");
            }
            else
            {
                json = await ReadViaCliAsync(pid, ct);
            }

            sw.Stop();
            _logger.LogDebug("Memory read in {Ms}ms, JSON length: {Len}", sw.ElapsedMilliseconds, json!.Length);
            return MemoryReadingResult.Success(json, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return MemoryReadingResult.Failure("Memory reading was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory reading failed for PID {Pid}", pid);
            return MemoryReadingResult.Failure($"Memory reading failed: {ex.Message}");
        }
    }

    // ─── CLI mode (read-memory-64-bit.exe) ──────────────────────────────────

    private async Task<string> ReadViaCliAsync(int pid, CancellationToken ct)
    {
        var args = $"read-memory-eve-online --pid={pid}";

        // Pass cached root address to skip the full memory scan and avoid the
        // Sanderling NRE bug that fires when 0 UI trees are found on a full scan.
        if (_cachedRootAddress != null && _cachedRootPid == pid)
            args += $" --root-address={_cachedRootAddress}";

        if (_config.OutputFilePath != null)
            args += $" --output-file=\"{_config.OutputFilePath}\"";

        _logger.LogDebug("CLI read: {Exe} {Args}", _config.ExecutablePath, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_config.ReadTimeoutMs);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        // Sanderling crashes (NRE) when it finds 0 trees — stale cached root address.
        // Clear the cache so the next tick does a full scan to rediscover the root.
        if (stdout.Contains("Read 0 UI trees", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("CLI: 0 UI trees found — clearing cached root address");
            _cachedRootAddress = null;
            throw new InvalidOperationException("read-memory-64-bit found 0 UI trees (stale root address or EVE not ready).");
        }

        // Only log stderr when it's not the expected NRE from the 0-trees bug
        if (!string.IsNullOrWhiteSpace(stderr) &&
            !stderr.Contains("NullReferenceException", StringComparison.Ordinal))
        {
            _logger.LogWarning("Sanderling stderr:\n{Error}", stderr);
        }

        var outputFilePath = _config.OutputFilePath ?? ParseOutputFilePath(stdout);
        if (outputFilePath == null)
            throw new InvalidOperationException($"Could not parse Sanderling output path from stdout:\n{stdout}");

        if (!File.Exists(outputFilePath))
            throw new FileNotFoundException($"Sanderling output file not found: {outputFilePath}", outputFilePath);

        LastOutputFilePath = outputFilePath;

        // Cache the UI root address for the next tick
        var addrMatch = Regex.Match(stdout, @"from address (0x[0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
        if (addrMatch.Success)
        {
            _cachedRootAddress = addrMatch.Groups[1].Value;
            _cachedRootPid = pid;
            _logger.LogDebug("CLI: cached root address {Addr} for PID {Pid}", _cachedRootAddress, pid);
        }

        var json = await File.ReadAllTextAsync(outputFilePath, ct);

        // Delete auto-named temp files immediately after reading — prevents accumulation.
        // If the caller configured a fixed OutputFilePath, leave it alone.
        if (_config.OutputFilePath == null)
        {
            try { File.Delete(outputFilePath); }
            catch (Exception ex) { _logger.LogDebug("CLI: could not delete temp file: {Ex}", ex.Message); }
        }

        return json;
    }

    private static string? ParseOutputFilePath(string stdout)
    {
        var match = Regex.Match(stdout, @"to file '(.+?\.json)'", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        match = Regex.Match(stdout, @"'([^']+\.json)'", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public void Dispose()
    {
        _disposed = true;
        _altUi?.Dispose();
    }
}

/// <summary>Result of a memory reading operation.</summary>
public sealed class MemoryReadingResult
{
    public bool IsSuccess { get; private init; }
    public string? Json { get; private init; }
    public string? ErrorMessage { get; private init; }
    public TimeSpan Elapsed { get; private init; }

    public static MemoryReadingResult Success(string json, TimeSpan elapsed) =>
        new() { IsSuccess = true, Json = json, Elapsed = elapsed };

    public static MemoryReadingResult Failure(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}
