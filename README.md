# EBot — EVE Online Bot Framework

A mature C# framework for automating EVE Online. Reads the live game UI directly from process memory, parses it into typed models, runs a behavior-tree decision engine, and simulates mouse/keyboard input — exposed through a real-time web dashboard, REST API, SignalR feed, and MCP server for AI agents.

```
Read (in-process memory) → Parse (UI tree) → Decide (behavior tree) → Act (Win32 input) → Repeat
```

---

## Requirements

| Requirement | Notes |
|---|---|
| .NET SDK | **9.0** |
| OS | Windows 10 / 11 (Win32 input simulation) |
| EVE Online | 64-bit client, running and logged in |

No external executables required. Memory is read in-process via the bundled `read-memory-64-bit.dll`.

---

## Quick Start

```powershell
git clone <repo-url> ebot && cd ebot
dotnet build
dotnet run --project src/EBot.WebHost
start http://localhost:5000
```

The framework auto-detects the EVE client. First memory scan takes ~60 s; subsequent ticks are fast (< 500 ms).

---

## Available Bots

| Bot | Description |
|---|---|
| **Mining Bot** | Mines asteroid belts, docks when ore hold full, surveys with the Mining Surveyor, manages drones, multi-belt discovery with depletion tracking |
| **Mining Bot (Scripted)** | Script-driven mining using a simple command DSL |
| **Travel Bot** | Follows in-game autopilot route with warp-to-0; auto-docks at destination; handles gates, stations, and player structures |
| **Travel Bot (Scripted)** | Scripted autopilot using `AUTOPILOT` command |

When no bot is active, **Monitor mode** runs continuously — reading and broadcasting game state with no input.

---

## Web Dashboard (`http://localhost:5000`)

The single-page dashboard updates in real time via SignalR.

**HUD:**
- Shield / Armor / Hull / Capacitor bars with percentages
- Speed, system name, security status (color-coded)
- Docked / In Space / Warping state

**Ship systems:**
- Module grid — colored by state (green=active, yellow=busy, red=overloaded, gray=offline)
- Drones panel — in-space vs in-bay counts with HP bars
- Holds panel — all cargo/ore holds with capacity gauge and item list

**Situational awareness:**
- Targets panel — locked targets with HP mini-bars and distance
- Overview table — space objects with name, type, distance

**Mining dashboard** (shown when Mining Bot is active):
- Top-5 asteroid target queue with scores and status
- Belt Indexer — list of discovered belts with depleted/excluded toggles

**Travel section:**
- Station search (powered by EVE SDE — instant results)
- Saved destinations list
- Recent destinations (last 5, browser-stored)

**Logs:**
- Scrolling event log, last 100 entries

---

## REST API

| Method | Path | Description |
|---|---|---|
| GET | `/api/status` | Bot state + full game summary |
| GET | `/api/bots` | Available bot names |
| GET | `/api/processes` | Running EVE clients |
| POST | `/api/start` | Start a bot (`{ botName, pid?, destination?, homeStation?, oreHoldFullPct?, shieldEscapePct? }`) |
| POST | `/api/stop` | Stop bot, return to monitor mode |
| POST | `/api/emergency-stop` | Hard stop + release all input |
| POST | `/api/pause` / `/api/resume` | Pause control |
| POST | `/api/survival` | Toggle survival wrapper (`{ enabled: bool }`) |
| GET | `/api/log?count=50` | Recent log entries |
| GET | `/api/mining-stats` | Session stats (m³ unloaded, cycles, blackboard snapshot) |
| GET | `/api/mining/belts` | Discovered belt registry |
| POST | `/api/mining/belts/{idx}/toggle` | Toggle belt user-excluded status |
| GET | `/api/stations/search?q=jita` | SDE station search |
| GET | `/api/stations/sde-status` | SDE download progress |
| POST | `/api/save-frame` | Save raw UI JSON to `logs/frames/` |
| GET | `/api/dpi` | System DPI + coordinate scale |
| POST | `/api/dpi/scale` | Set coordinate scale (`{ scale: 1.25 }`) |
| GET | `/api/debug/reader` | Memory reader health |
| GET | `/api/debug/input` | Coordinate / DPI diagnostics |
| GET | `/api/debug/infopanel` | InfoPanel node tree dump |
| POST | `/api/undock` | Click Undock |
| POST | `/api/dock` | Right-click nearest station → Dock |

**SignalR** at `/botHub`: `TickUpdate`, `LogEntry`, `ActionLog`, `StateChanged`, `EmergencyStop`

**MCP** at `/mcp` (SSE): connect Claude Desktop or any MCP client for AI agent control.

---

## Emergency Stop

Press **Pause/Break** at any time — works even when EVE has focus. Immediately stops the bot and releases all held input.

---

## AI Configuration

Chat panel supports two backends:

```powershell
# Anthropic Claude (cloud)
$env:EBOT_AI_BACKEND  = "anthropic"
$env:ANTHROPIC_API_KEY = "sk-ant-..."

# Ollama (local)
$env:EBOT_AI_BACKEND   = "ollama"
$env:EBOT_OLLAMA_URL   = "http://localhost:11434"
$env:EBOT_OLLAMA_MODEL = "llama3.1"

dotnet run --project src/EBot.WebHost
```

| Variable | Default | Description |
|---|---|---|
| `EBOT_AI_BACKEND` | `ollama` | `anthropic` or `ollama` |
| `EBOT_OLLAMA_URL` | `http://192.168.1.40:11434` | Ollama server URL |
| `EBOT_OLLAMA_MODEL` | `llama3.2` | Model name |
| `ANTHROPIC_API_KEY` | *(required)* | Anthropic API key |

---

## DPI / Click Accuracy

If clicks land in the wrong position (common on HiDPI or with Windows display scaling):

- **Web UI**: enter scale factor in the **Scale** input → click Scale
- **API**: `POST /api/dpi/scale` with `{ "scale": 1.25 }`
- **Check**: `GET /api/dpi`

`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` is called at startup so `SetCursorPos` works in physical pixels matching EVE's coordinate space.

---

## Writing a Bot

See `CLAUDE.md` (developer guide) and `AGENTS.md` (deep technical reference).

Quick template:

```csharp
public sealed class MyBot : IBot
{
    public string Name => "My Bot";
    public string Description => "Does something.";
    public BotSettings GetDefaultSettings() => new() { TickIntervalMs = 2000 };
    public void OnStart(BotContext ctx) { }
    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Root",
            new SequenceNode("Do thing",
                new ConditionNode("In space?", ctx => ctx.GameState.IsInSpace),
                new ActionNode("Click module", ctx =>
                {
                    var mod = ctx.GameState.ParsedUI.ShipUI?.ModuleButtons.FirstOrDefault();
                    if (mod == null) return NodeStatus.Failure;
                    ctx.Click(mod.UINode);
                    return NodeStatus.Success;
                })),
            new ActionNode("Idle", _ => NodeStatus.Success));
}
```

Register in `BotOrchestrator.AvailableBots`.

---

## Reference Material

| Path | What's there |
|---|---|
| `ref/botimplement/eve-online-mining-bot/EveOnline/ParseUserInterface.elm` | Canonical EVE UI Python object type names and dict keys |
| `ref/botimplement/eve-online-mining-bot/Bot.elm` | Reference mining bot logic (Elm) |
| `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` | Source of the three DLL APIs EBot calls |
| `AGENTS.md` | Deep technical reference: all types, all endpoints, all blackboard keys |

---

## Safety Notes

- Memory reading is **read-only** — no code injection, no writes to EVE's process.
- Input simulation uses standard Win32 APIs detectable by client-side anti-cheat. **Use at your own risk.**
- Humanized delays + coordinate jitter reduce bot-pattern signatures but are not a guarantee.
