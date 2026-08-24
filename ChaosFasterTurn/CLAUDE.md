# ChaosFasterTurn Mod — Developer Reference

## Project overview
Phoenix Point mod that lets the player speed up (or reset) the playback rate of **enemy
animations during the enemy (AI) turn** — movement, shooting, idle, everything the AI plays
out while the player is otherwise just waiting. It also lets the player speed up their **own**
unit's movement animation while it's actually walking on the player's own turn. Neither speed
control touches anything else - the player's own shooting/aiming/idle time is untouched, same
as everything else on the player's turn outside of that one movement action.

Four hotkeys, **hardcoded** (not configurable — see "Hardcoded hotkeys" below), shared by two
entirely independent speed controls that can never both be active at the same time (one only
runs during the AI's turn, the other only during the player's own turn):
| Key | Effect |
|---|---|
| **Backspace** | Reset to default speed (x1) |
| **`-` (Minus)** | Decrease speed by 1 step (x1 floor — never goes below normal speed) |
| **`=`/`+` (Equals)** | Increase speed by 1 step (x1 → x1.5 → x2 → x3 → x4 → … up to `Max Speed Multiplier`, default 20) |
| **Space** | Toggle: x1 ↔ the last non-1 speed used (see "Toggle Speed key" below) |

**Enemy turn** (`FasterTurnState`, ChaosFasterTurnSpeedControl.cs): applies to every AI-controlled
actor while the AI's turn is in progress, and (unless `Reset Speed On Player Turn` is disabled)
carries over between enemy turns rather than resetting each time.

**Player movement** (`PlayerMoveSpeedState`, ChaosFasterTurnPlayerMoveSpeed.cs): applies only to
a player-controlled unit's own movement action, only on the player's own turn - not while aiming,
shooting, or otherwise idle, and not during the AI's turn. Every movement action always starts
fresh at x1 with no memory of any previous move (no config, no carry-over): the first Space press
mid-move jumps to x2 (same "default to x2" fallback as the enemy Toggle key), and +/- from there
step through the same x1 → x1.5 → x2 → x3 → … ladder up to the same `Max Speed Multiplier`. Wired
via Harmony postfixes on `TacticalNavigationComponent.OnNavigateStart`/`OnNavigateEnd`
(`ChaosFasterTurnPlayerMoveStartPatch`/`ChaosFasterTurnPlayerMoveEndPatch`) - the same start/end
hook pair `ChaosFasterTurnNavigateStartPatch` already uses for the AI-overshoot protection,
applied here to the opposite case (a player-controlled mover on the player's own turn).

Two on-screen indicators, shared by both speed controls (the badge shows whichever one is
currently active - see `ChaosFasterTurnUpdater.Update()`), active whenever the effective
multiplier isn't the default x1: a persistent "x`N`" label pinned to the top-left of the Tactical
HUD (`ChaosFasterTurnSpeedBadge`), and (enemy turn only) an "(x`N`)" suffix on the little name
popup that already appears above each enemy unit as it starts acting
(`ChaosFasterTurnActivitySuffixPatch`).

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
