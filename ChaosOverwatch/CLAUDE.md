# ChaosOverwatch — Mod for Phoenix Point

## Project Goal

Modify Overwatch/ReturnFire behavior for player-controlled units to allow FreeAim targeting instead of automatic fire.

Intended flow:
1. Enemy moves → Overwatch/ReturnFire triggers
2. Game pauses
3. FreeAim menu opens
4. Player manually selects where to shoot
5. Player presses fire → Game resumes

## Directory Rules

- **`ChaosOverwatch/`** — mod source files, the only directory you are allowed to modify
- **`../Assembly-CSharp/`** — Phoenix Point sources (dnSpy v6.6.0), **read only**
- All other directories and files — **read only**

## Implementation Rules

- Use **Harmony patching** for all game behavior modifications (prefix/postfix/transpiler patches)
- Do not modify game sources; patch at runtime via Harmony only
- When exploring game internals, read from `ModSDK/Assembly-CSharp/` to understand the classes and methods to patch