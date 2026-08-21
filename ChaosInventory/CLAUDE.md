# ChaosInventory — Mod for Phoenix Point

## Project Goal

Refactor Phoenix Point's Geoscape inventory system so that **aircraft become real
storage containers** instead of a thin pass-through to the faction/base pool:

1. **Unlimited aircraft storage** — an aircraft (`GeoVehicle`) can hold any number of
   items, uncapped by the normal base/faction storage-capacity accounting.
2. **Per-aircraft item isolation** — when a soldier *on* an aircraft unequips an item
   (via the soldier-equip screen), the item goes into **that aircraft's own storage**,
   not the shared global/faction pool and not the docked base's storage.
3. **No cross-aircraft access** — a soldier who is not on that aircraft must not be able
   to equip items sitting in another aircraft's storage.
4. **Base ↔ aircraft transfer while docked** — when an aircraft is sitting at a base
   (`GeoVehicle.CurrentSite != null`, not `Travelling`), items can be moved in both
   directions between the aircraft's storage and that base's `ItemStorage`.

Today none of this exists: `GeoVehicle` has no storage of its own at all. Equipping a
soldier who happens to be on an aircraft silently reads/writes whichever `ItemStorage`
the *docked base* (or the global faction pool, if `UseGlobalStorage` is on) resolves to
— there is no vehicle-specific storage concept anywhere in vanilla.

## Directory Rules

- **`ChaosInventory/`** (this file's directory) — solution root, **writable**
  - **`ChaosInventory/ChaosInventory/`** — mod source, the only folder meant to be edited
    - `meta.json` — still generic SDK template, needs a unique `ID` before shipping
    - `ChaosInventoryConfig.cs`, `ChaosInventoryMain.cs`, `ChaosInventoryGeoscape.cs`,
      `ChaosInventoryTactical.cs` — currently unmodified SDK boilerplate; no feature code
      exists yet
  - **`ChaosInventory/ModSDK/`** — read-only SDK DLLs (0Harmony, Assembly-CSharp,
    UnityEngine.CoreModule)
  - **`ChaosInventory/Data/`** — loose files copied to `Dist/Data/` on build (e.g.
    localization CSVs), same convention as sibling mods
  - **`ChaosInventory/Dist/`** — build output (`ChaosInventory.dll` + `meta.json` +
    `Data/*`)
- **`../Assembly-CSharp/`** — Phoenix Point sources (dnSpy v6.6.0), **read
  only**
- **`../ChaosFaction/`, `../ChaosMutoid/`, `../ChaosOverwatch/`, `../ChaosUkrainian/`,
  `../ChaosUmbraBalance/`, `../HavenUnderAttackTimer/`** — sibling mods, **read only**,
  useful as reference examples (Harmony patch conventions, config patterns, csproj/
  build.cmd structure — see each mod's own `CLAUDE.md`)
- All other directories and files — **read only**

## Build & deploy

- Open `ChaosInventory.sln` in Visual Studio 2019+.
- Debug/Release both compile to `Dist/`.
- **No post-build auto-deploy step** — removed at explicit user request. Builds only ever
  produce `Dist/`; nothing is auto-copied to the game's `WorkshopTool\TestMod` folder or
  anywhere else. The user deploys manually where/when needed. (Every sibling mod still does
  auto-copy to `TestMod` via their own `.csproj` post-build event - this is the one mod that
  deliberately doesn't.) Separately, `G:\SteamLibrary\...\Mods\ChaosInventory` is a symlink the
  user set up pointing at this project's own `Dist/` folder - not a separate copy.
- Target framework: **v4.7.2** (matches `ChaosMutoid`/`ChaosUkrainian`/
  `ChaosUmbraBalance`/`HavenUnderAttackTimer`). `build.cmd` uses a hardcoded VS2022
  MSBuild path plus `FrameworkPathOverride` pointed at the v4.8 reference assemblies
  (the v4.7.2 targeting pack isn't installed on this dev machine; v4.8 is a
  binary-compatible superset).
- Run `build.cmd [Debug|Release]` from this directory (defaults to `Debug`).
- **Debug from `Player.log` first, before guessing at a fix** —
  `C:\Users\Ukrai\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log` — search
  for the literal keyword `ChaosInventory` once logging exists. This environment can't
  run the game, so this file is the only ground truth for what actually happened in-game.

## Implementation rules

- Use **Harmony patching** for all game behavior modifications (prefix/postfix/
  transpiler patches).
- Do not modify game sources; patch at runtime via Harmony only.
- When exploring game internals, read from `ModSDK/Assembly-CSharp.dll` or `../Assembly-CSharp/` (already, read only).
- `GeoVehicle` cannot have a real new field added (it's game code) — attach
  per-vehicle storage externally, e.g. a static `ConditionalWeakTable<GeoVehicle,
  ItemStorage>` or a `Dictionary` keyed by a stable vehicle ID, populated via a Postfix
  on whatever initializes/loads a `GeoVehicle` and persisted through a custom save-data
  companion (mirror `GeoSite`'s own `ItemStorage` save/load: `GeoSiteInstaceData.
  ItemStorage`).

## Key game classes (read-only Assembly-CSharp)

| Class | Namespace / path | Role |
|---|---|---|
| `ItemStorage` | `PhoenixPoint.Geoscape.Entities.ItemStorage` | The actual item container type. Backed by `Dictionary<ItemDef, GeoItem>` (stacked by def). Key API: `AddItem`/`RemoveItem`/`PopItem`, `AddItems`/`RemoveItems` (bulk), `Except(other)` (set-difference, used everywhere for UI-diffing), `GetStorageUsed()` (sums `ItemDef.StorageUsed` across contents — the capacity-used metric), `TrimStorageToCapacity(int)` (randomly evicts down to a cap — **never call this on vehicle storage** if it must stay unlimited). Ctor `ItemStorage(bool isFactionStorage)` — `true` auto-unloads ammo/magazines on add. |
| `GeoFaction` | `PhoenixPoint.Geoscape.Levels.GeoFaction` | Owns the "global" pool: `ItemStorage` field (per-faction) + `AircraftItemStorage` (a `VehicleEquipmentStorage`, shared spare aircraft-weapons/modules pool — separate concept from soldier items). `bool UseGlobalStorage` (proxies `GeoFactionDef.UseGlobalStorage`, default `true`). `GetItemStorage(GeoSite relatedSite)` — returns `ItemStorage` if global, else the nearest owned site's storage; **the existing branch point to extend with a third "per-vehicle" case**. `GetTotalAvailableStorage()` sums `GeoSite.StorageProvided` across all owned sites — **aircraft contribute nothing and consume nothing today**. `StripCharacterEquipment(unit)` dumps a dismissed soldier's items straight into `this.ItemStorage`. |
| `GeoSite` | `PhoenixPoint.Geoscape.Entities.GeoSite` | Represents a base (map site). `public ItemStorage ItemStorage` (per-base pool), `int StorageProvided` (capacity contribution, set from `GeoPhoenixBase.UpdateStats()` → `Stats.ItemCapacity`). `IEnumerable<GeoVehicle> Vehicles` — `Map.Vehicles.Where(v => v.CurrentSite == this)`, i.e. **how to enumerate aircraft currently docked at this base**. Save/load via `GeoSiteInstaceData.ItemStorage` — the pattern to mirror for vehicle storage persistence. |
| `GeoVehicle` | `PhoenixPoint.Geoscape.Entities.GeoVehicle` | The aircraft class (`GeoActor, IGeoCharacterContainer, ...`). **Has no `ItemStorage`, no cargo field, no capacity field at all** — the central gap this mod fills. `GeoSite CurrentSite` (null while `Travelling`, i.e. mid-flight) — the "docked at a base" hook for goal 4. `bool Travelling`. `IEnumerable<GeoCharacter> Units`/`Soldiers` — crew roster (use this to check "is this soldier on this aircraft" for goal 3). `int MaxCharacterSpace`/`FreeCharacterSpace` — existing crew-capacity fields, a template pattern (but no analogous item-capacity field exists). `bool CanTransferBetweenContainer(IGeoCharacterContainer container)` — existing "can characters move between this vehicle and that container while docked" gate (`CurrentSite != null && !Travelling && (CurrentSite == container \|\| CurrentSite.Vehicles.Any(v => v == container))`) — **reuse this exact predicate shape for gating item transfer** in goal 4. |
| `GeoCharacter` | `PhoenixPoint.Geoscape.Entities.GeoCharacter` | A soldier on the Geoscape. Three read-only item lists (`ArmourItems`/`EquipmentItems`/`InventoryItems`) backed by private mutable lists. **`SetItems(armour, equipment, inventory, freeReload)`** is the single "commit new loadout" method — there's no incremental `Equip`/`Unequip` API; the UI diffs its own state and calls this wholesale. |
| `UIStateEditSoldier` | `PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier` | **The real patch target.** Private `ItemStorage StorageItems()` resolves to `_faction.ItemStorage` (global) or `_currentSite.ItemStorage` — no vehicle branch exists. `EnterState()` resolves `_currentSite` from `base.Context.View.SelectedActor as GeoVehicle` → **immediately collapses to `geoVehicle.CurrentSite`**, discarding which vehicle was actually selected. `UpdateStorage()` diffs `StorageList` against `StorageItems()` via `ItemStorage.Except` and commits with `AddItems`/`RemoveItems`. `UpdateSoldierEquipment(soldier)` calls `soldier.SetItems(...)`. Patch plan: Harmony-patch `StorageItems()` (via `AccessTools.Method`, it's private) to return the selected aircraft's own storage when the context actor is a `GeoVehicle`, and stop collapsing to `CurrentSite` so the specific vehicle identity survives for goal 3's isolation check. |
| `UIStateEditVehicle` | `PhoenixPoint.Geoscape.View.ViewStates.UIStateEditVehicle` | Same `StorageItems()`/`UpdateStorage()` shape as `UIStateEditSoldier`, but for ground-vehicle-unit (tank/crawler) equip — **not** aircraft, despite the name. **Patched identically to `UIStateEditSoldier` as of v43** (see "Implementation (current)" above) — ground-vehicle crew now gets the same per-aircraft isolation as soldiers. |
| `UIStateVehicleRoster` | `PhoenixPoint.Geoscape.View.ViewStates.UIStateVehicleRoster` | Aircraft weapon/module loadout screen ("Air Force" tab). `UpdateAircraftStorage()` diffs against `GeoFaction.AircraftItemStorage` — the shared (not per-aircraft) spare-parts pool, a different item category (vehicle equipment, not `GeoItem`) from everything else this mod tracks. **As of v47** (introduced v46, repositioned v47), this screen also shows a read-only `ChaosAircraftItemsDisplay` slot grid of the selected aircraft's claimed general items (`ChaosVehicleAssignment`), overlaid directly into the real equipment grid's own empty cells — see "Implementation (current)" above. |
| `UIModuleSoldierEquip` | `PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip` | Screen module owning the UI-side lists: `StorageList`/`InventoryList`/`ReadyList`/`ArmorList` (`UIInventoryList`). `AttemptSlotSwap` mutates only these UI-side lists; the screen controller (`UIStateEditSoldier`) is what commits back to real data afterward. `GetUsedStorage()`/`SetStorageIndicatorUI` drive the capacity bar. |
| `UIInventoryList` / `UIInventorySlot` | `PhoenixPoint.Common.View.ViewControllers.Inventory.*` | Reusable drag/drop inventory-list UI components (`AddItem`, `RemoveItem`, `CanAddItem`). Would back any new base↔aircraft transfer screen for goal 4. |
| `VehicleEquipmentStorage` | `PhoenixPoint.Geoscape.Entities.VehicleEquipmentStorage` | Mirrors `ItemStorage`'s API but for a `List<GeoVehicleEquipment>` (aircraft weapons/hulls/engines), owned globally via `GeoFaction.AircraftItemStorage`. Separate item category from soldier gear — noted for completeness, not a direct target. |
| `ItemDef` | `PhoenixPoint.Common.Entities.Items.ItemDef` | `int StorageUsed` (default `1`) — per-unit "weight" against capacity, read via `GeoItem.CommonItemData.StorageUsed`. `CanManufacture()`/`CanFinishManufacture()` check `faction.GetTotalAvailableStorage() - faction.ItemStorage.GetStorageUsed() < StorageUsed` — confirms capacity accounting never looks at aircraft. |

## Architecture notes for implementation

- **Unlimited aircraft storage (goal 1)** — **superseded as of v38**: the original plan below
  (give each `GeoVehicle` its own separate `ItemStorage`, keeping it structurally apart from
  capacity accounting) was the v1-v37 implementation, replaced in v38 by a metadata-overlay
  design (see "Implementation (current)" above) at the user's explicit request, for
  disable-safety. Under v38, aircraft-claimed items physically share the faction's real,
  capacity-tracked `ItemStorage`, so keeping them exempt from capacity now genuinely does
  require patching `ItemStorage` itself (`GetStorageUsed()` and `TrimStorageToCapacity()`,
  see the v38 notes) — the "no patch needed" property below no longer holds. Kept here for
  historical context on the original plan, not as current guidance. *Original text: give each
  `GeoVehicle` its own `ItemStorage` instance (external attachment, see Implementation rules
  above), and never subject it to `TrimStorageToCapacity` or the faction's
  `GetTotalAvailableStorage()` accounting — keeping it structurally separate from base/faction
  capacity is sufficient; no patch to `ItemStorage` itself is needed.*
- **Per-aircraft isolation (goals 2 & 3)**: patch `UIStateEditSoldier.StorageItems()`
  (and `UIStateEditVehicle.StorageItems()` if ground-vehicle units need the same
  treatment) to return the specific aircraft's storage when the equip screen was opened
  for a soldier on a `GeoVehicle` — keyed off which vehicle was actually selected, not
  collapsed to `CurrentSite` the way `EnterState()` does today. A soldier not currently
  in that vehicle's `Units`/`Soldiers` list must never resolve to that vehicle's storage.
- **Base ↔ aircraft transfer (goal 4)**: no vanilla UI does this today (items only ever
  move soldier↔storage, or storage↔storage implicitly via `Except`-diffing against a
  single resolved `ItemStorage`) — this needs new UI/state work. Reuse
  `UIInventoryList`/`UIInventorySlot` for presentation and `ItemStorage.AddItems`/
  `RemoveItems`/`Except` for the transfer itself; gate availability with the same
  docked-check shape as `GeoVehicle.CanTransferBetweenContainer`/`GeoSite.Vehicles`
  (`CurrentSite != null && !Travelling`).
