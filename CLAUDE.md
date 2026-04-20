# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

EBot is a mature C# framework for automating EVE Online. It reads the live game UI directly from process memory, parses it into typed models, runs a behavior-tree decision engine, and simulates mouse/keyboard input — exposed through a real-time web dashboard, REST API, SignalR feed, and MCP server for AI agents.

**Core data flow:**
```
Read (in-process memory) → Parse (UI tree) → Decide (behavior tree) → Act (Win32 input) → Repeat
```

**Requirements:** .NET 9 SDK, Windows 10/11, EVE Online 64-bit client running.

---

## Build & Run

```bash
dotnet build
dotnet run --project src/EBot.WebHost           # dashboard at http://localhost:5000
dotnet run --project src/EBot.WebHost -- --port 5001

dotnet test tests/EBot.Tests
dotnet test tests/EBot.Tests --filter "FullyQualifiedName~UIQueryTests"

dotnet run --project src/EBot.Runner -- run --bot mining --tick 2000
dotnet run --project src/EBot.Runner -- list-processes
.\start-ebot.bat
```

---

## Project Structure

```
src/EBot.Core/          — Framework library (no web, no UI)
src/EBot.ExampleBots/   — Concrete bot implementations
src/EBot.WebHost/       — ASP.NET Core server, dashboard, MCP server (main entry point)
src/EBot.Runner/        — Console CLI runner
tests/EBot.Tests/       — xUnit test project
read-memory-64/         — Native DLLs: read-memory-64-bit.dll, Pine.Core.dll
ref/                    — Read-only reference repos (Sanderling, Viir bots)
logs/                   — Session logs (session_YYYYMMDD_HHMMSS.log) + frames/ subdir
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

Each tick (~2000 ms default):
1. **Read** — `DirectMemoryReader` P/Invokes into `read-memory-64-bit.dll`, returns raw JSON ~1 MB
2. **Parse** — `UITreeParser.Parse(json)` → typed `ParsedUI` (ShipUI, Overview, Targets, Inventory, etc.)
3. **Decide** — `BehaviorTree.Tick(BotContext)` → nodes enqueue `BotAction` objects
4. **Act** — `ActionExecutor` drains queue; `InputSimulator` calls Win32 `SendInput` with Bézier-curved mouse movement and randomized delays
5. **Broadcast** — `BotOrchestrator` fires SignalR `TickUpdate` to browser + MCP clients

### EBot.Core — Key Abstractions

**`BotContext`** — passed to every node each tick:
```csharp
ctx.GameState          // GameStateSnapshot with ParsedUI, IsDocked, IsInSpace, IsWarping
ctx.Blackboard         // Persists across ticks — key-value store with cooldown helpers
ctx.TickCount          // long

// Action helpers (enqueue into internal queue, executed after Decide phase)
ctx.Click(uiNode)
ctx.Click(uiNode, VirtualKey.Control)   // ctrl+click = lock target
ctx.RightClick(uiNode)
ctx.Drag(fromNode, toNode)
ctx.Hover(uiNode)
ctx.KeyPress(VirtualKey.C, VirtualKey.Alt)
ctx.Wait(TimeSpan.FromSeconds(2))
ctx.Log(message)
ctx.RequestStop()

// Blackboard
ctx.Blackboard.Set("key", value)
ctx.Blackboard.Get<T>("key")            // returns default(T) if missing
ctx.Blackboard.Has("key")
ctx.Blackboard.Remove("key")
ctx.Blackboard.SetCooldown("name", TimeSpan.FromSeconds(5))
ctx.Blackboard.IsCooldownReady("name")  // true if never set OR expired
```

**Behavior tree node types:**

| Node | Semantics |
|------|-----------|
| `SequenceNode` | AND — runs children in order; stops + returns Failure on first Failure; **remembers cursor across ticks** |
| `SelectorNode` | OR — stops + returns Success on first Success; **remembers cursor across ticks** |
| `StatelessSelectorNode` | OR — **re-evaluates ALL children from index 0 every tick**. Use this when high-priority children must preempt Running siblings (e.g. ReturnToStation preempting BT_MineAtBelt) |
| `ConditionNode` | Wraps `Func<BotContext, bool>` → Success/Failure, never Running |
| `ActionNode` | Wraps `Func<BotContext, NodeStatus>` |
| `InverterNode` | Inverts Success↔Failure |
| `AlwaysSucceedNode` | Forces child result to Success |
| `RepeatNode` | Repeats child N times (-1 = infinite) |
| `WaitNode` | Delays then returns Success |

**Critical design note:** `StatelessSelectorNode` is what MiningBot's root and in-space selectors use. If you use a regular `SelectorNode` for a group where one member must preempt another mid-execution, it won't work — the cursor stays locked on the Running child.

### EBot.ExampleBots — Bot Implementations

**`MiningBot`** (7-file partial class):

| File | Responsibility |
|------|---------------|
| `MiningBot.cs` | `IBot` impl, `OnStart()` blackboard init, behavior tree root, `HandleDocked()`, `HandleInSpace()` |
| `MiningBot.Navigation.cs` | `ReturnToStation()` + `WarpToBelt()` state machines |
| `MiningBot.Space.cs` | `BT_MineAtBelt()` — surveyor, approach, lock, fire lasers |
| `MiningBot.Unload.cs` | `PerformUnload()` + `RememberStationAndUndock()` + `FinishUnload()` |
| `MiningBot.WorldState.cs` | `SynthesizeWorldState()` — asteroid scoring, target caching |
| `MiningBot.Utils.cs` | `StopAllModules()`, `RecallDrones()`, `RightClickInSpace()` |
| `MiningBot.Constants.cs` | Ore value ranking, asteroid keywords, station keywords, thresholds |

**MiningBot behavior tree (root = `StatelessSelectorNode`):**
```
Mining Root (StatelessSelectorNode)  ← re-evaluates ALL children every tick
├── Trace Start (always Failure — just logs)
├── World State Synthesis (always Failure — just caches WorldState to blackboard)
├── HandleMessageBoxes()
├── HandleStrayContextMenu()
├── HandleShieldEmergency()
├── HandleDocked() ─── SequenceNode: IsDocked? → PerformUnload() OR RememberStationAndUndock()
├── HandleWarping()
└── HandleInSpace() ─── SequenceNode: IsInSpace? →
        StatelessSelectorNode:
            WaitCapRegen()
            ReturnToStation()    ← preempts everything below when needs_unload=true
            BT_DroneSecurity()
            EnsureMiningTab()
            NavigateToMiningHold()
            DiscoverBeltsOnce()
            BT_MineAtBelt()      ← Failure when no asteroids in overview → falls through to:
            WarpToBelt()
```

**MiningBot blackboard keys (set in `OnStart()`):**

| Key | Type | Purpose |
|-----|------|---------|
| `needs_unload` | bool | Triggers immediate return to station; starts true |
| `return_phase` | string | Return-to-station state machine step |
| `unload_phase` | string | Unload state machine step |
| `belt_phase` | string | WarpToBelt state machine step |
| `mining_phase` | string | BT_MineAtBelt state machine step (NOT in OnStart — set via Remove/Set during execution) |
| `discover_phase` | string | One-time belt discovery phase |
| `last_belt_target` | int | Last belt index (-1 = none) |
| `belt_prop_started` | bool | Propulsion activated for current belt |
| `home_station` | string | Learned docking target name |
| `home_station_set` | bool | Whether home station is known |
| `home_menu_type` | string | Remembered right-click submenu type ("Stations"/"Structures") |
| `menu_expected` | bool | Guard: context menu was intentionally opened |
| `world` | WorldState | Synthesized asteroid/laser state (overwritten every tick) |

**`FinishUnload()` is the cycle-reset hook** (`MiningBot.Unload.cs`). After each unload it clears: `needs_unload`, `unload_phase`, `belt_index`, `last_belt_target`, `belt_prop_started`. Known bug: does NOT clear `mining_phase` or `belt_phase` — these must be cleared here too or the next cycle starts mid-state.

**`TravelBot`** — menu-based autopilot; follows the in-game route. Handles Dock/Jump/Warp from right-click context menus on route markers. Working and production-ready.

**`SurvivalNodes`** — wraps any bot tree to add: dismiss message boxes → emergency tank modules on low shield → stop on critical structure. Applied automatically when survival is enabled via API.

### EBot.WebHost

**`BotOrchestrator`** — singleton owning one perpetual `BotRunner`. The read-parse loop never stops; `SwapBot()` hot-swaps the decision tree at tick boundaries. When no real bot is active, `IdleBot` runs (monitor-only). `_holdCache` persists inventory hold data across ticks (only one hold visible at a time in EVE's UI).

**`SdeService`** — downloads CCP's station/system SQLite database (`eve_sde.db`). Powers the travel destination search. Auto-downloaded on first run.

**`GlobalHotKeyService`** — registers Pause/Break as a global hotkey (works even when EVE has focus). Triggers emergency stop.

**REST API highlights** (full list in AGENTS.md):
- `GET /api/status` — full bot state + GameStateSummary
- `GET /api/mining-stats` — cycle stats + blackboard snapshot
- `GET /api/mining/belts` — belt registry with depleted/excluded flags
- `POST /api/mining/belts/{idx}/toggle` — user-exclude a belt
- `GET /api/stations/search?q=jita` — SDE station search (3+ chars)
- `POST /api/save-frame` — saves raw UI JSON to `logs/frames/` for offline parser testing
- `GET /api/debug/reader` — memory reader diagnostics
- `GET /api/debug/input` — coordinate/DPI diagnostics

**AI chat backend** (env vars):
```
EBOT_AI_BACKEND=anthropic   ANTHROPIC_API_KEY=sk-ant-...
EBOT_AI_BACKEND=ollama      EBOT_OLLAMA_URL=http://...  EBOT_OLLAMA_MODEL=llama3.1
```

---

## Known Bugs & Sharp Edges

1. **Stale `mining_phase` after unload** — `FinishUnload()` does not call `ctx.Blackboard.Remove("mining_phase")`. After undocking, `BT_MineAtBelt()` reads the stale phase (e.g. `"fire_lasers"`) instead of starting fresh from `"open_surveyor"`. Fix: add the Remove call in `FinishUnload()`.

2. **Stale `belt_phase` after unload** — same issue; `FinishUnload()` doesn't reset `belt_phase` to `""`. If `WarpToBelt()` is called post-undock with a stale phase, it skips belt selection and continues from a dead state. Fix: add `ctx.Blackboard.Set("belt_phase", "")` in `FinishUnload()`.

3. **`assumed_locked` set grows across belts** — only cleared when target count == 0. Stale asteroid addresses from Belt A persist into Belt B. Fix: clear in `FinishUnload()`.

4. **`laser_range_m` never reset** — set once on first hover, never cleared. If the cached range is wrong (e.g. after a ship/module change), the bot uses the wrong approach distance. Fix: clear on session start or belt change.

5. **`belt_prop_started` not reset on failed return** — flag stays true if the bot fails to reach station and re-enters mining at the same belt. Next belt hop won't activate propulsion.

---

## Writing a New Bot

1. Implement `IBot` in `EBot.ExampleBots`
2. Build the tree in `BuildBehaviorTree()`
3. Read `ctx.GameState.ParsedUI` in node lambdas
4. Use `ctx.Click()`, `ctx.KeyPress()`, etc. to act
5. Add to `BotOrchestrator.AvailableBots` in `BotOrchestrator.cs`

Use `StatelessSelectorNode` as the root whenever any child may need to preempt a Running sibling. Use `SurvivalNodes.Wrap(tree)` for emergency tank behavior.

---

## Debugging UI Parsing

1. `GET /api/debug/reader` — parse health, element counts
2. `GET /api/debug/infopanel` — InfoPanel node tree dump
3. `POST /api/save-frame` → load JSON in `EBot.Tests` for offline testing
4. The canonical EVE Python type name list: `ref/botimplement/eve-online-mining-bot/EveOnline/ParseUserInterface.elm`
5. Add finder in `UITreeParser.cs` → add property to `ParsedUI.cs` → wire in `Parse()`

DPI: `GET /api/dpi`, `POST /api/dpi/scale`. Physical-pixel coordinates; `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` called at startup.

---

## Important Implementation Notes

- **Windows-only**: `InputSimulator` uses Win32 `SendInput`, `SetCursorPos`. No cross-platform.
- **Unsafe allowed**: `EBot.Core.csproj` has `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
- **Nullable enabled** across all projects — maintain `#nullable enable` discipline.
- **No .sln file**: build with `dotnet build` from repo root or `--project`.
- **Hot-swap**: never restart `BotRunner` to change bots — use `BotOrchestrator.SwapBot()`.
- **First scan slow**: `EnumeratePossibleAddressesForUIRootObjectsFromProcessId` takes ~60 s on first run; root address is cached after that.
- **JSON quirk**: DLL serializes `long`/`ulong` as JSON strings. `GetDictDouble`/`GetDictInt` parse string-as-number automatically.
- **Hold cache**: only one inventory hold is visible at a time in EVE; `BotOrchestrator._holdCache` persists data across ticks so the UI can show all holds.
