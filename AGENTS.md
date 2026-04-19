# EBot — Agent Reference

EVE Online bot framework. Reads EVE's UI from process memory, parses it into typed C# models, runs a behavior tree, and executes mouse/keyboard actions. A web UI and MCP server let human operators and AI agents inspect and control everything live.

---

## MCP Connection

SSE transport at `http://localhost:5000/mcp`. Connect Claude Desktop, Claude Code, or any MCP client.

| Tool | Description |
|---|---|
| `get_status` | Bot state + ship telemetry + game summary |
| `list_bots` | Available bot names and descriptions |
| `list_eve_processes` | Running EVE clients (PID, window title) |
| `start_bot` | Start a named bot (+ optional config) |
| `stop_bot` | Stop active bot |
| `pause_bot` / `resume_bot` | Pause control |

For everything beyond these tools, use the REST API directly.

---

## Full REST API

Base: `http://localhost:5000/api`

### Bot Control

| Method | Path | Body / Notes |
|---|---|---|
| GET | `/status` | Full `BotStatusResponse`: state, bot name, game summary, thought process |
| GET | `/bots` | `[{ name, description }]` |
| GET | `/processes` | `[{ pid, name, windowTitle }]` — running EVE clients |
| POST | `/start` | `{ "botName": "Mining Bot", "pid": 0, "destination": "Jita", "homeStation": "...", "oreHoldFullPct": 95, "shieldEscapePct": 25 }` |
| POST | `/stop` | Stop bot, return to monitor mode |
| POST | `/emergency-stop` | Hard stop + release all held input |
| POST | `/pause` | Pause |
| POST | `/resume` | Resume |
| POST | `/survival` | `{ "enabled": true }` — toggle survival wrapper |
| POST | `/kill` | Shut down EBot server process |

### Logs & Diagnostics

| Method | Path | Notes |
|---|---|---|
| GET | `/log?count=50` | Recent log entries (max 200) |
| POST | `/save-frame` | Save current raw UI JSON to `logs/frames/` for offline parser testing |
| GET | `/debug/reader` | EVE process, JSON size, parse results, root node children |
| GET | `/debug/infopanel` | InfoPanelLocationInfo node tree (names, texts, hints, depth 4) |
| GET | `/debug/modules` | Raw module slot dict keys |
| GET | `/debug/input` | DPI, screen metrics, EVE window bounds, SendInput test |
| GET | `/ai-info` | Active AI backend + model name |

### Manual Actions (fire-and-forget)

| Method | Path | Notes |
|---|---|---|
| POST | `/open-cargo` | Alt+C |
| POST | `/scan-holds` | Cycle through all hold nav entries to populate hold cache |
| POST | `/switch-hold` | `{ "label": "Ore Hold" }` — click hold in nav panel |
| POST | `/clear-destination` | Remove last route waypoint |
| POST | `/undock` | Click Undock button |
| POST | `/dock` | Right-click nearest dockable → Dock |

### DPI

| Method | Path | Notes |
|---|---|---|
| GET | `/dpi` | `{ systemDpi, coordinateScale }` |
| POST | `/dpi/scale` | `{ "scale": 1.25 }` — override (0.1–4.0) |

### Mining

| Method | Path | Notes |
|---|---|---|
| GET | `/mining-stats` | `{ totalM3, cycles, phases, needsUnload, beltIndex, … }` — full blackboard snapshot |
| GET | `/mining/belts` | `[{ index, name, isDepleted, isExcluded }]` |
| POST | `/mining/belts/{idx}/toggle` | Toggle user-excluded for belt at index |

### Travel / SDE

| Method | Path | Notes |
|---|---|---|
| GET | `/stations/search?q=jita` | Instant local SDE search (3+ chars required) |
| GET | `/stations/sde-status` | `{ isReady, progress, statusText }` |
| POST | `/stations/sde-refresh` | Force re-download SDE |
| GET | `/stations/sde-debug` | DB path + file existence check |
| GET | `/destinations` | Saved travel destinations |
| POST | `/destinations` | `{ name, stationId }` — create/update |
| DELETE | `/destinations/{id}` | Delete saved destination |

### Ollama (when `EBOT_AI_BACKEND=ollama`)

| Method | Path | Notes |
|---|---|---|
| GET | `/ollama/models` | Available models on Ollama server |
| POST | `/ollama/model` | `{ "model": "llama3.1" }` — set current model |

---

## SignalR Hub — `/botHub`

| Event | Payload |
|---|---|
| `TickUpdate` | `GameStateSummary` DTO (camelCase) — every tick |
| `LogEntry` | `{ time, level, category, message }` |
| `ActionLog` | Action executor events (clicks, key presses) |
| `StateChanged` | `"Idle"` / `"Running"` / `"Paused"` |
| `EmergencyStop` | Fired on emergency stop |
| `SurvivalChanged` | `bool` |

---

## Key Types

### `ParsedUI` — complete property list

| Property | Type | Notes |
|---|---|---|
| `UITree` | `UITreeNodeWithDisplayRegion?` | Root of annotated tree |
| `ContextMenus` | `IReadOnlyList<ContextMenu>` | Right-click menus on screen |
| `ShipUI` | `ShipUI?` | Modules, capacitor, HP bars, speed, stop/max speed |
| `Targets` | `IReadOnlyList<Target>` | Locked targets with HP/distance |
| `InfoPanelContainer` | `InfoPanelContainer?` | Route markers, system name, security, nearest location |
| `OverviewWindows` | `IReadOnlyList<OverviewWindow>` | Tabs, column headers, entries |
| `SelectedItemWindow` | `SelectedItemWindow?` | Selected object action buttons |
| `DronesWindow` | `DronesWindow?` | Drones in bay/space with HP |
| `InventoryWindows` | `IReadOnlyList<InventoryWindow>` | Holds with capacity gauge + nav entries + items |
| `ChatWindowStacks` | `IReadOnlyList<ChatWindowStack>` | Local/corp/fleet chat |
| `ModuleButtonTooltip` | `ModuleButtonTooltip?` | Tooltip on hovered module (range, falloff, etc.) |
| `Neocom` | `Neocom?` | Sidebar neocom |
| `MessageBoxes` | `IReadOnlyList<MessageBox>` | Modal dialogs |
| `CombatMessages` | `IReadOnlyList<string>` | Recent combat text |
| `StationWindow` | `StationWindow?` | Undock button when docked |
| `ProbeScannerWindow` | `ProbeScannerWindow?` | Probe scan results |
| `MiningScanResultsWindow` | `MiningScanResultsWindow?` | Surveyor results |
| `FleetWindow` | `FleetWindow?` | Fleet member list |

**`ShipUI`:**
- `Capacitor` → `LevelPercent`
- `HitpointsPercent` → `Shield`, `Armor`, `Structure`
- `Indication` → `ManeuverType` ("Warp", "Approach", "Orbit", …)
- `ModuleButtons[]` → `IsActive`, `IsBusy`, `IsOverloaded`, `IsOffline`, `RampRotationMilli`, `Name`
- `ModuleButtonsRows` → `Top[]`, `Middle[]`, `Bottom[]`
- `StopButton`, `MaxSpeedButton` (UINodes)
- `SpeedText` ("324 m/s")

**`InventoryWindow`:**
- `HoldType` (`InventoryHoldType` enum: `Unknown`, `Cargo`, `Mining`, `Infrastructure`, `ShipMaintenance`, `Fleet`, `Fuel`, `Item`)
- `CapacityGauge` → `Used`, `Maximum`, `FillPercent`
- `Items[]` → `Name`, `Quantity`, `UINode`
- `NavEntries[]` → `Label`, `HoldType`, `IsSelected`, `UINode`
- `ButtonToStackAll` (UINode)
- `SubCaptionLabelText` (window subtitle)

**`OverviewWindow`:**
- `ColumnHeaders[]`, `Tabs[]` (`Name`, `IsActive`, `UINode`)
- `Entries[]` → `Name`, `ObjectType`, `DistanceText`, `DistanceInMeters`, `IsAttackingMe`, `CellsTexts` (dict by column header)

**`MiningScanResultsWindow`:**
- `ScanButton` (UINode)
- `Entries[]` → `OreName`, `Quantity`, `Volume`, `ValueText`, `ValuePerM3`, `DistanceInMeters`, `IsGroup`, `IsExpanded`, `ExpanderNode`

### `UITreeNode` / `UITreeNodeWithDisplayRegion`

```csharp
node.PythonObjectTypeName          // EVE Python class name — key for finding elements
node.GetDictString("_setText")     // extracts text from dict (handles Link/Color nested objects)
node.GetDictDouble("_value")       // parses string-as-number (DLL quirk)
node.GetDictBool("_isSelected")
node.GetDictColor("_color")        // returns Color struct
node.GetAllContainedDisplayTexts() // all visible text in this node and children

// UITreeNodeWithDisplayRegion only:
node.Region                        // { X, Y, Width, Height } in physical pixels
node.Center                        // { X, Y }
node.FindFirst(predicate)          // depth-first search
node.FindAll(predicate)
```

**JSON quirks from the DLL:**
- `long`/`ulong` → JSON strings (`"42"`, not `42`). `GetDictDouble`/`GetDictInt` handle automatically.
- Nested Python objects (Link, Color) → `{ pythonObjectTypeName, dictEntriesOfInterest }`. `GetDictString` extracts `_setText`/`_text`.
- EVE HTML tags (`<color=…>`, `<fontsize=…>`, `<t>`) stripped by `EveTextUtil.StripTags`.

### `GameStateSnapshot`

Wraps `ParsedUI` + computed helpers:
- `IsDocked`, `IsInSpace`, `IsWarping`
- `CapacitorPercent`, `ShieldPercent`, `ArmorPercent`, `StructurePercent`
- `RouteJumpsRemaining`, `HasContextMenu`

### `BotContext` — full API

```csharp
ctx.GameState          // GameStateSnapshot
ctx.Blackboard         // Persists across ticks
ctx.TickCount          // long
ctx.StartTime          // DateTimeOffset
ctx.RunDuration        // TimeSpan
ctx.ActiveNodes        // Stack<string> — currently executing nodes (debug)
ctx.StopRequested      // bool

// Actions (enqueued, executed after Decide phase)
ctx.Click(node)
ctx.Click(node, VirtualKey.Control)      // ctrl+click
ctx.RightClick(node)
ctx.Drag(fromNode, toNode)
ctx.Hover(node)                          // mouse move only (for submenu hover)
ctx.Scroll(node, delta)
ctx.ClickAt(x, y)
ctx.KeyPress(key)
ctx.KeyPress(key, modifier)
ctx.TypeText("text")
ctx.Wait(TimeSpan)
ctx.Log(message)
ctx.RequestStop()

// Blackboard
ctx.Blackboard.Set("key", value)
ctx.Blackboard.Get<T>("key")             // default(T) if missing
ctx.Blackboard.Has("key")
ctx.Blackboard.Remove("key")
ctx.Blackboard.SetCooldown("name", TimeSpan.FromSeconds(5))
ctx.Blackboard.IsCooldownReady("name")  // true if never set OR expired
```

---

## MiningBot — Complete Blackboard Reference

### Initialized in `OnStart()`

| Key | Type | Init value | Purpose |
|-----|------|-----------|---------|
| `last_belt_target` | int | -1 | Last warped-to belt index |
| `return_phase` | string | "" | ReturnToStation state machine |
| `return_tick` | int | 0 | Ticks in current return phase |
| `home_menu_type` | string | "" | Learned: "Stations" or "Structures" |
| `return_tried_stations` | bool | false | Navigation heuristic |
| `return_tried_structures` | bool | false | Navigation heuristic |
| `return_current_menu` | string | "" | Active menu tracking |
| `unload_phase` | string | "" | PerformUnload state machine |
| `unload_ticks` | int | 0 | Ticks in current unload phase |
| `needs_unload` | bool | **true** | Trigger return + unload |
| `belt_phase` | string | "" | WarpToBelt state machine |
| `belt_phase_ticks` | int | 0 | Ticks in current belt phase |
| `discover_phase` | string | "" | One-time belt discovery |
| `discover_tick` | int | 0 | Ticks in discovery |
| `menu_expected` | bool | false | Context menu was opened intentionally |
| `belt_prop_started` | bool | false | Propulsion activated for this belt |
| `home_station` | string | *(from station or override)* | Docking target name |
| `home_station_set` | bool | *(true if name found)* | Home station is known |

### Set during execution

| Key | Type | Purpose |
|-----|------|---------|
| `mining_phase` | string | BT_MineAtBelt sub-state: `"open_surveyor"` / `"approach_lock"` / `"get_range"` / `"fire_lasers"` |
| `mining_tick` | int | Ticks in current mining phase |
| `world` | WorldState | Synthesized asteroid/laser state (overwritten every tick) |
| `laser_range_m` | double | Learned laser range in meters |
| `laser_targets` | `Dictionary<int,string>` | laser-index → asteroid address |
| `assumed_locked` | `HashSet<string>` | Asteroid addresses assumed locked |
| `belt_index` | int | Current belt rotation counter |
| `belt_target` | int | Belt index selected for next warp |
| `belt_drone_recall` | cooldown | Drone recall timeout for belt hop |
| `return_drone_timeout` | cooldown | Drone recall timeout for return |
| `undock_cd` | cooldown | Undock rate-limit |
| `surveyor_scan_long` | cooldown | 2-min between surveyor scans |
| `unload_vol_before` | double | Ore hold volume before unload started |
| `home_system` | string | Learned home system name |
| `total_unloaded_m3` | double | Session stat |
| `unload_cycles` | int | Session stat |

### `FinishUnload()` — cycle reset hook (`MiningBot.Unload.cs`)

Runs after each successful unload. Clears: `needs_unload`, `unload_phase`, `unload_vol_before`, `belt_index`, `last_belt_target`, `belt_prop_started`.

**Known bugs:** Does NOT clear `mining_phase` or `belt_phase`. On the next cycle, these state machines start mid-state rather than fresh. Add `ctx.Blackboard.Remove("mining_phase")` and `ctx.Blackboard.Set("belt_phase", "")` here to fix the undock/dock loop issue.

---

## Behavior Tree Nodes

| Node | Semantics |
|------|-----------|
| `SequenceNode` | AND — stops on first Failure; **cursor persists across ticks** |
| `SelectorNode` | OR — stops on first Success; **cursor persists across ticks** |
| `StatelessSelectorNode` | OR — **resets to index 0 every tick**; use for preemptible priority selection |
| `ConditionNode` | `Func<BotContext, bool>` — Success/Failure only |
| `ActionNode` | `Func<BotContext, NodeStatus>` — any status |
| `InverterNode` | Inverts Success↔Failure |
| `AlwaysSucceedNode` | Forces Success |
| `RepeatNode` | Repeats N times (-1 = infinite) |
| `WaitNode` | Waits duration → Success |

---

## Adding a New UI Window Parser

1. **Find node type** — `GET /api/debug/reader` or `GET /api/debug/infopanel`. Reference: `ref/botimplement/eve-online-mining-bot/EveOnline/ParseUserInterface.elm`
2. **Add typed model** to `ParsedUI.cs`
3. **Add finder** to `UITreeParser.cs`:
```csharp
private NewWindow? FindNewWindow(UITreeNodeWithDisplayRegion root) =>
    root.FindFirst(n => n.Node.PythonObjectTypeName == "NewWindowType") is { } node
        ? new NewWindow { UINode = node, SomeText = node.Node.GetDictString("_someText") }
        : null;
```
4. **Wire** in `UITreeParser.Parse()`: `NewWindow = FindNewWindow(root),`

---

## Live Debugging

### Memory reader health
```
GET http://localhost:5000/api/debug/reader
```
Expect: EVE process found, JSON > 100 KB, `UITree children > 0`, ShipUI/InfoPanel present.

If `UITree children: 0` → reader found the UIRoot class stub. The stub-skip filter (`"children":[{"`) should handle this, but check `DirectMemoryReader.cs`.

### Save a frame for offline testing
```
POST http://localhost:5000/api/save-frame
```
Writes current UI JSON to `logs/frames/`. Load in `EBot.Tests` to test `UITreeParser` without a live EVE client.

### First scan slowness
`EnumeratePossibleAddressesForUIRootObjectsFromProcessId` scans the full EVE heap — ~60 s on first run. Root address is cached; subsequent ticks are < 500 ms. This is normal.

### Click accuracy
```
GET  http://localhost:5000/api/dpi          → { systemDpi, coordinateScale }
GET  http://localhost:5000/api/debug/input  → screen/window/cursor diagnostics
POST http://localhost:5000/api/dpi/scale    { "scale": 1.25 }
```
`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` at startup means coordinates are physical pixels — matching EVE's `_displayX`/`_displayY`.

### Key log messages

| Message | Meaning |
|---|---|
| `DirectMemoryReader: live UI root at 0x…` | Correct root found |
| `DirectMemoryReader: 0x… is a stub UIRoot — skipping` | Stub filtered, searching |
| `[Mining] BT Tick #N \| Docked=… InSpace=… Warping=…` | Per-tick state heartbeat |
| `[Mining] WarpToBelt Phase: '…' (Ticks: N)` | Belt navigation progress |
| `[Mining] Unload: Found Mining hold` | Unload sequence found the ore hold |
| `[Mining] FinishUnload: X m³ unloaded` | Cycle complete |

---

## Known EVE UI Quirks

| Quirk | Handling |
|---|---|
| `long`/`ulong` dict values as JSON strings | `GetDictDouble`/`GetDictInt` parse string-as-number |
| Nested Python objects (Link, Color) | `GetDictString` extracts `_setText`/`_text` from `dictEntriesOfInterest` |
| UIRoot class stub returned before live instance | Filtered by checking `"children":[{"` in JSON |
| Overview column alignment | Cell-to-header mapping uses X-position proximity, not index |
| `_hint` on buttons pollutes location text | Security status search skips `_hint`; uses `_setText`/`_text` only |
| Module `ramp_active` as int (0/1) | `GetDictBool` handles |
| Only one inventory hold visible at a time | `BotOrchestrator._holdCache` persists across ticks |
| Context menu from right-click takes 500–800 ms | Bots use `ctx.Wait()` + `ctx.Blackboard.Set("menu_expected", true)` guard |
| Belt warp: ship in warp before `BT_MineAtBelt` gets control | `HandleWarping()` preempts in-space handlers during warp |

---

## Project Structure

```
src/
├── EBot.Core/
│   ├── MemoryReading/
│   │   ├── DirectMemoryReader.cs    # Primary: in-process P/Invoke → read-memory-64-bit.dll
│   │   ├── SanderlingReader.cs      # Fallback: CLI exe or HTTP alternate-ui
│   │   ├── EveProcessFinder.cs
│   │   └── IEveMemoryReader.cs
│   ├── GameState/
│   │   ├── UITreeParser.cs          # JSON → ParsedUI (all element finders)
│   │   ├── ParsedUI.cs              # Typed models
│   │   ├── UITreeNode.cs            # Raw deserialized node + dict accessors
│   │   └── GameStateSnapshot.cs     # Per-tick snapshot + computed helpers
│   ├── DecisionEngine/
│   │   ├── BehaviorTree.cs          # All node types
│   │   ├── BotContext.cs
│   │   └── Blackboard.cs
│   ├── Execution/
│   │   ├── InputSimulator.cs        # Win32 SetCursorPos + SendInput
│   │   ├── BotAction.cs             # Action types
│   │   └── ActionExecutor.cs
│   └── Bot/
│       ├── BotRunner.cs             # Main tick loop
│       ├── IBot.cs
│       └── BotSettings.cs
├── EBot.ExampleBots/
│   ├── MiningBot/                   # 7 partial-class files (see CLAUDE.md)
│   ├── AutopilotBot/TravelBot.cs    # TravelBot + TravelBotScripted
│   ├── SurvivalNodes.cs
│   └── IdleBot.cs
├── EBot.WebHost/
│   ├── Program.cs                   # All REST endpoints + DI
│   ├── BotOrchestrator.cs           # Singleton: owns BotRunner, hold cache
│   ├── GlobalHotKeyService.cs       # Pause/Break emergency stop
│   ├── Hubs/BotHub.cs              # SignalR hub
│   ├── Mcp/EveBotMcpTools.cs       # MCP tool server
│   ├── Services/SdeService.cs       # EVE station SDE download + SQLite
│   ├── Services/ChatService.cs      # Anthropic Claude backend
│   ├── Services/OllamaChatService.cs
│   ├── Terminal/TerminalDashboard.cs
│   └── wwwroot/index.html           # Single-page dashboard
└── EBot.Runner/                     # Minimal console CLI
```

---

## Reference Implementations

`ref/botimplement/` contains upstream Viir/bots Elm framework — same logical design, different language. Read-only.

| File | What to learn |
|---|---|
| `eve-online-mining-bot/EveOnline/ParseUserInterface.elm` | **All EVE Python object type names and dict keys** |
| `eve-online-mining-bot/Bot.elm` | Complete mining logic: ore hold detection, belt nav, drone mgmt, unload cycle |
| `eve-online-warp-to-0-autopilot/Bot.elm` | Route hop, gate jumping |
| `*/EveOnline/BotFramework.elm` | Process selection, tick event design |
| `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` | DLL API source — read when memory reading breaks |
