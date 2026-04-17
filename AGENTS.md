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
│   │   ├── MiningBot/       # Mine asteroids, dock when full (6 partial files)
│   │   ├── AutopilotBot/    # Warp-to-0 autopilot along a route
│   │   ├── TravelBot/       # Scripted travel variant
│   │   ├── SurvivalNodes.cs # Reusable emergency-tank behavior tree wrapper
│   │   └── IdleBot.cs       # Monitor-only bot (no actions)
│   │
│   ├── EBot.WebHost/        # ASP.NET Core web server
│   │   ├── BotOrchestrator.cs  # Singleton: owns the BotRunner, exposes control API
│   │   ├── Program.cs          # REST endpoints, DI, startup
│   │   ├── Hubs/BotHub.cs      # SignalR hub — pushes TickUpdate, StateChanged, LogEntry
│   │   ├── Mcp/                # MCP tool server (AI agent interface)
│   │   ├── Services/           # ChatService (Claude), OllamaChatService
│   │   ├── Terminal/           # Spectre.Console TUI dashboard
│   │   └── wwwroot/index.html  # Single-page web UI
│   │
│   └── EBot.Runner/         # Minimal console runner (alternative to WebHost)
│
├── tests/EBot.Tests/        # xUnit test project
├── read-memory-64/          # Viir/bots read-memory-64-bit binaries
│   └── read-memory-64-bit.dll   # Key DLL: EveOnline64.* API
├── ref/                     # Read-only reference repositories (not in git)
│   ├── botimplement/        # Elm reference bot implementations (Viir/bots)
│   │   ├── eve-online-mining-bot/
│   │   ├── eve-online-warp-to-0-autopilot/
│   │   └── eve-online-combat-anomaly-bot/
│   └── (Sanderling/ if cloned separately)
├── AGENTS.md                # This file
├── CLAUDE.md                # Claude Code context
└── GEMINI.md                # Gemini context
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
  └─ Enqueues actions into ctx.Actions  (see BotAction types below)
       │
       ▼
ActionExecutor.ExecuteAllAsync(actions, windowHandle)
  └─ InputSimulator: SetCursorPos + mouse_event / keybd_event (Win32)
  └─ Bézier-curved humanized mouse movement, randomized delays, coordinate jitter
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
| `ParsedUI` | Fully typed top-level model (see full property list below). |
| `GameStateSnapshot` | Wraps `ParsedUI` + timestamp + computed helpers (`IsInSpace`, `IsDocked`, `IsWarping`, `CapacitorPercent`, `RouteJumpsRemaining`, …). |

**`ParsedUI` properties — complete list:**

| Property | Type | Notes |
|---|---|---|
| `UITree` | `UITreeNodeWithDisplayRegion?` | Root of the annotated tree |
| `ContextMenus` | `IReadOnlyList<ContextMenu>` | Right-click menus currently visible |
| `ShipUI` | `ShipUI?` | Modules, capacitor, HP, speed |
| `Targets` | `IReadOnlyList<Target>` | Locked targets |
| `InfoPanelContainer` | `InfoPanelContainer?` | Left panel: location info, route |
| `OverviewWindows` | `IReadOnlyList<OverviewWindow>` | Space objects; each has `Tabs`, `ColumnHeaders`, `Entries` |
| `SelectedItemWindow` | `SelectedItemWindow?` | Action buttons when an object is selected |
| `DronesWindow` | `DronesWindow?` | Drones in bay / in space with HP |
| `InventoryWindows` | `IReadOnlyList<InventoryWindow>` | Cargo/ore hold windows with capacity gauge and nav entries |
| `ChatWindowStacks` | `IReadOnlyList<ChatWindowStack>` | Local, corp, fleet chat |
| `ModuleButtonTooltip` | `ModuleButtonTooltip?` | Tooltip shown on hover over a module button |
| `Neocom` | `Neocom?` | Sidebar neocom |
| `MessageBoxes` | `IReadOnlyList<MessageBox>` | Modal dialogs |
| `StationWindow` | `StationWindow?` | Undock button when docked |
| `ProbeScannerWindow` | `ProbeScannerWindow?` | Probe scanning results |
| `MiningScanResultsWindow` | `MiningScanResultsWindow?` | Survey scanner results with ore entries |
| `FleetWindow` | `FleetWindow?` | Fleet member list |

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
| `InverterNode` | Decorator — inverts child's Success/Failure |
| `AlwaysSucceedNode` | Decorator — always returns Success regardless of child |
| `BotContext` | Passed to every node. See full API below. |
| `Blackboard` | Key-value store persisting between ticks. `Get<T>`, `Set`, `IsCooldownReady`, `SetCooldown`. |

**`BotContext` full API:**

```csharp
// State
ctx.GameState          // GameStateSnapshot — current tick's parsed UI
ctx.Blackboard         // Persists across ticks
ctx.Actions            // ActionQueue — enqueue BotAction instances here
ctx.TickCount          // long — ticks since bot start
ctx.StartTime          // DateTimeOffset
ctx.RunDuration        // TimeSpan since start
ctx.ActiveNodes        // Stack<string> — currently executing nodes (debug)

// Action helpers (enqueue into ctx.Actions)
ctx.Click(node)                          // Left-click node center
ctx.Click(node, VirtualKey.Control)      // Ctrl+click (e.g. lock target)
ctx.RightClick(node)                     // Right-click node center
ctx.ClickAt(x, y)                        // Left-click at absolute coords
ctx.Hover(node)                          // Mouse move only (for submenu hover)
ctx.Scroll(node, delta)                  // Move to node then scroll wheel
ctx.KeyPress(key, modifiers)             // Key press with optional modifiers
ctx.TypeText(text)                       // Type a string
ctx.Wait(TimeSpan)                       // Insert delay in action queue
ctx.ClickMenuEntry(text)                 // Click first context menu entry matching text

// Diagnostics
ctx.Log(message)                         // Emit a per-tick diagnostic log message

// Self-termination
ctx.RequestStop()                        // Signal BotRunner to return to idle after this tick
ctx.StopRequested                        // bool — checked by BotRunner after Decide phase
```

### BotAction types (all records derived from `BotAction`)

| Type | Parameters | Notes |
|---|---|---|
| `ClickAction` | `X, Y, Modifiers` | Left-click; optional `VirtualKey[]` modifiers |
| `RightClickAction` | `X, Y, Modifiers` | Right-click |
| `DoubleClickAction` | `X, Y` | Double left-click |
| `DragAction` | `FromX, FromY, ToX, ToY` | Click-drag |
| `KeyPressAction` | `Key, Modifiers` | Key press with optional modifiers |
| `WaitAction` | `Duration` | Pause execution for a `TimeSpan` |
| `TypeTextAction` | `Text` | Type a string character by character |
| `MoveMouseAction` | `X, Y` | Mouse move (no click) — for hover-triggered submenus |
| `ScrollAction` | `Delta` | Mouse wheel (negative = down, positive = up) |

All `BotAction` records have an optional `PreDelay` (`TimeSpan`) property.

**`VirtualKey` enum** — full set available: `Shift`, `Control`, `Alt`, `F1`–`F12`,
`Escape`, `Tab`, `Enter`, `Space`, `Backspace`, `Delete`, arrow keys, `A`–`Z`, `D0`–`D9`.

### Orchestration

`BotOrchestrator` is a singleton that owns one perpetual `BotRunner`. The runner
loops forever. Bots are hot-swapped via `SwapBot()` without interrupting the read
cycle. When no real bot is active, `IdleBot` runs (reads + broadcasts, no actions).

---

## ParsedUI — Selected Type Details

### `InventoryWindow`
```
InventoryWindow
├── SubCaptionLabelText  // "Ore Hold", "Cargo Hold", etc.
├── HoldType             // InventoryHoldType enum (Unknown/Cargo/Mining/Fleet/Item/…)
├── CapacityGauge        // { Used, Maximum, FillPercent }
├── Items[]              // { Name, Quantity, UINode }
├── ButtonToStackAll     // UINode of the "Stack All" button
└── NavEntries[]         // Left-panel hold entries { Label, HoldType, IsSelected, UINode }
```

**`InventoryHoldType` enum:** `Unknown`, `Cargo`, `Mining`, `Infrastructure`, `ShipMaintenance`, `Fleet`, `Fuel`, `Item`

### `ShipUI`
```
ShipUI
├── Capacitor         // { LevelPercent }
├── HitpointsPercent  // { Shield, Armor, Structure }
├── Indication        // { ManeuverType } — "Warp", "Approach", "Orbit", etc.
├── ModuleButtons[]   // { IsActive, IsBusy, IsOverloaded, IsOffline, IsHiliteVisible, RampRotationMilli, Name }
├── ModuleButtonsRows // { Top[], Middle[], Bottom[] }
├── StopButton        // UINode
├── MaxSpeedButton    // UINode
└── SpeedText         // "324 m/s"
```

### `OverviewWindow`
```
OverviewWindow
├── ColumnHeaders[]   // Header label strings, left-to-right
├── Tabs[]            // { Name, IsActive, UINode }
└── Entries[]
    ├── Name, ObjectType, DistanceText, DistanceInMeters
    ├── IsAttackingMe
    ├── CellsTexts    // Dictionary<string, string> — column header → cell text
    └── Texts[]       // All display texts in entry (fallback)
```

### `MiningScanResultsWindow`
```
MiningScanResultsWindow
├── ScanButton    // UINode of the "Scan" button
└── Entries[]
    ├── OreName, Quantity, Volume, ValueText, ValuePerM3, DistanceInMeters
    ├── IsGroup, IsExpanded
    └── ExpanderNode   // UINode of the expand/collapse toggle
```

### `ContextMenu`
```
ContextMenu
└── Entries[]   // { Text, IsHighlighted, UINode }
```

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
| GET | `/api/save-frame` | Capture current raw UI JSON to disk (for parser regression tests) |
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

Register in `BotOrchestrator.AvailableBots` (in `Program.cs` or `BotOrchestrator.cs`):

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

### Blackboard patterns

```csharp
// Rate-limit an action to once per 30 seconds
if (ctx.Blackboard.IsCooldownReady("undock"))
{
    ctx.Click(stationWindow.UndockButton);
    ctx.Blackboard.SetCooldown("undock", TimeSpan.FromSeconds(30));
    return NodeStatus.Success;
}

// Store persistent state between ticks
ctx.Blackboard.Set("targetId", selectedEntry.UINode.Address);
var id = ctx.Blackboard.Get<long>("targetId");
```

### Context menu cascade pattern

```csharp
// Right-click overview entry, then click a menu item
new SequenceNode("Mine asteroid",
    new ActionNode("Right-click", ctx =>
    {
        ctx.RightClick(asteroidEntry.UINode);
        return NodeStatus.Success;
    }),
    new ActionNode("Click Mine", ctx =>
    {
        ctx.ClickMenuEntry("Mine");
        return NodeStatus.Success;
    }))
```

---

## Reference Implementations (Elm)

`ref/botimplement/` contains the upstream **Viir/bots** Elm framework — the same
logical design EBot is based on, but in a different language. These are read-only
references; do **not** modify them.

| Bot | Location | What to read |
|---|---|---|
| Mining bot | `ref/botimplement/eve-online-mining-bot/Bot.elm` | Complete mining logic: ore hold detection, belt navigation, dock/undock cycle, drone management, afterburner use, fleet hangar unload |
| Autopilot bot | `ref/botimplement/eve-online-warp-to-0-autopilot/Bot.elm` | Route hop logic, gate jumping, warp-to-0 sequence |
| Combat anomaly bot | `ref/botimplement/eve-online-combat-anomaly-bot/Bot.elm` | Combat state machine, targeting, drone launch/recall |

**Key Elm modules to cross-reference when extending EBot:**

| Elm file | C# equivalent |
|---|---|
| `EveOnline/ParseUserInterface.elm` | `UITreeParser.cs` + `ParsedUI.cs` |
| `EveOnline/BotFramework.elm` | `BotRunner.cs` + `BotContext.cs` |
| `EveOnline/BotFrameworkSeparatingMemory.elm` | Behavior tree node patterns |
| `Common/DecisionPath.elm` | Behavior tree node types |
| `Common/EffectOnWindow.elm` | `BotAction.cs` + `InputSimulator.cs` |

The Elm `ParsedUserInterface` type at the top of `ParseUserInterface.elm` is the
canonical list of all UI windows the upstream framework supports — use it as the
reference when adding support for a new window type to `UITreeParser.cs`.

The Elm `Bot.elm` files use a **decision path** pattern (rather than a behavior tree):
each function returns `describeBranch "label" (decideNext context)` which chains
decisions into a human-readable trace. When implementing new bot logic in C#, the
`ctx.Log("branch: label")` + `ctx.ActiveNodes` stack provides equivalent traceability.

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

### 3. Capture a UI frame for parser regression tests

```
GET http://localhost:5000/api/save-frame
```

Writes the current raw UI JSON to disk. Load it in `EBot.Tests` to test `UITreeParser`
against a real snapshot without needing a live EVE client.

### 4. Live logs

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

### 5. Full memory scan is slow (~60 s)

On first start or after EVE restarts, `EnumeratePossibleAddressesForUIRootObjectsFromProcessId`
scans the full process heap. This takes ~60 seconds. After the live root is found its
address is cached; subsequent ticks are fast (< 500 ms typically).

### 6. DPI / coordinate issues

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
| Overview column alignment | Cell-to-header mapping uses X-position proximity, not index |
| Module `ramp_active` field | Bool stored as int (0/1) in some EVE versions; `GetDictBool` handles |

---

## Adding a New UI Window Parser

Pattern to follow when adding support for a new EVE UI window:

1. **Find the node type** — use `GET /api/debug/reader` or `GET /api/debug/infopanel` to see live node names. The reference list of all EVE Python object type names is in `ref/botimplement/eve-online-mining-bot/EveOnline/ParseUserInterface.elm`.

2. **Add typed model** to `ParsedUI.cs` following the existing pattern (sealed class with `UINode` + typed properties).

3. **Add a finder method** to `UITreeParser.cs`:
```csharp
private NewWindow? FindNewWindow(UITreeNodeWithDisplayRegion root) =>
    root.FindFirst(n => n.Node.PythonObjectTypeName == "NewWindowType") is { } node
        ? new NewWindow
          {
              UINode = node,
              SomeText = node.Node.GetDictString("_someText"),
          }
        : null;
```

4. **Wire it up** in `UITreeParser.Parse()` by adding `NewWindow = FindNewWindow(root),` to the `ParsedUI` initializer.

5. **Add the property** to `ParsedUI.cs`.

---

## Reference Repositories

Two upstream open-source projects live in `ref/` as **read-only references**.
They are excluded from version control (`.gitignore`). Do **not** modify them.
Re-clone with the commands in the section below if they are missing.

```
ref/
├── botimplement/        # Viir/bots Elm reference implementations
│   ├── eve-online-mining-bot/
│   │   ├── Bot.elm                         # Complete mining bot logic
│   │   ├── EveOnline/ParseUserInterface.elm # Canonical UI node type catalogue
│   │   ├── EveOnline/BotFramework.elm       # Framework: event loop, process selection
│   │   └── EveOnline/BotFrameworkSeparatingMemory.elm  # Decision helpers
│   ├── eve-online-warp-to-0-autopilot/
│   │   └── Bot.elm                         # Autopilot logic
│   └── eve-online-combat-anomaly-bot/
│       └── Bot.elm                         # Combat state machine
│
└── Sanderling/ (optional, clone separately)
    └── implement/read-memory-64-bit/
        ├── EveOnline64.cs    # Source of the three DLL APIs we call
        ├── MemoryReader.cs   # Win32 ReadProcessMemory wrapper
        └── WinApi.cs         # P/Invoke declarations
```

### Why these refs matter

| Reference file | What to read there |
|---|---|
| `ref/botimplement/eve-online-mining-bot/EveOnline/ParseUserInterface.elm` | **Canonical list of all EVE UI Python object type names and dict entry keys.** Read this first when adding support for a new UI window. |
| `ref/botimplement/eve-online-mining-bot/Bot.elm` | Complete decision logic for a production mining bot: ore hold detection, belt warp, drone management, unload cycle. Cross-reference against `MiningBot/*.cs`. |
| `ref/botimplement/eve-online-warp-to-0-autopilot/Bot.elm` | Autopilot route hop logic and gate-jump sequence. Cross-reference against `AutopilotBot.cs`. |
| `ref/botimplement/eve-online-combat-anomaly-bot/Bot.elm` | Combat targeting, drone recall, anomaly navigation state machine. |
| `ref/botimplement/*/EveOnline/BotFramework.elm` | How the Elm framework selects the game client process and handles the tick event — context for `BotRunner.cs` design decisions. |
| `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` | Authoritative source of the three public DLL APIs. Read when memory reading breaks or the DLL version changes. |

### Re-cloning refs

```bash
mkdir -p ref

# Viir bots — sparse checkout (guide + implementations)
git clone --filter=blob:none --no-checkout --depth=1 https://github.com/Viir/bots ref/bots-viir
cd ref/bots-viir
git sparse-checkout init --cone
git sparse-checkout set guide/eve-online implement
git checkout main
cd ../..

# Full Sanderling source
git clone https://github.com/Arcitectus/Sanderling ref/Sanderling
```

---

## Building & Running

```bash
# Build all projects
dotnet build

# Run web server (default port 5000)
dotnet run --project src/EBot.WebHost

# Custom port
dotnet run --project src/EBot.WebHost -- --port 5001

# Run tests
dotnet test tests/EBot.Tests

# Run single test class
dotnet test tests/EBot.Tests --filter "FullyQualifiedName~UIQueryTests"

# Console runner
dotnet run --project src/EBot.Runner -- run --bot mining --tick 2000
dotnet run --project src/EBot.Runner -- list-processes
dotnet run --project src/EBot.Runner -- test-read
```

Requires .NET 9 SDK. Windows only. EVE Online must be running for memory reads to work.
The framework starts immediately; the first memory scan completes ~60 s after launch.
