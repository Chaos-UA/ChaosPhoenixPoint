# ChaosFaction Mod — Developer Reference

## Project goal
Phoenix Point mod that extends inter-faction communication and cooperation. Factions
should be able to actively help each other in combat, not just trade resources/tech via
diplomacy screens as in vanilla.

**Feature 1 — Call a friendly faction into a fight (implemented).** Four eligible mission
types so far, all gated through the same code path
(`ContextualAbilitiesPatchHelper.IsEligibleMission`): a Phoenix Point base under attack
(`GeoPhoenixBaseDefenseMission` — player owns the site, defending), a Pandoran base (Nest/
Lair/Citadel/Palace — the four tiers, individually toggleable) the player is about to
assault (`GeoAlienBaseMission` — the *aliens* own the site here; player identity comes
from `GeoLevelController.PhoenixFaction`, not `mission.Site.Owner`, which would be wrong
for this case), the player's own Phoenix
base after it's been infested and needs to be assaulted to clean out
(`GeoPhoenixBaseInfestationMission`), and an infested *Haven* that needs the same
cleanup (`GeoInfestationCleanseMission` — a distinct class, since the site here is owned
by New Jericho/Anu/Synedrion rather than the player's own Phoenix base; player identity
again comes from `GeoLevelController.PhoenixFaction`, not `mission.Site.Owner`, same
reasoning as the alien-base case). Haven *defense* (`GeoHavenDefenseMission`, the initial
attack before it can be cleansed) stays out of scope — only the infestation-cleanse
follow-up mission was brought into scope, not the defense itself. Left-clicking the
relevant site icon on the Geoscape (your base under attack, an enemy lair you're about to
hit, your own infested base, or an infested Haven) shows the normal
action context menu (same menu that has "Enter Base", "Move" when a vehicle is selected,
"Assault", etc.) with up to **three** extra entries, one per candidate helper faction —
**New Jericho, Anu, Synedrion** — each labeled "Ask X for Help (N Resource or -M
Relationship)", the actual current price baked right into the menu text (see
`FactionHelpOfferTracker` below for where that price comes from). Each entry is available
only if the player's relationship with that faction is **25 or higher** and hasn't already
been used for this specific mission — using *any one* faction's help marks the site as
used for *all three*, since it's the same one-favor-per-mission gate
(`AllyRequestTracker.ActiveRequests`, keyed by site, not by faction — see the
save/load-persistence implementation note for why site rather than mission). Once used,
the three per-faction entries disappear and are replaced by a single light-blue,
always-disabled status entry — "Already Requested Help (X)" (X = whichever faction
actually agreed) — instead of showing all three individually greyed out with the same
reason (see the "single status entry" note below). Clicking a still-available entry opens
a `MessageBox` popup ("How will you pay X for their help?") asking the player to choose
**the faction's current resource** (Supplies/Materials/Tech — each faction always wants a
*different* one, see below) or **Relationship** — picking the resource option deducts it
from the Wallet (with an affordability check first — if the player can't actually afford
it, a second popup says so and nothing happens); picking Relationship deducts it from that
faction's diplomacy toward the player instead (no native resource-cost equivalent for
relationship, so it's applied by hand via `FactionDiplomacy.ModifyDiplomacy`). Cancelling
the popup leaves everything untouched. **If the mission is cancelled/declined (or its
request simply expires) without ever being launched into tactical, whatever was spent is
*not* refunded** — explicit request: the cost is the price of having asked, not of the
mission actually happening, so the player keeps the request marker/objective (until it
expires or the mission launches) but doesn't get the resource/relationship back either
way. (An earlier version did refund in both cases — see the "no refund" implementation
note below for what changed and why.) If launched,
a randomly-rolled number of generic troopers (`HelpMinSquadSize`-`HelpMaxSquadSize`,
rolled fresh per request — see the "squad slot budget" paragraph below for how vehicles/
Mutogs weigh against this roll) from whichever faction helped spawn into the battle as an
allied faction (friendly to the player, hostile to the enemy), reusing the player's own
deploy zones since the map has none of its own for the helper.

**Prices fluctuate per faction, independently, via `FactionHelpOfferTracker`
(`ChaosFactionState.cs`) — independently per mission type too, not just per faction, and
(per a later explicit follow-up request) the *base value* those prices roll around is now
driven by `DifficultyThreatLevel` rather than mission type.** `Offers` is keyed
`Dictionary<(PPFactionDef, HelpMissionType, DifficultyThreatLevel), FactionHelpOffer>` (a
3-way tuple key — mission type stayed in the key even after threat level replaced it as
the *settings* axis, per the explicit request: "when you generate cost for mission it
should be per faction per siteType per DifficultyThreatLevel") — each of the three
candidate factions has its own current `FactionHelpOffer` *for every (mission type,
threat level) combination*, so New Jericho's price to help defend a Phoenix base at High
threat and New Jericho's price to help assault a Pandoran Lair at that same High threat
are rolled and tracked completely independently — both against the *same* `HighThreatHelp
ResourcesCost`/`HighThreatHelpRelationshipCost` base values (resolved via `MissionHelpSettings
Resolver.Get(DifficultyThreatLevel?, ...)`, see the "Squad size/cost settings are now
keyed by DifficultyThreatLevel" implementation note below — originally one base-value
pair per `HelpMissionType`, 7 of them, replaced with one pair per `DifficultyThreatLevel`,
4 of them), but each still gets its own independent roll, not a shared number. Resource
*type* assignment is deliberately **not** re-split per mission type or threat level: it's
still a *permutation* of `{Supplies, Materials, Tech}` across the three factions
(shuffled, one each, so no two factions ever want the same resource at once, resolved
once per faction per day), and every (mission type, threat level) combination's offer for
a given faction reuses that same resource type — only the rolled *amount* varies per
(faction, mission type, threat level) triple. Phoenix Point has no resource literally
called "Food", so "food" maps to `ResourceType.Supplies`, the closest in-game equivalent.
Both the resource amount and the relationship amount are that threat level's own
`{Level}HelpResourcesCost`/`{Level}HelpRelationshipCost` (the *base* values) times an
*independent* roll each, within ± `ChaosFactionConfig.HelpCostVariancePercent` (still
global — config-exposed, default 25 = ±25%, per explicit request — originally hardcoded
as a literal `Random.Range(0.75, 1.25)` in `FactionHelpOfferTracker.RollAmounts`, then
pulled out to config after the player asked where to control it) — a faction's resource
roll and relationship roll don't share one multiplier, different factions' rolls don't
share one either, and neither do different (mission type, threat level) combinations' own
rolls for the same faction. `HelpCostVariancePercent` is clamped to ≥0 before use
(`Mathf.Max(0, ...)`), so a 0 or misconfigured negative value degrades to "no variance"
(both rolled bounds pinned to the base value) rather than an inverted or degenerate
`Random.Range` call. Tech is priced at 1/5 the rolled value (`TechCostDivisor`), since
it's treated as worth 5x a unit of Supplies/Materials. Rerolling happens two ways: (1) a
full reroll (fresh resource-type permutation *and* fresh amounts for all three factions,
across every mission-type/threat-level combination each) whenever `EnsureRolled` notices
the in-game day has advanced since the last roll (`GeoLevelController.Timing`, `(Now -
StartTime).TimeSpan.TotalDays`) — checked every time the context menu opens, no separate
polling/timer needed; (2) using a specific faction's help for a specific (mission type,
threat level) pair rerolls *only that triple's own amounts* afterward (`RerollAfterUse`,
taking both a `HelpMissionType?` and a `DifficultyThreatLevel?` parameter), keeping the
faction's resource type as-is and leaving every *other* combination's price for that same
faction untouched — reshuffling just one faction's resource type would only ever put it
back to a type another faction already holds or leave it unchanged, not meaningfully
"fresh" without reshuffling all three (which the daily reroll already does), so per-use
reroll deliberately only touches amounts, for just the one (mission type, threat level)
pair actually used. Since the ability instances are cached per
(mission, faction) and only rebuilt at mission end, but the underlying offer can change
while a mission's abilities are still cached, `AskForHelpAbilityFactory.GetOrCreate`
refreshes the cached ability's stored `ResourceType`/`ResourceAmount`/`RelationshipAmount`
fields and its menu `DisplayName1` text on *every* call, not just on first creation — a
stale price would otherwise linger on a reopened context menu after a day passed or after
that faction's price rerolled from being used on a different mission.

**Why payment method is chosen via a popup after clicking, not as separate menu
entries**: an earlier design gave each faction two menu entries, one per payment method
(6 total for 3 factions) — but `UIModuleSiteContextualMenu.SetMenuItems` indexes into a
fixed-size, pre-instantiated button pool with **no bounds check**
(`this._menuItems[i]` for `i < abilities.Count`, `PhoenixPoint/Geoscape/View/ViewModules/
UIModuleSiteContextualMenu.cs:52-54`) — exceeding that pool's actual size (an
asset-defined number not visible from C#) would throw and crash the context
menu outright, not just look cluttered. Collapsing to one entry per faction (3 total,
same as the original single-faction, single-payment-method design's button count) avoids
the risk entirely while still letting the player choose how to pay, via `Base.UI.
MessageBox.MessageBox` (`GameUtl.GetMessageBox()`, the same generic confirm-dialog system
used throughout the game's own UI, e.g. `UIStateRosterRecruits`/`UIStateRosterDeployment`)
— `ShowSimplePrompt(..., MessageBoxButtons.YesNoCancel, buttonsOverrideLabels, callback,
...)` supports relabeling the Yes/No/Cancel buttons to arbitrary text, which is how "pay
with [the faction's current resource]" / "pay with Relationship" / (implicit) "back out"
get shown without needing any custom Unity UI of our own.

**This replaced an earlier "clone a button into the mission-brief popup" implementation**
(`PhoenixBaseDefenseDataBind`/`ModalType.GeoPhoenixBaseDefenseBrief`) that the player never
actually encountered in normal play — clicking your own base doesn't open that modal, it
opens the site's context menu instead. The modal patch is gone; everything now goes
through `GeoAbility`, the game's real extensible-actions system for the context menu (see
below). No vanilla mechanic lets one faction send troops to another faction's mission —
that part is still entirely custom, built with Harmony + direct engine API calls, wired
into an otherwise-native ability.

**Feature 2 — A Haven's own faction can spontaneously send free help nearby, as thanks
for a good relationship (implemented).** After any Haven defense concludes — win or
lose, and explicitly including a fully auto-resolved one the player never played out in
tactical (`HavenDefenseRewardTracker`, hooked via `GeoUpdateableMission.
OnUpdateableMissionEnded`, not `GeoMission.OnMissionCompleted` — see "Key game classes")
— there's a `HelpForFreeAfterHavenDefenseChancePercent` (20%) chance that, provided the
player's relationship with the Haven's owning faction (New Jericho/Anu/Synedrion — "the
faction which was attacked") meets `RelationshipThreshold`, that faction spontaneously
grants a **free** help request (no resource/relationship cost at all) for the closest
eligible site within `HelpForFreeAfterHavenDefenseRadius` of the defended Haven — reusing
the exact same eligibility rules (mission type, config toggles, "not already requested",
"not asking the attacker to fight itself") a manual request goes through. This reuses
essentially the entire existing pipeline (`AllyRequestTracker.MarkRequested`, called
directly rather than through the paid ability/`MessageBox` flow) with a
zero-amount `FactionHelpOffer`, so squad selection, the map marker, expiry, and the
Geoscape objective below all work identically regardless of whether the request was
paid for or free.

**Whenever any help request is active — paid or free — the Geoscape pauses and a
critical-path objective appears in the top-left menu (implemented),** the same way
native objectives like "Stop enemy invasion at haven X" do. `AllyRequestTracker.
AddActiveRequest`/`RemoveActiveRequest` are the single choke point both request types
flow through: they add/remove a custom `AskForHelpGeoFactionObjective` via `GeoFaction.
AddObjective`/`RemoveObjective` (the same sanctioned API vanilla's own
`GeoscapeFactionObjectiveSystem` uses) and force `Timing.Paused = true` while
`ActiveRequests.Count > 0`, restoring whatever the pause state was *before* the first
concurrently-active request (not unconditionally unpausing) once the last one resolves —
so this doesn't fight a pause the player set themselves for unrelated reasons.

**Feature 2 — hostile/allied "activity" incursions (implemented).** Originally built as
a `chaos_factions_debug`-gated testing tool; the activity system itself (auto-spawning,
squad composition, map marker/objective/mission-brief display, mutual exclusivity with
Ask-for-Help) is now a normal, always-on part of the mod, no console command needed —
only the *manual, free, on-demand* spawn button is still debug-gated (explicit
follow-up request: "All those activity options are available without any debug mode.
Debug mode is only needed to Show that debug context button to spawn for free"). The
`chaos_factions_debug 1` console command (0 to disable again) adds a single context-menu
entry — **"ChaosFactionsDebugOptions"** — to **any site with an active mission, not just
the four `IsEligibleMission` types** (`ContextualAbilitiesPatchHelper.
TryAddDebugActivityAbilities`, still gated on `ChaosFactionDebugFlag.Enabled` — this one
entry is the only thing that flag still controls), for manually spawning one of these
activities on demand without waiting for the auto-spawner. **Later explicit follow-up
request — "ChaosFactionsDebugOptions must be seen for any site" — broadened this from
the original four-mission-type gate** (still what governs the real, player-facing
Ask-for-Help entries and the auto-spawner) **to any `GeoMission` at all**, mirroring the
"[Debug] {MissionTypeName}" info entry's own unrestricted reach: safe to broaden, since
neither `DebugActivityTracker.AddActivity` nor `DebugActivityPickerAbility.
GrantFreeAllyHelp` (what picking a popup option actually leads to) re-check mission-type
eligibility themselves — both only check `DebugActivityTracker.CanAddActivity` (the
mission-type-agnostic mutual-exclusivity gate) plus their own faction/site resolution, so
a debug activity or free-ally-help grant works identically regardless of which
`GeoMission` subclass owns the site (e.g. `GeoScavengingMission`, previously only ever
showing the always-disabled mission-info entry). Clicking it opens a text-input popup
(`DebugActivityPickerAbility.ActivateInternal`, via `Base.UI.MessageBox.MessageBox.
ShowInputPrompt`) listing every `DebugActivityKindExtensions.All` kind by number and
name (6: Pure, Forsaken, New Jericho, Synedrion, Anu, Bandits); the player types a number
or name and confirms, and only then does `DebugActivityTracker.AddActivity` actually run.
**This one-entry-plus-popup design replaced an earlier one-button-per-kind design**
(one "[Debug] Add X Activity" context-menu entry per kind) after that version actually
crashed the context menu in-game once the kind count grew too high (this was while the
mod briefly also had two now-removed asset-only kinds, WildBeast/AnuExalted — see
"Implementation notes" below for their removal) — see the "button-pool-cap risk...
materialized" implementation note below for the full crash story and why a Prefix-based
cap (tried first) was reverted in favor of this redesign instead.
**The same popup also lists three more numbered options — "Free New Jericho/Anu/
Synedrion Help"** (explicit follow-up request: "add options to request ally to that
debug window, but for free") — which don't go through `DebugActivityTracker` at all;
`DebugActivityPickerAbility.GrantFreeAllyHelp` instead calls `AllyRequestTracker.
MarkRequested` directly with a zero-amount `FactionHelpOffer`, the exact same mechanism
`HavenDefenseRewardTracker`'s free-help reward already uses (see below) — so picking one
grants Feature 1's real ally-help squad (including the map marker, top-left objective,
mission-brief row, everything) with no cost and no `RelationshipThreshold` check, purely
via the debug tool's own `DebugActivityTracker.CanAddActivity` mutual-exclusivity gate.
Name matching checks the 6 activity kinds first, so plain "New Jericho" still means the
*activity* kind unchanged — only the distinctly-labeled "Free New Jericho Help" (or its
own number, `7`/`8`/`9`) reaches the free-ally-help path, so the two New Jericho options
never collide.

Each of the 6 activity kinds resolves through one of two paths once picked, branched via
`DebugActivityKind.IsSubFaction()` in `DebugActivityTracker.TryResolveActivityFaction`:
- **Sub-faction-backed** (Pure/Forsaken/Bandits) — `GeoSubFaction`/`GeoSubFactionDef`
  instances, a sub-faction tier distinct from the five main `GeoFaction`s, resolved by
  name via `DebugSubFactionResolver` (`PPFactionDef.MatchesShortName`, the same
  string-based lookup vanilla's own `subfaction_create`/`subfaction_set_mission_weight`
  console commands use, falling back to `GeoLevelController.CreateSubFaction` if the
  campaign hasn't instantiated that sub-faction yet), template pool via
  `GeoSubFaction.GetDeploymentForMission`.
- **Main-faction-backed** (New Jericho/Synedrion/Anu) — the exact same three regular
  `GeoFaction`s Feature 1 already uses as *friendly* helpers, resolved via
  `GeoLevelController.NewJerichoFaction`/`.SynedrionFaction`/`.AnuFaction`, template pool
  via `GeoFaction.GetEligibleTemplatesForMission` — added per explicit follow-up request
  so any of the three factions capable of fielding troops can be tested as a hostile
  third party too, not only the two original sub-factions.

Clicking an entry (no payment, no
popup — this is a debug tool, not a player-facing paid action) immediately rolls a squad
sized the same way Feature 1's help squads are (`HelpMinSquadSize`..`HelpMaxSquadSize`,
sampled with replacement), and marks the site the same way Feature 1 does — a map marker
with live countdown, top-left objective, mission-brief row, and a disabled context-menu
status entry once added — reusing the exact same display machinery as Feature 1
(`HelpRequestedMarker`, `IChaosTimedObjective`, the `CommonMissionDataController.SetData`
row-clone patch, `AlreadyRequestedHelpAbility` with a red `StatusColor` instead of the
default light blue) rather than duplicating it.
**An activity starts "unrevealed" — a neutral gray marker, generic "Unknown Activity"
label, and the Alien faction's icon on the objective, regardless of which kind it
actually is — and only reveals its real faction name/color the first time the player
investigates the site, explicit request: "first display it as unknown activity on
strategy map... and only reveal what faction is there when user come to mission and
open it."** `DebugActivityTracker.RevealIfNeeded` is the one-way latch/choke point,
called from both `ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities`
(opening the site's context menu) and the mission-brief row patch (a later point in the
same flow, kept as a defensive second call site) — see "Implementation notes" below for
the full mechanics, including a real pre-existing marker-color bug found and fixed while
building this. The context-menu status entry and mission-brief row themselves needed no
separate "unrevealed" text of their own: both call `RevealIfNeeded` first, so by the
time they read `IsHostile`/`Kind` the activity is already revealed on that same call —
only the map marker and top-left objective are visible *before* the player has
investigated, so only those two needed actual "unknown" display states. **All
activity types — Ask for Help, and each of the five debug activity kinds — are mutually
exclusive per site** (explicit request): `AllyRequestTracker.CanRequestHelp` and
`DebugActivityTracker.CanAddActivity` each check the other tracker's `ActiveRequests`/
`ActiveActivities` dictionary, and the context-menu patches for each only ever show their
own entries when the other's dictionary doesn't already have that site. Spawned debug
troops are hostile to **both** the player and whichever faction the mission's own enemy
already is (Pandoran on 3 of the 4 eligible mission types; a Phoenix base defense could
have a different, human attacker instead — either way, "hostile to everyone but itself"
is the intent) — this applies the same way regardless of which of the five factions was
picked, including the New Jericho/Synedrion/Anu case where that same faction is normally
*friendly* elsewhere in this mod. **A Pandoran escort (`ActivityPandoranEscortMinPercent`/
`MaxPercent`) originally spawned alongside a hostile debug-activity squad — removed per
explicit follow-up request, along with its two config fields.** See "Implementation
notes" below for what removing it touched. Debug activities **are** persisted across a
save/load (and across any ordinary Geoscape reload — see the "activity state now
save/restores" implementation note below), the same way Feature 1's `PendingHelpRequest`
already was — originally left unpersisted as an accepted limitation for what was a
testing tool, no longer the case now that activities auto-spawn as a normal, always-on
part of the mod. The single "ChaosFactionsDebugOptions" entry
(see above) keeps this mod's own per-site context-menu footprint fixed at one button
regardless of how many debug-activity kinds exist — see the "button-pool-cap risk...
materialized" implementation note below for why this replaced the original
one-button-per-kind design and why a different fix (capping the ability list in a
Harmony Prefix) was tried first and reverted. **Also when the flag is enabled**, every site
with *any* active mission (not just the four eligible types) gets one more always-disabled,
grey **"[Debug] {MissionTypeName}"** entry (`ContextualAbilitiesPatchHelper.
TryAddDebugMissionInfoAbility`/`DebugActivityAbilityFactory.GetOrCreateMissionInfo`) —
explicit request, so a mission's internal C# type name and `MissionDef` asset name can be
read straight off the context menu for future mod-development reference, without needing
that mission type to already be one this mod supports. Not mutually exclusive with
anything else (a separate lookup aid, not a competing action), and reuses
`AlreadyRequestedHelpAbility`/`StatusColor` the same way the "already active" status
entries do, just tinted `ChaosFactionColors.InfoGray`.

**Debug activities can also spawn on their own, without a click (implemented) — and, per
explicit follow-up request, this no longer requires `chaos_factions_debug 1` at all.**
`ActivitySpawnChancePercentPerDay` (config, default 20 — renamed from
`ActivityAutoSpawnChancePercentPerDay`, dropping "Auto" once it became the feature's
normal always-on behavior rather than a debug-gated extra) is the chance, **per day**,
for a Pure/Forsaken/etc. activity to auto-spawn at an eligible site — expressed as a
daily percentage for readability (explicit request), but actually rolled once per
**in-game hour** (`AutoDebugActivitySpawner.OnHourTicked`, subscribed to
`GeoLevelController.HourTicked` — a real public event, not a Harmony patch — from
`ChaosFactionGeoscape.OnGeoscapeStart`) so it doesn't all-or-nothing right at local
midnight; the daily percentage is converted to an equivalent independent hourly chance
via `1 - (1 - dailyChance)^(1/24)`. Every eligible site (visible to the player, not
destroyed, its active mission one of the four types this mod supports, and not already
busy with a help request or another debug activity — the same `IsEligibleMission`/
`DebugActivityTracker.CanAddActivity` checks a manual click would need to pass) rolls
independently each hour — explicit request: "each 20% is independent for each active
visible not destroyed mission type, so each nest for example has its own 20% daily roll
chance" — so multiple simultaneously-visible eligible sites each have their own 20%/day
shot, not one shared 20%/day roll across all of them. Which kind spawns on a successful
roll is **not** a flat random pick across all of them:
`DebugActivityKindExtensions.PickRandomWeightedByGroup` instead picks one of two groups
first — `HostileGroup` (Pure/Forsaken/Bandits — all three always-hostile, no-diplomacy
sub-faction kinds; Bandits joined this group per explicit follow-up request, having
originally been excluded/manual-click-only) or `AlliedCandidateGroup` (New Jericho/
Synedrion/Anu, named for what they're *capable* of resolving as, not a guarantee — see
the relationship-threshold paragraph below) — weighted by
`ActivityHostileGroupChancePercent` (config, default 50 = an even 50/50 split
between the two groups), then picks uniformly within whichever group won. Both groups
are size 3, so at the default 50 split every one of the six kinds now lands at an even
~16.7% each. 0% disables auto-spawning entirely.
**`chaos_factions_debug` no longer gates any of this** — `AutoDebugActivitySpawner.
OnHourTicked`'s own `ChaosFactionDebugFlag.Enabled` check was removed (explicit
follow-up request: "All those activity options are available without any debug mode.
Debug mode is only needed to Show that debug context button to spawn for free").
The console command's one remaining job is showing the "ChaosFactionsDebugOptions"
context-menu button (`ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities`/
`DebugActivityPickerAbility.GetDisabledStateInternal`, both still correctly gated) that
lets the player manually spawn one of these activities for free, on demand — the
activities themselves, however triggered, are otherwise a normal, always-on part of the
mod now, not a debug-only feature: the map marker/top-left objective/mission-brief row
for an active activity were never gated on the flag to begin with (`DebugActivityTracker.
AddActiveActivity` doesn't check it), so this change just brings the *triggering* side
in line with the *display* side, which was already flag-independent.

**New Jericho/Synedrion/Anu can be picked/rolled as a debug activity regardless of
current relationship, but only actually spawn hostile depending on it (implemented,
explicit request) — and, per a later follow-up request, that determination now reuses
the game's own real diplomacy-tier signal instead of a hand-picked numeric threshold.**
Pure/Forsaken always spawn hostile — a `GeoSubFaction` has no diplomacy relationship
toward the player the way a `GeoFaction` does. For the three main factions,
`DebugActivityTracker.ResolveIsHostile` originally read
`faction.Diplomacy.GetDiplomacy(playerFaction)` (a raw int) against a config field,
`HostileActivityRelationshipThreshold` (default `-75`) — **replaced** after the player
asked whether the game already has its own "at war" logic worth deferring to instead of
a guessed number. Investigated via source: `FactionDiplomacyState.AtWar`
turned out to be a dead end (a one-shot "reveal this faction's sites" latch, read
nowhere else in the entire codebase — no combat/mission code ever checks it), but
`PartyDiplomacyState.Conflict` is the real signal: it's the exact live condition
`VehicleFactionController.PlanCurrentSiteAttack` checks (independently of `AtWar`)
before a faction's own vehicles will actually schedule an attack on a player Haven or
Phoenix base — i.e. the same threshold the game itself uses to decide "is this faction
genuinely hostile enough to attack you." `ResolveIsHostile` now calls
`faction.Diplomacy.GetDiplomacyState(playerFaction)` (same directional read as
`GetDiplomacy` — relationship is stored on the *other* faction's side) at `AddActivity`
time (the only point `GeoFaction.Diplomacy` is reachable, before the Geoscape→Tactical
scene swap) and checks `state != PartyDiplomacyState.None && state <=
PartyDiplomacyState.Conflict` — the explicit `None` exclusion matters because that
sentinel is `-1`, numerically below `Conflict` (`0`), and would otherwise be
misread as "worse than Conflict" by a bare `<=`. At Conflict (or, per the defensive
`<=`, anything hypothetically worse), the activity spawns hostile to both the player and
the mission's own enemy, exactly like Pure/Forsaken; above it, it instead spawns as an
**ally** — friendly to the player, hostile only to whatever's already attacking — the
same relations a normal Ask-for-Help grant would set.
`HostileActivityRelationshipThreshold` itself was removed from `ChaosFactionConfig.cs`
entirely (no longer read anywhere) rather than left as dead config. The resolved
`IsHostile` value is snapshotted onto `PendingDebugActivity` at add time (not
re-evaluated at mission launch — a stale read within the ~24h expiry window is an
accepted, low-stakes tradeoff for a debug tool, same reasoning `BuildSquad`'s own
composition roll already accepts) and threaded through to Tactical via a
`DebugActivityTracker.PendingActivityIsHostile` static (mirroring
`PendingActivityFactionDef`'s own bridging pattern), where
`ChaosFactionTactical.SpawnRequestedDebugActivity` branches `FactionRelation.Enemy` vs
`.Friend` toward the player. The map marker/top-left objective/mission-brief row/
context-menu status entry all reflect the resolved state too — light blue instead of
red, and reworded ("Allied X activity"/"X troops are moving to help... as an ally"
instead of "Hostile X forces are massing...") — so it doesn't look like a hostile
incursion once it's actually going to fight for the player; this required no code
changes of its own once `ResolveIsHostile`'s determination was fixed, since the
mission-brief row (`Patch_CommonMissionDataController_SetData`) and every other display
surface already just read the stored `IsHostile` bool.

**Debug activity squads now roll vehicles/Mutogs the same capped, budgeted way the
Ask-for-Help squad does — later explicit follow-up request: "activity spawned units
never roll vehicle/mutog... allow it to be rolled identically as it done in help
requests."** Originally, `DebugActivityTracker.TryResolveActivityFaction` filtered
`TacCharacterDef.IsVehicle`/`IsMutog` out of the template pool entirely (for both the
sub-faction and main-faction resolution paths), since this debug tool's squad was a flat
random sample with no equivalent cap/unit-size concept for them, unlike
`AllyRequestTracker.BuildSquad` (Feature 1), which deliberately rolls vehicles/Mutogs as
their own capped, per-faction-budgeted slots. **Fix**: `TryResolveActivityFaction` no
longer filters them out — it returns the full eligible pool, same as the Ask-for-Help
squad's own `eligible` list — and `DebugActivityTracker.BuildSquad` was reworked to
match `AllyRequestTracker.BuildSquad`'s own mechanics almost exactly: the rolled
`HelpMinSquadSize`..`HelpMaxSquadSize` range is now a slot *budget* (not a flat trooper
count) since a vehicle/Mutog weighs more against it than a regular trooper, and
`AllyRequestTracker.RollBigUnitSlots` (promoted from `private` to `internal` so this
class can call it too) rolls the same "streak" — keep succeeding, keep adding, first
failure ends it — against the same per-faction max/unit-size resolution (New Jericho →
`HelpMaxNewJerichoVehiclesInSquad`/`NewJerichoVehicleUnitSize`, Synedrion →
`HelpMaxSynedrionVehiclesInSquad`/`SynedrionVehicleUnitSize`, Anu →
`HelpMaxAnuMutogsInSquad`/`MutogUnitSize`). This needed a new `HelperFactionKind?
kind` parameter threaded into `BuildSquad` — `AddActivity` already computed this exact
value (mapping `DebugActivityKind.NewJericho`/`.Anu`/`.Synedrion` to the matching
`HelperFactionKind`) for the pre-existing augmented-pool lookup, so it's reused rather
than re-derived; `null` for the three `GeoSubFaction`-backed kinds (Pure/Forsaken/
Bandits) naturally resolves to a 0 vehicle/Mutog max via the same switch
`AllyRequestTracker.BuildSquad` already uses for any non-NJ/Synedrion/Anu faction, no
special-casing needed. **Deliberately unchanged**: the remaining (non-vehicle/Mutog)
slot budget still fills via plain sampling-with-replacement — this method's own
pre-existing "usual logic" (unlike `AllyRequestTracker.BuildSquad`'s diversity-first
`PickPreferringNewClass`) — and the New Jericho/Anu augmented-pool per-slot swap still
only ever applies to those regular trooper slots, never to vehicles/Mutogs, matching the
Ask-for-Help squad's own behavior. No Tactical-side changes were needed at all —
`ChaosFactionTactical.SpawnSquad` (the shared per-template spawn loop every squad-build
path already funnels through) already branches on `TacCharacterDef.IsVehicle` generically
per template (`BoostVehicleAmmo`, etc.), regardless of which `BuildSquad` produced it.

## Directory rules
- **`ChaosFaction/`** (this file's directory) — solution root, **writable**
  - **`ChaosFaction/ChaosFaction/`** — mod source, the only folder meant to be edited
    - `meta.json`, `ChaosFactionConfig.cs`, `ChaosFactionMain.cs` — mod scaffolding
    - `ChaosFactionState.cs` — `AllyRequestTracker` (cross-cutting request/spawn state,
      bridges the Geoscape ability activation to the Tactical spawn) + shared `Log` helper
    - `ChaosFactionAbility.cs` — `AskForHelpAbility`/`AskForHelpAbilityDef` (the custom
      `GeoAbility`, built entirely in code via `ScriptableObject.CreateInstance`, no Unity
      Editor asset) + `AskForHelpAbilityFactory` (builds/caches one per active mission,
      destroys its Defs when the mission ends)
    - `ChaosFactionPatches.cs` — Harmony postfixes on both view-state
      `GetContextualAbilities` methods that append our ability into the site's context menu
    - `ChaosFactionTactical.cs` — `ChaosFactionTactical.OnTacticalStart` spawns the
      requested New Jericho troops when a flagged base-defense mission launches
    - `ChaosFactionDebug.cs` — Feature 2 (`chaos_factions_debug`): the console command
      registrar, `DebugSubFactionResolver`, `DebugActivityTracker`/`PendingDebugActivity`/
      `DebugActivityGeoFactionObjective`, `DebugActivityPickerAbility`/
      `DebugActivityAbilityFactory` (the `chaos_factions_list` diagnostic command and its
      backing `FactionInventoryLogger` class have since been removed — no longer needed,
      see the matching "Implementation notes" bullet below)
    - `ChaosFactionGeoscape.cs` — SDK demo template, currently unmodified boilerplate
    - `ChaosFactionLocalization.cs` — loads this mod's own `KEY_CHAOSFACTION_*` terms
      (`Data/Localization/ChaosFaction.csv`) into I2 Loc at `OnModEnabled` — see the
      "Localization" section below
  - **`ChaosFaction/ModSDK/`** — read-only SDK DLLs (0Harmony, Assembly-CSharp, UnityEngine, UnityEngine.UI)
  - **`ChaosFaction/Data/`** — loose files copied to `Dist/` on build; use for any assets a
    released mod needs to ship. `Data/Localization/ChaosFaction.csv` is this mod's own
    localization source — see the "Localization" section below
  - **`ChaosFaction/Dist/`** — build output (`ChaosFaction.dll` + `meta.json` + `Data/*`)
- **`../Assembly-CSharp/`** — Phoenix Point sources (dnSpy v6.6.0), **read only**
- **`../ChaosOverwatch/`, `../ChaosUkrainian/`, `../ChaosUmbraBalance/`, `../HavenUnderAttackTimer/`** —
  sibling mods, **read only**, useful as reference examples (Harmony patch conventions,
  config patterns, csproj structure, and — for `HavenUnderAttackTimer` and `GeoAlienBase`/
  `GeoHavenDefenseMission` — how haven/base-attack missions actually work)
- All other directories and files — **read only**

## Build & deploy
- Open `ChaosFaction.sln` in Visual Studio 2019+.
- Debug/Release both compile to `Dist/`.
- **No post-build auto-deploy** — explicit request ("remove that test deploy"): the
  `.csproj` used to `RMDIR`/`XCOPY` `Dist/*` into `%USERPROFILE%\AppData\LocalLow\Snapshot
  Games Inc\Phoenix Point\Steam\WorkshopTool\TestMod` on every successful build (both the
  `PostBuildEvent` and its enabling `RunPostBuildEvent` property were removed from
  `ChaosFaction.csproj`, and that TestMod folder itself was deleted). Root cause this was
  fixing: Player.log showed the game discovering this mod from *two* locations at once —
  a `Default`-loader copy at `G:\SteamLibrary\...\Mods\ChaosFaction` and a
  `Development`-loader copy at the auto-deployed TestMod folder — logged as `Found
  duplicated mod 'com.chaos.ChaosFaction', using latest version (1.0)`; load order
  appeared to favor the TestMod copy in practice, but tying versions made that implicit
  rather than guaranteed. A build now only produces `Dist/` — deploy to wherever the game
  actually loads this mod from manually (sibling mods' `.csproj` still show the old
  pattern if it's ever wanted back).
- Target framework: **v4.8**, not v4.7.2 despite what the SDK demo template ships with —
  `ChaosOverwatch.csproj` (and this project, matching it) target v4.8; only `ChaosUkrainian`/
  `ChaosUmbraBalance`/`HavenUnderAttackTimer` still say v4.7.2 in their `.csproj`. v4.8 is
  what's actually installed as a targeting pack on the dev machine used for this repo.
- **Always debug from `Player.log` first, before guessing at a fix.** This environment
  can't run the game, so this file is the only ground truth for what actually happened
  in-game: `C:\Users\Volodymyr\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log`
  (corrected from an earlier, stale `C:\Users\Ukrai\...` path once the real one was
  confirmed to actually exist and update on this machine).
  Search it for the literal keyword `ChaosFaction` — that keyword hits both this mod's own
  `Log.Debug` output (`[ChaosFaction]`-prefixed lines, gated on `ChaosFactionConfig.
  EnableDebugLog`, off by default — ask the player to enable it in Mod Settings if a report
  needs more detail than what's already logged) *and* any stack trace frame naming this
  mod's own types/methods (e.g. `at ChaosFaction.ChaosFactionTactical.SpawnRequestedAllies
  ()`), which is how a crash gets traced back to exactly which line threw, even though the
  surrounding `[ERROR]`/exception block itself is unprefixed engine output. The file is
  overwritten each game session, so a log a player pastes may already be gone from disk by
  the time it's read here — treat pasted log excerpts as equally authoritative. Do not
  speculate about a reported in-game failure (spawn not happening, wrong behavior, a hang)
  without first checking this file or asking for a fresh excerpt; several fixes in this
  file were revised or reverted after actually reading the log contradicted an earlier
  guess (see "Implementation notes" below).

## Mod framework basics
- `ModMain` (`ChaosFactionMain`) — loaded once per session; calls `harmony.PatchAll()` in
  `OnModEnabled`. Exposes `ChaosFactionMain.Cfg` (static) so static Harmony patch classes
  can read config without reflection.
- `ModTactical` (`ChaosFactionTactical`) — per-level-load instance; `OnTacticalStart`
  spawns any New Jericho troops requested on the Geoscape moments earlier.
- `ModGeoscape` (`ChaosFactionGeoscape`) — currently an unused template hook; the
  Geoscape-side request logic lives entirely in the `GeoAbility`/Harmony patch instead
  (see below), since it hooks a specific UI method rather than a level-lifecycle event.
- `ModConfig` (`ChaosFactionConfig`) — serialized to JSON, editable from in-game Mod
  Settings. **`HelpResourcesCost`/`HelpRelationshipCost`/`HelpMinSquadSize`/
  `HelpMaxSquadSize`/`HelpMaxNewJerichoVehiclesInSquad`/`HelpMaxSynedrionVehiclesInSquad`/
  `HelpMaxAnuMutogsInSquad`/`VehicleSpawnChancePercent` are no longer global — each is now
  4 separate fields, one per `DifficultyThreatLevel` (`LowThreatHelpResourcesCost`,
  `MediumThreatHelpResourcesCost`, `HighThreatHelpResourcesCost`, `ExtremeThreatHelpResourcesCost`, ... 32
  fields total — originally one block per `HelpMissionType`, 56 fields, replaced with
  this threat-level axis per a later explicit follow-up request) — see the "Squad
  size/cost settings are now keyed by DifficultyThreatLevel" implementation note below
  and `ChaosFactionConfig.cs`'s own class-level doc comment for the full reasoning and
  field layout.** `HelpCostVariancePercent` (25, still global) is the ± swing (as a
  percent) each per-(faction, mission type, threat level) roll is allowed to vary by —
  `NewJerichoVehicleUnitSize` (3)/`SynedrionVehicleUnitSize` (3)/`MutogUnitSize` (2)
  (still global — a faction's own vehicle/Mutog "weight," not something that varies by
  threat level) — how many slots one of that faction's vehicles/Mutogs eats out of the
  (now per-threat-level) squad slot budget, vs. 1 for a regular trooper —
  `NewJerichoAugmentedSoldierChance` (30)/
  `AnuForsekenSoldierChance` (30, both still global) — independent
  per-soldier-slot chance for any New Jericho/Anu trooper this mod spawns (Ask-for-Help
  *and* debug activity squads alike) to be pulled from the Purists/Fallen Ones
  splinter-faction pool instead of that faction's own regular pool (see the
  squad-composition implementation notes below), `RelationshipThreshold` (25),
  `PendingHelpHoursMin` (18)/`PendingHelpHoursMax` (28) — how long a paid-for request
  stays valid before auto-expiring (no refund — see the "no refund" implementation note
  below) if the mission is never launched, rolled
  randomly per-request between the two rather than one fixed value (renamed/split from
  a single `HelpRequestExpiryHours` (24), explicit follow-up request), shown as a live
  countdown on the map marker and in the "Already Requested Help" tooltip (see the
  "Already requested" persistence/expiry implementation note below) —
  `PendingActivityHoursMin` (14)/`PendingActivityHoursMax` (24) — the same idea for a
  `chaos_factions_debug` activity's own expiry, previously sharing
  `HelpRequestExpiryHours` with the help-request case, now its own independently-rolled
  range —
  `HelpForFreeAfterHavenDefenseChancePercent` (20)/`HelpForFreeAfterHavenDefenseRadius`
  (1500) — chance and search radius for the "defend a Haven, its faction sends free help
  nearby" reward (see `HavenDefenseRewardTracker`, "Implementation notes" below) —
  `EnableForPhoenixBaseDefense`/`PhoenixBaseDefenseReinforcementMinTurns`
  (2)/`PhoenixBaseDefenseReinforcementMaxTurns` (3) — Phoenix base defense only, delays
  the ally squad's arrival to a randomly-rolled later turn as reinforcements at the
  map's entrance instead of spawning immediately (0/0 = immediate, like every other
  mission type) — `PhoenixBaseDefenseAllyReinforcementFirstTurnAP` (`100`) — Phoenix
  base defense only, what percent (0-100) of max Action Points that delayed
  reinforcement squad has on the turn they arrive (100 also grants full Will Points;
  see the Root cause 4 implementation note below); every turn after the first is
  unaffected, and this doesn't affect the immediate ally-help spawn, which always gets
  the full amount regardless —
  `EnableForPhoenixBaseInfestation`/`EnableForHavenInfestation`/
  `EnableForPandoranNestAssault`/
  `EnableForPandoranLairAssault`/`EnableForPandoranCitadelAssault`/
  `EnableForPandoranPalaceAssault` (all default `true` — per-mission-type checkboxes
  read by `ContextualAbilitiesPatchHelper.IsEligibleMission`, the one shared gate both
  Ask-for-Help *and* `chaos_factions_debug` activities check — renamed from
  `EnableHelpFor*` per explicit follow-up request, since the old "Help" prefix implied
  Ask-for-Help only when it always gated both features; the four `*PandoranXAssault`
  flags replace what used to be one combined `EnableForAlienBaseAssault`, gated per
  Pandoran base *tier* rather than per mission class — see "Key game classes" below;
  named "Pandoran" to match player-facing
  lore/UI terminology, even though the underlying game classes are still named
  `GeoAlienBaseMission`/`AlienBaseTypeDef` etc.), `EnableDebugLog`. Every field is
  annotated with `[ConfigField(description: "...")]` (`PhoenixPoint.Modding.
  ConfigFieldAttribute` — read by `ModConfigField`/`UIModuleModManager`, the game's own
  Mod Settings screen) so each option shows an explanatory tooltip in-game rather than
  just its raw field name. Per-level
  ally stat growth (Strength/Will/Speed) is *not* config-exposed — see the level/stat
  randomization note below for why. `ActivitySpawnChancePercentPerDay` (20)/
  `ActivityHostileGroupChancePercent` (50) — Feature 2's auto-spawner: daily
  chance per eligible site for an activity to spawn on its own, and the
  hostile-vs-allied-candidate group split for which kind it rolls — see the auto-spawn
  paragraph under "Project goal" above — `ActivityFirstTurnActionPointsPercent` (25) —
  activity squads only (not Ask-for-Help), what percent of max Action Points they have
  on their first turn after spawning, every turn after unaffected (see the "arrival
  Action Points" implementation note below). Neither of these three (nor
  `ActivityMinDistanceFromPlayerUnits`) requires `chaos_factions_debug 1` — that flag
  now only shows the manual
  "ChaosFactionsDebugOptions" free-spawn button, not the underlying activity system
  itself (explicit follow-up request; see "Project goal" above and the matching
  "Implementation notes" bullet). `ActivitySpawnChancePercentPerDay` was originally
  `ActivityAutoSpawnChancePercentPerDay` — renamed again (having already once moved from
  an earlier `Debug*`/`AutoDebug*` prefix to `Activity*` for the same "this isn't debug
  only" reason) once "Auto" stopped being a meaningful distinction: with debug mode no
  longer required, *every* way an activity can appear is arguably "automatic" from the
  player's perspective except the one manual button, so keeping "Auto" in this one
  field's name specifically (out of several `Activity*` fields) read as needlessly
  singling it out. (`ActivityPandoranEscortMinPercent`/`MaxPercent` originally lived
  alongside these two, controlling a Pandoran escort that spawned with hostile debug
  activities — removed per explicit follow-up request; see "Implementation notes" below.)
- `HarmonyInstance` is provided by the framework per-mod.
- `ChaosFactionDebugFlag`/`ChaosFactionDebugConsoleCommands` (`ChaosFactionDebug.cs`) —
  registers `chaos_factions_debug` as a real console command via reflection into
  `ConsoleCommandAttribute`'s own private `CommandToInfo` dictionary, called once from
  `ChaosFactionMain.OnModEnabled()`. Not framework-provided like `HarmonyInstance` above —
  see the matching "Console command registration" implementation note below for why a mod
  assembly's own `[ConsoleCommand]` methods need this extra step at all.

## Localization
Explicit request: "expose hardcoded not translated text to Localization files... add
localization currently only for English... Don't localize options available via debug
mode and don't localize config." Every player-facing string this mod ever shows —
Ask-for-Help popups/context-menu text, the top-left Geoscape objective title/description,
map marker labels, the mission-brief row, disabled-reason tooltips — now resolves through
a `KEY_CHAOSFACTION_*` term in `Data/Localization/ChaosFaction.csv` instead of a hardcoded
C# string literal. Two categories were deliberately left as plain literals, per the
request: `ChaosFactionConfig.cs`'s own `[ConfigField(description: "...")]` text (Mod
Settings tooltips, not in-game UI), and anything only ever shown behind the
`chaos_factions_debug` console flag (`DebugActivityPickerAbility`'s own picker popup, the
`"ChaosFactionsDebugOptions"` button, `DebugActivityAbilityFactory.
GetOrCreateMissionInfo`'s `"[Debug] {missionTypeName}"` entry, and the "activity already
active" status text `GetOrCreateStatus` builds — all only reachable once that flag is on).

**No engine-native "just ship CSVs in a mod's own folder" mechanism exists** — confirmed
by reading `PhoenixPoint.Modding.PPModLoader` (only ever reads `meta.json` + the mod's own
`.dll`, nothing about a `Data`/`Assets` folder) and `PhoenixPoint.Common.Core.
OptionsManager.InitLanguage()` (the *only* place the game itself scans a folder of `*.csv`
for I2 Loc — but it's hardcoded to the single shared `Application.streamingAssetsPath +
"/Localization"` folder, not any individual mod's own directory). Checked TFTV's actual
installed workshop copy as a real-world data point: its CSVs live at its own private
`Assets/Localization/*.csv`, sitting right next to its own `TFTV.dll` — strong evidence it
ships that DLL specifically to load those CSVs itself, the same way `ChaosUkrainian` does
(`ChaosUkrainianLocalization.cs`) for its own translations.

**`ChaosFactionLocalization.LoadOwnTranslations(modDirectory)`** (called once from
`ChaosFactionMain.OnModEnabled()`, passed `Instance.Entry.Directory`) is deliberately much
simpler than `ChaosUkrainianLocalization`'s own loader, for a specific structural reason:
`ChaosUkrainian` only ever *overrides* translations for keys some other system (vanilla,
or a third-party mod like TFTV) already owns, and that owner might register its own term
*after* `ChaosUkrainian` has already run — so it can't safely call `Import_CSV` directly on
a real shared source (`Import_CSV` creates a term if missing, and stub-creating a term
before its real owner does makes that owner's own "does this already exist?" check skip
its own registration — confirmed root cause of a real reported TFTV-blank-in-every-
language-but-Ukrainian bug, see `ChaosUkrainianLocalization.cs`'s own long doc comment on
`ImportCsvDirectory`/`TryApplyTranslation`). None of that risk applies here: every
`KEY_CHAOSFACTION_*` term is exclusively owned by this mod (the prefix guarantees no
vanilla or third-party mod will ever register the same key), so this mod's loader parses
its CSV into its own brand-new `LanguageSourceData` (`Import_CSV(string.Empty, csvText,
eSpreadsheetUpdateMode.Replace, ',')`) and registers that source directly with
`I2.Loc.LocalizationManager.Sources.Add(...)` — `LocalizationManager`'s own lookups
(`TryGetTranslation`/`GetTermData`) already iterate every registered source in that list,
so this needs no interaction with vanilla's or any other mod's source objects at all, and
carries none of `ChaosUkrainian`'s own collision risk. Single-attempt, no retry (unlike
`ChaosUkrainianLocalization`, which re-checks on every Options-panel `Init`/
`ChangeLanguageTo`) — nothing here depends on any other mod or on load order, so a load
that fails once (missing/malformed CSV) will fail identically on any retry, making retries
pure waste.

**`ChaosFactionLocalization.Get(key, params object[] args)`** is the call-site helper —
`string.Format(new LocalizedTextBind(key).Localize(null), args)`, mirroring the exact
`string.Format(x.Localize(null), args)` pattern already established elsewhere in this mod
before this feature existed (`Patch_GeoObjectiveElementController_Update`'s own
`string.Format(__instance.TimeFormatter.HoursText.Localize(null), hoursRounded)`) rather
than inventing a new pattern or depending on I2 Loc's own `{0}`-style parameter system
(which needs live `GameObject` binding this mod's plain-string call sites don't have).
Every `viewElementDef.DisplayName1`/`.Description` assignment still wraps the *resolved*
text in `new LocalizedTextBind(text, true)` (`doNotLocalize: true`) exactly as before —
only the *source* of that text changed, from a hardcoded literal to a
`ChaosFactionLocalization.Get(...)` call; the display-time architecture (pre-resolved text
stored on the Def, refreshed on every `GetOrCreate` call so a stale price/name never
lingers) is completely unchanged.

**CSV shape**: `Key,Type,Desc,English,Chinese (Simplified),French,German,Italian,Polish,
Russian,Spanish,UI_Tester,Ukrainian` — initially shipped English-only, then filled out
for every other language in a later follow-up request ("add localization here for all
remaining languages"), machine-translated directly rather than sourced from vanilla
(these are brand-new mod-only strings, no vanilla equivalent exists to reuse) — `Import_
CSV`'s own `SetupLanguages` step auto-creates whichever languages the header row
declares within this mod's own dedicated source, it doesn't require the full vanilla
language set to already exist anywhere else. `UI_Tester` duplicates the Spanish column
for every row, matching the (inconsistent, but Spanish-most-often) pattern observed in
vanilla's own real CSV exports (`ChaosUkrainian/SourceLocalization`) — `UI_Tester` isn't
a real player-selectable language, just an internal QA slot. `Desc` carries a short
`{0}`/`{1}` parameter-meaning note for whoever edits the CSV next, kept English-only
across every row (translator-facing, not player-facing). Several duplicate literal
strings (e.g. "Ask X for Help" on both the cache-hit and fresh-create code paths, the
"already agreed to help" tooltip, the `"h remaining"` countdown fragment used in three
separate places) were collapsed to one shared key each rather than kept as independent
per-call-site keys.

**The `Ukrainian` column only ever actually shows if `ChaosUkrainian` is also installed
and the player has selected Ukrainian.** Vanilla itself has no Ukrainian language option
at all — `ChaosUkrainian` is what registers "Ukrainian" as a real, selectable option in
the first place (`ChaosUkrainianLocalization.EnsureLanguageRegistered`/`AddLanguage`, a
separate mechanism from this mod's own CSV import). This mod's own source doesn't need
`ChaosUkrainian` enabled to *import* its own "Ukrainian" column — `Import_CSV`'s
`SetupLanguages` auto-creates a matching language entry within this mod's own dedicated
source regardless — but without `ChaosUkrainian` around to unlock the option, the
in-game language dropdown never offers "Ukrainian" at all, so `I2.Loc.
LocalizationManager.CurrentLanguage` can never actually become the string `"Ukrainian"`
for this mod's own Ukrainian terms to match against. Harmless either way: if
`ChaosUkrainian` isn't installed, this column is simply inert data no lookup ever
reaches, not a source of any error.

**Fallback display names for New Jericho/Purists/Fallen Ones/Bandits reuse the game's own
real vanilla localization keys instead of new mod-owned ones — later explicit follow-up
request: "reuse as much as possible to not create additional translation keys."** The
three `KEY_CHAOSFACTION_FALLBACK_*` keys this mod originally minted for these (in
`HelperFactionKindExtensions.DisplayName`, `ChaosFactionState.cs`, and
`DebugActivityKindExtensions.DisplayName()`, `ChaosFactionDebug.cs` — both fallback paths
used only when no live `GeoFaction`/`GeoSubFaction` can be resolved) were removed from
`ChaosFaction.csv` entirely; those call sites now construct `new LocalizedTextBind("KEY_
FACTION_NAME_...").Localize(null)` directly against the vanilla term instead —
`KEY_FACTION_NAME_NEW_JERICHO` ("New Jericho"), `KEY_FACTION_NAME_PURE` ("THE PURE"),
`KEY_FACTION_NAME_FALLEN` ("THE FORSAKEN"), `KEY_FACTION_NAME_BANDITS` ("SCAVENGERS") —
all confirmed to exist by the player, from `ChaosUkrainian/SourceLocalization`.
`DebugActivityKindExtensions.DisplayName()`'s fallback switch also gained an explicit
`DebugActivityKind.Bandits` case for the first time (previously left to fall through to
`kind.ToString()`, i.e. the raw enum name "Bandits" — acceptable before this request since
no real key was known for it yet). The live `DisplayName(GeoLevelController)` overload
(reading `GeoFaction.Name`/`GeoSubFaction.Name` off the actual live instance) is
unaffected — it's only these two "nothing else could be resolved" fallbacks that changed.

**A real, pre-existing `.csproj` bug was found and fixed while adding this**: `Content
Include="..\Data\**"` had no `<Link>` metadata, so `Data/`'s nested subfolder structure was
never actually preserved on copy to `Dist/` — MSBuild's default `CopyToOutputDirectory`
behavior for a `Content` item with no `Link` copies to the output root using only the bare
filename. This had never been noticed before because `Data/` was completely empty prior to
this feature (nothing had ever tested the path). Fixed by adding the exact same `<Link>
Data\%(RecursiveDir)%(Filename)%(Extension)</Link>` metadata `ChaosUkrainian.csproj`
already uses successfully for its own nested `Data/Icons/`+`Data/Localization/**`
structure — confirmed via a real build that `Dist/Data/Localization/ChaosFaction.csv` now
lands at the correct nested path instead of flattening to `Dist/ChaosFaction.csv`.

**A real grammatical-agreement bug was found and fixed, reported by the player from an
in-game Ukrainian string: "Ворожий активність {factionName} поблизу {siteName}" ("Hostile
[masculine] activity[feminine] {factionName} near {siteName}") — masculine adjective
against a feminine noun, correctly flagged as wrong; the fix should be "{factionName} -
Ворожа[feminine] активність поблизу {siteName}", per the player's own suggested phrasing.**
Root cause: `KEY_CHAOSFACTION_ACTIVITY_TITLE_REVEALED` was built by plugging a
*standalone, pre-translated* adjective (`KEY_CHAOSFACTION_VERB_HOSTILE`/`_VERB_ALLIED`,
translated in isolation as e.g. "Hostile"/"Ворожий") into a template sentence
(`{0} {1} activity near {2}`) via `string.Format` — the adjective can't agree with a noun
("activity") whose grammatical gender varies by language, because at translation time the
adjective has no idea what noun it'll end up modifying. Re-auditing every language column
for this same composition confirmed the bug wasn't Ukrainian-specific: the masculine
`{0}` value clashed with a feminine "activity" noun in French (*Alliée*, not *Allié*),
German (*Feindliche*, not *Feindlich* — German also requires an inflection ending on a
prenominal adjective that a bare, uninflected dictionary-form word can't supply), Italian
(*Alleata*, not *Alleato*), Polish (*Wroga*/*Sojusznicza*, not *Wrogi*/*Sojuszniczy*),
Russian (*Враждебная*/*Союзная*, not *Враждебный*/*Союзный*), and Spanish (*Aliada*, not
*Aliado*) — i.e. every language in this CSV with grammatical gender except Chinese (no
gender at all) had the identical class of bug, just silent because no player had reported
it in those languages yet. **Fix, matching the player's own suggested restructuring**:
split the one composed key into two whole-sentence keys,
`KEY_CHAOSFACTION_ACTIVITY_TITLE_HOSTILE_REVEALED`/`_ALLIED_REVEALED` — same
`{0}=kind name, {1}=site name` shape as before, but the adjective is now hardcoded
in-line, in the correct gender/inflected form per language, agreeing with that language's
own noun for "activity" (never plugged in from another key) — the exact pattern this
mod's own `KEY_CHAOSFACTION_ACTIVITY_DESC_HOSTILE`/`_ALLIED` pair (an unrelated, separate
key that already read as two whole sentences, not a composition) had already used
successfully, and grep-confirmed (`KEY_CHAOSFACTION_VERB_HOSTILE|_ALLIED|
_ACTIVITY_TITLE_REVEALED` against every `.cs` file) to be the *only* other place in the
CSV doing this specific "plug a lone translated adjective into a sentence" thing — so no
other key needed the same restructuring, only this one. Word order also changed to match
the player's suggestion (kind name, then a plain `-` separator, then the fixed adjective+
noun+location phrase) — e.g. Ukrainian became `"{0} - Ворожа активність поблизу {1}"`.
`DebugActivityGeoFactionObjective.GetTitle()` (`ChaosFactionDebug.cs`) now picks between
the two keys directly based on `IsHostileNow()` instead of building an intermediate
`verb` string first; `KEY_CHAOSFACTION_VERB_HOSTILE`/`_VERB_ALLIED` were removed from the
CSV entirely (grep-confirmed unused anywhere else) rather than left as dead keys.
**Two smaller non-gender wording issues, found during the same full re-audit "recheck all
translations" pass, fixed alongside the main bug**: (1) `KEY_CHAOSFACTION_
NOT_ENOUGH_RESOURCE`'s French cell used a parenthetical `insuffisant(e)` to dodge not
knowing the dynamic resource name's own French gender at translation time — technically
honest but unusual as in-game UI wording; reworded to "Quantité de {0} insuffisante
({1} nécessaire)." so the adjective agrees with the *fixed* word "Quantité" (always
feminine) instead of the variable resource name, sidestepping the ambiguity entirely
rather than flagging it. (2) `KEY_CHAOSFACTION_AWAIT_HELP_TITLE`'s German cell used an
English-style bolt-on possessive, `"Warte auf {0}s Unterstützung..."` (appending a bare
"s" to a foreign faction-name placeholder), which isn't idiomatic German possessive
formation for an indeclinable proper noun; rewritten to `"Warte auf Unterstützung von
{0}..."` ("support from {0}"), mirroring the same "from"-preposition workaround the
Russian and Ukrainian translations of that same key already used for the identical
"can't decline a dynamically-inserted proper noun" problem. Every other row was checked
against this same "does an adjective/verb need to agree with a noun/subject whose form I
don't fully control" question and found already safe — most either use plural nouns
(Slavic/Romance plural adjective agreement doesn't distinguish gender the way singular
does, e.g. `KEY_CHAOSFACTION_ACTIVITY_DESC_HOSTILE`'s "hostile forces") or avoid an
inflected adjective touching the dynamic placeholder at all.

## Implementation rules
- Use **Harmony patching** for all game behavior modifications (prefix/postfix/transpiler patches).
- Do not modify game sources; patch at runtime via Harmony only.
- When exploring game internals, read from `ModSDK/Assembly-CSharp.dll`
  or `../Assembly-CSharp/` (read only) to understand the classes and
  methods to patch.

## Key game classes (read-only Assembly-CSharp)
| Class | Namespace | Role |
|---|---|---|
| `GeoPhoenixBaseDefenseMission` | `PhoenixPoint.Geoscape.Entities` | The live attack, a `GeoMission`; `.PhoenixBase` → `GeoPhoenixBase`, `.Site.Owner` → the player's `GeoPhoenixFaction` (site is player-owned here, unlike `GeoAlienBaseMission` below). `OnMissionActivated`/`OnMissionCancel` are plain C# events on the instance, inherited from `GeoMission` — used instead of Harmony to detect "launched" vs "declined" |
| `GeoAlienBaseMission` | `PhoenixPoint.Geoscape.Entities` | The live "assault this Pandoran base" mission, also a `GeoMission`; `.AlienBase` → `GeoAlienBase`. `.Site.Owner` here is the **alien** faction, not the player — do not reuse the `GeoPhoenixBaseDefenseMission` pattern of reading player identity off `mission.Site.Owner` for this type |
| `GeoAlienBase.AlienBaseTypeDef` / `SharedData.{Nest,Lair,Citadel,Palace}BaseTypeDef` | `PhoenixPoint.Geoscape.Levels` / `PhoenixPoint.Common.Core` | The four Pandoran base tiers are separate `GeoAlienBaseTypeDef` data assets, not a C# enum — `ContextualAbilitiesPatchHelper.IsEligibleAlienBaseType` compares `mission.AlienBase.AlienBaseTypeDef` by reference against `geoLevel.SharedData.{Nest,Lair,Citadel,Palace}BaseTypeDef` to resolve which of the four `EnableForPandoran*Assault` config flags applies; an unrecognized base type (e.g. from another mod/DLC) defaults to eligible rather than silently blocked |
| `GeoInfestationCleanseMission` | `PhoenixPoint.Geoscape.Entities.Missions` | The live "assault this infested Haven to clean it up" mission, a plain `GeoMission` (not `GeoUpdateableMission`, unlike `GeoHavenDefenseMission`). `.Site` is the Haven, owned by New Jericho/Anu/Synedrion, not the player — same `GeoLevelController.PhoenixFaction` pattern as `GeoAlienBaseMission` applies for reading player identity, not `mission.Site.Owner`. Distinct from `GeoPhoenixBaseInfestationMission` (the player's own infested Phoenix base, a separate class/config toggle) and from `GeoHavenDefenseMission` (the Haven's initial infestation *attack*, which stays out of scope — only this cleanup follow-up mission is eligible) |
| `GeoSite.ActiveMission` | `PhoenixPoint.Geoscape.Entities` | Public property, typed `GeoMission`; `ContextualAbilitiesPatchHelper.IsEligibleMission(site.ActiveMission)` (an `is` check against the four eligible mission classes, each gated by its own config toggle) is how the context-menu patch detects "this site has a mission we offer help for" |
| `UIStateNothingSelected` / `UIStateVehicleSelected` | `PhoenixPoint.Geoscape.View.ViewStates` | Both have a private `GetContextualAbilities(GeoSite site)` building the `List<GeoAbility>` for the site's left-click context menu — patch target (Harmony postfix appends our ability) |
| `GeoAbility` / `GeoAbilityDef` / `GeoAbilityView` | `PhoenixPoint.Geoscape.Entities.Abilities` | The game's real extensible-action system (this is what "Move"/"Enter Base" are). `GeoAbility : Ability : Defineable` — a plain C# class, **not** a `MonoBehaviour`/`ComponentSet` member, so a working instance can be built by hand (`new`, then `.Init(def, repo)` + `.SetActor(site)`) without any Unity Editor asset or ComponentSetDef wiring. `Ability.Activate` already auto-deducts `GeoAbilityDef.Cost` (a `ResourcePack`) and `GetTargetDisabledState` auto-derives `NotEnoughResources` from it — no manual `Wallet` code needed for cost/affordability |
| `GeoscapeAbilityViewElementDef` | `PhoenixPoint.Common.UI` | The ability's display data (`DisplayName1`/`Description` as `LocalizedTextBind`s, `ContextMenuVisibility` etc. as `VisibilityCondition`s — these must be given non-null `List<...>` filter fields or `VisibilityCondition.IsTrue` NPEs). Built via `ScriptableObject.CreateInstance<T>()`, not authored in the Unity Editor |
| `UIModuleSiteContextualMenu.SetMenuItems` | `PhoenixPoint.Geoscape.View.ViewModules` | Consumes the ability list; reads `ability.View.ViewElementDef.DisplayName1` for the button label — assigns into a **pre-instantiated, fixed-size pool** of `SiteContextualMenuItem`s (`ButtonsHolder`'s existing children, walked once into the private `_menuItems` by `InitMenuItems`), so there's a real cap on simultaneously-visible abilities per site — confirmed hit in-game once Feature 2 grew to 8 debug-activity kinds (`ArgumentOutOfRangeException` on `this._menuItems[i]`); a Prefix-based cap was tried and reverted (see "Implementation notes"), fixed instead by collapsing Feature 2's debug-activity entries down to one `ChaosFactionsDebugOptions` button so this mod's own footprint on the pool no longer grows with kind count |
| `GeoAbilityView.GetDisabledStateText(GeoAbilityTarget)` | `PhoenixPoint.Geoscape.Entities.Abilities` | Not `virtual` — a `GeoAbilityView` subclass override wouldn't be called through the `GeoAbilityView`-typed call site in `UIModuleSiteContextualMenu`, so the specific "why is this disabled" tooltip text is patched via Harmony postfix instead, keyed off `__instance.GeoAbility is AskForHelpAbility` |
| `GeoFaction.Diplomacy` (`FactionDiplomacy`) | `PhoenixPoint.Geoscape.Levels` / `...Levels.Factions` | Relationship is directional and stored on the **other faction's** side: `njFaction.Diplomacy.GetDiplomacy(playerFaction)` is the correct read (matches vanilla's own diplomacy UI, `MainDiplomacyRelationDisplayRowElement.cs:20`); `playerFaction.Diplomacy.GetDiplomacy(njFaction)` reads the player's own (unused, stays 0) dictionary and was an early bug here |
| `GeoLevelController.NewJerichoFaction` / `.AnuFaction` / `.SynedrionFaction` / `.PhoenixFaction` | `PhoenixPoint.Geoscape.Levels` | Direct accessors for the three candidate helper factions (`GeoFaction`, each backed by the matching `SharedData.*FactionDef`) and the player (`GeoPhoenixFaction`, upcasts fine to `GeoFaction`) — `.PhoenixFaction` is the correct way to get "the player" regardless of mission type, since `mission.Site.Owner` isn't the player for a `GeoAlienBaseMission` |
| `Base.UI.MessageBox.MessageBox` / `GameUtl.GetMessageBox()` | `Base.UI.MessageBox` / `Base.Core` | The game's generic confirm-dialog system, usable from anywhere (`GameUtl.GetMessageBox()` returns it or `null`, no view-state reference needed). `ShowSimplePrompt(content, icon, buttons, buttonsOverrideLabels, callback, sender, userData)` — `buttonsOverrideLabels` (`Dictionary<MessageBoxButtons, string>`) relabels the stock Yes/No/Cancel buttons to arbitrary text, which is how the payment-method choice popup works without any custom Unity UI; the callback receives a `MessageBoxCallbackResult` whose `DialogResult` (`MessageBoxResult.Yes`/`No`/`Cancel`/etc.) says which button was pressed |
| `Wallet` | `PhoenixPoint.Common.Core` | `GeoFaction.Wallet`; the cost deduction goes through the ability framework's own `Cost`-driven `Wallet.Take`, not ours. No code path calls `Wallet.Give(..., OperationReason.Refund)` any more — a cancelled/expired request is never refunded, see the "no refund" implementation note below |
| `GeoFaction.GetEligibleTemplatesForMission` | `PhoenixPoint.Geoscape.Levels` | Returns generic (non-hero) `TacCharacterDef` troopers for a faction, filtered by `AvailableForGenericDeployment`/`DaysBeforeAvailable` — sampled for the 5 random New Jericho templates |
| `TacticalLevelController` | `PhoenixPoint.Tactical.Levels` | `.TacMission.MissionData.MissionType.Tags` (checked against `SharedData.SharedGameTags.BaseDefenseMissionTag`) confirms the tactical session is actually a base defense; `.Factions`, `.Repo`, `.TacticalLevelControllerDef`, `.GetTacticalFaction(...)` are all public and used to create New Jericho's `TacticalFaction` the same way the engine's own `CreateFactions` does |
| `TacticalFaction` | `PhoenixPoint.Tactical.Levels` | `ParticipantKind` (settable — the injected faction gets whichever of `Intruder`/`Residents` the mission's actual enemy isn't using, see `ResolveEnemyParticipant`/`GetOrCreateHelperFaction`), `DeployZones` (only returns zones pre-assigned to that faction in the map — irrelevant for a runtime-created faction, hence reusing the player's own zones instead), static `SetMutualRelation(a, b, FactionRelation)` |
| `TacMissionTypeDef.ParticipantsRelations` | `PhoenixPoint.Common.Levels.Missions` | `MutualParticipantsRelations[]`, each a `FirstParticipant`/`SecondParticipant`/`MutualRelation` triple — the actual source of truth for "who is enemies with whom" on a given mission type; `TacMissionParticipant` kinds (`Intruder` vs `Residents`) aren't fixed to "attacker" vs "defender", they vary by mission type (base defense: enemy is `Intruder`; alien base assault: enemy is `Residents`), so resolving "the enemy of Player" means searching this array (mirrors vanilla's own `GeoMission.GetEnemyParticipantData()`) rather than hardcoding one kind |
| `TacticalDeployZone.SpawnActor` (static overload) | `PhoenixPoint.Tactical.Levels.ActorDeployment` | `SpawnActor(ComponentSetDef, ActorInstanceData, TacticalFactionDef, TacMissionParticipant, Vector3, Quaternion, object)` — spawns an actor as an arbitrary faction at an arbitrary position, independent of which zone "owns" it; the instance-method overload was deliberately avoided since it always spawns as the zone's own faction |
| `TacticalDeployZone.IsReinforcementZone` | `PhoenixPoint.Tactical.Levels.ActorDeployment` | `this.MissionDeployment.Any(x => !x.ExcludeActor && x.DeactivateAfterTurn > 0)` — true for a zone authored (per map/mission-Def scene data, not visible in C#) as an "off-map entrance new forces can arrive at after turn 0," the same flag `TacParticipantSpawn` (vanilla's own reinforcement-wave spawner) filters by — used by `ChaosFactionTactical.GetSpawnZones` for the delayed Phoenix-base-defense reinforcement spawn's "entrance" targeting, found via `UnityEngine.Object.FindObjectsOfType<TacticalDeployZone>()` since it needs to be found regardless of which faction's Def references it, not `TacticalFaction.DeployZones` (only returns a specific faction's own zones) |
| `DieAbility.GetDroppableItems`/`DropItems` | `PhoenixPoint.Tactical.Entities.Abilities` | `GetDroppableItems()` (protected, **not** `virtual`) filters an actor's `InventoryComponent`/`AddonsManager` contents down to what `DropItems()` (called from `Die()`, and inherited unoverridden by `RagdollDieAbility`) actually drops/destroys on death — no per-actor "never drops loot" flag exists anywhere in code, loot suppression is normally an item-`TacticalItemDef`-level concern (`DropOnActorDeath`/`DestroyOnActorDeathPerc`), not an actor-instance one. Since the method isn't `virtual`, patching the base `DieAbility` declaration (not each subclass) covers every subclass's call to `this.GetDroppableItems()` |
| `GeoHavenDefenseMission` / `GeoUpdateableMission` | `PhoenixPoint.Geoscape.Entities` | `GeoHavenDefenseMission : GeoUpdateableMission : GeoMission`. `.Site` is the Haven, `.Site.Owner` is the faction being attacked/defended (the "faction which was attacked" for the free-help reward). `GeoUpdateableMission.OnUpdateableMissionEnded` (not `GeoMission.OnMissionCompleted`) is the event that fires for *both* a fully auto-resolved defense (ticked down by `OnUpdateMission`/`SimulateBattle` while the player is elsewhere, never touches tactical) and a player-played-out one — `OnMissionCompleted` only fires from `GeoMission.Complete(TacMissionResult)`, reachable only via the tactical-play path, so it would silently never fire for an auto-resolved outcome. Two ways a `GeoHavenDefenseMission` comes into existence (its normal public constructor, `GeoSite.cs:798,1728`, or a private one used only by `DeserializeCustomCreate` during save-load) — `StartUpdating`/`ResumeUpdating` (both public, both called exactly once per instance regardless of which constructor built it) are the actual Harmony patch targets used to catch every instance, not either constructor |
| `Timing.Paused` | `Base.Core` | Public settable `bool` — the same property `UIModuleTimeControl.OnPauseTime` flips when the player clicks the pause button (`this._timing.Paused = pause;`), confirmed via source as the sanctioned way to pause Geoscape time from outside the UI layer, not a workaround. `geoLevel.Timing.Paused = true` is exactly this |
| `GeoFactionObjective` / `GeoFaction.AddObjective`/`RemoveObjective` | `PhoenixPoint.Geoscape.Levels.Objectives` / `PhoenixPoint.Geoscape.Levels` | Plain C# abstract class (not a `Def`/`ScriptableObject`) backing every entry in the top-left Geoscape objectives panel; `MissionGeoFactionObjective` is the closest vanilla analogue (one objective tied to one `GeoMission`) and the template this mod's own `AskForHelpGeoFactionObjective` copies the shape of — `[SerializeType]` class, `[SerializeMember]` on live-reference fields, private parameterless constructor for the deserializer — since instances live inside `GeoFaction._objectives`, itself normal save data, whether a modder wants that or not. `AddObjective`/`RemoveObjective` (both public, e.g. `GeoscapeFactionObjectiveSystem`'s own usage) are the real, sanctioned integration point — confirmed via source, not inferred |
| `GeoMap.SitesInRange` / `.DistanceBetweenSites` / `EarthUnits` | `PhoenixPoint.Geoscape.Levels` / `PhoenixPoint.Common.Core` | `SitesInRange(GeoSite, EarthUnits, excludeSelf)` is the same spatial-cache-backed range query vanilla itself uses (e.g. scavenging ambush range) rather than a naive scan-and-filter. `EarthUnits` is roughly "kilometers on a realistic-Earth scale" (`InMeters => Value * 1000f`, and `ToGlobe` references Earth's real radius, 6371, directly) — `new EarthUnits(cfg.HelpForFreeAfterHavenDefenseRadius)` is how a plain `float` config value becomes one |
| `GeoSubFaction` / `GeoSubFactionDef` | `PhoenixPoint.Geoscape.Levels.Factions` | Feature 2 (Pure/Forsaken): a sub-faction tier, `IGeoFactionMissionParticipant` but **not** a `GeoFaction` (separate hierarchy — has its own `PPFactionDef`/`GeoFactionViewDef`-backed `ViewDef`, but `GeoFactionObjective`'s `GivenByFaction` is `GeoFaction`-typed, so `DebugActivityGeoFactionObjective` uses the Alien faction for its icon instead, see below). `GetDeploymentForMission(GeoMission, int factionDeployment, Predicate<ClassTagDef> unitFilter = null)` returns `List<IDeployableUnit>` (always `TacCharacterDef`s in practice — confirmed by reading the method body — falling back to `SubFactionDef.StartingUnits` if the sub-faction's own eligible-templates list is empty). `GeoLevelController.SubFactions` (already-instantiated) / `GeoLevelController.CreateSubFaction(GeoSubFactionDef)` (idempotent — returns the existing instance if already created) / `DefRepository.GetAllDefs<GeoSubFactionDef>()` (the full catalog, regardless of whether a given campaign instantiated it) are the three places `DebugSubFactionResolver` checks, in that order |
| `PPFactionDef.MatchesShortName(string)` | `PhoenixPoint.Common.Core` | Case-insensitive match against the Def's own `ShortNames: string[]` array — the same string-based faction lookup vanilla's own `subfaction_create`/`subfaction_set_mission_weight`/`subfaction_get_mission_weight` console commands use (`GeoSubFaction.cs`). `DebugSubFactionResolver.Resolve("pure"/"forsaken")` uses this, with a substring-against-`GetName()` fallback since the exact `ShortNames` values for Pure/Forsaken can't be confirmed from C# alone (sub-factions are data-driven assets, not compiled field names) |
| `ConsoleCommandAttribute` | `Base.Utils.GameConsole` | `LoadCommands()` only ever scans `Assembly.GetExecutingAssembly().GetTypes()` — i.e. `Assembly-CSharp.dll` itself — and already ran (`GameConsole.Init()`) before any mod is discovered/enabled, so a mod assembly's own `[ConsoleCommand]` methods are never picked up the normal way. `ChaosFactionDebugFlag.RegisterConsoleCommands` reflects them into the same private `CommandToInfo: SortedList<string, ConsoleCommandAttribute>` dictionary `LoadCommands()` itself populates (setting the private `_methodInfo` field the same way), mirroring `ChaosUkrainian`'s own already-proven `ChaosUkrainianLocalization.RegisterConsoleCommands` almost verbatim rather than inventing a second technique for the same problem. Method signature convention: first parameter `IConsole`, remaining parameters must have a `Helper.TypeToConvertFunc` entry (confirmed `int` is supported) — `public static void SetDebugMode(IConsole console, int value)` is what `chaos_factions_debug 1` dispatches to |
| `GeoLevelController.HourTicked` | `PhoenixPoint.Geoscape.Levels` | Public `event Action<int>` (`GeoLevelController.cs:337`), fired once per in-game hour from the private `LevelHourlyUpdateCrt` (itself scheduled via `Timing.Start(..., NextUpdate.After(TimeUtils.GetNextHour(...)))` and re-scheduling itself every call — confirmed via source, the same hourly tick vanilla's own faction/haven/research updates run from) — a real, sanctioned, already-public hook, so `AutoDebugActivitySpawner.OnHourTicked` subscribes to it directly (`ChaosFactionGeoscape.OnGeoscapeStart`) instead of needing a Harmony patch on the private method itself |

## Implementation notes / known limitations

### Eligibility & diplomacy gating
- **`EnableRequestForHelpForAnyMissionType`** (config, default `false`) broadens Ask-for-Help
  (never activities) beyond the 4 known mission classes to any `GeoMission` that resolves
  an enemy faction at all. `IsEligibleMissionForAskForHelp` returns `true` immediately for
  the 4 known classes; otherwise, only when the toggle is on, applies the same generic
  guards (`PassesGenericGuards`) plus `GetInspected` (not `GetVisible` — a site can be
  visible/spotted without being inspected/identified; vanilla itself gates site actions on
  `Inspected`). Only `TryAddAskForHelpAbility` and `HavenDefenseRewardTracker.
  FindClosestEligibleSite` use the broadened check; debug activities always use the
  narrower `IsEligibleMission`, so the toggle can never open hostile/allied activities for
  an unrecognized mission type.
- **Per-candidate-helper-faction war check** (`IsHelperFactionEligibleAgainstEnemy`): for
  the 4 known mission types any candidate is eligible (only the attacker itself is
  excluded). For a broadened mission type, if the enemy resolves to one of New
  Jericho/Anu/Synedrion, the candidate is only eligible if `IsFactionAtWarWith` (checked
  **bidirectionally** — the two factions' own recorded diplomacy toward each other can
  genuinely diverge, confirmed via `FactionDiplomacy.ShiftToHostileDiplomacy`). Haven
  Defense uses an opposite-shaped rule instead of this one: the Haven's owner is excluded
  from ever helping itself, and a candidate is only eligible if it's **not** at war with
  the Haven's owner (not the attacker) — a different relationship than every other
  broadened mission type. "At war" = `PartyDiplomacyState.Conflict` or worse via
  `GetDiplomacyState`, read fresh on every context-menu build (nothing to invalidate) —
  the same real threshold `VehicleFactionController.PlanCurrentSiteAttack` uses to decide
  whether a faction's vehicles will actually attack. `FactionDiplomacyState.AtWar` is a
  dead one-shot flag, confirmed never read by any combat code — deliberately not used.
- **`IsEligibleMission`** also requires `!mission.IsCompleted` and `Site.State ==
  Functioning`. `GeoMission.ApplyMissionOutcome` doesn't unconditionally clear
  `Site.ActiveMission` on completion (depends on the mission's own `ClearMission` Def
  flag), so a finished mission's object can keep sitting in `ActiveMission` without these
  checks. `GeoMission.IsRunnable` was considered and ruled out as a substitute — `virtual`
  with zero overrides anywhere in code, so it's always `true` regardless of
  actual state.

### Pricing & squad-size settings
- Cost/squad-size settings are keyed by `DifficultyThreatLevel` (Low/Medium/High/Extreme,
  4×8=32 fields), replacing an earlier per-`HelpMissionType` scheme (7×8=56 fields).
  `DifficultyThreatLevel` is the game's own real enum — confirmed via
  `CommonMissionDataController.SetData`, the same value the mission-brief popup's own
  Threat Level row displays. `ContextualAbilitiesPatchHelper.ResolveThreatLevel(GeoMission)`
  calls `GetMissionThreatLevel` directly rather than reading the `GeoMission.ThreatLevel`
  property (only populated after `Launch()` runs — reading it earlier just returns the
  unset default) — falls back to `MissionDef.DifficultyThreatLevel` on error, `Low` if the
  mission itself is unavailable.
- `FactionHelpOfferTracker.Offers` stays a 3-way key —
  `(PPFactionDef, HelpMissionType, DifficultyThreatLevel)` — not a straight swap. Mission
  type still identifies *which* offer a (faction, site) pair is quoted, so two mission
  types at the same threat level don't coincidentally show the same rolled number; the
  *base value* those numbers roll around now comes from the threat-level's settings block
  instead of the mission-type's. Resource type (Supplies/Materials/Tech) is unaffected by
  either axis — still a per-faction daily permutation.

### Squad composition
- Squad size is a rolled **slot budget** (`HelpMinSquadSize`..`HelpMaxSquadSize`), not a
  flat troop count — a vehicle costs `NewJerichoVehicleUnitSize`/`SynedrionVehicleUnitSize`
  (3) slots, a Mutog costs `MutogUnitSize` (2), infantry costs 1.
- Vehicle/Mutog counts are capped **per faction** (`HelpMaxNewJerichoVehiclesInSquad`/
  `HelpMaxSynedrionVehiclesInSquad`/`HelpMaxAnuMutogsInSquad`, default 1 each) and rolled
  as a **streak** via `RollBigUnitSlots`: each iteration rolls `VehicleSpawnChancePercent`
  once, and only a success both adds that candidate *and* loops to roll again (if the cap
  isn't reached) — the first failed roll ends the loop immediately, unlike an earlier
  version that made `maxCount` independent attempts (which could "skip" a failed roll and
  still land the next one).
- Non-vehicle/Mutog slots fill diversity-first (`PickPreferringNewClass` — prefer a
  `TacCharacterDef.ClassTag` not yet represented in the squad, falling back to an
  unrestricted pick once every distinct class already has a member).
- New Jericho/Anu squads — Ask-for-Help *and* debug-activity squads alike — can pull
  individual troopers from an "augmented" splinter pool instead of the faction's regular
  one: New Jericho from Purists, Anu from Fallen Ones, each an independent per-soldier-slot
  roll (`NewJerichoAugmentedSoldierChance`/`AnuForsekenSoldierChance`, both default 30%).
  Synedrion has no augmented pool — a variant drawing from Purists filtered to templates
  whose gear name-matched "Synedrion-style body armor" was tried and reverted; no Purists
  template's gear actually matched by name in testing.
- Mutogs get their own capped roll pass, separate from vehicles (priced via
  `MutogUnitSize` rather than the vehicle unit sizes) — each Mutog subtype apparently
  carries its own distinct `ClassTag`, so leaving them in the diversity-first infantry pass
  would let "one random template per distinct class" pick one of *every* Mutog subtype,
  uncapped.

### Ally spawn mechanics
- **Level/stats**: `TacCharacterDef.GenerateActorDeployData()` only builds the ability set
  for the template's base level (via a private `ApplyPogression`), so spawned allies came
  out low-level with flat stats by default. `ChaosFactionTactical.RandomizeLevelAndStats`
  rolls a level, re-grants abilities up to it, and adds flat per-level Strength/Will/Speed
  bonuses (hardcoded consts, deliberately not config-exposed). `RollStat` rolls
  independently *per level* and sums the results — an earlier version rolled one rate once
  and multiplied by level, which gave a fixed total for any given roll with zero
  per-level variance. Must run between `GenerateActorDeployData()` and
  `InitializeInstanceData()` — the latter bakes `Strength`/`Will`/`Speed` into
  `BaseStrength`/`BaseWill`/`BaseSpeed`, after which changes have no effect.
- **Spare ammo**: a generic-pipeline actor otherwise carries only its weapon's pre-loaded
  magazine. `ReloadAbility` is a fully generic, non-player-gated ability, so AI already
  uses it on its own once there's something to reload with — the missing piece was ammo,
  not AI capability. `GrantSpareAmmo` adds `SpareAmmoClipsPerWeapon` (4) spare clips of
  each weapon's first `CompatibleAmmunition` entry to the pre-spawn instance data.
- **Bonus medkits**: `GrantBonusMedkits`, run right after `GrantSpareAmmo` in the same
  pre-`InitializeInstanceData()` step — explicit request ("add additional 1-3 medkits to
  spawned soldier"). Originally gated behind a 50% per-soldier chance, removed per a later
  explicit follow-up ("there must be always rolled medkit, no need for 50%, just roll 1-3
  additional medkits randomly") — every soldier this reaches now always gets a roll in
  [1, 3], never zero. The count range is a plain hardcoded constant rather than a
  `ChaosFactionConfig` field, per the earlier explicit request ("don't add to config, just
  roll randomly"), mirroring `SpareAmmoClipsPerWeapon`'s own not-config-exposed style.
  A soldier's own carried medkit (if any — checked across both `EquipmentItems` and
  `InventoryItems`, identified structurally via `IsMedkitItem`: any `ItemDef` whose
  `Abilities` contains a `HealAbilityDef`, confirmed via source to be the exact signal
  vanilla's own `UIModuleWeaponSelection.SetupSelectedWeaponInfo` uses to detect a
  medkit-type item) is preferred, so the bonus matches the tier they'd actually use.
  **Originally fell back to a random pick across every recognized medkit `ItemDef` in the
  game (a `GetMedkitItemPool` scan) when the template carried none — simplified per a
  later explicit follow-up request** ("If no medkit items then simply add 1-3 random
  items for specific Medkit_EquipmentDef items", also "Remove this log", i.e. drop the
  diagnostic logging that scan added): `ResolveDefaultMedkitItemDef` now just looks up
  the base game's own standard `Medkit_EquipmentDef` by its literal asset name (confirmed
  via Player.log as the name every vanilla/spawned soldier's Medkit actually shows up
  as), no broader pool/logging involved. Only ever reached for units with a valid
  `LevelProgression` — `RandomizeLevelAndStats` (the method both grants live in) already
  returns before reaching either grant otherwise, so vehicles/Mutogs (no
  `LevelProgression`) never roll this, with no separate vehicle/Mutog check needed.
- **Heal to full on spawn**: `HealToFullHealth`, called right after `SpawnActor` in
  `SpawnSquad` (after `SetAlertedOnSpawn`/`BoostVehicleAmmo`) — explicit request, reported
  in-game as spawned allies sometimes looking damaged: "some abilities added which
  increase strength etc, but health is not updated... heal those soldiers to have 100% of
  their HP". Root cause: `TacticalActor.ProcessInstanceDataStats` already calls
  `Health.SetToMax()` itself for a fresh spawn, but that runs as part of the actor's own
  base-stats setup step — *before* ability-driven passive effects (granted later in the
  same `ProcessInstanceData` cascade, still inside `SpawnActor`) get a chance to raise
  `Health.Max` further, leaving `Value` stuck below the new, higher `Max`. Since
  `ProcessInstanceData` (and everything it triggers, including ability grants) runs
  synchronously inside `SpawnActor`, calling this once `SpawnActor` has already returned
  guarantees it always gets the final word regardless of what raised `Max` along the way.
  Mirrors `HealAbility`'s own two-part healing (general `Health` + every body-part slot's
  own health via `BodyState.GetHealthSlots()`), just going all the way to full
  (`StatusStat.SetToMax()`) instead of adding a fixed amount. Applies to vehicles/Mutogs
  too (`Health` is a `TacticalActorBase`-level field) — only the body-part loop is
  character-only, guarded by an `as TacticalActor` check.
- **Vehicle ammo**: vehicle AI never invokes `ReloadAbility` on its own turret weapon, so
  `BoostVehicleAmmo` instead inflates already-loaded magazines *after* spawn, via
  `CommonItemData.ModifyCharges` called directly on each loaded magazine item (checks only
  that magazine's own per-clip `ChargesMax`, converting overflow into its stack `Count`).
  A first attempt inflating the pre-spawn `Ammo` data instead left vehicles at 0 ammo:
  `AmmoManager.LoadMagazine` self-evicts against the weapon's *shared* `ItemDef.
  ChargesMax` (its normal total capacity), so every inflated magazine overflowed that
  check and got evicted immediately, including the last one.
- **Alertness**: allies spawn already alerted (`TacAIActor.IsAlerted` set directly on the
  live component right after spawn) instead of waiting for their own AI perception to
  notice a threat — `GenerateInstanceData()` never populates `AIActorData` at all for a
  generic-pipeline spawn, so every such spawn (ally or enemy) otherwise starts
  unalerted by default.
- The full New Jericho roster is used, including vehicles and turret-deployers
  ("Armadillo", "Technician") — see the asset-loading race below for why these were once
  reported invisible on some mission types.

### A cold-asset-loading race, not a content gap (Technician/vehicle invisibility)
Confirmed root cause, after several ruled-out theories (permanent content gap,
`TacMissionParticipant`/`ParticipantKind` correlation, random per-actor identity-tag
variance — all tested and disproven with direct evidence, don't re-open them if this
recurs): `AddonSkinDataBase.GetPrefabAsset` only reads `assetReference.Asset` — whatever
Unity Addressables already has loaded in memory — and never triggers a load itself.
Vanilla missions preload their entire scripted roster ahead of time
(`AssetsManager.StartAcquireDependencies`, during level setup); this mod's ally squad is
chosen and injected by mod code that preloader has no visibility into, so an asset is only
"warm" if something else in the mission already happened to reference it (e.g. Technicians
appear naturally on a Lair assault, so their assets are warm there, but not on an
all-Pandoran base infestation cleanup). **Fix**: `AllyRequestTracker.MarkRequested` calls
`AssetsManager.StartAcquireDependencies` as soon as the squad is picked (at Ask-for-Help
click time), giving the async load the entire mission-launch loading-screen transition to
finish — not a hard guarantee, but matches the real mechanism vanilla itself relies on. If
this is ever reported again, get a fresh log and re-check preload timing first rather than
re-deriving from scratch.

Separately, a defensive fix at the actual crash site: `TacticalActor.GetAimPoints()`
doesn't null-filter (unlike `CharacterTargetDummy`'s own version), and has 15+ unguarded
callers (AI targeting, damage prediction, vision) — some New Jericho weapon/body
combinations our random squad picker can produce hit a data gap where a slot has no aim
point, NRE-ing whichever coroutine calls it. `Patch_TacticalActor_GetAimPoints` filters
nulls at the source instead of chasing every triggering combination individually.

### Tactical faction / participant mechanics
- **`TacMissionParticipant.Residents` can already be claimed by the mission itself** (e.g.
  an alien-base assault's own captured civilians/hostages). `GetOrCreateHelperFaction`
  must check whether the *specific helper faction* — not just "is `Residents`
  occupied by anyone" — already has an authored role in `MissionData.MissionParticipants`
  before creating/reusing a `TacticalFaction` for it. An earlier, broader "refuse if
  `Residents` is occupied at all" check silently blocked every alien-base request (a
  different, unrelated faction legitimately owns that slot there); missing the check
  entirely corrupted the mission's own reinforcement pipeline (observed as malformed
  vehicle/infantry spawns whose missing aim points then NRE'd `TacticalFactionVision`'s
  line-of-sight checks and froze the game).
- New Jericho can itself be the mission's attacker (a hostile human raid on a Phoenix
  base) — `GetOrCreateHelperFaction` refuses to reuse an existing New Jericho
  `TacticalFaction` whose `ParticipantKind` is `Player` or matches the resolved enemy
  participant, rather than silently flipping the attacker's own relations to Friendly.
- **`TacMissionParticipant` roles (`Intruder`/`Residents`) aren't fixed per side of the
  fight** — they're assigned per mission type via `TacMissionTypeDef.
  ParticipantsRelations`. `ResolveEnemyParticipant` searches that table the same way
  vanilla's own `GeoMission.GetEnemyParticipantData()` does, instead of hardcoding
  `Intruder` as "the enemy" — that hardcoding is what originally caused allies to spawn
  friendly-to-player but neutral-toward-Pandorans specifically on alien base assaults,
  since aliens are tagged `Residents` there, not `Intruder`.
- Ally instance data must go through `TacCharacterDef.GenerateActorDeployData()` →
  `ActorDeployData.InitializeInstanceData()`, not a bare `ComponentSetDef` — passing `null`
  for `ActorInstanceData` falls back to a loadout-less instance and NREs deep in vanilla
  code the moment it enters play.
- **Turn order & arrival AP**: `GetOrCreateHelperFaction` calls the new faction's
  `OnLevelStart()` (wires the vision/perception event subscriptions AI turn-taking depends
  on) — omitting this left an AI-controlled helper faction with no perception of any
  enemy, reading in-game as "it just skipped its turn." Mod-spawned factions are inserted
  into `TacticalLevelController.Factions` immediately before the resolved enemy faction
  (not appended to the end) so they get a turn before the enemy every round — driven by
  the real `FactionEndedTurnEvent` (fires once per faction-turn transition), not the
  original once-per-round `OnTacticalNewTurn` hook, which couldn't place a
  runtime-appended faction ahead of one already earlier in the list.
  A freshly-spawned actor also picks up the vanilla `"DoNotResetThisTurn"` trait whenever
  it's spawned while already `TacticalLevelController.CurrentFaction` — true for the
  reinforcement case *by construction*, since it's inserted at the current faction's own
  list index — which makes `RestartAbilities()` skip `ActionPoints.SetToMax()` entirely.
  A pre-spawn "grant full AP" call can never beat this, since the trait is added inside
  `SpawnActor` itself, before mod code ever gets the actor back. **Fix**: a Postfix on
  `TacticalActor.StartTurn()` itself (always runs last) grants AP from a one-shot
  `PendingArrivalActionPoints` dictionary (actor → AP fraction), consumed on that actor's
  first `StartTurn()` only — Phoenix-base-defense reinforcements get 100% AP + full Will
  (`PhoenixBaseDefenseAllyReinforcementFirstTurnAP`), activity squads get a smaller
  fraction (`ActivityFirstTurnActionPointsPercent`, default 25%) and no Will change.
- Spawned actors also get the same vanilla arrival VFX/sound cue real reinforcements get
  (`TacParticipantSpawn.AdjustSpawned`, called right after `SpawnActor`) — vanilla
  reinforcements have no walk-in/landing sequence of their own to imitate; they spawn
  instantly too, so this is just the missing vanilla side-effect call, not a missing
  animation.

### Spawn positioning
- Debug activities spawn "near the map center," approximated by averaging every
  `TacticalDeployZone`'s position (no NavMesh/`NavAreas` query is usable before an actor
  exists to own one) and picking a random point within half the map's zone-radius of that
  centroid — sampled uniformly by area, not the exact centroid every time.
- Both the activity spawn position and that random target point steer away from live
  player-unit positions (`ActivityMinDistanceFromPlayerUnits`, default 30) and from
  squadmates already placed earlier in the same spawn call
  (`MinSquadmateSpawnDistanceTiles`, default 2 tiles — applied to debug activities and to
  the delayed Phoenix-base-defense reinforcement, never the immediate Ask-for-Help spawn).
  Both fall back to the least-bad candidate rather than refusing to spawn if nothing
  clears the distance.
- Vehicles/Mutogs prefer the lowest floor found among a zone's deploy candidates (within a
  documented-approximate height tolerance), since `TacticalDeployZone.
  GetDeploymentPositions` mixes candidates from every floor stacked at a grid point with
  no floor-index API to filter by — falls through to the unrestricted search if no
  ground-floor candidate exists.
- Phoenix-base-defense reinforcements spawn at `TacticalDeployZone.IsReinforcementZone`
  entrances (the same flag vanilla's own turn-based reinforcement waves use) instead of
  the player's normal deploy zones, falling back to the normal zones if no
  reinforcement-flagged zone exists on the map.

### Persistence (save/load and Geoscape reload)
- "Already requested" survives save/load and auto-expires after a random
  `PendingHelpHoursMin`–`HoursMax` window (18–28h default) if never launched — no refund
  on expiry or cancel (the cost is the price of asking, not of the mission happening).
  Keyed by `GeoSite` (`SiteId`), not `GeoMission` — a reload creates a *new* `GeoMission`
  instance for the same logical attack, so a `GeoMission`-keyed dictionary silently reset
  and let the player pay twice. Save data is flattened to plain primitives only (no live
  Def/`GeoSite`/`GeoFaction` references, since round-tripping those through this project's
  save serializer is unconfirmed) and restored from `OnGeoscapeStart`, not
  `ProcessGeoscapeInstanceData` (site/mission state isn't guaranteed safe to touch that
  early). The squad itself isn't persisted — a restored request that goes on to launch
  picks its squad fresh at that point. Debug activities later got the identical
  save/restore treatment (`ChaosFactionSavedActivity`), including revealed-state
  round-tripping.
- **Every ordinary Geoscape reload** — not just a true save/load, launching and returning
  from *any* mission recreates the whole `GeoSite`/`GeoMission`/`GeoFaction` object graph —
  can orphan static tracker dictionaries and leave stray serialized objective instances
  behind if not defensively cleared. `AllyRequestTracker.RestorePendingRequests` already
  unconditionally strips stray `AskForHelpGeoFactionObjective` instances on every
  `OnGeoscapeStart` (why a stuck-objective bug was only ever reported for debug
  activities, which lacked the equivalent cleanup until added). A second, independent
  layer in the objective-panel countdown patch removes an objective the moment its
  backing tracker entry can no longer be found, so a stale row can't outlive its data
  either way it might have gone stale.
- Closing the "launch now?" confirmation popup (without truly declining) was wrongly
  wiping a pending request — `GeoMission.Cancel()` fires the same `OnMissionCancel` event
  for both a true decline and "back out to decide later," distinguishable only by whether
  `Site.ActiveMission` still equals the mission afterward (mission-type-dependent, via
  `MissionDef.ClearMissionOnCancel`) — the handler now checks that before wiping.
- A single shared `SitePauseCoordinator` (not one counter per tracker) captures/restores
  the pre-existing Geoscape pause state, so a help request and a debug activity active at
  once (always at different sites — the two systems are mutually exclusive per site)
  still only capture/restore that state once, correctly.

### UI
- **Context-menu button pool has a real, fixed size** — confirmed via an in-game crash
  once the debug-activity kind count grew past it (`UIModuleSiteContextualMenu.
  SetMenuItems` indexes into it with no bounds check). A Harmony Prefix that truncated the
  ability list to fit was tried first and reverted (reported to break something
  unspecified in-game). **Fix**: every debug-activity kind now funnels through one
  "ChaosFactionsDebugOptions" context-menu entry with a text-input picker popup, instead
  of one button per kind — fixes the crash at its root (a fixed per-site footprint
  regardless of how many kinds exist) rather than working around it.
- **Map marker** (`HelpRequestedMarker`) is built from primitives — no vanilla
  icon/highlight/haven-defense-ring mechanism fit (all considered and rejected: each tied
  to a specific vanilla feature, silently fights native hover-highlight, or purpose-built
  around a mission-progress value this feature has none of). Parented under
  `SiteIconRenderer.transform` (not `VisualsContainer`, which wasn't visible in-game). Its
  label uses a reflected `UnityEngine.TextMesh` — `Canvas`+`UI.Text` needs
  `UnityEngine.UIModule`/`TextRenderingModule` DLLs not in this project's ModSDK reference
  set; `TextMesh` needs no `Canvas` and resolves purely via reflection against the
  already-loaded runtime type. Two real bugs were found and fixed while extending this for
  debug activities: (1) `Show()`'s `color` parameter was never actually read in the method
  body — every "hostile" marker had silently been rendering the default friendly blue the
  whole time; (2) the label's color-update component cached its reflection handle in
  `Awake()`, before `Show()` had assigned the field it depends on (Unity runs a
  freshly-added component's `Awake()` synchronously inside `AddComponent`, before the
  caller's next line runs) — permanently null, so per-frame color/pulse updates never
  applied at all. Fixed by resolving lazily on first `Update()` instead. An activity marker
  starts as a neutral gray "Unknown Activity" (and the top-left objective uses the Alien
  faction's icon regardless of kind) until the player first opens the site, at which point
  `RevealIfNeeded` (a one-way latch) recolors the marker and rebuilds the objective with
  the real faction's icon — `GeoFactionObjective.GivenByFaction` is `readonly`, so the
  objective can't be updated in place and is removed/re-added instead.
- **Mission-brief popup** gets a light-blue "X will help in this fight" row, added by
  patching the one shared `CommonMissionDataController.SetData` (all four eligible mission
  types' brief modals route through it) and cloning the existing `EnemyRow` rather than
  hand-building UI. Every `Text` under the cloned row needs relabeling, not just the value
  text — the row also carries `EnemyRow`'s own static "ENEMY" category-label sibling,
  which an early version of this patch missed, leaving the clone literally reading "ENEMY".
- **Top-left objective countdown/tooltip**: `GeoObjectiveElementController`'s native
  countdown only activates for `EventGeoFactionObjective` (a registered `GeoscapeEventDef`
  timer), which this mod's objectives aren't — a Postfix on its own `Update()` overwrites
  the row text/tooltip directly instead, throttled to only run when the rounded hour value
  actually changes. An icon inside the tooltip popup itself was requested but isn't
  implemented — `UITooltip`/`UITooltipText` have no icon slot anywhere in the shared
  tooltip system (checked; none exists for any vanilla feature either) — the objective
  row's own icon already covers "which faction, at a glance" without it.
- The Tactical HUD reinforcement countdown badge (Phoenix base defense only) copies the
  sibling `ChaosFasterTurn` mod's own badge pattern (a plain `UI.Text` parented under the
  End Turn button's HUD Canvas, rebuilt per level) rather than inventing a new one.
- Player-facing faction names route through the live `GeoFaction.Name`/`GeoSubFaction.Name`
  (real, already-localized `LocalizedTextBind`s) rather than `PPFactionDef.GetName()` (an
  internal identifier, e.g. `"NewJericho"`) or a hardcoded English literal.
  `ModalCommonResources.GetFactionInfo(...).Name` is used only in the one spot already tied
  to an open UI modal (the mission-brief row) — it isn't resolvable from arbitrary code.

### Removed or reverted
- A Synedrion-specific augmented-pool variant (Purists templates filtered to
  "Synedrion-style" body armor by name) — no template's gear actually matched in testing.
- A Pandoran escort accompanying hostile debug activities
  (`ActivityPandoranEscortMinPercent`/`MaxPercent`) — removed per explicit request; the
  debug-activity squad itself is unaffected.
- The `WildBeast`/`AnuExalted` debug-activity kinds (asset-only, no live
  `GeoFaction`/`GeoSubFaction` backing, roster read straight off a `GeoFactionDef` asset)
  — removed per explicit request. `Bandits` (`GeoSubFactionDef`-backed, added alongside
  them) was kept.
- The `chaos_factions_list` diagnostic command / `FactionInventoryLogger` — removed once
  it had done its one job: confirming Pure/Forsaken's real in-game short names are
  `NJ_Purists`/`AN_FallenOnes` (with `"purists"`/`"fallenones"` as the resolvable short
  names), not the originally-guessed `"pure"`/`"forsaken"`, which never matched anything.
- `DebugSpawnTechniciansOnly`, a temporary diagnostic override used to isolate the
  Technician-invisibility bug above — removed once its job was done.

### Misc gotchas
- `GeoAbilityDef.Cost` must never be `null`, even for the Relationship payment method
  which has no resource cost — `GeoAbility.Activate`/`GetTargetDisabledState` both call
  `GetResourceCost(target).Count` unconditionally. The Relationship variant uses an empty
  `ResourcePack`, not `null`; the actual relationship deduction happens by hand via
  `FactionDiplomacy.ModifyDiplomacy`, outside the resource-cost pipeline.
- `AskForHelpAbilityFactory` caches one ability **per (mission, payment method)** and
  explicitly `Destroy`s them on mission end — `ScriptableObject.CreateInstance` objects
  are native-backed and don't get garbage-collected like plain C# objects, so skipping
  this would leak a Def pair per menu-open for the rest of the session.
- Paying with Relationship correctly updates the underlying value and fires
  `OnDiplomacyChanged` — confirmed via source, not a data bug. A specific
  "top menu" relationship display was reported as staying stale until navigating away and
  back; the one fix tried (force-refreshing `UIModuleDiplomacySection`) didn't help and
  was reverted. Still unresolved, purely cosmetic — get a precise description of which
  on-screen element before trying another fix, rather than guessing a second candidate.
- A faction's help entry is hidden from the context menu entirely (not shown disabled)
  once relationship drops below `RelationshipThreshold` — reuses the same
  `AllyRequestTracker.CanRequestHelp` gate `GetDisabledStateInternal` already checked, so
  the disabled-state machinery stays in place as harmless defense-in-depth even though
  it's no longer reachable via this path in practice. To test this without waiting for
  relationship to naturally cross the threshold, lower `RelationshipThreshold` temporarily
  in Mod Settings.

## Harmony patch conventions
- Follow sibling mods' pattern: a dedicated `ChaosFactionPatches.cs`, namespace
  `ChaosFaction`, applied via `harmony.PatchAll()` in `OnModEnabled`.
- For a new `.cs` file, add a `<Compile Include="NewFile.cs" />` entry in
  `ChaosFaction.csproj`; new classes in existing files need no csproj changes.
