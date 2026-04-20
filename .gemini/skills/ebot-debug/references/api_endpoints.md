# EBot API Endpoints Reference

This document lists the primary endpoints used for debugging and controlling the bot.

## Base URL
`http://localhost:5000/api`

## Core Status
- **GET `/status`**: Returns the overall bot state (Idle/Running/Paused), active bot name, and a summary of the last known game state (location, HP, targets).

## Debugging
- **GET `/debug/state`**: returns a full `BotStateDto`.
    - `TickCount`: Total ticks since start.
    - `ActiveNodes`: The current execution path in the Behavior Tree (stack).
    - `Blackboard`: All key-value pairs stored in the bot's memory.
    - `QueuedActions`: List of strings describing the actions planned for the next tick.
- **POST `/debug/step`**: Advances the bot by exactly one tick. Only valid when the bot is in the `Paused` state.
- **POST `/save-frame`**: Saves the current raw UI JSON to the `logs/frames/` directory.

## Recording
- **POST `/debug/record/start`**: Begins recording every tick into memory.
- **POST `/debug/record/stop`**: Stops the current recording.
- **GET `/debug/record/download`**: Downloads the recorded ticks as a JSON session file.
