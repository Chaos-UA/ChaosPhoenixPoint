# ChaosMutoid Mod — Developer Reference

This mod now has two independent features, documented in their own sections below:
**ability re-learn** (original feature, "Project goal" through most of this file) and
**Mutoid body-part mutation** (added later, see "Feature: Mutoid Body-Part Mutation").

## Project goal

Phoenix Point mod that lets a **Mutoid** soldier re-learn/swap an ability it has already
learned on any of its per-level ability tracks shown on the roster's **Edit Soldier**
screen (reached via the Roster's **Mutoids** tab): the **Personal** (mutation) track,
and — if the Mutoid was human before mutating and kept its original class progression —
its retained **PrimaryClass** and **SecondaryClass** ("soldier") tracks. Paying the same
Mutagen cost as learning that ability fresh applies to all three. Clicking an
already-learned slot on any of them reopens the exact same vanilla specialization-picker
popup used for a first-time pick, and the exact same vanilla purchase-confirmation
dialog (`ModalType.CharacterProgressionConfirmCharacter`) appears before anything is
actually learned. Vanilla otherwise never lets a Mutoid touch a slot again once it's
been decided — the slot becomes a permanent, non-interactive "owned" icon.

**Root cause of the vanilla limitation, confirmed by reading source and now
patched (see "Status" below):**
- `MutoidAbilityTrackContainerElement.SetAbilitySlot` (`PhoenixPoint/Geoscape/View/
  ViewControllers/MutoidAbilityTrackContainerElement.cs`) renders **any** slot with a
  non-null `AbilityTrackSlot.Ability` as a fixed/owned icon with `isBuyable=false`
  (`element.SetSkill(..., false, false, false, true)`) — since `AbilityTrackSkillEntry
  Element.OnPointerClick()` bails out immediately when `!IsBuyableSkill`, the click never
  even reaches the container's own click handler for an already-learned slot.
- `SpecializedAbilityTrackPopupElement.Init` (`PhoenixPoint/Geoscape/View/
  ViewControllers/SpecializedAbilityTrackPopupElement.cs`) — the picker popup itself —
  always builds the *entire* Pandoran ability-track grid (every specialization column ×
  every level row) in one call. For any level whose real `AbilityTrackSlot.Ability` is
  already non-null, every candidate ability at that level is marked `isLearnable=false`
  (`num2`/`flag3` in the source), which is what stops a level from ever being
  re-picked once decided — not `CharacterProgression.CanLearnAbility`, which the Pandoran
  purchase path never even calls (see `UIModuleCharacterProgression.
  OnTrackSlotPointerClicked`'s `_hasPandoranProgression ||
  CanLearnAbility(...)` short-circuit).
- Cost: the Pandoran/Mutoid purchase path (`UIModuleCharacterProgression.BuyAbility`)
  charges `slot.Ability.CharacterProgressionData.MutagenCost` — i.e. the *newly bought*
  ability's own fresh-learn cost — via `Wallet.Take(Mutagen, cost, OperationReason.
  Purchase)`, **not** `CharacterProgression.GetAbilitySlotCost`/`SkillPointCost`, which is
  only used on the non-Pandoran (human `SkillPoints`) path. Reusing this vanilla method
  unmodified for a re-learn automatically satisfies "same cost as learning it fresh," with
  no separate cost formula needed.
- A real vanilla gap found while implementing this: `BuyAbility()`'s Pandoran branch adds
  the new ability (`Progression.AddAbility`) and overwrites the slot's `.Ability`, but
  never removes whatever ability the slot held *before* — because in vanilla a slot is
  never bought twice, so this case never arises. Left alone, a re-learn would leave the
  old ability permanently active (still in `CharacterProgression`'s private `_abilities`
  list) alongside the new one. `CharacterProgression` has no public single-ability
  removal method (only `AddAbility`), so this mod removes the old one via reflection
  into the private `_abilities` field — see `ChaosMutoidPatches.cs`.

## Directory rules

- **`ChaosMutoid/`** (this file's directory) — solution root, **writable**
  - **`ChaosMutoid/ChaosMutoid/`** — mod source, the only folder meant to be edited
    - `meta.json` — still the generic SDK template (see "Status" for the one open item)
    - `ChaosMutoidConfig.cs` — `EnableMutoidBodyPartMutation` (`[ConfigField]`, defaults
      `true`, gates the Mutation/Anu screen) + `EnableMutoidBodyPartAugmentation`
      (`[ConfigField]`, defaults `false`, gates the Bionics/New Jericho screen
      independently — see "Feature: Mutoid Body-Part Mutation") +
      `EnableReplacementForMutationAndAugmentation` (`[ConfigField]`, defaults `false`,
      applies to any soldier, not just Mutoids — see "Feature: Cross-Category Augment
      Replacement") + `EnableDebugLog` (`[ConfigField]`, defaults `false`), mirroring
      `ChaosOverwatch`'s own debug-log toggle
    - `ChaosMutoidMain.cs` — `OnModEnabled` calls `harmony.PatchAll()`, then
      `ChaosMutoidLocalization.LoadOwnTranslations(Instance.Entry.Directory)`; also sets
      the static `Cfg` accessor (`ChaosMutoidMain.Cfg`) from `Config` in both
      `OnModEnabled` and `OnConfigChanged`, so patches can read `EnableDebugLog` without
      reflection
    - `ChaosMutoidLocalization.cs` — loads `Data/Localization/ChaosMutoid.csv`
      (`KEY_CHAOSMUTOID_*` terms) into I2 Loc, mirroring `ChaosFaction`'s own
      `ChaosFactionLocalization.cs` line for line (own dedicated `LanguageSourceData`
      registered directly with `LocalizationManager.Sources` — safe because every
      `KEY_CHAOSMUTOID_` term is exclusively owned by this mod, unlike `ChaosUkrainian`'s
      loader which has to defer/retry against terms vanilla or TFTV might own). Currently
      backs the single `KEY_CHAOSMUTOID_RELEARN_TOOLTIP` term (the "click to learn a
      different ability" tooltip) — any future player-facing string should go through
      this the same way rather than a hardcoded literal
    - `ChaosMutoidPatches.cs` — the actual feature: `Log` (gated `Debug.Log` helper,
      mirrors `ChaosOverwatch`'s `Log.Debug`) + `MutoidRelearnState` (bridges the
      popup-build patch, the redirect patch, and the purchase patch — `Pending` is a
      `List<PendingPick>`, one entry per already-decided slot on the clicked slot's own
      track that's currently blanked out for this popup session, not just the clicked
      one; `RedirectTrack` is set only while that same `Init` call is executing, and
      only for a `PrimaryClass` slot — see "Status") +
      `Patch_MutoidAbilityTrackContainerElement_SetAbilitySlot` (makes an already-learned
      slot clickable for **any** `abilitySource` — empty slots are the only thing left to
      vanilla) + `Patch_SpecializedAbilityTrackPopupElement_Init` (temporarily blanks
      *every* already-decided slot on `slot.AbilityTrack` — the clicked slot's real
      track, not a hardcoded one — so the picker treats the whole track as fresh and any
      level can be re-picked in one popup session; also sets `MutoidRelearnState.
      RedirectTrack` when that track is `PrimaryClass` specifically, see next patch) +
      `Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect` (Prefix-only — while
      `RedirectTrack` is set, redirects vanilla `Init`'s own hardcoded
      `GetAbilityTrack(Personal)` call to the clicked slot's real track instead; see
      "Status" for the vanilla bug this corrects) +
      `Patch_UIModuleCharacterProgression_BuyAbility` (removes whichever pending pick's
      old ability got superseded after a successful re-learn, since vanilla's own
      purchase path never does) +
      `Patch_UIModuleCharacterProgression_OnTrackSlotPointerClicked_Diagnostics`
      (diagnostic-only Prefix, no behaviour change — logs why a click on a popup
      candidate did or didn't lead to a purchase; see "Status" for the bug it's meant to
      confirm) — plus the unrelated Mutoid body-part mutation feature:
      `Patch_EditUnitButtonsController_SetContextButtonVisibility` +
      `Patch_GeoscapeView_ToMutateState` + `Patch_GeoscapeView_ToBionicsState` +
      `ChaosMutoidGeoscapeViewUtl` (shared `_statesStack` reflection accessor) +
      `Patch_ClassRequirementDef_IsRequirementMet` (bypasses class-restricted
      Mutation/Bionics candidates, e.g. Priest-only heads, for Mutoids) — see
      "Feature: Mutoid Body-Part Mutation" below for all five — plus the unrelated
      (applies to any soldier, not just Mutoids) cross-category replacement feature:
      `Patch_UIModuleMutate_InitCharacterInfo_AllowReplacement` +
      `Patch_UIModuleBionics_InitCharacterInfo_AllowReplacement` — see "Feature:
      Cross-Category Augment Replacement" below
    - `ChaosMutoidGeoscape.cs` / `ChaosMutoidTactical.cs` — SDK demo template hooks,
      still unmodified boilerplate — this feature needed neither
  - **`ChaosMutoid/ModSDK/`** — read-only SDK DLLs (0Harmony, Assembly-CSharp,
    UnityEngine.CoreModule)
  - **`ChaosMutoid/Data/`** — loose files copied to `Dist/Data/` on build (e.g.
    `Data/Localization/ChaosMutoid.csv`) — the `.csproj`'s `Data\**` content item needs
    an explicit `<Link>Data\%(RecursiveDir)%(Filename)%(Extension)</Link>` (matching
    `ChaosFaction.csproj`'s own) to actually preserve subfolder structure on copy; without
    it MSBuild silently flattens every nested file straight into `Dist/` root, which
    silently broke `ChaosMutoidLocalization`'s CSV lookup path the first time this was
    added (fixed the same session — worth remembering if a future `Data/` subfolder is
    added and its files mysteriously land in the wrong place)
  - **`ChaosMutoid/Dist/`** — build output (`ChaosMutoid.dll` + `meta.json` + `Data/*`)
- **`../Assembly-CSharp/`** — Phoenix Point sources (dnSpy v6.6.0), **read
  only**
- **`../ChaosFaction/`, `../ChaosOverwatch/`, `../ChaosUkrainian/`, `../ChaosUmbraBalance/`,
  `../HavenUnderAttackTimer/`** — sibling mods, **read only**, useful as reference
  examples (Harmony patch conventions, config patterns, csproj/build.cmd structure — see
  each mod's own `CLAUDE.md` for how it documents its own feature set)
- All other directories and files — **read only**

## Build & deploy

- Open `ChaosMutoid.sln` in Visual Studio 2019+.
- Debug/Release both compile to `Dist/`.
- The post-build event in the `.csproj` copies `Dist/*` into the game's mod test folder
  automatically on build success (`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\
  Phoenix Point\Steam\WorkshopTool\TestMod`), same as every sibling mod.
- Target framework: **v4.7.2** (matches `ChaosUkrainian`/`ChaosUmbraBalance`/
  `HavenUnderAttackTimer` — *not* the v4.8 that `ChaosFaction`/`ChaosOverwatch` use).
  `build.cmd` therefore follows those three mods' pattern: a hardcoded VS2022 MSBuild
  path plus `FrameworkPathOverride` pointed at the v4.8 reference assemblies (the v4.7.2
  targeting pack isn't installed on this dev machine; v4.8 is a binary-compatible
  superset), rather than `ChaosFaction`/`ChaosOverwatch`'s `vswhere`-based script.
- Run `build.cmd [Debug|Release]` from this directory (defaults to `Debug`, matching the
  same three sibling mods).
- **Always debug from `Player.log` first, before guessing at a fix** —
  `C:\Users\Ukrai\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log` — search
  for the literal keyword `ChaosMutoid` once logging exists. This environment can't run
  the game, so this file is the only ground truth for what actually happened in-game.

## Mod framework basics

- `ModMain` (`ChaosMutoidMain`) — loaded once per session; `OnModEnabled` calls
  `harmony.PatchAll()`, which picks up every `[HarmonyPatch]`-attributed class in
  `ChaosMutoidPatches.cs` automatically.
- `ModConfig` (`ChaosMutoidConfig`) — one field, `EnableDebugLog` (the re-learn cost
  itself is always the target ability's own normal fresh-learn `MutagenCost`, not a
  separately tunable value, so no other config surface was needed). Toggling it in-game
  flips `Log.Debug`'s output on/off immediately via `OnConfigChanged` → `Cfg = Config`,
  no rebuild required.
- `ModGeoscape`/`ModTactical` (`ChaosMutoidGeoscape`/`ChaosMutoidTactical`) — unmodified
  template hooks, untouched. This feature is a pure Geoscape-UI Harmony patch active for
  the whole session once `PatchAll()` runs — it needed neither a per-level-load hook nor
  any Tactical-side code.
- `HarmonyInstance` is provided by the framework per-mod, same as every sibling mod.

## Implementation rules

- Use **Harmony patching** for all game behavior modifications (prefix/postfix/transpiler patches).
- Do not modify game sources; patch at runtime via Harmony only.
- When exploring game internals, read from `ModSDK/Assembly-CSharp.dll` (via a decompiler)
  or `../Assembly-CSharp/` (read only) to understand the classes and
  methods to patch.

## Key game classes (read-only Assembly-CSharp)

| Class | Namespace | Role |
|---|---|---|
| `MutoidAbilityTrackContainerElement` | `PhoenixPoint.Geoscape.View.ViewControllers` | The Mutoid-specific subclass of `AbilityTrackContainerElement` driving all three of a Mutoid's per-level ability tracks (Personal, and — if it was human before mutating — PrimaryClass/SecondaryClass) on the Edit Soldier screen, all through the same `SetAbilitySlot`. `SetAbilitySlot` override is patched (`Patch_MutoidAbilityTrackContainerElement_SetAbilitySlot`, Prefix) — for `slot.Ability != null`, regardless of `abilitySource`, we call `element.SetSkill(..., isBuyable: true)` ourselves and skip the original (`return false`); only the empty-slot branch (`slot.Ability == null`) is left to vanilla (`return true`) |
| `AbilityTrackSkillEntryElement` | `PhoenixPoint.Geoscape.View.ViewControllers` | `OnPointerClick()`'s `if (!IsBuyableSkill) return;` guard is the actual thing that was blocking every click on a learned slot — `SetSkillState(isLocked, isAvailable, isBuyable, isLearnable)` sets `IsBuyableSkill` straight from the 3rd bool. Not patched directly; fixed by changing what `SetAbilitySlot` passes in |
| `SpecializedAbilityTrackPopupElement` | `PhoenixPoint.Geoscape.View.ViewControllers` | The picker popup — has exactly **one caller in the whole game**, `MutoidAbilityTrackContainerElement.OnTrackSlotPointerClicked`, which passes `isHuman = (source == AbilityTrackSource.SecondaryClass)`; despite the parameter's name every call is for a Mutoid. `Init(GeoCharacter, AbilityTrackSlot, bool isHuman, AbilityTrackAvailabilityProperties)` is patched (`Patch_SpecializedAbilityTrackPopupElement_Init`, Prefix+Postfix) — Prefix reads `slot.AbilityTrack` (the clicked slot's **real** track, whichever of the three it actually is) and nulls **every** already-decided slot on it (saving each `(slot, oldAbility)` pair + the character's `CharacterProgression` into `MutoidRelearnState.Pending`), but only when `slot != null && slot.Ability != null`; Postfix restores all of them immediately after `Init` returns, before the popup is ever shown. `Init` always rebuilds the *entire* grid (every specialization × every level) in one call — nulling the whole track means every level's row evaluates as pickable in this one popup session, not just the row the player clicked from. Separately: `Init` always hardcodes `abilityTrack2 = progression.GetAbilityTrack(isHuman ? SecondaryClass : Personal)` for its candidate lookup, **ignoring `slot.AbilityTrack` entirely** — correct for Personal (isHuman=false) and SecondaryClass (isHuman=true, since that's exactly what the ternary picks), but a real vanilla bug for `PrimaryClass` (isHuman=false too, but the ternary still resolves `Personal`, not `PrimaryClass` — never anticipated for a Pandoran character). The Prefix sets `MutoidRelearnState.RedirectTrack = slot.AbilityTrack` only when `!isHuman && realTrack.Source != Personal` (i.e. only for `PrimaryClass`), which `Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect` uses to correct that one call — see "Status" for why this is safe (the `SecondSpecializationLevel`-skip index `Init` removes is a single game-wide constant, not track-specific, confirmed via `HumanAbilityTrackContainer.SetDualSpec`/`LevelProgressionDef.SecondSpecializationLevel`) |
| `UIModuleCharacterProgression` | `PhoenixPoint.Geoscape.View.ViewModules` | Owns the real purchase flow. `OnTrackSlotPointerClicked` resolves the clicked candidate (`button.AbilityDef` for Pandoran), and — critically — only proceeds past `else if (!this._character.Progression.Abilities.Contains(tacticalAbilityDef))`; if the clicked ability is already anywhere in `Progression.Abilities` it silently does nothing (no error, no popup) — see "Status" for why this hasn't actually bitten in testing so far. It then checks level/afford (skipped entirely for `CanLearnAbility` via `_hasPandoranProgression \|\| ...`), then fires `AbilityBoughtConfirmation` — the vanilla confirmation popup. `_boughtAbilitySource` here is always the clicked slot's own real `AbilityTrackSource` (threaded through correctly from `Init`'s `abilityTrack.Source` — `abilityTrack = slot.AbilityTrack` at the very top of `Init`, untouched by the `abilityTrack2` bug above), so `BuyAbility`'s own `GetAbilityTrack(this._boughtAbilitySource)` call already resolves the right track for all three sources with no redirect needed. Patched twice: `Patch_UIModuleCharacterProgression_OnTrackSlotPointerClicked_Diagnostics` (Prefix, diagnostic-only, logs whether the `Abilities.Contains` guard is about to block the click) and `Patch_UIModuleCharacterProgression_BuyAbility` (Postfix-only) is the actual charge+learn: for Pandoran it takes `ability.CharacterProgressionData.MutagenCost` from the `Wallet` and does `Progression.AddAbility(new)` + `abilityTrack.GetAbilitySlotForLevel(level).Ability = new` — the postfix then walks every `MutoidRelearnState.Pending` entry and removes whichever one's slot `Ability` actually changed from `Progression`'s private `_abilities` list via reflection (skips cleanup for every other pending level, and for all of them if the popup was cancelled) |
| `UIStateEditSoldier` | `PhoenixPoint.Geoscape.View.ViewStates` | The roster "Edit Soldier" state (reached via the Roster's Mutoids tab) — owns `UIModuleCharacterProgression`, subscribes to its `AbilityBoughtConfirmation` to show the `ModalType.CharacterProgressionConfirmCharacter` dialog, and calls `BuyAbility()` from its own confirm handler. Not patched — this is the vanilla confirmation window itself, reused unmodified, which is what satisfies "confirm before learning" |
| `CharacterProgression` | `PhoenixPoint.Common.Entities.Characters` | `Abilities`/private `_abilities` — no public single-ability removal method exists (only `AddAbility`), which is why `MutoidRelearnState.RemoveAbility` reflects into the private field directly. `GetAbilityTrack(AbilityTrackSource)` is patched (`Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect`, Prefix) — a narrowly-scoped redirect, only active while `MutoidRelearnState.RedirectTrack` is set (during one `SpecializedAbilityTrackPopupElement.Init` call for a `PrimaryClass` slot specifically), only intercepts requests for `AbilityTrackSource.Personal` on the matching `CharacterProgression` instance; every other call is untouched. `CanLearnAbility`/`GetAbilitySlotCost`/`LearnAbility` all turned out to be **irrelevant** to the Pandoran/Mutoid path specifically — they're only used on the human `SkillPoints` path (`UIModuleCharacterProgression.OnTrackSlotPointerClicked`'s `_hasPandoranProgression \|\| CanLearnAbility(...)` short-circuits past `CanLearnAbility` entirely for Mutoids) |
| `EditUnitButtonsController` | `PhoenixPoint.Common.View.ViewModules` | Owns the roster/Edit Soldier screen's circular action buttons. `SetContextButtonVisibility(bool isAugmentationModeOn)` ANDs both the `MutationButton` and `BionicsButton` visibility with `!_isCurrentActorMutoid` (a private property reading `CurrentCharacter.TemplateDef.IsMutoid`) — Gate A of the two independent gates blocking Mutoid body-part mutation (see "Feature: Mutoid Body-Part Mutation"). Patched (`Patch_EditUnitButtonsController_SetContextButtonVisibility`, Prefix, `return false` — skips original) — reimplements the method identically minus that one conjunct, computed **independently per button** (`EnableMutoidBodyPartMutation` for `MutationButton`, `EnableMutoidBodyPartAugmentation` for `BionicsButton`) via reflection into its private `_isAugmentationOn`/`_bionicsAvailable`/`_mutationAvailable`/`_isCurrentActorMutoid` members and private `SetCircularButtonVisibility` method; only intercepts at all when at least one of the two config flags is on. Still calls the real (public) `SetEditUnitButtonsBasedOnType()` afterwards unmodified, so its own per-UI-state overrides (e.g. always hiding these buttons on the plain roster list) are untouched |
| `GeoscapeView` | `PhoenixPoint.Geoscape.View` | `ToMutateState`/`ToBionicsState` filter the character list to `CheckIsHuman() && !CheckIsMutoid()` before opening the Mutation/Bionics screen — Gate B, the deeper one: even with Gate A's button visible, a Mutoid-only selection filters down to zero characters and the method silently `return`s. Both patched (`Patch_GeoscapeView_ToMutateState`, gated on `EnableMutoidBodyPartMutation` / `Patch_GeoscapeView_ToBionicsState`, gated on `EnableMutoidBodyPartAugmentation`; Prefix, `return false`) — full reimplementations identical to vanilla minus the `!CheckIsMutoid()` conjunct (Mutoids already pass `CheckIsHuman()`, so dropping just that one conjunct is enough). `_statesStack` (needed for `SwitchToState`/`CurrentState`) is private with no public accessor — read once via reflection (`ChaosMutoidGeoscapeViewUtl.GetStatesStack`), then used through its own public API like vanilla does |
| `UIModuleMutationSection` / `ClassRequirementDef` | `PhoenixPoint.Geoscape.View.ViewModules` / `PhoenixPoint.Geoscape.Entities.Requirement` | `UIModuleMutationSection.CanApplyAugumentation(ItemDef)` — shared by both the Mutation and Bionics screens (both drive the *same* `UIModuleMutationSection` component per body-part row) — ANDs the faction's `UnlockedAugmentations` research gate with `augumentation.UnlockRequirements.IsRequirementMet(context)`. Class-restricted candidates (e.g. Priest-only heads — `ItemDef.UnlockRequirements`' own dev tooltip literally says *"This is really Equip Requirement for priest heads and similar"*) use a `ClassRequirementDef` for that second check, which resolves the viewed `GeoCharacter`'s `GameTags` against a `RequiredClass` tag — Mutoids have no class tag at all, so every class-gated candidate fails regardless of which class it requires (Gate C). `ClassRequirementDef.IsRequirementMet` has no other caller anywhere in the game (confirmed via search) — patched (`Patch_ClassRequirementDef_IsRequirementMet`, Prefix, `return false`) to short-circuit `__result = true` for Mutoid characters only (checked via `ICommonActorProvider.GetCommonActor() is GeoCharacter { IsMutoid: true }`), gated on *either* `EnableMutoidBodyPartMutation` or `EnableMutoidBodyPartAugmentation` being on (by the time a Mutoid reaches this check it already passed Gate A/B for one of the two screens, so either flag is sufficient). Regular human soldiers are completely unaffected — normal weapon/armor proficiency checks are a separate, tag-based system (`CommonCharacterUtils.IsActorProficientWithItem`) this patch never touches |
| `UIModuleMutate` / `UIModuleBionics` | `PhoenixPoint.Geoscape.View.ViewModules` | `InitCharacterInfo()` (private, mirrored near-identically in both classes) is what decides, per body-part row, whether the slot is `Available`, `AugumentationLimitReached` (global 2-augment cap, via `AugmentScreenUtilities.GetNumberOfAugments`), or `BlockedByPermenantAugument` — the last one fires whenever `AugmentScreenUtilities.GetAugmentAtSlot(character, slot)` returns an item tagged with the *other* category's `GameTagDef` (`_bionicsTag` in `UIModuleMutate`, `_mutationTag` in `UIModuleBionics`) — vanilla only ever allows same-category replacement. This is a genuine, narrow UI rule layered on top of a hard structural fact confirmed separately: Mutation and Bionic `ItemDef`s for a given body part both bind to the *exact same* `AddonSlotDef` (no separate "organic slot"/"robotic slot" exists in the data model — `AddonSlotDef` has no category field at all), and the runtime attachment (`Addon.AddonSlotImpl.StrongAddon`) only ever holds one occupant — so a body part can *structurally never* hold both regardless of this rule; the rule only decides whether *replacing* across categories is allowed at all. Patched (`Patch_UIModuleMutate_InitCharacterInfo_AllowReplacement` / `Patch_UIModuleBionics_InitCharacterInfo_AllowReplacement`, Postfix) — see "Feature: Cross-Category Augment Replacement" for why a Postfix recompute-and-`ResetContainer()`-again is safe here rather than a full reimplementation. A second, independent pair of Postfixes (`Patch_UIModuleMutate_InitCharacterInfo_FullAugmentation` / `Patch_UIModuleBionics_InitCharacterInfo_FullAugmentation`, gated on `EnableFullAugmentationOrMutation`) targets the *other* thing `InitCharacterInfo` locks: any genuinely empty section once `MAX_AUGMENTATIONS = 2` is reached, which vanilla marks `AugumentationLimitReached` regardless of which 2 of the 3 body parts are already filled, permanently blocking the 3rd. These two Postfixes only ever touch disjoint sections from the AllowReplacement ones (empty vs. cross-category-occupied), so patch-ordering between the two pairs doesn't matter; the AllowReplacement pair's own `underLimit` check was also updated to OR in `EnableFullAugmentationOrMutation`, so a cross-category slot bypasses the cap too when both flags are on |

## Feature: Mutoid Body-Part Mutation

Lets a Mutoid soldier use the vanilla **Mutation** (Anu organic augments) and **Bionics**
(New Jericho robotic augments) screens to change its Head/Torso/Legs body parts, exactly
like a regular human soldier — vanilla excludes Mutoids from both screens entirely. The
two screens are gated by **separate** config flags: `EnableMutoidBodyPartMutation`
(default `true`) for Mutation, `EnableMutoidBodyPartAugmentation` (default `false`) for
Bionics — explicit user request to keep "Augmentation" (Bionics) opt-in even though
Mutation defaults on.

Investigated by reading source (no in-game confirmation possible from this
environment, except where noted "confirmed via `Player.log`") and found **three
independent, unrelated-looking gates**, found and fixed across two rounds (Gates A/B
shipped first and confirmed working; Gate C added after that same test surfaced a
second, distinct issue):

- **Gate A** (`EditUnitButtonsController.SetContextButtonVisibility`) hides the
  "Mutation"/"Bionics" circular buttons themselves whenever the character being viewed
  is a Mutoid — without this fix the player has no way to even *open* either screen.
- **Gate B** (`GeoscapeView.ToMutateState`/`ToBionicsState`) filters the character list
  passed into the screen down to non-Mutoids — even bypassing Gate A alone, clicking the
  button would silently do nothing, since the resulting character list would be empty
  for a Mutoid-only selection and the method just returns.
- **Gate C** (`ClassRequirementDef.IsRequirementMet`, via `UIModuleMutationSection.
  CanApplyAugumentation`) locks any class-restricted candidate (e.g. Priest-only heads)
  for a Mutoid, since Mutoids carry no class tag at all to satisfy the requirement with —
  found from the very first successful in-game test of Gates A/B ("only 3 of 6 heads
  were selectable, the other 3 needed a Priest class").

Gates A and B use `TacCharacterDef.CheckIsMutoid()` (or its `IsMutoid` property
wrapper, which just calls the same method) as the *only* thing distinguishing a Mutoid
from a regular soldier in this code path — Mutoids already pass `CheckIsHuman()` (they
carry both `HumanTag` and `MutoidTag`), so both patches only need to drop that one
conjunct, not rewrite any real logic. Gate C is a different mechanism entirely (a
requirement-object check, not a Mutoid-tag check) — see the `UIModuleMutationSection` /
`ClassRequirementDef` table row above for details.

**Confirmed working in-game via `Player.log`** for the Mutation screen (`ToMutateState:
opening Mutation screen including Mutoid character(s) (12 eligible)` +
`SetContextButtonVisibility` firing repeatedly as the player browsed the roster) — both
buttons showed and the screen opened for a Mutoid, and the Mutoid character template's
own `AddonsManagerDef`/`AddonSlotDef` data turned out to already have compatible
Head/Torso/Legs mutation slots configured (unverifiable from C# alone —
confirmed by the player actually seeing real candidates in the Head row). Gate C (the
class-requirement bypass) is **not yet re-verified in-game** — implemented in response
to that same test, not yet tested itself.

## Feature: Cross-Category Augment Replacement

Lets **any** soldier (not just Mutoids) replace an already-mutated body part with a
Bionic augment, or an already-augmented body part with a Mutation — same as swapping
any other equipment. Gated by `EnableReplacementForMutationAndAugmentation` (default
`false`). Explicit user request, and explicitly scoped to *replacement only*: a body
part can never hold both a Mutation and a Bionic at once, by design (see below for why
that's not just a policy choice but a hard structural fact).

Investigated by reading source and found this is **not** a simple boolean
bug — it's a genuine, narrow UI-level rule (`UIModuleMutate`/`UIModuleBionics.
InitCharacterInfo`, see the table row above) layered on top of a hard structural design:
Mutation and Bionic `ItemDef`s for a given body part both bind (`RequiredSlotBinds`) to
the *exact same* `AddonSlotDef` — there is no separate "organic slot" vs "robotic slot"
in the data model at all (`AddonSlotDef` has no category/type field whatsoever), and the
runtime attachment object (`Addon.AddonSlotImpl.StrongAddon`) is a single reference, not
a list. This was confirmed at three independent layers before concluding a patch was
even worthwhile: `CommonCharacterUtils.CanSwapItem`'s slot-occupancy accounting is
purely structural (`PopulateProvidedUsedSlots` counts *any* item using a given
`AddonSlotPair`, no category comparison), `AugmentScreenUtilities.GetAugmentAtSlot`
returns a single `ItemDef` per slot (not a list), and the actual attach-eligibility
check (`AddonSlotImpl.CanAttachDirectly`) requires `StrongAddon == null`. **True
simultaneous coexistence would require a genuinely separate second `AddonSlotDef` per
body part in the game's asset data (plus every affected `ItemDef`'s `RequiredSlotBinds`
re-pointed at it) — not something a Harmony boolean-bypass patch can deliver**, which is
exactly why this feature is scoped to replacement only, matching what the user actually
asked for.

The one genuine rule that *does* block even a same-slot replacement across categories:
`InitCharacterInfo` sets `AugumentSlotState.BlockedByPermenantAugument` (a hard lock,
distinct from the separate 2-augment global cap) whenever the slot's current occupant
carries the *other* category's `GameTagDef` (`_bionicsTag` in `UIModuleMutate`,
`_mutationTag` in `UIModuleBionics`) — confirmed as a single-purpose method not reused
by weapon/armor equip-compatibility logic (`CommonCharacterUtils.CanSwapItem`/
`InventoryListFilterDef`), so patching it doesn't risk normal equipment behavior.

Implemented as a **Postfix**, not a full Prefix reimplementation, because
`InitCharacterInfo` also does unrelated bookkeeping (the 2-augment cap label text, and a
second pass over `ArmourItems` marking which candidate is "currently applied" via
`SetMutationUsed`). The postfix recomputes, per section, whether it was blocked
specifically by the *other* category's occupant (re-deriving the same
`GetAugmentAtSlot`/tag-`Contains` check the original used) and — only for those
sections — calls the same public `ResetContainer(state, text)` the original already
uses, recomputing `state` as `Available` or `AugumentationLimitReached` exactly like the
original's own fallback branch would (so the global cap is still respected; this only
removes the cross-category lock specifically). This is safe against clobbering the
original's own `SetMutationUsed` pass: that pass only ever touches *same-category*
occupied sections (since an item can't carry both category tags), so it can never
overlap with the sections this postfix corrects.

**Confirmed working in-game via `Player.log`**, both directions: `UIModuleMutate.
InitCharacterInfo: allowing replacement of Bionic-occupied slot (...) with a Mutation -
state=Available` and `UIModuleBionics.InitCharacterInfo: allowing replacement of
Mutation-occupied slot (...) with a Bionic - state=Available`, with the occupant category
on the same slot flipping between successive log entries and never appearing as both at
once.

## Feature: Full Augmentation/Mutation (Lift The 2-Body-Part Cap)

Vanilla caps every soldier (human or Mutoid) at 2 augmented body parts out of the 3
available (Head/Torso/Legs combined, Mutation or Bionic counted together via
`AugmentScreenUtilities.GetNumberOfAugments`, checked against `UIModuleMutate`/
`UIModuleBionics`'s private `MAX_AUGMENTATIONS = 2` constant) — the 3rd body part's
section is always shown as `AugumentationLimitReached`, even if that 3rd slot is
completely empty. Gated by `EnableFullAugmentationOrMutation` (default `false`).
Explicit user request: "in vanilla it allow only up to 2 body parts to be augmented... I
want you to add EnableFullAugmentationOrMutation = false, when true then full
augmentation/mutation should be allowed (means all 3 body parts)".

Unlike the Cross-Category Replacement feature above, there's no structural blocker here —
the cap is a pure UI-level counter check in `InitCharacterInfo`'s main loop (`bool flag =
_currentCharacterAugmentsAmount < 2`), not a data-model limitation. Same-category-occupied
sections were already exempt from this cap in vanilla (re-picking an existing
Mutation/Bionic doesn't raise the count), so the only case actually needing a fix is a
genuinely *empty* section once the cap is hit. Implemented as two more Postfixes
(`Patch_UIModuleMutate_InitCharacterInfo_FullAugmentation` /
`Patch_UIModuleBionics_InitCharacterInfo_FullAugmentation`) that force `ResetContainer
(AugumentSlotState.Available, null)` on any section where `GetAugmentAtSlot` is still
`null`, mirroring the existing AllowReplacement pair's Postfix-not-Prefix approach for the
same reason (avoid re-implementing the unrelated label/`SetMutationUsed` bookkeeping
`InitCharacterInfo` also does). The pre-existing AllowReplacement pair's own cap check was
also updated to OR in this flag, so a cross-category slot bypasses the cap too when both
`EnableReplacementForMutationAndAugmentation` and `EnableFullAugmentationOrMutation` are
on together. The "X out of 2" label text and its warning color threshold are left
hardcoded to 2 (cosmetic only, out of scope for "should be allowed" — worth revisiting if
it reads as confusing in practice).

**Not yet verified in-game** — implemented this session in response to the user's
request, no `Player.log` confirmation yet.

## Status

**Implemented, builds clean, and confirmed working in-game via `Player.log`** (search
for `ChaosMutoid` with `EnableDebugLog` on) — the Personal/mutation-track re-learn flow
(click an already-learned slot → picker opens → pick a candidate → vanilla confirmation
dialog → Mutagen charged at the new ability's normal fresh-learn cost → old ability
removed) has multiple confirmed successful runs in a real play session (e.g. `BuyAbility:
slot.Ability changed to SilentEcho_AbilityDef - removing old ability
MindControlImmunity_AbilityDef`).

`meta.json`'s `ID` (`"com.chaos"`) is still the generic template default, not yet a
mod-unique ID like `ChaosOverwatch`'s `com.chaos.ChaosOverwatch` — worth fixing before
this mod ships alongside its siblings, to avoid an ID collision with another same-author
mod. Not fixed here since it wasn't part of this feature.

**Four real issues found and fixed across three rounds of `Player.log`-driven
iteration** (each round shipped before the next was diagnosed — this mod's whole
approach to the "soldier tracks" has been "log it, don't guess," per the Build & deploy
rule above; round 1's `PrimaryClass` fix is still unverified in-game, see item 1):

1. **Re-learning a retained `PrimaryClass` (pre-mutation "soldier") ability silently did
   nothing** (first round). A Mutoid that was human before mutating keeps its original
   class ability track (`AbilityTrackSource.PrimaryClass` — e.g. `Fishman_Regeneration_
   Passive_AbilityDef`, `ArmourBreak_AbilityDef`), rendered in the *same*
   `MutoidSkillContainer` panel as the Personal/mutation track, going through the same
   `SetAbilitySlot`/`OnTrackSlotPointerClicked` overrides. `SpecializedAbilityTrackPopup
   Element.Init` **hardcodes** `abilityTrack2 = progression.GetAbilityTrack(isHuman ?
   SecondaryClass : Personal)` for its candidate lookup and never consults
   `slot.AbilityTrack` — so for a `PrimaryClass` slot, nulling `slot.Ability` had zero
   effect on what `Init` actually read; every candidate in that row evaluated against the
   unrelated, untouched Personal track's slot at that index and came out non-buyable.
   Confirmed in the log: repeated `Init` nulling/restoring cycles for
   `Fishman_Regeneration_Passive_AbilityDef` with **zero** subsequent
   `OnTrackSlotPointerClicked` log lines — the click never even left
   `AbilityTrackSkillEntryElement.OnPointerClick`'s own `IsBuyableSkill` guard.
   First round shipped a conservative fix (restrict clicking to `Personal` only, leaving
   `PrimaryClass` on vanilla's honest locked icon) rather than risk a wrong fix. Second
   round (this one, at the user's explicit request to support `PrimaryClass` too)
   implements the real fix: `Patch_SpecializedAbilityTrackPopupElement_Init` now reads
   `slot.AbilityTrack` (the clicked slot's real track) instead of hardcoding anything,
   and sets `MutoidRelearnState.RedirectTrack` when that track isn't `Personal`; the new
   `Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect` (Prefix on
   `CharacterProgression.GetAbilityTrack`) intercepts *only* the one internal
   `GetAbilityTrack(Personal)` call `Init` makes while that redirect is active, returning
   the real `PrimaryClass` track instead — every other call (any other progression, any
   other source, any time `RedirectTrack` isn't set) is untouched. This was checked for a
   specific risk before shipping: `Init` also does `list4.RemoveAt(3)` to skip the level
   reserved for the dual-class pick, and if `PrimaryClass`'s own level layout didn't
   share that same skip index, the redirect would misalign which candidate maps to which
   level. Confirmed via `HumanAbilityTrackContainer.SetDualSpec` (`_primaryClassSkillElements
   [secondSpecializationLevel - 1].SetDualClassEntry(...)`) and `LevelProgressionDef.
   SecondSpecializationLevel` (`= 4`, a single game-wide constant, not per-track) that
   `PrimaryClass` genuinely shares the same skip-index convention as `Personal` — so the
   redirect is structurally safe. `_boughtAbilitySource`/`BuyAbility`'s own track
   resolution were already confirmed correct for `PrimaryClass` without any redirect
   (they thread through `slot.AbilityTrack.Source`, never the hardcoded lookup), so this
   one redirect is the only piece that was actually broken. **Not yet re-verified
   in-game** — next `Player.log` capture (search `ChaosMutoid`, watch for `GetAbilityTrack:
   redirecting` and any `BuyAbility`/`OnTrackSlotPointerClicked` lines for a
   `PrimaryClass` slot) should confirm row alignment is correct across all levels,
   including levels after the skipped one.

2. **Only the row for the exact slot clicked was unlocked**, so picking a different
   ability for a *different* level required closing the popup and clicking that other
   level's icon on the main screen first. `Patch_SpecializedAbilityTrackPopupElement_
   Init` now nulls **every** already-decided slot on the clicked slot's own track (not
   just the one clicked) before `Init` builds the grid, and restores all of them right
   after — `MutoidRelearnState.Pending` is a `List<PendingPick>` rather than a single
   slot, and `BuyAbility`'s postfix walks all of them, removing whichever one's `Ability`
   actually changed. One popup open can now re-pick any already-decided level on that
   track, not just the one it was opened from (buying still closes the popup immediately
   after, same as vanilla always does).

3. **Round 1's "soldier abilities" fix guessed the wrong `AbilityTrackSource`** — round
   1 assumed the retained pre-mutation track was `PrimaryClass` and built the whole fix
   (item 1 above) around that, shipping conservatively (`Personal` + `PrimaryClass`
   unlocked, everything else left vanilla). Round 3's log — `SetAbilitySlot: NOT
   unlocking learned SecondaryClass slot (ability=Technician_ClassProficiency_
   AbilityDef)...` — confirmed the character's actual "soldier abilities" track the user
   meant was `SecondaryClass` (a retained human dual-class track), not `PrimaryClass`.
   This diagnostic-log-first line (`Patch_MutoidAbilityTrackContainerElement_
   SetAbilitySlot` logging every already-learned slot it *declines* to unlock, not just
   the ones it accepts) is what surfaced this cleanly instead of another guess-and-check
   round. Fixed by widening `Patch_MutoidAbilityTrackContainerElement_SetAbilitySlot` to
   unlock clicking for **any** `abilitySource` (empty slots are the only thing still left
   to vanilla), and removing `Patch_SpecializedAbilityTrackPopupElement_Init`'s
   `isHuman` early-return entirely — confirmed safe once it was clear `Init` has exactly
   one caller in the whole game (`MutoidAbilityTrackContainerElement` itself), so there is
   no real "human dual-class popup" code path here to accidentally break; `isHuman` is
   purely a Mutoid-internal signal for "is this the SecondaryClass track." Unlike
   `PrimaryClass`, `SecondaryClass` needs **no** `GetAbilityTrack` redirect at all: for
   `isHuman=true`, `Init`'s hardcoded ternary already resolves `GetAbilityTrack
   (SecondaryClass)` — exactly the real track — so this case was never actually broken by
   the same bug as `PrimaryClass`, only blocked upstream by `SetAbilitySlot`'s
   then-narrower unlock condition. `MutoidRelearnState.RedirectTrack` is now only ever
   set for `!isHuman && realTrack.Source != Personal`, i.e. `PrimaryClass` specifically.

**Still an open, unconfirmed edge case (not yet hit in testing):**
`UIModuleCharacterProgression.OnTrackSlotPointerClicked` only proceeds past `else if
(!this._character.Progression.Abilities.Contains(tacticalAbilityDef))` — it silently
no-ops if the clicked candidate ability is already anywhere in `Progression.Abilities`.
Since the old ability isn't removed from `Abilities` until *after* a successful purchase
of a *different* ability (`BuyAbility`'s postfix), re-clicking the *exact same* ability a
slot already holds (now indistinguishable from any other candidate in the reopened
picker, since nulling removed its "already learned" highlight) would hit this guard and
silently do nothing. Every real click logged so far picked a genuinely different ability
(`alreadyInAbilities=False` every time), so this hasn't actually been observed yet.
Diagnosed via `Patch_UIModuleCharacterProgression_OnTrackSlotPointerClicked_Diagnostics`
(Prefix, diagnostic-only, no behaviour change) — logs `alreadyInAbilities` for every
Pandoran click so this can be confirmed from `Player.log` if it ever comes up. **Not
fixed** because the correct fix — keeping the old ability out of `Abilities` for the
*entire* time the popup is open, not just after a successful purchase — needs a way to
put it back if the player cancels instead of buying (otherwise cancelling would
permanently strip the ability), and there's currently no patched hook for that cancel
path (`UIModuleCharacterProgression.OnCancelInputHandler`/whatever closes
`SpecializedAbilityTrackPopup` without buying). Worth designing carefully rather than
guessing at a version that risks silently deleting a soldier's ability.
