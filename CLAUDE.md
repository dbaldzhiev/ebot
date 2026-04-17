# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

EBot is a C# framework for automating EVE Online. It reads the live game UI directly from process memory, parses it into typed models, runs a behavior-tree decision engine, and simulates mouse/keyboard input — all exposed through a real-time web dashboard, REST API, SignalR feed, and MCP server for AI agents.

**Core data flow:**
```
Read (in-process memory) → Parse (UI tree) → Decide (behavior tree) → Act (Win32 input) → Repeat
```

**Requirements:** .NET 9 SDK, Windows 10/11 (Win32 P/Invokes), EVE Online 64-bit client running.

---

## Build & Run

```bash
# Build all projects
dotnet build

# Run the main web server (default entry point, opens dashboard at http://localhost:5000)
dotnet run --project src/EBot.WebHost

# Custom port
dotnet run --project src/EBot.WebHost -- --port 5001

# Run tests
dotnet test tests/EBot.Tests

# Run a single test class
dotnet test tests/EBot.Tests --filter "FullyQualifiedName~UIQueryTests"

# Console runner alternative (for headless/CLI use)
dotnet run --project src/EBot.Runner -- run --bot mining --tick 2000
dotnet run --project src/EBot.Runner -- list-processes
dotnet run --project src/EBot.Runner -- test-read

# Quick build + launch batch script
.\start-ebot.bat
```

---

## Project Structure

```
src/EBot.Core/          — Framework library (no web, no UI)
src/EBot.ExampleBots/   — Concrete bot implementations
src/EBot.WebHost/       — ASP.NET Core server, dashboard, MCP server (main entry point)
src/EBot.Runner/        — Console CLI runner (alternative to WebHost)
src/EBot.InputAgent/    — Minimal standalone input agent
tests/EBot.Tests/       — xUnit test project
read-memory-64/         — Native DLLs: read-memory-64-bit.dll, Pine.Core.dll
ref/                    — Reference docs (Sanderling, Viir bots EVE UI guide)
```

**Dependency graph:**
```
EBot.WebHost  ──►  EBot.ExampleBots  ──►  EBot.Core  ──►  read-memory-64-bit.dll
EBot.Runner   ──►  EBot.ExampleBots
EBot.Tests    ──►  EBot.Core, EBot.ExampleBots
```

---

## Architecture

### Main Tick Loop (`BotRunner.cs`)

Each tick (~2000 ms default, configurable):

1. **Read** — `DirectMemoryReader` calls into `read-memory-64-bit.dll` (in-process) via P/Invoke, which returns raw JSON (~1 MB) representing the full EVE UI tree
2. **Parse** — `UITreeParser.Parse(json)` walks the node tree to produce a typed `ParsedUI` object with strongly-typed members (`ShipUI`, `Overview`, `Targets`, `Inventory`, `Chat`, etc.)
3. **Decide** — `BehaviorTree.Tick(BotContext)` traverses the bot's node graph; nodes enqueue `BotAction` objects into `ctx.Actions`
4. **Act** — `ActionExecutor` drains the queue, calling `InputSimulator` which issues Win32 `SendInput` calls with Bézier-curved humanized mouse movement and randomized delays
5. **Broadcast** — `BotOrchestrator` fires SignalR `TickUpdate` to connected browsers and MCP clients

### EBot.Core — Key Abstractions

**`IBot`** — Every bot implements this interface: provides a behavior tree root node and `BotSettings`.

**`IBehaviorNode` / `NodeStatus`** — Behavior tree nodes return `Success`, `Failure`, or `Running`.
- `SequenceNode` — AND: runs children in order until one fails
- `SelectorNode` — OR: runs children until one succeeds
- `ConditionNode` / `ActionNode` — Leaf nodes with lambda predicates/actions
- `InverterNode`, `AlwaysSucceedNode` — Decorators

**`BotContext`** — Passed to every node each tick; contains `GameState` (current `ParsedUI`), `Blackboard` (persistent key-value store with cooldown helpers), and `Actions` (action queue).

**`ParsedUI`** — The central typed model built by `UITreeParser`. All bot logic reads from here. Key members: `ShipUI`, `Overview`, `Targets`, `ContextMenu`, `MessageBox`, `Drones`, `Inventory`, `Chat`, `Station`, `InfoPanelLocationInfo`, `MiningSurveyResults`.

**`UITreeNode` / `UITreeNodeWithDisplayRegion`** — Raw JSON node with dict accessors; the annotated version adds screen coordinates used for click targeting.

**Memory reading strategy:** `DirectMemoryReader` is the primary path (bundled DLL, fast, no external process). `SanderlingReader` is a fallback that either spawns a CLI exe or talks to an `alternate-ui` HTTP server — use only when the DLL approach fails.

### EBot.ExampleBots — Bot Implementations

**`MiningBot`** — Production-quality multi-file partial class (`MiningBot.cs`, `.Navigation.cs`, `.Space.cs`, `.WorldState.cs`). Mines asteroids, docks when ore hold is full, tracks session statistics. Configurable via `MiningBotConfig` (home station, fill threshold, shield escape threshold).

**`TravelBot` / `AutopilotBot`** — Warps along autopilot route. `TravelBotScripted` supports scripted waypoint sequences.

**`SurvivalNodes`** — Reusable behavior tree wrapper that dismisses message boxes, activates tank modules, and stops the ship on critical shield/hull damage. Wrap any bot's root node with this.

### EBot.WebHost — Web Server

**`BotOrchestrator`** — Singleton owning `BotRunner`. Exposes start/stop/pause/resume, bot registry, and hot-swap (swap bots at tick boundary without stopping the read cycle).

**REST API** (defined in `Program.cs`):
- `GET /api/status` — Full bot state + `GameStateSummary`
- `POST /api/start` — Start bot (`pid`, `exePath`, `tickMs`, `destination`, mining params)
- `POST /api/stop` / `/api/pause` / `/api/resume`
- `GET /api/debug/infopanel` — InfoPanelLocationInfo raw node tree (UI parsing debug)
- `GET /api/debug/reader` — Memory reader diagnostic

**SignalR hub** at `/botHub` — Events: `TickUpdate`, `StateChanged`, `LogEntry`, `SurvivalChanged`.

**MCP server** at `/mcp` — Exposes bot tools to AI agents (Claude, etc.) via `EveBotMcpTools.cs`.

**AI chat backend** — `ChatService` uses Anthropic Claude Opus 4.6 by default; `OllamaChatService` as local fallback. Selected at startup based on config.

---

## Writing a New Bot

1. Create a class in `EBot.ExampleBots` that implements `IBot`
2. Build a behavior tree in `BuildTree()` returning the root `IBehaviorNode`
3. Read game state from `ctx.GameState.ParsedUI` in node lambdas
4. Enqueue actions via `ctx.Actions.Enqueue(new BotAction.Click(node.DisplayRegion.Center))`
5. Use `ctx.Blackboard.SetCooldown("key", TimeSpan.FromSeconds(5))` for rate-limiting
6. Register the bot in `BotOrchestrator` (the `_availableBots` dictionary in `Program.cs`)

Use `SurvivalNodes.Wrap(rootNode, ctx)` to add emergency-stop behavior for free.

---

## Debugging UI Parsing

When `UITreeParser` doesn't find an expected element:
1. `GET /api/debug/infopanel` — dumps the raw `InfoPanelLocationInfo` node tree as JSON
2. `GET /api/debug/reader` — checks memory reader health and last read timestamp
3. Add a new finder method to `UITreeParser.cs` following the pattern of existing finders (search for `UITreeNodeWithDisplayRegion` instances by `pythonObjectTypeName` or by walking child nodes)
4. Add the typed model to `ParsedUI.cs`

DPI scaling: coordinate math is done in physical pixels. If clicks land in wrong positions, check `GET /api/dpi` and use `POST /api/dpi/scale`.

---

## Important Implementation Notes

- **Windows-only:** `InputSimulator` uses Win32 `SendInput`, `SetCursorPos`, and `GetForegroundWindow`. Do not attempt to abstract this for cross-platform.
- **Unsafe code allowed:** `EBot.Core.csproj` enables `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for native interop.
- **Nullable enabled** across all projects — maintain `#nullable enable` / non-null discipline.
- **No solution file (.sln):** Build individual projects with `--project` or build all from the repo root with `dotnet build`.
- **Hot-swap:** `BotOrchestrator.SwapBot()` applies a pending bot replacement at the next tick boundary — do not stop/restart `BotRunner` to change bots.
