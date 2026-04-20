# EBot Blackboard Keys Reference

The Blackboard is the bot's "short-term memory". These keys are frequently used across different bot implementations.

## Navigation & World State
- `world`: A complex object summarizing the surroundings (asteroids, NPCs, stations).
- `home_station`: The name of the preferred docking location.
- `current_belt_index`: (Mining) The index of the belt currently being harvested.
- `destination`: (Travel) The target system or station name.

## Status Flags
- `needs_unload`: Boolean indicating if the cargo/ore hold is full.
- `needs_repair`: Boolean indicating if ship HP is below safety thresholds.
- `is_at_home`: Boolean indicating if the bot is in its home system/station.

## Mining Specific
- `total_unloaded_m3`: Cumulative volume of ore successfully docked.
- `unload_cycles`: Number of trips made to the station.
- `unload_phase`: Current step in the unloading sequence (e.g., "Docking", "Transferring").

## Common Node Status
Nodes often set flags on the blackboard to communicate state to parent nodes, such as `last_action_success` or `wait_until_timestamp`.
