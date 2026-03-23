# EBot — EVE Online Bot Framework

A C# framework for automating EVE Online. It reads the live game UI directly from
process memory, parses it into typed models, runs a behavior-tree decision engine,
and simulates mouse/keyboard input — all exposed through a real-time web dashboard,
REST API, SignalR feed, and MCP server for AI agents.

```
Read (in-process memory) → Parse (UI tree) → Decide (behavior tree) → Act (Win32 input) → Repeat
```

---

## Requirements

| Requirement | Version / Notes |
|---|---|
| .NET SDK | **9.0** |
| OS | Windows 10 / 11 (Win32 input simulation) |
| EVE Online | 64-bit client, running and logged in |

No external executables required — memory is read in-process via the bundled
`read-memory-64-bit.dll`.

---

## Quick Start

```powershell
# 1. Clone
git clone <repo-url> ebot
cd ebot

# 2. Build
dotnet build

# 3. Launch web server (EVE must be running)
dotnet run --project src/EBot.WebHost

# 4. Open dashboard
start http://localhost:5000
```

The framework auto-detects the EVE client. The first memory scan takes ~60 seconds;
subsequent ticks are fast (< 500 ms). Ship status, overview, and location appear in
the dashboard once the scan completes.

Custom port:

```powershell
dotnet run --project src/EBot.WebHost -- --port 5001
```

---

## Repository Layout

```
ebot/
│
├── src/                            # All production source code
│   ├── EBot.Core/                  # Framework library — no web, no UI
│   │   ├── MemoryReading/
│   │   │   ├── IEveMemoryReader.cs      # Interface: ReadMemoryAsync()
│   │   │   ├── DirectMemoryReader.cs    # In-process read via read-memory-64-bit.dll
│   │   │   ├── SanderlingReader.cs      # Fallback: CLI exe or HTTP alternate-ui
│   │   │   ├── EveProcessFinder.cs      # Finds "exefile" / "eve" processes
│   │   │   └── AlternateUiClient.cs     # HTTP client for alternate-ui server
│   │   ├── GameState/
│   │   │   ├── UITreeNode.cs            # Raw deserialized node + dict accessors
│   │   │   ├── UITreeParser.cs          # JSON → ParsedUI (all element finders)
│   │   │   ├── ParsedUI.cs              # Typed models: ShipUI, Overview, Targets…
│   │   │   └── GameStateSnapshot.cs     # Per-tick snapshot + computed properties
│   │   ├── DecisionEngine/
│   │   │   ├── BehaviorTree.cs          # Node types: Sequence, Selector, Condition, Action…
│   │   │   ├── BotContext.cs            # Passed to nodes: GameState, Blackboard, Actions
│   │   │   └── Blackboard.cs            # Persistent key-value store + cooldown helpers
│   │   ├── Execution/
│   │   │   ├── InputSimulator.cs        # Win32 SetCursorPos + mouse_event/keybd_event
│   │   │   ├── BotAction.cs             # Action types: Click, RightClick, KeyPress, Wait…
│   │   │   └── ActionExecutor.cs        # Drains the action queue each tick
│   │   └── Bot/
│   │       ├── IBot.cs                  # Interface every bot implements
│   │       ├── BotRunner.cs             # Main loop: Read → Parse → Decide → Act
│   │       └── BotSettings.cs           # Tick interval, delays, jitter, max runtime
│   │
│   ├── EBot.ExampleBots/           # Concrete bot implementations
│   │   ├── MiningBot/MiningBot.cs       # Mine asteroids, dock when hold full
│   │   ├── AutopilotBot/AutopilotBot.cs # Warp-to-0 autopilot along a route
│   │   ├── SurvivalNodes.cs             # Reusable emergency-tank behavior wrapper
│   │   └── IdleBot.cs                   # Monitor-only bot (reads state, no actions)
│   │
│   ├── EBot.WebHost/               # ASP.NET Core web server (primary entry point)
│   │   ├── Program.cs                   # DI setup, all REST endpoints, startup
│   │   ├── BotOrchestrator.cs           # Singleton owning BotRunner; exposes control
│   │   ├── Models.cs                    # DTOs: GameStateSummary, StartRequest, …
│   │   ├── Hubs/BotHub.cs               # SignalR hub: TickUpdate, StateChanged, LogEntry
│   │   ├── Mcp/EveBotMcpTools.cs        # MCP tools for AI agents
│   │   ├── Services/ChatService.cs      # Anthropic Claude AI backend
│   │   ├── Services/OllamaChatService.cs# Ollama local LLM backend
│   │   ├── Terminal/TerminalDashboard.cs# Spectre.Console TUI (shown in the terminal)
│   │   └── wwwroot/index.html           # Single-page browser dashboard
│   │
│   └── EBot.Runner/                # Minimal console CLI (alternative to WebHost)
│       └── Program.cs
│
├── tests/
│   └── EBot.Tests/                 # xUnit test project
│       ├── EBot.Tests.csproj            # References EBot.Core + EBot.ExampleBots
│       └── UnitTest1.cs                 # Placeholder — expand with parser / BT tests
│
├── read-memory-64/                 # Bundled Viir/bots binaries (separate-assemblies build)
│   ├── read-memory-64-bit.dll           # Key DLL: EveOnline64.* API
│   ├── Pine.Core.dll                    # Dependency of the above
│   ├── read-memory-64-bit.exe           # Standalone CLI (used by SanderlingReader fallback)
│   └── runtimes/                        # Native runtime dependencies
│
├── ref/                            # Read-only reference repos (git-ignored, re-clone as needed)
│   ├── Sanderling/                      # github.com/Arcitectus/Sanderling
│   │   ├── implement/read-memory-64-bit/    # C# source of the memory-reading engine (EveOnline64.cs)
│   │   └── implement/alternate-ui/          # Elm/JS alternate UI renderer (not used by EBot)
│   └── bots/guide/eve-online/           # github.com/Viir/bots — EVE UI parsing docs & bot guides
│       ├── parsed-user-interface-of-the-eve-online-game-client.md  # node types, JSON schema
│       ├── developing-for-eve-online.md
│       ├── eve-online-warp-to-0-autopilot-bot.md
│       └── eve-online-mining-bot.md
│
├── inspect-asm/                    # Scratch project for inspecting DLL API (not built)
│
├── AGENTS.md                       # Deep technical reference for AI agents
└── README.md                       # This file
```

### Project dependency graph

```
EBot.Tests ──────────────────────────────┐
                                         ▼
EBot.WebHost ──► EBot.ExampleBots ──► EBot.Core ──► read-memory-64-bit.dll
EBot.Runner  ──► EBot.ExampleBots ──► EBot.Core       Pine.Core.dll
```

---

## NuGet References

| Project | Package | Version | Purpose |
|---|---|---|---|
| EBot.Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.5 | Logging interfaces |
| EBot.WebHost | `Microsoft.AspNetCore.SignalR` | 1.1.0 | Real-time push to browser |
| EBot.WebHost | `ModelContextProtocol.AspNetCore` | 1.1.0 | MCP server (AI agent interface) |
| EBot.WebHost | `Anthropic` | 12.9.0 | Claude AI chat backend |
| EBot.WebHost | `Spectre.Console` | 0.49.1 | Terminal dashboard TUI |
| EBot.Tests | `xunit` | 2.5.3 | Test framework |
| EBot.Tests | `xunit.runner.visualstudio` | 2.5.3 | VS / dotnet test runner |
| EBot.Tests | `coverlet.collector` | 6.0.0 | Code coverage |
| EBot.Tests | `Microsoft.NET.Test.Sdk` | 17.8.0 | Test host |

### Native / non-NuGet references

| DLL | Source | Used by |
|---|---|---|
| `read-memory-64-bit.dll` | `read-memory-64/` folder | `EBot.Core` (DirectMemoryReader) |
| `Pine.Core.dll` | `read-memory-64/` folder | Dependency of read-memory-64-bit.dll |

Both DLLs are referenced via `<Reference>` with a `HintPath` in `EBot.Core.csproj`
and copied to the output directory automatically.

---

## Reference Repositories

`ref/` holds two upstream projects as **read-only reference material** — not part of
the build, not tracked in git. Consult them when reverse-engineering new EVE UI
elements or when the memory-reading DLL API changes.

| Path | Source | What to read there |
|---|---|---|
| `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` | [Arcitectus/Sanderling](https://github.com/Arcitectus/Sanderling) | Source of all three `EveOnline64.*` APIs called by `DirectMemoryReader` |
| `ref/Sanderling/implement/read-memory-64-bit/MemoryReader.cs` | same | Win32 `ReadProcessMemory` wrapper |
| `ref/bots/guide/eve-online/parsed-user-interface-of-the-eve-online-game-client.md` | [Viir/bots](https://github.com/Viir/bots/tree/main/guide/eve-online) | Authoritative catalogue of EVE UI node types, `dictEntriesOfInterest` key names, JSON schema |
| `ref/bots/guide/eve-online/developing-for-eve-online.md` | same | Bot development walkthrough |
| `ref/bots/guide/eve-online/eve-online-warp-to-0-autopilot-bot.md` | same | Autopilot design patterns |
| `ref/bots/guide/eve-online/eve-online-mining-bot.md` | same | Mining bot design patterns |

**Re-cloning** (from the repo root):

```bash
mkdir -p ref

# Full Sanderling source
git clone https://github.com/Arcitectus/Sanderling ref/Sanderling

# Viir bots guide — sparse checkout of guide/eve-online only
git clone --filter=blob:none --no-checkout --depth=1 https://github.com/Viir/bots ref/bots
cd ref/bots && git sparse-checkout init --cone && git sparse-checkout set guide/eve-online && git checkout main && cd ../..
```

---

## Running Tests

```powershell
dotnet test tests/EBot.Tests
```

Tests reference `EBot.Core` and `EBot.ExampleBots` directly. The test project is
the right place for:
- `UITreeParser` unit tests (feed known JSON, assert parsed fields)
- Behavior tree logic tests (mock `BotContext`, assert action queue contents)
- `EveTextUtil.StripTags` edge cases

---

## Web Dashboard

Launch with `dotnet run --project src/EBot.WebHost`, then open `http://localhost:5000`.

| Panel | Content |
|---|---|
| **Ship Status** | System name, security status (coloured by value), in-space / docked / warping, shield / armor / structure / capacitor, speed, context menu entries |
| **Overview** | Live table of objects in space with name, type, distance |
| **Log** | Scrolling log; all levels captured including Debug |
| **AI Command** | Natural-language chat; Claude or Ollama executes bot commands |

Header controls: **Start Bot**, **Pause**, **Resume**, **Stop**, **Survival** toggle,
**DPI scale** input, **Diag** button (runs the live reader diagnostic).

### REST API

| Method | Path | Body / Params | Description |
|---|---|---|---|
| GET | `/api/status` | — | Full state: bot state + `GameStateSummary` |
| GET | `/api/bots` | — | Available bot names + descriptions |
| GET | `/api/processes` | — | Running EVE clients (pid, name, window title) |
| POST | `/api/start` | `{ botName, pid?, exePath?, tickMs? }` | Start a bot |
| POST | `/api/stop` | — | Stop active bot, return to monitor mode |
| POST | `/api/pause` | — | Pause |
| POST | `/api/resume` | — | Resume |
| POST | `/api/survival` | `{ enabled: bool }` | Toggle survival wrapper |
| GET | `/api/log` | `?count=50` | Recent log entries |
| GET | `/api/dpi` | — | System DPI + current coordinate scale |
| POST | `/api/dpi/scale` | `{ scale: float }` | Override coordinate scale (0.1–4.0) |
| GET | `/api/debug/reader` | — | Live diagnostic: EVE process, JSON length, parsed element counts |
| GET | `/api/debug/infopanel` | — | Dump InfoPanelLocationInfo node tree (for UI patch analysis) |

### SignalR — `/botHub`

| Event | Payload |
|---|---|
| `TickUpdate` | `GameStateSummary` (camelCase) on every tick |
| `StateChanged` | `"Idle"` / `"Running"` / `"Paused"` |
| `LogEntry` | `{ time, level, category, message }` |
| `SurvivalChanged` | `bool` |

### MCP — `/mcp`

SSE transport. Connect Claude Desktop, Claude Code, or any MCP client.
See `AGENTS.md` for the full tool list and intended agent workflow.

---

## Writing a Bot

Implement `IBot` in `EBot.ExampleBots` (or a new project):

```csharp
public sealed class MyBot : IBot
{
    public string Name => "My Bot";
    public string Description => "Does something useful.";

    public BotSettings GetDefaultSettings() => new() { TickIntervalMs = 2000 };
    public void OnStart(BotContext ctx) { }
    public void OnStop(BotContext ctx) { }

    public IBehaviorNode BuildBehaviorTree() =>
        new SelectorNode("Root",
            new SequenceNode("Mine asteroid",
                new ConditionNode("In space?",   ctx => ctx.GameState.IsInSpace),
                new ConditionNode("Not warping?", ctx => !ctx.GameState.IsWarping),
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

Register it in `BotOrchestrator.AvailableBots`:

```csharp
public static readonly IReadOnlyList<IBot> AvailableBots =
[
    new MiningBot(),
    new AutopilotBot(),
    new MyBot(),   // ← add here
];
```

### Behavior tree nodes

| Node | Semantics |
|---|---|
| `SequenceNode` | AND — runs children in order, stops on first Failure |
| `SelectorNode` | OR — runs children in order, stops on first Success |
| `ConditionNode` | Wraps `Func<BotContext, bool>` → Success / Failure |
| `ActionNode` | Wraps `Func<BotContext, NodeStatus>` |
| `InverterNode` | Inverts child result |
| `AlwaysSucceedNode` | Child can never fail |

### BotContext helpers

```csharp
ctx.Click(uiNode)                           // left-click node center
ctx.RightClick(uiNode)                      // right-click node center
ctx.ClickAt(x, y)                           // click absolute coords
ctx.KeyPress(VirtualKey.F1)                 // key press
ctx.KeyPress(VirtualKey.C, VirtualKey.Alt)  // key + modifier
ctx.TypeText("Jita")                        // type string
ctx.Wait(TimeSpan.FromSeconds(2))           // delay
ctx.Blackboard.Set("key", value)
ctx.Blackboard.Get<T>("key")
ctx.Blackboard.SetCooldown("name", TimeSpan.FromSeconds(5))
ctx.Blackboard.IsCooldownReady("name")
```

### Survival wrapper

Wrap any tree to add automatic emergency response (dismiss message boxes, activate
tank modules on low shields, stop ship on critical structure):

```csharp
public IBehaviorNode BuildBehaviorTree() =>
    SurvivalNodes.Wrap(MyMainTree());
```

This is applied automatically when **Survival mode** is enabled in the orchestrator.

---

## AI Configuration

The chat panel supports two backends, selected via `EBOT_AI_BACKEND`.

### Anthropic Claude (cloud)

```powershell
$env:EBOT_AI_BACKEND  = "anthropic"
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run --project src/EBot.WebHost
```

Uses **Claude Opus 4.6** with extended thinking.

### Ollama (local)

```powershell
$env:EBOT_AI_BACKEND   = "ollama"
$env:EBOT_OLLAMA_URL   = "http://localhost:11434"
$env:EBOT_OLLAMA_MODEL = "llama3.1"
dotnet run --project src/EBot.WebHost
```

Recommended tool-capable models: `llama3.1:8b`, `llama3.2:3b`, `qwen2.5:7b`.

#### Expose Ollama on LAN (Windows)

Set `OLLAMA_HOST=0.0.0.0` as a system environment variable, then restart Ollama from
the system tray. Ensure port **11434** is open in Windows Firewall.

#### Environment variable reference

| Variable | Default | Description |
|---|---|---|
| `EBOT_AI_BACKEND` | `ollama` | `anthropic` or `ollama` |
| `EBOT_OLLAMA_URL` | `http://192.168.1.40:11434` | Ollama server base URL |
| `EBOT_OLLAMA_MODEL` | `llama3.2` | Model name (must be pulled first) |
| `ANTHROPIC_API_KEY` | *(required for Anthropic)* | Anthropic API key |

---

## DPI / Click Accuracy

`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` is called
at startup so `SetCursorPos` uses physical pixels, matching EVE's `_displayX`/`_displayY`
coordinate space.

If clicks land in the wrong position (common on HiDPI monitors or with Windows display
scaling), adjust the coordinate scale:

- **Via the web UI** — enter a scale factor in the **Scale** input in the header and click **Scale**
- **Via API** — `POST /api/dpi/scale` with `{ "scale": 1.25 }`
- **Check current DPI** — `GET /api/dpi`

A scale of `1.0` means no correction. Values `> 1.0` move clicks further right/down.

---

## BotSettings Reference

| Setting | Default | Description |
|---|---|---|
| `TickIntervalMs` | 2000 | Milliseconds between ticks |
| `MinActionDelayMs` | 50 | Min delay between consecutive input events |
| `MaxActionDelayMs` | 200 | Max delay (randomized for humanization) |
| `CoordinateJitter` | 3 | Max pixel offset added to every click |
| `MaxRuntime` | `TimeSpan.Zero` (unlimited) | Auto-stop after this duration |
| `LogMemoryReadings` | `false` | Save each raw JSON to `LogDirectory` |

---

## Safety Notes

- Memory reading is **read-only** — no code injection, no writes to EVE's process.
- Input simulation uses **standard Win32 APIs** (`SetCursorPos`, `mouse_event`,
  `keybd_event`). These are detectable by client-side anti-cheat. Use at your own risk.
- Humanization (random delays + coordinate jitter) reduces bot-pattern signatures but
  is not a guarantee of undetectability.

---

## For AI Agents

See **[AGENTS.md](AGENTS.md)** for a complete technical reference covering:
- Full data-flow diagram
- All key types and their JSON serialization quirks
- How to use the MCP tools
- Live debugging procedures
- Known EVE UI edge cases and how each is handled
