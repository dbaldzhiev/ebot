# GEMINI.md - EBot Framework Context

## Project Overview
EBot is a high-performance C# framework designed for automating EVE Online. It operates on a **Read-Parse-Decide-Act** cycle, reading the game's UI directly from process memory to avoid traditional screen-scraping limitations.

- **Main Technologies:** .NET 9.0, ASP.NET Core, SignalR, Win32 API, Spectre.Console.
- **Key Capability:** Direct in-process memory reading via `read-memory-64-bit.dll`.
- **Decision Engine:** Robust Behavior Tree (BT) implementation for complex state-based logic.
- **Interfaces:** Web Dashboard (browser), REST API, SignalR Feed, and MCP (Model Context Protocol) for AI agents.

## Repository Structure & Key Components

### Core Logic (`src/EBot.Core`)
- **MemoryReading:** Interfaces and implementations for reading EVE process memory. `DirectMemoryReader` is the primary in-process reader.
- **GameState:** Parses raw UI JSON into `ParsedUI`, providing typed access to `ShipUI`, `Overview`, `Targets`, `Inventory`, etc.
- **DecisionEngine:** Contains the behavior tree nodes (`SequenceNode`, `SelectorNode`, `ConditionNode`, `ActionNode`) and the `BotContext`.
- **Execution:** Handles Win32 input simulation (`InputSimulator`) with humanization features like jitter and randomized delays.
- **Bot:** Defines the `IBot` interface and `BotRunner` which orchestrates the main execution loop.

### Entry Points & Orchestration
- **EBot.WebHost (`src/EBot.WebHost`):** The primary entry point. Hosts the ASP.NET Core server, manages the `BotOrchestrator` singleton, and provides the Web/MCP/SignalR interfaces.
- **EBot.Runner (`src/EBot.Runner`):** A lightweight console CLI alternative to the web host.
- **EBot.ExampleBots (`src/EBot.ExampleBots`):** Contains production-ready bot implementations like `MiningBot` and `TravelBot`.

### Supporting Assets
- **read-memory-64/:** Binary dependencies for the memory reading engine.
- **examples/:** Reference material and templates for bot development.
- **data/:** Local SQLite databases for the EVE Static Data Export (SDE).

## Building and Running

### Prerequisites
- .NET 9.0 SDK
- Windows 10/11 (required for Win32 input simulation)
- EVE Online 64-bit client running and logged in

### Key Commands
- **Build the project:** `dotnet build`
- **Run the Web Dashboard:** `dotnet run --project src/EBot.WebHost`
- **Run with custom port:** `dotnet run --project src/EBot.WebHost -- --port 5001`
- **Run Tests:** `dotnet test tests/EBot.Tests`

## Development Conventions

### Bot Implementation
- **Interface:** All bots must implement `IBot`.
- **Logic:** Decision logic should be encapsulated in a Behavior Tree returned by `BuildBehaviorTree()`.
- **Registration:** New bots must be registered in `BotOrchestrator.AvailableBots` in the WebHost project.
- **Safety:** Use `SurvivalNodes.Wrap()` to add standard emergency responses (shields, armor, warp-out) to any bot tree.

### UI Interaction
- Always use `ctx.Click(uiNode)` or `ctx.RightClick(uiNode)` rather than absolute coordinates when possible, as this leverages the framework's DPI-aware coordinate scaling.
- UI elements are accessible via `ctx.GameState.ParsedUI`.

### Coding Standards
- **Asynchronous Patterns:** Use `async/await` for I/O and long-running operations.
- **Logging:** Use the `LogSink` or the injected `ILogger` to ensure logs appear in the web dashboard and session files.
- **Input Security:** DPI awareness must be initialized at startup (`InputSimulator.SetDpiAwareness()`) to ensure click accuracy.

### Testing
- Unit tests for the UI parser and behavior tree nodes should be added to the `EBot.Tests` project.
- Use the `/api/save-frame` endpoint to capture raw UI JSON for use in regression testing the parser.

## AI & MCP Integration
- EBot exposes an **MCP Server** at `/mcp` (SSE transport).
- AI agents (Claude, Ollama) can interact with the bot using tools defined in `EveBotMcpTools.cs`.
- Technical reference for AI interaction is maintained in `AGENTS.md`.
