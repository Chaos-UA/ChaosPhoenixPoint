# ChaosFasterTurn Mod — Developer Reference

## Project overview
Phoenix Point mod that lets the player speed up (or reset) the playback rate of **enemy
animations during the enemy (AI) turn** — movement, shooting, idle, everything the AI plays
out while the player is otherwise just waiting. It does not touch the player's own turn.

Four hotkeys, step size 1, **hardcoded** (not configurable — see "Hardcoded hotkeys" below):
| Key | Effect |
|---|---|
| **Backspace** | Reset to default speed (x1) |
| **`-` (Minus)** | Decrease speed by 1 step (x1 floor — never goes below normal speed) |
| **`=`/`+` (Equals)** | Increase speed by 1 step (x1 → x2 → x3 → x4 → x5 → … up to `Max Speed Multiplier`, default 20) |
| **Space** | Toggle: x1 ↔ the last non-1 speed used (see "Toggle Speed key" below) |

Two on-screen indicators, both active whenever the multiplier isn't the default x1 and an AI
turn is in progress: a persistent "x`N`" label pinned to the top-left of the Tactical HUD
(`ChaosFasterTurnSpeedBadge`), and an "(x`N`)" suffix on the little name popup that already
appears above each enemy unit as it starts acting (`ChaosFasterTurnActivitySuffixPatch`).

`Reset Speed On Player Turn` (bool, default **enabled**) controls whether the chosen
multiplier is forgotten when the player's own turn starts (so every enemy turn begins at x1
again) or carried over to the next enemy turn automatically.

Whenever a **player-controlled** unit fires an overwatch or return-fire reaction shot during
the enemy's turn, the speed-up is suppressed to x1 for the duration of that shot and restored
immediately afterward (`ChaosFasterTurnOverwatchPausePatch` — see "Overwatch/return-fire
suppression" below), so the reacting AI actor plays that one shot at normal speed instead of
potentially fighting the shot's own dramatic-pause/camera timing.

`Reset Speed On Enemy Attack` (bool, default **enabled**) does the same thing in the opposite
direction: whenever an **AI-controlled** unit fires at a **player-controlled** unit (a regular
attack, or its own overwatch/return-fire trigger against the player), the speed-up is suppressed
to x1 for that shot and restored immediately afterward, so attacks on the player are always
visible at normal speed regardless of the chosen enemy-turn multiplier. Same
`ChaosFasterTurnOverwatchPausePatch` mechanism, same `Suppressed` flag — see "Reset Speed On
Enemy Attack" below.

**Status: implemented**, builds clean (`build.cmd Debug` / `Release`). Not yet smoke-tested
in-game (no automated way to do that from here — load it via the Steam WorkshopTool
`TestMod` folder the build already copies to, and confirm in a real mission).
