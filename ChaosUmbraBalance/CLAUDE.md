# ChaosUmbraBalance Mod — Developer Reference

## Project overview
Phoenix Point mod that balances the Umbra (internally called "Yuggoth") by:
1. Limiting action points on its first turn after spawning (configurable 0–100% AP)
2. Optionally preventing Umbra from spawning at all

## Directory layout
```
ChaosUmbraBalance/           ← solution root (this CLAUDE.md lives here)
  ChaosUmbraBalance/         ← mod source (only this folder is editable)
    ChaosUmbraBalance.csproj
    meta.json
    ChaosUmbraBalanceConfig.cs
    ChaosUmbraBalanceMain.cs
    ChaosUmbraBalanceTactical.cs
    ChaosUmbraBalanceGeoscape.cs
    UmbraPatches.cs            ← Harmony patches
    Properties/AssemblyInfo.cs
  ModSDK/                    ← read-only SDK DLLs (0Harmony, Assembly-CSharp, UnityEngine)
  Dist/                      ← build output (ChaosUmbraBalance.dll + meta.json)
../Assembly-CSharp/          ← read-only game source (dnSpy v6.6.0)
```

## Build & deploy
- Open `ChaosUmbraBalance.sln` in Visual Studio 2019+.
- **Debug build**: compiles to `Dist/`.
- The post-build event in the `.csproj` handles deployment automatically on build success.
- Target framework: .NET 4.7.2 (matches the game's Unity runtime).

## Mod framework basics
- `ModMain` (`ChaosUmbraBalanceMain`) — loaded once per session; wires Harmony patches.
- `ModTactical` (`ChaosUmbraBalanceTactical`) — created fresh each time a Tactical level loads.
- `ModConfig` (`ChaosUmbraBalanceConfig`) — serialized to JSON; editable from the in-game Mod Settings UI.
- `HarmonyInstance` is provided by the framework; call `harmony.PatchAll()` in `OnModEnabled`.

## Key game classes (read-only Assembly-CSharp)
| Class | Namespace | Role |
|---|---|---|
| `TacticalActorYuggoth` | `PhoenixPoint.Tactical.Entities` | The Umbra entity — extends TacticalActor |
| `TacticalActorYuggothDef` | `PhoenixPoint.Tactical.Entities` | ScriptableObject def for Yuggoth actor |
| `TacticalActor` | `PhoenixPoint.Tactical.Entities` | Base for all in-game tactical characters |
| `TacticalLevelController` | `PhoenixPoint.Tactical.Levels` | Root controller; exposes `ActorEnteredPlayEvent` |
| `TacticalDeployZone` | `PhoenixPoint.Tactical.Levels.ActorDeployment` | Spawns actors; its static `SpawnActor` is patched |
| `CharacterStats` | `PhoenixPoint.Common.Entities` | Holds `ActionPoints` (a `StatusStat`) |
| `DamageResult` | `PhoenixPoint.Tactical.Entities` | Struct passed to `ApplyDamage`; `HealthDamage` field |

## Config fields (`ChaosUmbraBalanceConfig`)
| Field | Type | Default | Meaning |
|---|---|---|---|
| `UmbraFirstTurnActionPoints` | int | 0 | % of max AP Umbra gets on its first turn (0 = no actions, 100 = no restriction, <0 = feature disabled/vanilla) |
| `ReinforcementsFirstTurnActionPoints` | int | -1 (disabled) | % of max AP AI-controlled reinforcements get on their arrival turn (0 = no actions, 100 = no restriction, <0 = feature disabled/vanilla) |
| `DisableUmbra` | bool | false | Prevent Umbra from spawning at all |
| `EnableDebugLog` | bool | false | Write mod decisions to the Unity log |

## Implementation approach

### Feature 1 — AP restriction on spawn turn
- `ChaosUmbraBalanceTactical.OnTacticalStart` subscribes to `Controller.ActorEnteredPlayEvent`.
- When a `TacticalActorYuggoth` enters play, it is added to the static `UmbraTracker.NewlySpawned` set.
- A Harmony **postfix** on `TacticalActor.StartTurn` checks if the actor is a tracked Yuggoth, then sets `CharacterStats.ActionPoints` to `(UmbraFirstTurnActionPoints / 100f) * Max`.
- After applying, the actor is removed from the set (so only the first turn is restricted).

### Feature 2 — Disable Umbra spawn entirely
- A Harmony **prefix** on `TacticalDeployZone.SpawnActor` (the static 7-parameter overload) intercepts spawn calls.
- If the `ComponentSetDef` contains a `TacticalActorYuggothDef` component, `__result` is set to `null` and the method is skipped.
- The game's `TacParticipantSpawn.DeployForTurn` handles a `null` result gracefully (logs a Unity error, then continues deploying other actors).

### Feature 3 — AP restriction for AI reinforcements
- A Harmony **postfix** on `TacticalDeployZone.SpawnActor` (the same 7-parameter overload as Feature 2) reads its last parameter (`source`) as the `TacticalDeployZone` the actor was placed by — always populated for spawns going through the normal deploy pipeline (`TacParticipantSpawn.DoSpawnActor`), which covers the mission's initial squad, scheduled reinforcement waves, and ability-triggered ones (e.g. `CallReinforcementsAbility`) alike.
- Only actors whose zone has `IsReinforcementZone == true` qualify — the same flag the game uses to draw the "reinforcements incoming" arrow marker at the edge of the map; vanilla rules never use these zones for the initial squad (turn 0), only for reinforcements. AI-controlled, non-Yuggoth actors from such a zone are queued into `UmbraTracker.PendingReinforcementAP`. Skipped entirely when `ReinforcementsFirstTurnActionPoints < 0` (feature disabled).
- A Harmony **postfix** on `TacticalActor.StartTurn` consumes that queue entry the first time it fires for the actor, setting `CharacterStats.ActionPoints` to `(ReinforcementsFirstTurnActionPoints / 100f) * Max`.
- The real Yuggoth is excluded — it already gets its own dedicated restriction from Feature 1.

## Harmony patch conventions
- All patch classes are in `UmbraPatches.cs`, namespace `ChaosUmbraBalance`.
- Patches are applied via `harmony.PatchAll(typeof(ChaosUmbraBalanceMain).Assembly)` in `OnModEnabled`.
- `ChaosUmbraBalanceMain.Instance` provides access to `Config` from static patch methods.
- `UmbraTracker` (static class in `UmbraPatches.cs`) holds the cross-class shared state (`NewlySpawned` HashSet).

## Adding new patches
1. Add a static class with `[HarmonyPatch(...)]` attribute inside `UmbraPatches.cs`.
2. No changes to `.csproj` are needed for new classes in existing files.
3. For a new `.cs` file: add a `<Compile Include="NewFile.cs" />` entry in `ChaosUmbraBalance.csproj`.