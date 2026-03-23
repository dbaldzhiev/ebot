This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion of the conversation.

Summary:
1. Primary Request and Intent:

The session began with the user reporting the regression was still present: "ok still no ship telemetry is read or presented.. right now the framework is running and the eve client is running so you can probe and try to debug live." The user wanted live debugging with EVE running. Subsequently the user requested:
- Creating `AGENTS.md` explaining how the software works, live debugging, general project architecture for other agents
- Updating `README.md` with folder structure, tests, references, etc.

2. Key Technical Concepts:

- **DirectMemoryReader stub UIRoot problem**: `EnumeratePossibleAddressesForUIRootObjectsFromProcessId` returns multiple candidates. The first one is always a Python UIRoot **class stub** (the class definition object, not a live instance) with `children: null` and empty `dictEntriesOfInterest`. The live instance has children and populated dict entries.
- **Stub detection**: A live UIRoot always has children. Serialized JSON will contain `"children":[{`. A stub produces JSON with `"children":null`. Filtering by `json.Contains("\"children\":[{")` reliably skips stubs.
- **Nested Python objects in dict values**: Some dict entries (like `_text` on InfoPanel nodes) are themselves Python objects (e.g., `Link`) serialized as nested JSON objects with `pythonObjectTypeName` + `dictEntriesOfInterest`. `GetDictString` was returning raw JSON for these; fixed to extract `_setText`/`_text` from their `dictEntriesOfInterest`.
- **EVE UI node naming**: Security status node is `headerLabelSecStatus` (not "security"); system name node is `headerLabelSystemName`.
- **`_hint` pollution**: `GetAllContainedDisplayTexts` includes `_hint` values. Security status search must skip hints to avoid getting tooltip strings like "List items in solar system".
- **MCP server** at `/mcp` using SSE transport for AI agent connection.
- **SignalR** hub at `/botHub` pushing `TickUpdate`, `StateChanged`, `LogEntry`, `SurvivalChanged`.
- **BotRunner `LastRawJson`** property: stores last raw JSON from memory reader for diagnostics without needing a new memory read.
- **`read-memory-64-bit.dll`** API: `EveOnline64.EnumeratePossibleAddressesForUIRootObjectsFromProcessId(int pid)`, `EveOnline64.ReadUITreeFromAddress(ulong addr, IMemoryReader reader, int maxDepth)`, `EveOnline64.SerializeMemoryReadingNodeToJson(object obj)`.
- **JSON format from DLL**: `long`/`ulong` → JSON strings (`"42"`); `double` → JSON number; nested Python objects → JSON objects with `dictEntriesOfInterest`.

3. Files and Code Sections:

- **`src/EBot.Core/MemoryReading/DirectMemoryReader.cs`**
  - Most critical fix: skip stub UIRoot candidates (no children) in both full scan and cached-root paths
  - Full scan loop now validates JSON contains `"children":[{` before accepting:
  ```csharp
  var json = EveOnline64.SerializeMemoryReadingNodeToJson(tree);
  if (!json.Contains("\"children\":[{", StringComparison.Ordinal))
  {
      _logger.LogDebug("DirectMemoryReader: 0x{Addr:X} is a stub UIRoot (no children) — skipping", addr);
      continue;
  }
  _cachedRootAddress = addr;
  _cachedRootPid = pid;
  _logger.LogInformation("DirectMemoryReader: live UI root at 0x{Addr:X} for PID {Pid}", addr, pid);
  return json;
  ```
  - Cached-root fast path also validates:
  ```csharp
  var json = EveOnline64.SerializeMemoryReadingNodeToJson(tree);
  if (json.Contains("\"children\":[{", StringComparison.Ordinal))
      return json;
  _logger.LogDebug("Cached root 0x{Addr:X} returned stub tree — rescanning", _cachedRootAddress);
  ```

- **`src/EBot.Core/GameState/UITreeNode.cs`**
  - `GetDictString` modified to handle nested UITreeNode objects (Python Link, Color, etc.) by extracting `_setText`/`_text` from their `dictEntriesOfInterest`:
  ```csharp
  return element.ValueKind switch
  {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number => element.GetRawText(),
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      JsonValueKind.Null => null,
      // Nested UITreeNode (Python object like Link) — extract visible text from its dict
      JsonValueKind.Object when element.TryGetProperty("dictEntriesOfInterest", out var dei) =>
          dei.TryGetProperty("_setText", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() :
          dei.TryGetProperty("_text",    out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() :
          null,
      // For objects like {"int": "...", "int_low32": 1234}, try _setText
      JsonValueKind.Object when element.TryGetProperty("_setText", out var setText) =>
          setText.GetString(),
      _ => element.GetRawText(),
  };
  ```

- **`src/EBot.Core/GameState/UITreeParser.cs`**
  - `FindInfoPanelLocationInfo`: system name search updated to match "SystemName" case-insensitive:
  ```csharp
  var sysNode = node.FindFirst(n =>
      (n.Node.GetDictString("_name") ?? "").Contains("SystemName", StringComparison.OrdinalIgnoreCase)
      || (n.Node.GetDictString("_name") ?? "").Contains("labelsystemname", StringComparison.OrdinalIgnoreCase));
  ```
  - Security status search updated to match "SecStatus" (actual node name is `headerLabelSecStatus`) and exclude `_hint`:
  ```csharp
  var secNode = node.FindFirst(n =>
      (n.Node.GetDictString("_name") ?? "").Contains("SecStatus", StringComparison.OrdinalIgnoreCase)
      || (n.Node.GetDictString("_name") ?? "").Contains("security", StringComparison.OrdinalIgnoreCase));
  var secText = secNode != null
      ? (EveTextUtil.StripTags(secNode.Node.GetDictString("_setText"))
         ?? EveTextUtil.StripTags(secNode.Node.GetDictString("_text")))
      : null;
  ```

- **`src/EBot.Core/Bot/BotRunner.cs`**
  - Added `LastRawJson` property for diagnostics:
  ```csharp
  public string? LastRawJson { get; private set; }
  ```
  - Stored in tick loop: `LastRawJson = readResult.Json;`

- **`src/EBot.WebHost/BotOrchestrator.cs`**
  - Added `GetLastRawJsonSample` method:
  ```csharp
  public string? GetLastRawJsonSample(int maxChars = 2000) =>
      _runner?.LastRawJson is { } j ? j[..Math.Min(maxChars, j.Length)] : null;
  ```

- **`src/EBot.WebHost/Program.cs`**
  - Replaced slow one-shot `/api/debug/reader` (created new DirectMemoryReader, timed out) with fast version using `orch.GetLastRawJsonSample(int.MaxValue)`:
  ```csharp
  api.MapGet("/debug/reader", (BotOrchestrator orch, ILoggerFactory lf) => { ... });
  ```
  - Added `/api/debug/infopanel` endpoint that dumps InfoPanelLocationInfo node tree with names, texts, hints up to depth 4.

- **`src/EBot.WebHost/wwwroot/index.html`**
  - Added "Diag" button in header
  - Added diagnostic overlay modal (`#diag-overlay`) with pre-formatted report + JSON preview
  - Added JS handler for `btn-diagnose` that fetches `/api/debug/reader` and populates the overlay

- **`AGENTS.md`** (created new)
  - Full technical reference for AI agents: solution structure, data flow diagram, key types table, REST API, SignalR events, MCP tools, writing a new bot example, live debugging procedures, known EVE UI quirks table, build instructions

- **`README.md`** (rewritten)
  - Updated to .NET 9, removed references to Sanderling CLI as primary
  - Added detailed repository layout with all files
  - Added NuGet references table and native DLL references table
  - Added tests section, project dependency graph
  - Full REST API table including debug endpoints
  - DPI section, BotSettings reference table
  - Link to AGENTS.md for AI agents

4. Errors and Fixes:

- **`/api/debug/reader` timeout (exit code 28)**: Original endpoint created a fresh `DirectMemoryReader` which did a full 60-second memory scan. Fixed by storing `LastRawJson` in BotRunner and using the already-running runner's cached JSON instead.

- **`UITree children: 0` / stub UIRoot**: `EnumeratePossibleAddressesForUIRootObjectsFromProcessId` returned the UIRoot class object first (address `0x12827126BC0`). JSON was 525 chars, `children: null`, `dictEntriesOfInterest: {}`, `otherDictEntriesKeys` contained Python class methods (`__module__`, `__init__`, `_GetColor`). Fixed by checking `json.Contains("\"children\":[{")` before accepting any root candidate.

- **Build fails — DLLs locked by running process**: Attempted to rebuild while the framework was running. Fixed by `taskkill //f //im dotnet.exe` before each rebuild.

- **`systemName` showing raw JSON**: `GetDictString("_text")` returned the raw JSON of a Link Python object (`{"pythonObjectAddress":...,"pythonObjectTypeName":"Link",...}`). Fixed in `GetDictString` to check for `dictEntriesOfInterest` property and extract `_setText`/`_text` from it.

- **`securityStatus` showing "Apanake" (same as system name)**: `GetAllContainedDisplayTexts` was returning "Apanake" twice (once from the parent node's Link `_text`, once from the child Link node), so `texts.Skip(1)` got "Apanake" again instead of the security value. Fixed by searching for `headerLabelSecStatus` node by name (contains "SecStatus").

- **`securityStatus` showing "List items in solar system"**: The fallback (walking direct children) was picking up a `_hint` tooltip from a `ListSurroundingsBtn`. Fixed by using only `_setText`/`_text` (not `_hint`) in the security status search.

- **Security node not found by "security" keyword**: Actual node name is `headerLabelSecStatus`. The string "security" is NOT a substring of "SecStatus" in lowercase. Fixed by searching for "SecStatus" case-insensitively in addition to "security".

5. Problem Solving:

**Main regression resolved**: DirectMemoryReader was finding the UIRoot class stub (Python type object, not a live instance) as its first candidate and caching it. The stub has `children: null` and no useful dict entries, producing a valid but useless 525-char JSON. The fix filters out any root whose JSON lacks `"children":[{`, forcing iteration to the next candidate which is the real live UIRoot (found at `0x1284CCE7EF0`, producing a ~962KB JSON with 12 children and all UI elements).

**Parsing issues resolved**: After getting the real UIRoot, three parsing issues were discovered and fixed:
1. Nested Python Link objects in dict values were returning raw JSON instead of text
2. Security status node name mismatch ("SecStatus" vs "security")
3. `_hint` values polluting the security status search

**Final verified state** (from live API probe):
```
inSpace: True, capacitor: 100%, shield: 100%, armor: 100%
system: Apanake, security: 0.5, speed: 0.0 m/s, overview: 15 entries
```

6. All User Messages:

- "ok still no ship telemetry is read or presented.. right now the framework is running and the eve client is running so you can probe and try to debug live"
- "create AGENTS.MD explaining how this software works, how to live debug, general project architecture so it can be exposed to other agents"
- "update the README.MD / somewhere explain the folder structure, tests, references etc..."
- [Summary request - current message]

7. Pending Tasks:

No explicitly pending tasks remain from the user's requests. All three explicit requests in this session have been completed:
1. ✅ Fixed ship telemetry regression (DirectMemoryReader stub UIRoot + parsing fixes)
2. ✅ Created AGENTS.md
3. ✅ Updated README.md

Previously identified tasks that may still need attention (from prior sessions):
- Verify autopilot context menu fix works with EVE running (uses `"ContextMenu"` / `"MenuEntryView"` type names)
- Verify DPI fix causes clicks to land correctly

8. Current Work:

The last completed task was rewriting `README.md`. The file was completely replaced with a comprehensive document covering:
- Updated prerequisites (`.NET 9`, no external executables required)
- Quick start (3 commands)
- **Full repository layout** with every file and folder annotated
- **Project dependency graph** (ASCII art)
- **NuGet references table** (all packages with versions and purposes)
- **Native DLL references** (read-memory-64-bit.dll, Pine.Core.dll via HintPath)
- Running tests section (`dotnet test tests/EBot.Tests`)
- Web dashboard description with all REST endpoints
- SignalR events table
- MCP reference with link to AGENTS.md
- Writing a bot section with code example
- Behavior tree nodes table
- BotContext helpers code block
- Survival wrapper usage
- AI configuration (Anthropic + Ollama) including LAN exposure instructions
- DPI / click accuracy section
- BotSettings reference table
- Safety notes
- "For AI Agents" section linking to AGENTS.md

9. Optional Next Step:

The README and AGENTS.md are complete. No pending tasks remain from the user's explicit requests. If the user wants to continue, natural next steps would be to verify the autopilot bot's context menu behavior with the now-working memory reader, but this has not been explicitly requested. The session's work is concluded.
