using System.Diagnostics;

namespace EBot.Core.MemoryReading;

/// <summary>
/// Discovers running EVE Online client processes.
/// </summary>
public static class EveProcessFinder
{
    /// <summary>
    /// Known EVE Online process names.
    /// </summary>
    private static readonly string[] EveProcessNames = ["exefile", "eve"];

    /// <summary>
    /// Finds all running EVE Online client processes.
    /// </summary>
    public static IReadOnlyList<EveClientInfo> FindEveClients()
    {
        var clients = new List<EveClientInfo>();

        foreach (var name in EveProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var proc in processes)
            {
                try
                {
                    clients.Add(new EveClientInfo
                    {
                        ProcessId = proc.Id,
                        ProcessName = proc.ProcessName,
                        MainWindowTitle = proc.MainWindowTitle,
                        MainWindowHandle = proc.MainWindowHandle,
                        StartTime = proc.StartTime
                    });
                }
                catch
                {
                    // Process may have exited between enumeration and access
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        return clients;
    }

    /// <summary>
    /// Finds the first available EVE Online client, or null if none found.
    /// </summary>
    public static EveClientInfo? FindFirstClient()
    {
        var clients = FindEveClients();
        return clients.Count > 0 ? clients[0] : null;
    }
}

/// <summary>
/// Information about a running EVE Online client process.
/// </summary>
public sealed class EveClientInfo
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string MainWindowTitle { get; init; } = string.Empty;
    public IntPtr MainWindowHandle { get; init; }
    public DateTime StartTime { get; init; }

    public override string ToString() =>
        $"PID={ProcessId} | {ProcessName} | \"{MainWindowTitle}\" | Started={StartTime:HH:mm:ss}";
}
