# HavenUnderAttackTimer Mod — Developer Reference

## Project overview
Phoenix Point mod that extends the Geoscape "Objectives" panel (top-left checklist of
active objectives) so that the entry for a haven under attack ("[Haven] is under attack")
gets a live-updating **time-remaining suffix appended directly to its visible text** — an
estimate of how many more hours until the battle is resolved. Vanilla shows no such estimate
anywhere in the UI. This is deliberately *not* a tooltip: a tooltip only refreshes on mouse
hover, so an earlier version of this mod that used one was replaced with this approach,
which updates every frame the row is visible, no hover required.

The estimate is **not exact** - the real battle rolls independent randomness each hour, so
there's no single correct answer, only a range. The mod projects a best case and a worst
case and displays both, e.g. "4-6h", using the game's own time-formatting localization
(see "Reusing existing translations" below) rather than a hardcoded English string.

## Directory layout
```
HavenUnderAttackTimer/         ← solution root (this CLAUDE.md lives here) — WRITABLE
  HavenUnderAttackTimer/       ← mod source (only this folder is edited)
    HavenUnderAttackTimer.csproj
    meta.json
    HavenUnderAttackTimerConfig.cs
    HavenUnderAttackTimerMain.cs
    HavenUnderAttackTimerGeoscape.cs   ← unused template hook, left as-is
    HavenUnderAttackTimerTactical.cs   ← unused template hook, left as-is
    HavenUnderAttackTimerPatches.cs    ← Harmony patch + hour estimation logic
    Properties/AssemblyInfo.cs
  ModSDK/                      ← read-only SDK DLLs (0Harmony, Assembly-CSharp, UnityEngine, UnityEngine.UI)
  Dist/                        ← build output (HavenUnderAttackTimer.dll + meta.json)
../Assembly-CSharp/            ← READ-ONLY game source (dnSpy v6.6.0). Never edit.
../ChaosOverwatch, ../ChaosUmbraBalance ← READ-ONLY sibling mods, useful as reference examples.
```
Everything outside `HavenUnderAttackTimer/` is read-only context, not a place to write files.

## Build & deploy
- Open `HavenUnderAttackTimer.sln` in Visual Studio 2019+.
- Debug/Release builds both compile to `Dist/`.
- The post-build event in the `.csproj` copies `Dist/*` into the game's Steam WorkshopTool
  `TestMod` folder automatically on build success.
- Target framework: .NET 4.7.2 (matches the game's Unity/Mono runtime).

## Mod framework basics
- `ModMain` (`HavenUnderAttackTimerMain`) — loaded once per session; calls `harmony.PatchAll()`
  in `OnModEnabled` and exposes `HavenUnderAttackTimerMain.Cfg` as a static accessor so
  Harmony patch classes (static context) can read config without reflection.
- `ModConfig` (`HavenUnderAttackTimerConfig`) — only field is `EnableDebugLog`; this mod has no
  gameplay-tunable numbers, so it stays minimal.
- `HarmonyInstance` is provided by the framework per-mod.

## The UI element being patched
"Haven under attack" surfaces in the Geoscape as an **objective entry**, not a map tooltip:
- `GeoscapeFactionObjectiveSystem.OnSiteMissionStarted` (Assembly-CSharp, read-only) creates a
  `MissionGeoFactionObjective` wrapping the `GeoHavenDefenseMission` and adds it to the
  viewer faction's objective list whenever a haven defense mission starts.
- `UIModuleGeoObjectives.InitObjective` (private method) builds each row: it calls
  `objective.GetTitle()` for the row's display text and passes it into
  `GeoObjectiveElementController.SetObjective(...)`, which stores it in `ObjectiveText.text`.
- `GeoObjectiveElementController` is a `MonoBehaviour` with its own `Update()` that runs every
  frame while the row is visible — but vanilla's `Update()` only refreshes text for
  `EventGeoFactionObjective`-based objectives with a valid countdown `_endTime` (diplomacy
  missions etc). For a `MissionGeoFactionObjective` (haven defense) `_endTime` stays
  `TimeUnit.Invalid`, so vanilla's `Update()` no-ops and the row's text never changes on its own.
  We deliberately do **not** hook into that `_endTime` mechanism: it calls
  `gameObject.SetActive(false)` once time runs out, which would make our *estimated* deadline
  hide the whole objective row if the real (random) battle happens to run longer than projected.
  Instead we drive `ObjectiveText.text` ourselves, independent of that field.

## Key game classes (read-only Assembly-CSharp)
| Class | Namespace | Role |
|---|---|---|
| `GeoFactionObjective` / `MissionGeoFactionObjective` | `PhoenixPoint.Geoscape.Levels.Objectives` | Objective list entries; `MissionGeoFactionObjective.Mission` (public) links back to the `GeoMission` |
| `GeoHavenDefenseMission` | `PhoenixPoint.Geoscape.Entities` | The live attack; public `AttackerDeployment`, `DefenderDeployment`, `Status`, `Site`; battle math lives in its private `SimulateBattle()` |
| `HavenDefenceSettings` | `PhoenixPoint.Common.Levels.Missions` | Public tunables mirrored by our estimator: `DeploymentDamageScale`, `BattleZoneDamageRage`, `MinimalMissionDeployment`. Reached via `site.GeoLevel.MissionTypeSettingsDef.HavenDefenceSettings` |
| `UIModuleGeoObjectives` | `PhoenixPoint.Geoscape.View.ViewModules` | Objectives panel; patch target is its private `InitObjective(element, objective)` |
| `GeoObjectiveElementController` | `PhoenixPoint.Geoscape.View.ViewControllers` | Per-entry UI; public `Text ObjectiveText` field is what we overwrite; its private `Update()` is our other patch target; also exposes the public `TimeFormatter`/`TimedObjectiveFormatter` fields we reuse (see below) |
| `TimeRemainingFormatterDef` / `UIUtil.FormatTimeRemaining` | `Base.UI` | The game's existing hours/days duration formatter (`HoursText`, `DaysText`, `InfiniteText` are all `LocalizedTextBind`s translated into every supported language already) |
| `TimeUnit` | `Base.Core` | Game's duration type; `GeoHavenDefenseMission`'s private `_waitTime` field (prep time before battle simulation starts ticking) is this type |

## Implementation approach

### Estimating "hours remaining" as a range
`GeoHavenDefenseMission` has no fixed end time — its battle is simulated stochastically every
in-game hour (`UpdateInterval = 60 min`), applying an independent `Random.Range(0.9f, 1.1f)`
damage roll to each side, until one side's deployment hits 0. There is no single correct
"hours remaining" - only a range - so `HavenUnderAttackTimerPatches.cs` projects it twice:
- It copies the same damage formula `SimulateBattle()` uses (asymmetric log-scaled damage
  based on the attacker/defender deployment ratio), but replaces the per-side independent
  random roll with one flat multiplier applied uniformly to both sides' damage each tick.
  Because the multiplier cancels out of the log-asymmetry ratio, this is algebraically
  equivalent to just scaling both sides' per-tick damage by that multiplier.
- Running that projection with `1.1f` gives the soonest plausible resolution
  (`HourRange.MinHours`); running it with `0.9f` gives the latest (`HourRange.MaxHours`).
- Neither projection mutates real mission state — both only read public
  `AttackerDeployment`/`DefenderDeployment`/`Status` plus the public `HavenDefenceSettings`
  fields. If either exceeds a safety cap (`MaxProjectedHours`, currently 240h ≈ 10 days)
  without resolving, the whole estimate is treated as indeterminate.
- `_waitTime` (prep-time-before-battle-starts, usually 0 unless a faction researched extra prep
  time) is read via Harmony `Traverse` reflection and added to both bounds, since the battle
  doesn't even start ticking until it elapses.

### Reusing existing translations
Rather than hardcoding an English "(Nh remaining)" string, the suffix is built from the same
localized assets vanilla already uses for other timed objectives, both exposed as public fields
on `GeoObjectiveElementController`:
- `TimeFormatter` (a `TimeRemainingFormatterDef`) supplies the localized hour unit text
  (`HoursText`, a format string like `"{0}h"`), and `InfiniteText` for the indeterminate case.
  `HavenAttackTimerEstimator.FormatHoursRange` reuses `HoursText`'s `"{0}"` placeholder to
  insert either a single number or a combined `"min-max"` string (e.g. `"23-35h"`) - always in
  hours, even past 24, rather than switching to `DaysText`/`UIUtil.FormatTimeRemaining`'s day
  conversion (haven defense battles are short enough that hours-only stays readable, and it
  avoids a mixed "1d 3h - 2d 1h"-style range).
- `TimedObjectiveFormatter` supplies the localized wrapper phrase (with `{Objective}`/`{time}`
  placeholders) that vanilla's own `GeoObjectiveElementController.UpdateText()` uses for
  countdown objectives - reusing it keeps our text visually/linguistically consistent with
  every other timed objective in the same panel, in every supported language.

### Wiring
- `HavenAttackTimerState.Tracked` is a `Dictionary<GeoObjectiveElementController, Entry>` that
  maps a visible row to its live `GeoHavenDefenseMission` plus its plain base title text
  (`Entry.BaseTitle`), and caches the deployment numbers last used to compute the suffix.
- `[HarmonyPatch(typeof(UIModuleGeoObjectives), "InitObjective")]` **prefix**: every time the
  panel (re)builds a row, re-key (or remove) that row's `Tracked` entry. This runs whenever the
  objectives list refreshes (new objective added/removed, faction turn processed, etc.).
- `[HarmonyPatch(typeof(GeoObjectiveElementController), "Update")]` **postfix**: runs every
  frame for every visible row. Looks up the row in `Tracked`; if present and the mission is
  still `InProgress`, compares the mission's current `AttackerDeployment`/`DefenderDeployment`
  against the cached values from the last computation. Only when they differ (i.e. the
  background hourly battle tick actually happened) does it re-run the projection and rebuild
  `ObjectiveText.text` from `TimedObjectiveFormatter` + `BaseTitle` + the formatted hour range.
  This keeps the per-frame cost to a dictionary lookup and two int comparisons in the common
  case, while the visible text is always current — no hover needed, and no reliance on
  vanilla's endTime-hide-on-expiry behavior.

## Harmony patch conventions
- All patches live in `HavenUnderAttackTimerPatches.cs`, namespace `HavenUnderAttackTimer`.
- Applied via `harmony.PatchAll()` (scans the whole assembly) in `OnModEnabled`.
- `HavenUnderAttackTimerMain.Cfg` gives static patch methods config access without reflection.
- A small `Log` static helper gates `UnityEngine.Debug.Log` calls behind `Cfg.EnableDebugLog`.

## Adding new patches
1. Add a static class with `[HarmonyPatch(...)]` inside `HavenUnderAttackTimerPatches.cs` (or a
   new file — no `.csproj` changes needed for new classes in existing files).
2. For a new `.cs` file: add a `<Compile Include="NewFile.cs" />` entry in
   `HavenUnderAttackTimer.csproj`.