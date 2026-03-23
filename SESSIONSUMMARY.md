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
