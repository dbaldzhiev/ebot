---
name: ebot-debug
description: Debugging and state analysis for EBot. Use when you need to inspect bot variables, step through execution, or analyze behavior tree logic via the local REST API.
---

# EBot Debugging Skill

This skill allows you to perform deep "brain scans" and step-by-step debugging of a running EBot instance.

## Workflow: Deep State Analysis

When a user reports a bug or you need to understand the bot's current thinking:

1. **Check Status**: Run `node scripts/ebot_api.js status` to see if a bot is running and its general state.
2. **Scan Blackboard**: Run `node scripts/ebot_api.js state`.
3. **Analyze**:
    - Look at the `blackboard` for variables that explain the current behavior.
    - Check the `active_nodes` stack to see which part of the behavior tree is currently running.
    - Reference `references/blackboard_keys.md` for variable meanings.
4. **Step Debug**: If the bot is paused, use `node scripts/ebot_api.js step` to advance one tick and re-check the state.

## Resource Reference

- **Helper Script**: `scripts/ebot_api.js` (Commands: `state`, `step`, `status`)
- **API Docs**: `references/api_endpoints.md`
- **Variable Guide**: `references/blackboard_keys.md`

## Common Debugging Scenarios

- **Stuck Logic**: If the bot repeats the same action, check the `active_nodes` and look for variables in the `blackboard` that are failing to update.
- **Unexpected Actions**: If the bot clicks the wrong thing, check the `queued_actions` in the state dump and the `world` state in the blackboard to see how it perceives its surroundings.
