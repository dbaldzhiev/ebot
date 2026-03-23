# EBot — Agent Reference

EVE Online bot framework. Reads EVE's UI from process memory, parses it into typed
C# models, runs a behavior tree, and executes mouse/keyboard actions. A web UI and
MCP server let human operators and AI agents inspect and control everything live.

---

## Solution Structure

```
ebot/
├── src/
│   ├── EBot.Core/           # All game logic — no UI, no web
│   │   ├── MemoryReading/   # Read EVE process memory → raw JSON
│   │   ├── GameState/       # Parse JSON → typed ParsedUI
│   │   ├── DecisionEngine/  # Behavior tree engine + BotContext
│   │   ├── Execution/       # InputSimulator (mouse/keyboard via Win32)
│   │   └── Bot/             # BotRunner (tick loop), IBot interface
│   │
│   ├── EBot.ExampleBots/    # Concrete bot implementations
│   │   ├── MiningBot/       # Mine asteroids, dock when full
│   │   ├── AutopilotBot/    # Warp-to-0 autopilot along a route
│   │   ├── SurvivalNodes.cs # Reusable emergency-tank behavior tree wrapper
│   │   └── IdleBot.cs       # Monitor-only bot (no actions)
│   │
│   ├── EBot.WebHost/        # ASP.NET Core web server
│   │   ├── BotOrchestrator.cs  # Singleton: owns the BotRunner, exposes control API
│   │   ├── Program.cs          # REST endpoints, DI, startup
│   │   ├── Hubs/BotHub.cs      # SignalR hub — pushes TickUpdate, StateChanged, LogEntry
│   │   ├── Mcp/                # MCP tool server (AI agent interface)
│   │   └── wwwroot/index.html  # Single-page web UI
│   │
│   └── EBot.Runner/         # Minimal console runner (alternative to WebHost)
│
├── read-memory-64/          # Viir/bots read-memory-64-bit binaries (separate assemblies)
│   └── read-memory-64-bit.dll   # Key DLL: EveOnline64.* API
└── AGENTS.md                # This file
```

**Target framework:** .NET 9, Windows only (uses Win32 P/Invokes).

---

## Core Data Flow — one tick

```
EVE process memory
       │
       ▼
DirectMemoryReader.ReadMemoryAsync()
  └─ EveOnline64.EnumeratePossibleAddressesForUIRootObjectsFromProcessId(pid)
  └─ EveOnline64.ReadUITreeFromAddress(addr, reader, maxDepth=99)
  └─ EveOnline64.SerializeMemoryReadingNodeToJson(tree)  →  JSON string (~1 MB)
       │
       ▼
UITreeParser.Parse(json)
  └─ Deserialize → UITreeNode hierarchy
  └─ BuildAnnotatedTree() → UITreeNodeWithDisplayRegion (adds screen coords)
  └─ FindShipUI / FindContextMenus / FindOverviewWindows / …  →  ParsedUI
       │
       ▼
BehaviorTree.Tick(BotContext)
  └─ Reads ctx.GameState.ParsedUI
  └─ Enqueues actions into ctx.Actions  (Click, RightClick, KeyPress, Wait, TypeText)
       │
       ▼
ActionExecutor.ExecuteAllAsync(actions, windowHandle)
  └─ InputSimulator: SetCursorPos + mouse_event / keybd_event (Win32)
       │
       ▼
BotRunner fires OnTick → BotOrchestrator → SignalR TickUpdate → Web UI / MCP
```

Tick interval default: **2 000 ms** (configurable per bot in `BotSettings`).

---

## Key Types

### Memory reading

| Type | Purpose |
|---|---|
| `IEveMemoryReader` | Interface — `ReadMemoryAsync() → MemoryReadingResult` |
| `DirectMemoryReader` | **Primary.** Reads live EVE process in-process via the `read-memory-64-bit.dll` API. No file I/O, no child process. |
| `SanderlingReader` | Fallback. Spawns `read-memory-64-bit.exe` CLI or talks to an alternate-ui HTTP server. |
| `EveProcessFinder` | Finds EVE processes by name (`exefile` or `eve`). |

**Critical DirectMemoryReader behaviour:** `EnumeratePossibleAddressesForUIRootObjectsFromProcessId`
returns multiple candidates including a UIRoot class stub (no children). The reader
skips any address whose serialized JSON does not contain `"children":[{` — that
check filters out the stub and finds the live instance.

### Game state

| Type | Purpose |
|---|---|
| `UITreeNode` | Raw deserialized JSON node. Dict entries accessed via `GetDictString`, `GetDictDouble`, `GetDictBool`, `GetDictColor`. |
| `UITreeNodeWithDisplayRegion` | Annotated node with absolute screen `Region` (X,Y,W,H) and `Center`. Has `FindAll` / `FindFirst` helpers. |
| `ParsedUI` | Fully typed top-level model. Properties: `ShipUI`, `ContextMenus`, `Targets`, `InfoPanelContainer`, `OverviewWindows`, `InventoryWindows`, `StationWindow`, `MessageBoxes`, … |
| `GameStateSnapshot` | Wraps `ParsedUI` + timestamp + computed helpers (`IsInSpace`, `IsDocked`, `IsWarping`, `CapacitorPercent`, `RouteJumpsRemaining`, …). |

**JSON format from the DLL:**
- `long` / `ulong` values → JSON **strings** (`"42"`, not `42`). `GetDictDouble` handles this.
- `double` values → JSON numbers.
- Nested Python objects (e.g. Link, Color) → JSON objects with `pythonObjectTypeName` + `dictEntriesOfInterest`. `GetDictString` extracts `_setText`/`_text` from these automatically.
- EVE HTML tags (`<color=...>`, `<fontsize=...>`, `<t>`) stripped by `EveTextUtil.StripTags`.

### Decision engine

| Type | Purpose |
|---|---|
| `IBot` | Interface: `Name`, `Description`, `GetDefaultSettings()`, `OnStart`, `OnStop`, `BuildBehaviorTree()` |
| `IBehaviorNode` | Tick returns `NodeStatus` (Success / Failure / Running) |
| `SelectorNode` | OR — tries children in order, returns first Success |
| `SequenceNode` | AND — runs children in order, stops on first Failure |
| `ConditionNode` | Wraps a `Func<BotContext, bool>` |
| `ActionNode` | Wraps a `Func<BotContext, NodeStatus>` |
| `BotContext` | Passed to every node. Contains `GameState`, `Blackboard`, `Actions`. Helpers: `ctx.Click(node)`, `ctx.RightClick(node)`, `ctx.KeyPress(key, mods)`, `ctx.TypeText(text)`, `ctx.Wait(ts)`. |
| `Blackboard` | Key-value store persisting between ticks. `Get<T>`, `Set`, `IsCooldownReady`, `SetCooldown`. |

### Orchestration

`BotOrchestrator` is a singleton that owns one perpetual `BotRunner`. The runner
loops forever. Bots are hot-swapped via `SwapBot()` without interrupting the read
cycle. When no real bot is active, `IdleBot` runs (reads + broadcasts, no actions).

---

## REST API (port 5000 by default)

| Method | Path | Description |
|---|---|---|
| GET | `/api/status` | Full `BotStatusResponse` with game state DTO |
| GET | `/api/bots` | List available bot names |
| GET | `/api/processes` | List running EVE processes (PID, title) |
| POST | `/api/start` | `{ "botName": "Mining Bot" }` — start a bot |
| POST | `/api/stop` | Stop the active bot |
| POST | `/api/pause` | Pause |
| POST | `/api/resume` | Resume |
| POST | `/api/survival` | `{ "enabled": true }` — toggle survival wrapper |
| GET | `/api/log?count=50` | Recent log entries |
| GET | `/api/dpi` | System DPI and current coordinate scale |
| POST | `/api/dpi/scale` | `{ "scale": 1.0 }` — override coordinate scale |
| **GET** | **`/api/debug/reader`** | **Live diagnostic — EVE process, JSON length, parse results, root children types** |
| GET | `/api/debug/infopanel` | Dumps InfoPanelLocationInfo node tree (names, texts, hints) |

### SignalR hub — `/botHub`

Events pushed to connected clients:

| Event | Payload |
|---|---|
| `TickUpdate` | `GameStateSummary` DTO (camelCase JSON) on every tick |
| `StateChanged` | `"Idle"` / `"Running"` / `"Paused"` |
| `LogEntry` | `{ time, level, category, message }` |
| `SurvivalChanged` | `bool` |

### MCP server — `/mcp`

SSE transport. Connect Claude Desktop or any MCP client. Tools:

| Tool | Description |
|---|---|
| `get_status` | Bot state + ship telemetry |
| `list_bots` | Available bot types |
| `list_eve_processes` | Running EVE clients |
| `start_bot` | Start a named bot |
| `stop_bot` | Stop the bot |
| `pause_bot` / `resume_bot` | Pause control |
| `get_overview` | Current overview entries |
| `get_targets` | Locked targets |
| `get_ship_status` | HP, cap, module rows |
| `get_log` | Recent log entries |

---

## Writing a New Bot

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
            // Priority 1 — highest
            new SequenceNode("Do thing A",
                new ConditionNode("Condition for A", ctx => ctx.GameState.IsInSpace),
                new ActionNode("Action A", ctx =>
                {
                    var node = ctx.GameState.ParsedUI.ShipUI?.StopButton;
                    if (node == null) return NodeStatus.Failure;
                    ctx.Click(node);
                    return NodeStatus.Success;
                })),
            // Priority 2 — fallback
            new ActionNode("Idle", _ => NodeStatus.Success));
}
```

Register in `BotOrchestrator.AvailableBots`:

```csharp
public static readonly IReadOnlyList<IBot> AvailableBots =
[
    new MiningBot(),
    new AutopilotBot(),
    new MyBot(),   // ← add here
];
```

Wrap with `SurvivalNodes.Wrap(myBot.BuildBehaviorTree())` for emergency tank behaviour
(already applied automatically when `SurvivalEnabled = true` in the orchestrator).

---

## Live Debugging

### 1. Check the diagnostic endpoint

```
GET http://localhost:5000/api/debug/reader
```

Returns:
- EVE process found (PID, title)
- JSON length from last successful read
- `UITree root type` — should be `"UIRoot"` with `children > 0`
- Whether `ShipUI`, `InfoPanelContainer`, `OverviewWindows` were found
- Root child type names (the 12 `LayerCore` nodes are expected)

If `UITree children: 0` → reader is finding the UIRoot class stub, not a live
instance. Fix: ensure the stub-skip check in `DirectMemoryReader` is present
(skips addresses whose JSON lacks `"children":[{"`).

### 2. InfoPanel structure dump

```
GET http://localhost:5000/api/debug/infopanel
```

Shows the InfoPanelLocationInfo node tree with `_name`, `_setText`, `_text`, `_hint`
for each node up to depth 4. Use this to find the exact node names for new UI elements
you want to parse (e.g. if security status parsing breaks after an EVE patch).

### 3. Live logs

```
GET http://localhost:5000/api/log?count=100
```

Or watch via the web UI at `http://localhost:5000`.

Key log messages to watch for:

| Message | Meaning |
|---|---|
| `DirectMemoryReader: live UI root at 0x...` | Correct root found, reading working |
| `DirectMemoryReader: 0x... is a stub UIRoot (no children) — skipping` | Stub filtered, searching for real root |
| `Memory reading failed: ...` | Reader error — check EVE is running |
| `Direct read in Xms, Y bytes` | Normal tick (Debug level) |

### 4. Full memory scan is slow (~60 s)

On first start or after EVE restarts, `EnumeratePossibleAddressesForUIRootObjectsFromProcessId`
scans the full process heap. This takes ~60 seconds. After the live root is found its
address is cached; subsequent ticks are fast (< 500 ms typically).

### 5. DPI / coordinate issues

```
GET http://localhost:5000/api/dpi
POST http://localhost:5000/api/dpi/scale  {"scale": 1.25}
```

`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` is called
at startup so `SetCursorPos` uses physical pixels matching EVE's coordinate space.
If clicks land in the wrong place, adjust `coordinate_scale` via the web UI "Scale"
button or the POST endpoint.

---

## Known EVE UI Quirks

| Quirk | Handling |
|---|---|
| `long` dict values serialized as JSON strings | `GetDictDouble` / `GetDictInt` parse string as number |
| Nested Python objects (Link, Color) as dict values | `GetDictString` extracts `_setText`/`_text` from `dictEntriesOfInterest` |
| Security status node named `headerLabelSecStatus` | Parser searches for "SecStatus" case-insensitive |
| System name node named `headerLabelSystemName` | Parser searches for "SystemName" case-insensitive |
| Context menu container type: `ContextMenu` | Entries type: `MenuEntryView` |
| Route markers sorted by `(Region.X + Region.Y)` ascending | First = next hop |
| UIRoot class stub returned before live instance | Filtered by checking for `"children":[{` in JSON |
| `_hint` on buttons pollutes location text | Security status search skips `_hint`; uses only `_setText`/`_text` |

---

## Reference Repositories

Two upstream open-source projects live in `ref/` as **read-only references**.
They are excluded from version control (`.gitignore`). Do **not** modify them.
Re-clone with the commands in the section below if they are missing.

```
ref/
├── Sanderling/          # github.com/Arcitectus/Sanderling
│   ├── implement/
│   │   ├── read-memory-64-bit/   # SOURCE of EveOnline64.cs — the memory-reading engine
│   │   │   ├── EveOnline64.cs    # ← primary reference: EnumeratePossibleAddresses*, ReadUITreeFromAddress, SerializeMemoryReadingNodeToJson
│   │   │   ├── MemoryReader.cs   # Win32 ReadProcessMemory wrapper
│   │   │   ├── WinApi.cs         # P/Invoke declarations (OpenProcess, VirtualQueryEx, …)
│   │   │   └── ProcessSample.cs  # Memory-region sampling helpers
│   │   └── alternate-ui/         # Elm/JavaScript alternate UI renderer (not used by EBot)
│   ├── explore/                  # Exploratory scripts from 2019–2020 reverse-engineering sessions
│   └── guide/                    # Sanderling user guide (markdown)
│
└── bots/                # github.com/Viir/bots  (sparse: guide/eve-online only)
    └── guide/eve-online/
        ├── parsed-user-interface-of-the-eve-online-game-client.md  # ← key doc: all UI node types, JSON shape, dict entry names
        ├── developing-for-eve-online.md   # bot development walkthrough
        ├── eve-online-warp-to-0-autopilot-bot.md   # autopilot bot design patterns
        ├── eve-online-mining-bot.md       # mining bot design patterns
        ├── eve-online-combat-anomaly-bot.md
        └── eve-online-players-strategies.md
```

### Why these refs matter

| Reference file | What to read there |
|---|---|
| `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` | Authoritative source of the three public APIs we call: `EnumeratePossibleAddressesForUIRootObjectsFromProcessId`, `ReadUITreeFromAddress`, `SerializeMemoryReadingNodeToJson`. Read this when memory reading breaks or the DLL version changes. |
| `ref/Sanderling/implement/read-memory-64-bit/MemoryReader.cs` | How Win32 `ReadProcessMemory` is used; useful if debugging address enumeration performance or access-denied errors. |
| `ref/bots/guide/eve-online/parsed-user-interface-of-the-eve-online-game-client.md` | Complete catalogue of EVE UI node types, `pythonObjectTypeName` values, `dictEntriesOfInterest` key names, and the JSON schema produced by the DLL. **Read this first when adding support for a new UI window.** |
| `ref/bots/guide/eve-online/developing-for-eve-online.md` | High-level bot design guide. Good starting point for understanding the overall approach. |
| `ref/bots/guide/eve-online/eve-online-warp-to-0-autopilot-bot.md` | Autopilot logic patterns; useful reference when extending `AutopilotBot`. |
| `ref/bots/guide/eve-online/eve-online-mining-bot.md` | Mining logic patterns; useful reference when extending `MiningBot`. |

### Re-cloning refs

```bash
# From the repo root
mkdir -p ref

# Full Sanderling source
git clone https://github.com/Arcitectus/Sanderling ref/Sanderling

# Viir bots guide (sparse — guide/eve-online only)
git clone --filter=blob:none --no-checkout --depth=1 https://github.com/Viir/bots ref/bots
cd ref/bots
git sparse-checkout init --cone
git sparse-checkout set guide/eve-online
git checkout main
cd ../..
```

---

## Building & Running

```bash
# Build
cd src/EBot.WebHost
dotnet build

# Run (default port 5000)
dotnet run

# Custom port
dotnet run -- --port=5001
```

Requires .NET 9 SDK. Windows only. EVE Online must be running for memory reads to work.
The framework starts immediately; the first memory scan completes ~60 s after launch.
