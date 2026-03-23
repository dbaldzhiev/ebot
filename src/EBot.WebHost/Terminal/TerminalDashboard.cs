using EBot.Core.Bot;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace EBot.WebHost.Terminal;

/// <summary>
/// Runs a Spectre.Console live dashboard in the terminal.
/// Shows bot state, ship status, overview entries, and recent logs.
/// </summary>
public sealed class TerminalDashboard(
    BotOrchestrator orchestrator,
    LogSink logSink,
    int port) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give ASP.NET Core a moment to finish startup
        await Task.Delay(500, stoppingToken);

        PrintBanner(port);

        await AnsiConsole.Live(BuildLayout())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        ctx.UpdateTarget(BuildLayout());
                        await Task.Delay(1000, stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* ignore render errors */ }
                }
            });
    }

    private IRenderable BuildLayout()
    {
        var state = orchestrator.State;
        var botName = orchestrator.CurrentBotName ?? "none";
        var ctx = orchestrator.LastContext;

        // ─── Header bar ───────────────────────────────────────────────────
        var stateColor = state switch
        {
            BotRunnerState.Running => "green",
            BotRunnerState.Paused => "yellow",
            _ => "grey",
        };
        var stateText = $"[{stateColor}]{state}[/]";

        var tpm = orchestrator.TicksPerMinute;
        var tpmText = tpm > 0 ? $"  [cyan]{tpm:F1}[/] tpm" : "";

        var header = new Panel(
            new Markup($"  [bold cyan]EBot[/] [dim]//[/] Bot: [white]{Markup.Escape(botName)}[/]  " +
                       $"State: {stateText}{tpmText}  " +
                       $"[dim]Web UI → http://localhost:{port}   MCP → http://localhost:{port}/mcp[/]"))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(0, 0),
        };

        // ─── Bot + Game Status ─────────────────────────────────────────────
        var statusTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Key", c => c.Width(14))
            .AddColumn("Value");

        if (ctx != null)
        {
            var gs = ctx.GameState;
            var ui = gs.ParsedUI;

            var tpmRow = tpm > 0 ? $"[cyan]{tpm:F1}[/] /min" : "[grey]—[/]";
            statusTable
                .AddRow("[dim]Tick[/]", ctx.TickCount.ToString())
                .AddRow("[dim]TPM[/]", tpmRow)
                .AddRow("[dim]Runtime[/]", ctx.RunDuration.ToString(@"hh\:mm\:ss"))
                .AddRow("[dim]Location[/]", gs.IsInSpace ? "[green]Space[/]" : gs.IsDocked ? "[blue]Docked[/]" : "[grey]Unknown[/]")
                .AddRow("[dim]Maneuver[/]", Markup.Escape(ui.ShipUI?.Indication?.ManeuverType ?? "—"));

            if (ui.ShipUI != null)
            {
                var hp = ui.ShipUI.HitpointsPercent;
                var cap = ui.ShipUI.Capacitor?.LevelPercent;
                statusTable
                    .AddRow("[dim]Shield[/]", HpBar(hp?.Shield))
                    .AddRow("[dim]Armor[/]", HpBar(hp?.Armor))
                    .AddRow("[dim]Structure[/]", HpBar(hp?.Structure))
                    .AddRow("[dim]Capacitor[/]", CapBar(cap))
                    .AddRow("[dim]Speed[/]", Markup.Escape(ui.ShipUI.SpeedText ?? "—"));

                var mods = ui.ShipUI.ModuleButtons;
                if (mods.Count > 0)
                {
                    var modStr = string.Concat(mods.Select(m =>
                        m.IsBusy         ? "[yellow]◈[/]" :
                        m.IsActive == true  ? "[green]◉[/]" :
                        m.IsActive == false ? "[grey]○[/]" : "[grey]·[/]"));
                    statusTable.AddRow("[dim]Modules[/]", modStr);
                }
            }

            statusTable.AddRow("[dim]Targets[/]", gs.TargetCount.ToString());
        }
        else
        {
            statusTable.AddRow("[dim]Status[/]", "[grey]No game state — start a bot[/]");
        }

        var statusPanel = new Panel(statusTable)
        {
            Header = new PanelHeader("[bold]Ship Status[/]"),
            Border = BoxBorder.Rounded,
        };

        // ─── Overview ─────────────────────────────────────────────────────
        var overviewTable = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Name")
            .AddColumn("Type")
            .AddColumn("Distance", c => c.RightAligned());

        var entries = ctx?.GameState.ParsedUI.OverviewWindows.FirstOrDefault()?.Entries ?? [];
        if (entries.Count == 0)
        {
            overviewTable.AddRow("[grey]—[/]", "", "");
        }
        else
        {
            foreach (var e in entries.Take(10))
            {
                var name = Markup.Escape(e.Name ?? "—");
                if (e.IsAttackingMe) name = $"[red]{name}[/]";
                overviewTable.AddRow(name, Markup.Escape(e.ObjectType ?? ""), Markup.Escape(e.DistanceText ?? ""));
            }

            if (entries.Count > 10)
                overviewTable.AddRow($"[grey]… {entries.Count - 10} more[/]", "", "");
        }

        var overviewPanel = new Panel(overviewTable)
        {
            Header = new PanelHeader($"[bold]Overview[/] [dim]({entries.Count})[/]"),
            Border = BoxBorder.Rounded,
        };

        // ─── Log ──────────────────────────────────────────────────────────
        var logs = logSink.GetRecent(12);
        var logTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Time", c => c.Width(10))
            .AddColumn("Lvl", c => c.Width(5))
            .AddColumn("Message");

        foreach (var entry in logs)
        {
            var levelColor = entry.Level switch
            {
                "Error" or "Crit" => "red",
                "Warn" => "yellow",
                "Info" => "green",
                _ => "grey",
            };
            logTable.AddRow(
                $"[grey]{entry.Time:HH:mm:ss}[/]",
                $"[{levelColor}]{entry.Level,-4}[/]",
                Markup.Escape(entry.Message));
        }

        var logPanel = new Panel(logTable)
        {
            Header = new PanelHeader("[bold]Log[/]"),
            Border = BoxBorder.Rounded,
        };

        // ─── Compose ──────────────────────────────────────────────────────
        var topRow = new Columns(statusPanel, overviewPanel);

        return new Rows(header, topRow, logPanel);
    }

    private static string HpBar(int? pct)
    {
        if (pct == null) return "[grey]N/A[/]";
        var color = pct >= 70 ? "green" : pct >= 30 ? "yellow" : "red";
        var bar = new string('█', pct.Value / 10).PadRight(10, '░');
        return $"[{color}]{bar}[/] {pct}%";
    }

    private static string CapBar(int? pct)
    {
        if (pct == null) return "[grey]N/A[/]";
        var color = pct >= 50 ? "cyan" : pct >= 20 ? "yellow" : "red";
        var bar = new string('█', pct.Value / 10).PadRight(10, '░');
        return $"[{color}]{bar}[/] {pct}%";
    }

    private static void PrintBanner(int port)
    {
        AnsiConsole.Write(new FigletText("EBot").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]EVE Online Bot Framework[/]");
        AnsiConsole.MarkupLine($"Web UI  → [link]http://localhost:{port}[/]");
        AnsiConsole.MarkupLine($"MCP     → [link]http://localhost:{port}/mcp[/]  [dim](connect your AI agent here)[/]");
        AnsiConsole.MarkupLine($"Press [bold]Ctrl+C[/] to exit\n");
    }
}
