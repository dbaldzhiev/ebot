# EBot — Session Summary

A living document capturing the current development state, recent fixes, and active
context for agents or developers picking up work mid-stream.

---

## Current Status (2026-03-23)

**Bot framework is working end-to-end.** Ship telemetry reads correctly from a live
EVE client. The web dashboard at `http://localhost:5000` shows live game state.

Last verified live output:
```
inSpace: True, capacitor: 100%, shield: 100%, armor: 100%
system: Apanake, security: 0.5, speed: 0.0 m/s, overview: 15 entries
```

---

## Key Fixes Applied This Session

### 1. DirectMemoryReader — UIRoot stub filtering

`EnumeratePossibleAddressesForUIRootObjectsFromProcessId` returns several addresses.
The **first address is always the Python UIRoot class stub** — the type-definition
object, not a live instance. It serializes to ~525 bytes with `children: null` and
empty dict entries, causing the parser to see a valid but empty game state.

**Fix (`DirectMemoryReader.cs`):** After serializing each candidate, reject any whose
JSON does not contain `"children":[{"`. The real live root is found at the next
address and cached for subsequent ticks.

### 2. Nested Python objects in dict values

Some `dictEntriesOfInterest` values are themselves Python objects (e.g. `Link`,
`Color`) serialized as `{ "pythonObjectTypeName": "Link", "dictEntriesOfInterest": { "_setText": "Apanake" } }`.

**Fix (`UITreeNode.GetDictString`):** When a dict value is a JSON object with
`dictEntriesOfInterest`, extract `_setText` or `_text` from it instead of returning
raw JSON.

### 3. Security status node name

The actual EVE UI node name for security status is `headerLabelSecStatus`.
The string `"security"` is not a substring of `"SecStatus"`, so the original parser
never found it.

**Fix (`UITreeParser.cs`):** Search for `"SecStatus"` case-insensitively in addition
to `"security"`.

### 4. `_hint` pollution in security status

`GetAllContainedDisplayTexts` includes `_hint` tooltip strings. The `ListSurroundingsBtn`
near the security label has a `_hint` of "List items in solar system", which was
being returned as the security status value.

**Fix:** Search for the `headerLabelSecStatus` node by name and read only its
`_setText`/`_text`, never its `_hint`.

---

## ESI API — Set Destination Alternative

The [EveOnlineMCP](https://github.com/WaterPistolAI/EveOnlineMCP) project uses only the
**ESI REST API** (no memory reading, no UI automation). The ESI has an endpoint:

```
POST https://esi.evetech.net/ui/autopilot/waypoint/
  ?destination_id=<solar_system_id>
  &clear_other_waypoints=true
  &add_to_beginning=false
Authorization: Bearer <access_token>
Scope required: esi-ui.write_waypoint.v1
```

This directly sets the in-game autopilot destination **without any UI interaction** — more
reliable than the current `Shift+S` search approach. To get a solar system ID from a name:
```
POST https://esi.evetech.net/universe/ids/
Body: ["Jita"]
→ { "systems": [{ "id": 30000142, "name": "Jita" }] }
```

**Current blocker**: ESI requires OAuth2 with `esi-ui.write_waypoint.v1` scope — a full
browser auth flow per character. Not yet implemented. The `AutopilotBot` currently uses
UI automation (Shift+S search) which is fragile. Adding ESI auth would remove the destination-
setting failure mode entirely. The coordinate offset fix (below) addresses the click accuracy
issue but ESI is the long-term solution for destination setting.

## Features Added This Session

### Window client-area offset (windowed mode fix)
`ActionExecutor.ExecuteAllAsync` now calls `ClientToScreen(windowHandle, (0,0))` each tick
to get the EVE window's client-area screen origin. This offset is stored in
`InputSimulator.WindowClientOffsetX/Y` and added to every click coordinate, fixing click
positions when EVE is running in windowed mode (title bar / border skew).

### Global Pause/Break kill switch
`GlobalHotKeyService` (new `BackgroundService`) registers `VK_PAUSE` as a system hotkey via
`RegisterHotKey`. When pressed (even if EVE has focus), `BotOrchestrator.EmergencyStopAsync()`
is called:
1. Bot swapped out to `IdleBot` immediately
2. `InputSimulator.ReleaseAllInput()` sends LEFTUP, RIGHTUP, SHIFT/CTRL/ALT KEYUP
3. All web UI clients receive `EmergencyStop` SignalR event (red flash)

Web UI also has a **NUKE** button (`POST /api/emergency-stop`) as a backup for when the
keyboard hotkey isn't accessible.

### Verbose action log panel
`ActionExecutor.ActionPerformed` event fires before each action with a human-readable
description: `"Click (432, 267)"`, `"RightClick (650, 300)"`, `"KeyPress: S + Shift"`, etc.
`BotOrchestrator` subscribes and broadcasts via SignalR `ActionLog` event.
The web UI shows a dedicated **Actions** panel (left side of bottom centre panel) separate
from the framework log. The framework log (right side) continues to show operational messages.

## Not Currently a Focus

- **Tests** (`tests/EBot.Tests`): exist as a placeholder; not up to date and not
  actively maintained at this stage of development.
- **`SanderlingReader`** fallback: present in the codebase but `DirectMemoryReader`
  is used exclusively.
- **`EBot.Runner`**: minimal console runner; `EBot.WebHost` is the primary entry point.

---

## Open / Unverified

| Item | Status |
|---|---|
| Autopilot context menu fix (`"ContextMenu"` / `"MenuEntryView"` type names) | Code updated; not yet verified against live EVE context menu |
| DPI click accuracy | Code applies `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`; adjust per-machine via `POST /api/dpi/scale` |

---

## Reference Material

| Resource | Path |
|---|---|
| Memory-reading engine source | `ref/Sanderling/implement/read-memory-64-bit/EveOnline64.cs` |
| EVE UI node type catalogue | `ref/bots/guide/eve-online/parsed-user-interface-of-the-eve-online-game-client.md` |
| Autopilot bot patterns | `ref/bots/guide/eve-online/eve-online-warp-to-0-autopilot-bot.md` |
| Mining bot patterns | `ref/bots/guide/eve-online/eve-online-mining-bot.md` |
| Full agent/AI reference | `AGENTS.md` |
| Full developer reference | `README.md` |

> `ref/` is git-ignored. Re-clone instructions are in `README.md § Reference Repositories`
> and `AGENTS.md § Reference Repositories`.

---

## Architecture in One Paragraph

EBot reads EVE's UI from process memory using `read-memory-64-bit.dll`
(`EveOnline64.ReadUITreeFromAddress`), serializes the Python object graph to JSON,
deserializes it into a `UITreeNode` hierarchy, annotates each node with absolute
screen coordinates, then parses the tree into a fully typed `ParsedUI` model.
A behavior tree (built by the active `IBot`) ticks against a `BotContext` and
enqueues mouse/keyboard actions that are executed via Win32 `SetCursorPos` /
`mouse_event`. The web host exposes the state via REST, SignalR, and an MCP server.
