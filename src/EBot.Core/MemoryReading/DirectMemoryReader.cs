#pragma warning disable CA1416  // Windows-only — EVE Online only runs on Windows
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using read_memory_64_bit;

namespace EBot.Core.MemoryReading;

/// <summary>
/// Reads the EVE Online UI tree directly in-process using the Sanderling library
/// (<c>read-memory-64-bit.dll</c>) — no child process spawning, no file I/O.
///
/// Algorithm per tick:
///   1. Open a <see cref="MemoryReaderFromLiveProcess"/> handle to the EVE PID.
///   2. If we have a cached UI root address, use it; otherwise search all memory.
///   3. Call <see cref="EveOnline64.ReadUITreeFromAddress"/> to build the tree.
///   4. Call <see cref="EveOnline64.SerializeMemoryReadingNodeToJson"/> → JSON string.
///   5. Return the JSON — no disk write, no process spawn.
/// </summary>
public sealed class DirectMemoryReader : IEveMemoryReader
{
    private readonly ILogger<DirectMemoryReader> _logger;
    private int _pid;
    private bool _disposed;

    // Cached UI root address per PID — skips full memory scan after first read
    private ulong _cachedRootAddress;
    private int   _cachedRootPid;

    private const int MaxDepth = 99;

    /// <param name="pid">EVE Online process ID. 0 = auto-detect each tick.</param>
    public DirectMemoryReader(ILogger<DirectMemoryReader> logger, int pid = 0)
    {
        _logger = logger;
        _pid    = pid;
    }

    /// <summary>Updates the target PID (e.g. after the EVE client restarts).</summary>
    public void SetPid(int pid) => _pid = pid;

    public Task<MemoryReadingResult> ReadMemoryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var pid = _pid;
        if (pid <= 0)
        {
            var client = EveProcessFinder.FindFirstClient();
            if (client == null)
                return Task.FromResult(MemoryReadingResult.Failure("No EVE Online client process found."));
            pid = client.ProcessId;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var json = ReadJson(pid);
            sw.Stop();

            if (json == null)
                return Task.FromResult(MemoryReadingResult.Failure("No UI tree found in EVE process memory."));

            _logger.LogTrace("Direct read in {Ms}ms, {Len} bytes", sw.ElapsedMilliseconds, json.Length);
            return Task.FromResult(MemoryReadingResult.Success(json, sw.Elapsed));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(MemoryReadingResult.Failure("Cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct memory read failed for PID {Pid}", pid);
            return Task.FromResult(MemoryReadingResult.Failure($"Direct read failed: {ex.Message}"));
        }
    }

    private string? ReadJson(int pid)
    {
        using var reader = new MemoryReaderFromLiveProcess(pid);

        // Fast path: use cached root address
        if (_cachedRootAddress != 0 && _cachedRootPid == pid)
        {
            try
            {
                var tree = EveOnline64.ReadUITreeFromAddress(_cachedRootAddress, reader, MaxDepth);
                if (tree != null)
                {
                    var json = EveOnline64.SerializeMemoryReadingNodeToJson(tree);
                    if (json.Contains("\"children\":[{", StringComparison.Ordinal))
                        return json;
                    // Cached root turned stale — fall through to full scan
                    _logger.LogDebug("Cached root 0x{Addr:X} returned stub tree — rescanning", _cachedRootAddress);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Cached root 0x{Addr:X} transient read error ({Ex}), retrying once", _cachedRootAddress, ex.Message);
                // EVE's Python GC can briefly invalidate a read mid-parse — wait a moment and
                // retry the same address before triggering the expensive full rescan (~18 s).
                Thread.Sleep(600);
                try
                {
                    var retryTree = EveOnline64.ReadUITreeFromAddress(_cachedRootAddress, reader, MaxDepth);
                    if (retryTree != null)
                    {
                        var retryJson = EveOnline64.SerializeMemoryReadingNodeToJson(retryTree);
                        if (retryJson.Contains("\"children\":[{", StringComparison.Ordinal))
                        {
                            _logger.LogDebug("Cached root 0x{Addr:X} recovered after retry", _cachedRootAddress);
                            return retryJson;
                        }
                    }
                }
                catch (Exception retryEx)
                {
                    _logger.LogDebug("Cached root retry also failed ({Ex}), doing full scan", retryEx.Message);
                }
            }
            _cachedRootAddress = 0;
        }

        // Full scan for UI root candidates
        var roots = EveOnline64.EnumeratePossibleAddressesForUIRootObjectsFromProcessId(pid);
        if (roots == null || roots.Count == 0)
        {
            _logger.LogWarning("DirectMemoryReader: no UI root found for PID {Pid}", pid);
            return null;
        }

        _logger.LogDebug("DirectMemoryReader: {Count} root candidate(s) for PID {Pid}", roots.Count, pid);

        foreach (var addr in roots)
        {
            try
            {
                var tree = EveOnline64.ReadUITreeFromAddress(addr, reader, MaxDepth);
                if (tree == null) continue;

                var json = EveOnline64.SerializeMemoryReadingNodeToJson(tree);

                // Reject stub/class UIRoot objects — they have no children.
                // A real live UIRoot always has at least one child window.
                if (!json.Contains("\"children\":[{", StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "DirectMemoryReader: 0x{Addr:X} is a stub UIRoot (no children) — skipping", addr);
                    continue;
                }

                _cachedRootAddress = addr;
                _cachedRootPid     = pid;
                _logger.LogInformation("DirectMemoryReader: live UI root at 0x{Addr:X} for PID {Pid}", addr, pid);
                return json;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("DirectMemoryReader: 0x{Addr:X} failed: {Ex}", addr, ex.Message);
            }
        }

        _logger.LogWarning("DirectMemoryReader: all root candidates failed for PID {Pid}", pid);
        return null;
    }

    public void Dispose() => _disposed = true;
}
