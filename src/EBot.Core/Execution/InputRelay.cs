using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace EBot.Core.Execution;

/// <summary>
/// Named-pipe client that forwards mouse/keyboard commands to EBot.InputAgent,
/// which runs in the interactive Windows session (RDP or physical console).
///
/// This is the solution for the SSH-started-process problem:
///   • SSH processes have no window station → SendInput returns 0.
///   • InputAgent runs in the interactive session and actually calls SendInput.
///   • InputRelay bridges the two via \\.\pipe\ebot-input.
///
/// Usage:
///   var relay = new InputRelay(logger);
///   bool ok = await relay.TryConnectAsync();   // call once at startup
///   if (ok) { relay is available }
///
/// InputSimulator checks InputRelay.IsConnected and routes calls through it
/// when available, falling back to direct SendInput otherwise.
/// </summary>
public sealed class InputRelay : IDisposable
{
    public const string PipeName = "ebot-input";

    private readonly ILogger<InputRelay> _logger;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _pipe?.IsConnected == true;

    public InputRelay(ILogger<InputRelay> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to connect to EBot.InputAgent on the named pipe.
    /// Returns true if successful.  Non-throwing — returns false on any failure.
    /// </summary>
    public async Task<bool> TryConnectAsync(int timeoutMs = 2000)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs);

            _pipe   = pipe;
            _reader = new StreamReader(pipe, leaveOpen: true);
            _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            // Handshake
            var response = await SendCommandAsync("PING");
            if (response == "OK")
            {
                // Ask agent for its screen dimensions so we can log any mismatch
                var info = await SendCommandAsync("SCREENINFO");
                _logger.LogInformation(
                    "InputRelay connected to agent — {Info}", info);
                return true;
            }

            _logger.LogWarning("InputRelay: unexpected PING response: {R}", response);
            Disconnect();
            return false;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("InputRelay: agent not found on pipe '{P}' (not started?)", PipeName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("InputRelay: connect failed — {Msg}", ex.Message);
            return false;
        }
    }

    // ─── Mouse ─────────────────────────────────────────────────────────────

    public Task MoveAsync(int screenX, int screenY) =>
        SendFireAndForget($"MOVE {screenX} {screenY}");

    public Task LeftDownAsync(int screenX, int screenY) =>
        SendFireAndForget($"LDOWN {screenX} {screenY}");

    public Task LeftUpAsync() =>
        SendFireAndForget("LUP");

    public Task RightDownAsync(int screenX, int screenY) =>
        SendFireAndForget($"RDOWN {screenX} {screenY}");

    public Task RightUpAsync() =>
        SendFireAndForget("RUP");

    // ─── Keyboard ──────────────────────────────────────────────────────────

    public Task KeyDownAsync(ushort vk) =>
        SendFireAndForget($"KEYDOWN {vk}");

    public Task KeyUpAsync(ushort vk) =>
        SendFireAndForget($"KEYUP {vk}");

    public Task TypeCharAsync(char ch) =>
        SendFireAndForget($"TYPE {(int)ch}");

    // ─── Internal ──────────────────────────────────────────────────────────

    /// <summary>Sends a command and returns the response line.</summary>
    private async Task<string> SendCommandAsync(string cmd)
    {
        if (_writer == null || _reader == null) return "ERR not connected";

        await _writer.WriteLineAsync(cmd);
        var response = await _reader.ReadLineAsync() ?? "ERR null response";
        return response;
    }

    /// <summary>
    /// Sends a command without waiting for the full response (just checks for ERR).
    /// Disconnects on pipe errors so InputSimulator falls back to direct SendInput.
    /// </summary>
    private async Task SendFireAndForget(string cmd)
    {
        if (!IsConnected) return;

        await _lock.WaitAsync();
        try
        {
            var response = await SendCommandAsync(cmd);
            if (response.StartsWith("ERR"))
                _logger.LogWarning("InputAgent error for '{Cmd}': {R}", cmd, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("InputRelay pipe error ({Msg}) — disconnecting", ex.Message);
            Disconnect();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Disconnect()
    {
        _reader?.Dispose(); _reader = null;
        _writer?.Dispose(); _writer = null;
        _pipe?.Dispose();   _pipe   = null;
    }

    public void Dispose() => Disconnect();
}
