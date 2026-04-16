using EBot.Core.Bot;
using EBot.Core.Execution;
using EBot.Core.GameState;
using EBot.Core.MemoryReading;
using EBot.ExampleBots.MiningBot;
using Microsoft.Extensions.Logging;

namespace EBot.Runner;

/// <summary>
/// Console entry point for the EBot framework.
/// Provides CLI commands: run, list-processes, test-read.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "[HH:mm:ss] ";
                    options.SingleLine = true;
                });
        });

        var logger = loggerFactory.CreateLogger("EBot");

        PrintBanner();

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        return command switch
        {
            "run" => await RunBot(args, loggerFactory, logger),
            "list-processes" or "list" => ListProcesses(logger),
            "test-read" or "test" => await TestRead(args, loggerFactory, logger),
            "help" or "--help" or "-h" => PrintUsage(),
            _ => PrintUnknownCommand(command),
        };
    }

    // ─── Commands ──────────────────────────────────────────────────────

    private static async Task<int> RunBot(string[] args, ILoggerFactory loggerFactory, ILogger logger)
    {
        var botName = GetArgValue(args, "--bot") ?? "mining";
        var pidStr = GetArgValue(args, "--pid");
        var exePath = GetArgValue(args, "--exe") ?? "read-memory-64-bit.exe";
        var tickStr = GetArgValue(args, "--tick");
        var debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

        IBot bot = botName.ToLowerInvariant() switch
        {
            "mining" => new MiningBot(),
            _ => throw new ArgumentException($"Unknown bot: {botName}. Available: mining"),
        };

        var settings = bot.GetDefaultSettings();
        settings.Sanderling.ExecutablePath = exePath;
        if (debugMode) settings.LogMemoryReadings = true;

        if (pidStr != null && int.TryParse(pidStr, out var pid))
            settings.Sanderling.ProcessId = pid;

        if (tickStr != null && int.TryParse(tickStr, out var tick))
            settings.TickIntervalMs = tick;

        logger.LogInformation("Bot: {Name} | PID: {Pid} | Tick: {Tick}ms",
            bot.Name,
            settings.Sanderling.ProcessId > 0 ? settings.Sanderling.ProcessId.ToString() : "auto",
            settings.TickIntervalMs);

        var reader = new SanderlingReader(settings.Sanderling,
            loggerFactory.CreateLogger<SanderlingReader>());
        var parser = new UITreeParser(loggerFactory.CreateLogger<UITreeParser>());
        var input = new InputSimulator(loggerFactory.CreateLogger<InputSimulator>());
        var executor = new ActionExecutor(input, loggerFactory.CreateLogger<ActionExecutor>());

        using var runner = new BotRunner(bot, settings, reader, parser, input, executor,
            loggerFactory.CreateLogger<BotRunner>());

        runner.OnTick += ctx =>
        {
            var gs = ctx.GameState;
            logger.LogInformation(
                "Tick #{Tick} | {State} | Targets: {Targets} | Cap: {Cap}% | Actions: {Actions}",
                ctx.TickCount,
                gs.IsInSpace ? "In Space" : gs.IsDocked ? "Docked" : "Unknown",
                gs.TargetCount,
                gs.CapacitorPercent?.ToString() ?? "N/A",
                ctx.Actions.Count);
        };

        runner.OnError += ex =>
        {
            logger.LogError(ex, "Bot error");
        };

        // Handle Ctrl+C
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Ctrl+C received — stopping bot...");
            exitEvent.Set();
        };

        runner.Start();
        logger.LogInformation("Bot is running. Press Ctrl+C to stop.");

        exitEvent.Wait();
        await runner.StopAsync();

        return 0;
    }

    private static int ListProcesses(ILogger logger)
    {
        var clients = EveProcessFinder.FindEveClients();

        if (clients.Count == 0)
        {
            logger.LogWarning("No EVE Online client processes found.");
            return 1;
        }

        logger.LogInformation("Found {Count} EVE Online client(s):", clients.Count);
        foreach (var client in clients)
        {
            Console.WriteLine($"  {client}");
        }

        return 0;
    }

    private static async Task<int> TestRead(string[] args, ILoggerFactory loggerFactory, ILogger logger)
    {
        var pidStr = GetArgValue(args, "--pid");
        var exePath = GetArgValue(args, "--exe") ?? "read-memory-64-bit.exe";

        var config = new SanderlingConfig
        {
            ExecutablePath = exePath,
        };

        if (pidStr != null && int.TryParse(pidStr, out var pid))
            config.ProcessId = pid;

        var reader = new SanderlingReader(config, loggerFactory.CreateLogger<SanderlingReader>());
        var parser = new UITreeParser(loggerFactory.CreateLogger<UITreeParser>());

        logger.LogInformation("Performing test memory read...");

        var result = await reader.ReadMemoryAsync();
        if (!result.IsSuccess)
        {
            logger.LogError("Memory reading failed: {Error}", result.ErrorMessage);
            return 1;
        }

        logger.LogInformation("Memory reading successful! JSON size: {Size} bytes, Time: {Time}ms",
            result.Json!.Length, result.Elapsed.TotalMilliseconds);

        // Parse and display summary
        var ui = parser.Parse(result.Json!);

        Console.WriteLine();
        Console.WriteLine("=== Parsed UI Summary ===");
        Console.WriteLine($"  Ship UI:          {(ui.ShipUI != null ? "Present" : "Not found")}");
        Console.WriteLine($"  Overview windows: {ui.OverviewWindows.Count}");
        Console.WriteLine($"  Targets:          {ui.Targets.Count}");
        Console.WriteLine($"  Inventory:        {ui.InventoryWindows.Count}");
        Console.WriteLine($"  Drones:           {(ui.DronesWindow != null ? "Present" : "Not found")}");
        Console.WriteLine($"  Context menus:    {ui.ContextMenus.Count}");
        Console.WriteLine($"  Chat stacks:      {ui.ChatWindowStacks.Count}");
        Console.WriteLine($"  Message boxes:    {ui.MessageBoxes.Count}");
        Console.WriteLine($"  Station window:   {(ui.StationWindow != null ? "Present" : "Not found")}");
        Console.WriteLine($"  Neocom:           {(ui.Neocom != null ? "Present" : "Not found")}");

        if (ui.ShipUI != null)
        {
            Console.WriteLine();
            Console.WriteLine("=== Ship UI ===");
            Console.WriteLine($"  Capacitor:  {ui.ShipUI.Capacitor?.LevelPercent ?? -1}%");
            Console.WriteLine($"  Shield:     {ui.ShipUI.HitpointsPercent?.Shield ?? -1}%");
            Console.WriteLine($"  Armor:      {ui.ShipUI.HitpointsPercent?.Armor ?? -1}%");
            Console.WriteLine($"  Structure:  {ui.ShipUI.HitpointsPercent?.Structure ?? -1}%");
            Console.WriteLine($"  Modules:    {ui.ShipUI.ModuleButtons.Count} total");
            Console.WriteLine($"    Top:      {ui.ShipUI.ModuleButtonsRows.Top.Count}");
            Console.WriteLine($"    Middle:   {ui.ShipUI.ModuleButtonsRows.Middle.Count}");
            Console.WriteLine($"    Bottom:   {ui.ShipUI.ModuleButtonsRows.Bottom.Count}");
        }

        // Save the JSON for inspection
        var outputPath = $"test_reading_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json";
        await File.WriteAllTextAsync(outputPath, result.Json!);
        logger.LogInformation("Raw JSON saved to: {Path}", Path.GetFullPath(outputPath));

        return 0;
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
            if (args[i].StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                return args[i][(key.Length + 1)..];
        }
        // Check last arg for key=value format
        if (args.Length > 0 && args[^1].StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
            return args[^1][(key.Length + 1)..];
        return null;
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("""
        
         ███████╗██████╗  ██████╗ ████████╗
         ██╔════╝██╔══██╗██╔═══██╗╚══██╔══╝
         █████╗  ██████╔╝██║   ██║   ██║   
         ██╔══╝  ██╔══██╗██║   ██║   ██║   
         ███████╗██████╔╝╚██████╔╝   ██║   
         ╚══════╝╚═════╝  ╚═════╝    ╚═╝   
                 EVE Online Bot Framework
        
        """);
        Console.ResetColor();
    }

    private static int PrintUsage()
    {
        Console.WriteLine("Usage: EBot.Runner <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  run              Run a bot");
        Console.WriteLine("    --bot <name>   Bot to run (default: mining)");
        Console.WriteLine("    --pid <pid>    EVE process ID (default: auto-detect)");
        Console.WriteLine("    --exe <path>   Path to read-memory-64-bit.exe");
        Console.WriteLine("    --tick <ms>    Tick interval in milliseconds");
        Console.WriteLine("    --debug        Enable frame/screenshot logging");
        Console.WriteLine();
        Console.WriteLine("  list-processes   List running EVE Online clients");
        Console.WriteLine();
        Console.WriteLine("  test-read        Perform a single memory reading");
        Console.WriteLine("    --pid <pid>    EVE process ID (default: auto-detect)");
        Console.WriteLine("    --exe <path>   Path to read-memory-64-bit.exe");
        Console.WriteLine();
        Console.WriteLine("  help             Show this help message");
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown command: {command}");
        Console.ResetColor();
        PrintUsage();
        return 1;
    }
}
