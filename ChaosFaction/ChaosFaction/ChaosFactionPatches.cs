using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Entities.Missions;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.Levels.Objectives;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;
using UnityEngine.UI;

namespace ChaosFaction
{
	/// <summary>
	/// Shared light-blue tint used everywhere this mod marks something as "informational,
	/// not actually disabled/unavailable" — the "Already Requested Help" context-menu
	/// entry, and now the mission-brief helper row below. One shared constant so every
	/// place this mod uses "our" blue stays in sync instead of drifting apart.
	/// </summary>
	internal static class ChaosFactionColors
	{
		public static readonly Color LightBlue = new Color(0.55f, 0.8f, 1f);

		/// Debug/testing-only Pure/Forsaken hostile-activity entries use this instead —
		/// distinct from LightBlue so a glance at the context menu/mission brief tells
		/// "friendly help is on its way" (blue) apart from "a hostile incursion is nearby"
		/// (red) at a glance.
		public static readonly Color HostileRed = new Color(0f, 1f, 1f);

		/// Debug/testing-only "here's this site's internal mission info" entry — neutral,
		/// distinct from both the friendly (blue) and hostile (red) status tints, since it's
		/// neither: purely a lookup aid for mod development, not describing an active
		/// request/threat.
		public static readonly Color InfoGray = new Color(0.75f, 0.75f, 0.75f);
	}

	/// <summary>
	/// Adds "Ask X for Help" actions (one per eligible helper faction — New Jericho, Anu,
	/// Synedrion) into the site's own left-click context menu (the one showing "Move"/
	/// "Enter Base"/etc.) instead of a standalone popup — the menu is fed by a plain
	/// List&lt;GeoAbility&gt; built fresh on every click (UIStateNothingSelected/
	/// UIStateVehicleSelected.GetContextualAbilities), so a postfix can just append our
	/// own GeoAbility instances to it. No UI-cloning needed.
	/// </summary>
	internal static class ContextualAbilitiesPatchHelper
	{
		/// <summary>
		/// Mission types eligible for "ask for help": a Phoenix base under attack
		/// (GeoPhoenixBaseDefenseMission — player owns the site, defending), a Pandoran
		/// base the player is about to assault (GeoAlienBaseMission — the *aliens* own
		/// the site here, so playerFaction below can't be read from mission.Site.Owner
		/// and must come from GeoLevelController.PhoenixFaction instead; individually
		/// toggleable per base tier — see IsEligibleAlienBaseType), the player's own
		/// Phoenix base after it's been infested and needs to be assaulted to clean out
		/// (GeoPhoenixBaseInfestationMission), or an infested Haven that needs the same
		/// treatment (GeoInfestationCleanseMission — distinct class, since it's a Haven
		/// site owned by New Jericho/Anu/Synedrion rather than the player's own Phoenix
		/// base; playerFaction below still comes from GeoLevelController.PhoenixFaction,
		/// same as the alien-base case, not mission.Site.Owner). Haven *defense*
		/// (GeoHavenDefenseMission) stays out of scope the same way it always has — this
		/// only brings the infestation-cleanse follow-up mission into scope, not the
		/// initial attack.
		/// </summary>
		/// Internal (not private) so HavenDefenseRewardTracker (ChaosFactionState.cs) can
		/// reuse the exact same eligibility rules when picking a site for a free help grant
		/// after a Haven defense — the same mission types/config toggles apply there as
		/// they do for the player's own manual "Ask for Help" requests. This is also the
		/// gate `TryAddDebugActivityAbilities`/`AutoDebugActivitySpawner` check for
		/// `chaos_factions_debug` activities — via `IsEligibleMissionForActivity` below,
		/// not this method directly, since Phoenix Base Defense is deliberately excluded
		/// from activities specifically (explicit request: "only help request is allowed
		/// for this mission type") while still fully governed here for Ask-for-Help. For
		/// every *other* mission type, each `EnableFor*` toggle below (renamed from
		/// `EnableHelpFor*` — explicit request, since "Help" implied Ask-for-Help only)
		/// still controls both features for that mission type at once: false disables
		/// both Ask-for-Help *and* debug activities for it, not just one.
		///
		/// Two generic guards apply before any type-specific check, since both are
		/// "already resolved/inaccessible" conditions that can leave `GeoSite.ActiveMission`
		/// non-null even though the mission itself is no longer genuinely available:
		/// `GeoMission.cs:531` (`ApplyMissionOutcome`) only clears
		/// `Site.ActiveMission` `if (missionOutcomeByState.ClearMission && ...)` — i.e. NOT
		/// unconditionally on completion, so a finished mission can keep sitting in
		/// `ActiveMission` depending on its outcome/`ClearMission` Def flag.
		/// `GeoMission.IsCompleted` (set `true` by both `Complete(TacMissionResult)` — the
		/// player-played-out path — and `GeoUpdateableMission.TriggerMissionEnd()` — the
		/// auto-resolved path, e.g. a Haven defense/infestation that ticks to a conclusion
		/// off-screen) is the one signal that's reliably `true` the instant a mission is
		/// actually done regardless of which path or `ClearMission` outcome applies, so it's
		/// the correct check here, not `ActiveMission == null` alone. Separately,
		/// `GeoAlienBaseMission.cs:72` only clears `ActiveMission` `if (... && Site.State ==
		/// GeoSiteState.Functioning)`, implying a non-Functioning site (destroyed/abandoned)
		/// can *also* leave a stale `ActiveMission` behind — `site.State ==
		/// GeoSiteState.Functioning` guards against offering help for a site that's already
		/// gone. `IsRunnable` (`GeoMission`, `virtual`, base returns `true`) was checked and
		/// ruled out as a substitute for either of these — confirmed via source
		/// that *no* mission subclass in the game overrides it, so it's always `true`
		/// regardless of mission state and isn't a real signal to depend on.
		internal static bool IsEligibleMission(GeoMission mission, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			if (!PassesGenericGuards(mission))
				return false;
			if (mission is GeoPhoenixBaseDefenseMission)
				return cfg.EnableForPhoenixBaseDefense;
			if (mission is GeoAlienBaseMission alienBaseMission)
				return IsEligibleAlienBaseType(alienBaseMission, geoLevel, cfg);
			if (mission is GeoPhoenixBaseInfestationMission)
				return cfg.EnableForPhoenixBaseInfestation;
			if (mission is GeoInfestationCleanseMission)
				return cfg.EnableForHavenInfestation;
			return false;
		}

		/// <summary>
		/// The two "already resolved/inaccessible" guards `IsEligibleMission` itself
		/// applies before its type-specific checks — extracted so `IsEligibleMissionForAskForHelp`
		/// (below) can apply the exact same guards to its own broader, non-type-specific
		/// Pandoran-enemy check without duplicating them or accidentally skipping them.
		/// </summary>
		private static bool PassesGenericGuards(GeoMission mission)
		{
			if (mission == null || mission.IsCompleted)
				return false;
			if (mission.Site == null || mission.Site.State != GeoSiteState.Functioning)
				return false;
			return true;
		}

		/// <summary>
		/// Ask-for-Help's own eligibility — a strict superset of `IsEligibleMission`,
		/// originally added as "Add EnableForAnyPandoranThreatMission, this should allow
		/// to request for help for any site mission where enemy is pandoran" — since
		/// renamed to `EnableRequestForHelpForAnyMissionType` and broadened per a later
		/// explicit follow-up request: "when true it should allow to ask for help, but
		/// only if in that mission the enemy is not New Jericho, Synedrion, Anu (which
		/// means allowed against pandorans, pure, forsaken, bandits etc). Request for
		/// help against enemy as New Jericho, Synedrion, Anu, should be also allowed but
		/// only if corresponding faction is at war against the faction which is enemy in
		/// that mission." That second half — which *specific* helper faction is actually
		/// askable — can't be decided mission-wide (it depends on which of the three
		/// candidates is being considered), so it's handled separately by
		/// `IsHelperFactionEligibleAgainstEnemy` below, called once per candidate inside
		/// `TryAddAskForHelpAbility`'s own loop; this method only decides whether the
		/// mission itself is in scope for Ask-for-Help at all. Only
		/// `TryAddAskForHelpAbility` and `HavenDefenseRewardTracker.FindClosestEligibleSite`
		/// (the free-help-after-Haven-defense reward, which explicitly reuses "the same
		/// eligibility rules... a manual request goes through") call this — debug
		/// activities (`TryAddDebugActivityAbilities`/`AutoDebugActivitySpawner`) call
		/// `IsEligibleMissionForActivity` instead (itself layered on `IsEligibleMission`,
		/// not this method), so the toggle never opens up activities for a mission type
		/// this mod doesn't otherwise recognize. Already-known types (the 4
		/// `IsEligibleMission` classes) are unaffected either way — this only ever adds
		/// eligibility for mission types `IsEligibleMission` doesn't recognize at all,
		/// gated on `EnableRequestForHelpForAnyMissionType` and on the mission actually
		/// resolving *some* enemy faction at all (`GetEnemyFaction()`, a public virtual
		/// `GeoMission` method, works generically for any mission type via `MissionDef.
		/// ParticipantsRelations`) — which faction that enemy is (Pandoran vs. one of the
		/// three main candidates) no longer matters here, only in the per-candidate check.
		///
		/// Also requires `mission.Site.GetInspected(playerFaction)` — bug fix, reported
		/// in-game: with this toggle on, Ask-for-Help was being offered for a site the
		/// player hadn't actually investigated yet. A first attempt checked `GetVisible`
		/// instead, but the player reported that still wasn't enough — "site was visible,
		/// but it was like unknown". Confirmed via source (`GeoSite.cs`):
		/// `Visible` and `Inspected` are two separate per-faction flags — `GetVisible`
		/// only means the site's marker/type shows on the map at all (e.g. within sensor
		/// range), while `GetInspected` is the real "the player has actually identified
		/// this site" signal — `RevealSite` sets both together, but a site can be visible
		/// without being inspected (a marker the player has spotted but not yet scanned/
		/// looked at up close — this is exactly what "like unknown" meant). Confirmed
		/// `GetInspected` is the correct gate by checking what vanilla itself uses to
		/// decide whether a site's own actions are available at all —
		/// `LaunchMissionAbility.GetTargetDisabledState`/`EnterBaseAbility`/
		/// `ExploreSiteAbility` all check `GetInspected`, not `GetVisible`, for exactly
		/// this "is this site actually known well enough to act on" purpose. The 4
		/// mission types `IsEligibleMission` itself recognizes don't need this same check
		/// — reaching any of them already implies the site is inspected (you can't open a
		/// context menu, or get a mission-brief, for a site you haven't investigated) —
		/// but this broader "any mission type" branch has no such guarantee, since it
		/// doesn't care what mission type it's looking at.
		/// </summary>
		internal static bool IsEligibleMissionForAskForHelp(GeoMission mission, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			if (IsEligibleMission(mission, geoLevel, cfg))
				return true;
			if (cfg == null || !cfg.EnableRequestForHelpForAnyMissionType)
				return false;
			if (!PassesGenericGuards(mission))
				return false;
			GeoFaction playerFaction = geoLevel?.PhoenixFaction;
			if (playerFaction == null || mission.Site == null || !mission.Site.GetInspected(playerFaction))
				return false;
			return mission.GetEnemyFaction() != null;
		}

		/// <summary>
		/// Per-candidate-helper-faction half of the `EnableRequestForHelpForAnyMissionType`
		/// request (see `IsEligibleMissionForAskForHelp`'s own doc comment for the full
		/// quote) — decides whether `helperFaction` specifically is askable for this
		/// mission, as opposed to whether the mission is in scope at all. For the 4
		/// mission types `IsEligibleMission` itself recognizes, this always returns
		/// `true` — that existing behavior (only the attacker itself is excluded from
		/// being asked, via `TryAddAskForHelpAbility`'s own separate `enemyFaction ==
		/// helperFaction` check) is deliberately untouched, since the request was scoped
		/// to the broadened toggle specifically, not to Ask-for-Help in general. For any
		/// other mission type (only reachable when `EnableRequestForHelpForAnyMissionType`
		/// is on, since that's the only way `IsEligibleMissionForAskForHelp` accepts one),
		/// if the mission's enemy resolves to New Jericho/Anu/Synedrion,
		/// `helperFaction` is only eligible if it is itself at war with that enemy —
		/// `PartyDiplomacyState.Conflict` or worse, the same real vanilla "genuinely
		/// hostile" diplomacy tier `DebugActivityTracker.ResolveIsHostile` already
		/// established (confirmed via source to be the exact condition
		/// `VehicleFactionController.PlanCurrentSiteAttack` itself checks before a
		/// faction's own vehicles will actually attack another faction), not a guessed
		/// threshold. Against any other enemy (Pandoran, Pure, Forsaken, Bandits, etc.)
		/// every candidate stays unrestricted, same as before.
		/// </summary>
		internal static bool IsHelperFactionEligibleAgainstEnemy(GeoFaction helperFaction, IGeoFactionMissionParticipant enemyFaction, GeoMission mission, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			if (IsEligibleMission(mission, geoLevel, cfg))
				return true;

			// Haven Defense's own rule is the OPPOSITE shape from the enemy-based rule below
			// — explicit follow-up request: "So for haven defense request for help can be
			// allowed if both factions are not in war against each other." The relevant
			// relationship for a Haven attack isn't helper-vs-attacker (mission.GetEnemyFaction()
			// — typically Pandoran/alien, and often irrelevant to whether a major faction would
			// actually send help) but helper-vs-the-Haven's-OWNER (mission.Site.Owner — the
			// faction actually being defended, e.g. Anu): a candidate at war with the very
			// faction it would be defending obviously wouldn't send help, regardless of who's
			// attacking. See IsHelperFactionEligibleForHavenDefense's own doc comment for the
			// full reasoning, including the "can't offer a faction help defending its own
			// Haven" exclusion. Checked here (not just in TryAddAskForHelpAbility's own loop)
			// so HavenDefenseRewardTracker.FindClosestEligibleSite - which reuses this exact
			// method for the free-help-after-Haven-defense reward, passing havenOwner in
			// helperFaction's role - gets the same correction for free.
			if (mission is GeoHavenDefenseMission)
				return IsHelperFactionEligibleForHavenDefense(helperFaction, mission);

			GeoFaction enemyMainFaction = ResolveMainCandidateFaction(enemyFaction, geoLevel);
			if (enemyMainFaction == null)
				return true;
			return IsFactionAtWarWith(helperFaction, enemyMainFaction);
		}

		/// <summary>
		/// Haven Defense's own per-candidate rule, deliberately the OPPOSITE shape from
		/// IsHelperFactionEligibleAgainstEnemy's "must BE at war with the enemy" rule for
		/// every other broadened mission type — explicit user clarification: "So for haven
		/// defense request for help can be allowed if both factions are not in war against
		/// each other." Checked against the Haven's own OWNER (mission.Site.Owner — the
		/// faction actually being defended, e.g. Anu — confirmed via the
		/// GeoHavenDefenseMission constructor/OnUpdateGeoscapeMissionState that Site.Owner is
		/// always the real resident faction, not null or the attacker, for the whole life of
		/// the mission), not mission.GetEnemyFaction() (the attacker — typically Pandoran/
		/// alien, confirmed via the GeoMission.GetEnemyFaction resolving through
		/// MissionDef.ParticipantsRelations to whichever participant is the Intruder — though
		/// inter-faction Haven attacks are also possible, per the
		/// UpdateGeoscapeMissionState's own "faction destroys another faction's Haven"
		/// diplomacy-penalty branch). A helper at war with the Haven's owner obviously
		/// wouldn't send help to defend it, regardless of who's actually attacking.
		///
		/// Also excludes the owner itself from ever being offered as its own helper —
		/// explicit clarification: "so if haven defense mission is for Synedrion, then
		/// Synedrion request for help must not be available" — a case neither of
		/// TryAddAskForHelpAbility's existing playerFaction/enemyFaction exclusions cover
		/// (the owner is neither the player nor, in the common Pandoran-attacker case, the
		/// enemy).
		/// </summary>
		internal static bool IsHelperFactionEligibleForHavenDefense(GeoFaction helperFaction, GeoMission mission)
		{
			GeoFaction owner = mission?.Site?.Owner;
			if (owner == null)
				return true;
			if (owner == helperFaction)
				return false;
			return !IsFactionAtWarWith(helperFaction, owner);
		}

		/// <summary>
		/// Resolves an `IGeoFactionMissionParticipant` enemy down to the matching live
		/// `GeoFaction`, but only if it's one of the three main Ask-for-Help candidates
		/// (New Jericho/Synedrion/Anu) — null for anything else (Pandoran, a sub-faction
		/// like Pure/Forsaken/Bandits, or an unresolvable participant), which is exactly
		/// the "not one of the three" case `IsHelperFactionEligibleAgainstEnemy` treats as
		/// unrestricted.
		/// </summary>
		private static GeoFaction ResolveMainCandidateFaction(IGeoFactionMissionParticipant enemyFaction, GeoLevelController geoLevel)
		{
			if (enemyFaction == null || geoLevel == null)
				return null;
			if (geoLevel.NewJerichoFaction != null && enemyFaction.PPFactionDef == geoLevel.NewJerichoFaction.PPFactionDef)
				return geoLevel.NewJerichoFaction;
			if (geoLevel.SynedrionFaction != null && enemyFaction.PPFactionDef == geoLevel.SynedrionFaction.PPFactionDef)
				return geoLevel.SynedrionFaction;
			if (geoLevel.AnuFaction != null && enemyFaction.PPFactionDef == geoLevel.AnuFaction.PPFactionDef)
				return geoLevel.AnuFaction;
			return null;
		}

		/// <summary>
		/// "At war" per `DebugActivityTracker.ResolveIsHostile`'s own established
		/// reasoning (`ChaosFactionDebug.cs`) — `PartyDiplomacyState.Conflict` or worse,
		/// read via `GetDiplomacyState`, not the separate `FactionDiplomacyState.AtWar`
		/// bool (confirmed dead/never read by any combat code) — the same real vanilla
		/// condition that gates whether one faction's vehicles will actually attack
		/// another (`VehicleFactionController.PlanCurrentSiteAttack`), not a guessed
		/// threshold.
		///
		/// Checked in BOTH directions — explicit follow-up request ("do you check for
		/// ... both sides relationship if in war?"). `PartyDiplomacy` stores each
		/// faction's own opinion of the other independently (`faction.Diplomacy.
		/// GetDiplomacyState(other)` is only ever `faction`'s own recorded view), and per
		/// the `FactionDiplomacy.ShiftToHostileDiplomacy` — which explicitly
		/// compares `base.GetDiplomacy(other)` against `other.Diplomacy.GetDiplomacy(this)`
		/// and nudges them a little closer together each tick rather than keeping them in
		/// lockstep — the two sides' recorded relations CAN genuinely diverge at any given
		/// moment, not just during some unreachable edge case. So a war registered from
		/// only one side (e.g. an event/reward that penalizes just one faction's own
		/// relation) is now caught here too, instead of silently passing this check
		/// because the mirrored record on the other side hasn't caught up yet. `None`
		/// (the enum's `-1` sentinel, "no relation data") is explicitly excluded on each
		/// side so it isn't misread as "worse than Conflict" by a bare `&lt;=`.
		///
		/// Re-evaluated fresh on every context-menu build (never cached) — so if
		/// relations on either side later climb back out of Conflict, the candidate
		/// becomes askable again on the very next click, with nothing else to
		/// invalidate.
		/// </summary>
		private static bool IsFactionAtWarWith(GeoFaction faction, GeoFaction other)
		{
			if (faction == null || other == null)
				return false;
			return IsDirectionallyAtWarWith(faction, other) || IsDirectionallyAtWarWith(other, faction);
		}

		private static bool IsDirectionallyAtWarWith(GeoFaction faction, GeoFaction other)
		{
			PartyDiplomacyState state = faction.Diplomacy.GetDiplomacyState(other);
			return state != PartyDiplomacyState.None && state <= PartyDiplomacyState.Conflict;
		}

		/// <summary>
		/// Debug/hostile-allied activities' own eligibility — a strict *subset* of
		/// IsEligibleMission (the mirror image of IsEligibleMissionForAskForHelp's strict
		/// superset above), explicit request: "be sure that this activity
		/// ActivitySpawnChancePercentPerDay never works for phoenix base defense mission,
		/// only help request is allowed for this mission type." Phoenix Base Defense is
		/// the one mission type of the four IsEligibleMission recognizes where the player
		/// is defending their own base against an already-hostile attacker — a
		/// hostile/allied third-party activity showing up mid-defense doesn't fit the way
		/// it does for the other three (assaulting/cleansing something that's already the
		/// enemy), so it's excluded here regardless of EnableForPhoenixBaseDefense (that
		/// toggle still fully controls Ask-for-Help eligibility for this mission type,
		/// completely unaffected by this check). Both activity call sites —
		/// TryAddDebugActivityAbilities (the manual "ChaosFactionsDebugOptions" button)
		/// and AutoDebugActivitySpawner.IsEligibleForAutoSpawn (ActivitySpawnChancePercentPerDay
		/// itself) — now call this instead of IsEligibleMission directly, so neither can
		/// ever offer/spawn an activity for this one mission type; every other mission
		/// type/config toggle IsEligibleMission already governs is completely unaffected.
		/// </summary>
		internal static bool IsEligibleMissionForActivity(GeoMission mission, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			if (mission is GeoPhoenixBaseDefenseMission)
				return false;
			return IsEligibleMission(mission, geoLevel, cfg);
		}

		/// <summary>
		/// Alien bases come in four tiers (SharedData.NestBaseTypeDef/LairBaseTypeDef/
		/// CitadelBaseTypeDef/PalaceBaseTypeDef — Nest is the smallest/earliest, Palace the
		/// largest/final), each independently toggleable. Any base type that doesn't match
		/// one of the four known Defs (e.g. a future DLC/mod-added tier) defaults to
		/// eligible rather than silently never offering help for it — ResolveMissionType
		/// returning null for that same case is what makes this "default eligible" rather
		/// than "default blocked".
		/// </summary>
		private static bool IsEligibleAlienBaseType(GeoAlienBaseMission mission, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			HelpMissionType? missionType = ResolveAlienBaseMissionType(mission, geoLevel);
			switch (missionType)
			{
				case HelpMissionType.PandoranNestAssault: return cfg.EnableForPandoranNestAssault;
				case HelpMissionType.PandoranLairAssault: return cfg.EnableForPandoranLairAssault;
				case HelpMissionType.PandoranCitadelAssault: return cfg.EnableForPandoranCitadelAssault;
				case HelpMissionType.PandoranPalaceAssault: return cfg.EnableForPandoranPalaceAssault;
				default: return true;
			}
		}

		/// <summary>
		/// Maps any of this mod's four eligible mission classes (see IsEligibleMission) to
		/// its matching HelpMissionType — the one shared classification both the
		/// EnableFor*/per-mission-type-settings systems key off of. Returns null for a
		/// mission type this mod doesn't recognize at all, or (for GeoAlienBaseMission) an
		/// alien base tier that doesn't match any of the four known Defs — callers decide
		/// their own fallback for null (IsEligibleAlienBaseType defaults to eligible;
		/// MissionHelpSettingsResolver/FactionHelpOfferTracker default to the
		/// PhoenixBaseDefense settings block) rather than this method guessing one itself.
		/// </summary>
		internal static HelpMissionType? ResolveMissionType(GeoMission mission, GeoLevelController geoLevel)
		{
			if (mission is GeoPhoenixBaseDefenseMission)
				return HelpMissionType.PhoenixBaseDefense;
			if (mission is GeoAlienBaseMission alienBaseMission)
				return ResolveAlienBaseMissionType(alienBaseMission, geoLevel);
			if (mission is GeoPhoenixBaseInfestationMission)
				return HelpMissionType.PhoenixBaseInfestation;
			if (mission is GeoInfestationCleanseMission)
				return HelpMissionType.HavenInfestation;
			return null;
		}

		private static HelpMissionType? ResolveAlienBaseMissionType(GeoAlienBaseMission mission, GeoLevelController geoLevel)
		{
			GeoAlienBaseTypeDef baseType = mission.AlienBase?.AlienBaseTypeDef;
			SharedData sharedData = geoLevel?.SharedData;
			if (baseType == null || sharedData == null)
				return null;
			if (baseType == sharedData.NestBaseTypeDef)
				return HelpMissionType.PandoranNestAssault;
			if (baseType == sharedData.LairBaseTypeDef)
				return HelpMissionType.PandoranLairAssault;
			if (baseType == sharedData.CitadelBaseTypeDef)
				return HelpMissionType.PandoranCitadelAssault;
			if (baseType == sharedData.PalaceBaseTypeDef)
				return HelpMissionType.PandoranPalaceAssault;
			return null;
		}

		/// <summary>
		/// The game's own real threat-level signal for a mission — the same one shown in
		/// the mission-brief popup's own Threat Level row (`CommonMissionDataController.
		/// SetData` calls this exact method, confirmed via source: `DifficultyThreatLevel
		/// threatLevel = mission.GetMissionThreatLevel(mission.MissionDef);`). Deliberately
		/// calls `GeoMission.GetMissionThreatLevel(MissionDef)` — a public `virtual` method,
		/// resolvable at any time the mission exists, including before it's ever launched —
		/// rather than reading `GeoMission.ThreatLevel` (a property only ever *populated* by
		/// `GenerateMissionThreatLevel`, called from `Launch()`; reading it before launch
		/// would just return the enum's unset default). This is what makes it possible to
		/// resolve threat level from the Geoscape context-menu/offer-pricing code, not only
		/// from the mission-brief popup itself. Falls back to `MissionDef.DifficultyThreatLevel`
		/// (the mission type's own static baseline, same fallback `GetMissionThreatLevel`
		/// itself uses when no `ThreatLevelProvider` is configured) if the call throws, and
		/// to `DifficultyThreatLevel.Low` if even the mission/MissionDef itself is
		/// unavailable — non-nullable return, since a real `GeoMission` should always be
		/// able to resolve to *some* threat level, unlike `ResolveMissionType` (which can
		/// legitimately fail to classify a mission this mod doesn't support).
		/// </summary>
		internal static DifficultyThreatLevel ResolveThreatLevel(GeoMission mission)
		{
			if (mission?.MissionDef == null)
				return DifficultyThreatLevel.Low;
			try
			{
				return mission.GetMissionThreatLevel(mission.MissionDef);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to resolve mission threat level; falling back to the mission type's own static DifficultyThreatLevel: " + ex);
				return mission.MissionDef.DifficultyThreatLevel;
			}
		}

		public static void TryAddAskForHelpAbility(GeoSite site, ref List<GeoAbility> __result)
		{
			try
			{
				GeoMission mission = site.ActiveMission;
				if (mission == null)
					return;

				ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
				GeoLevelController geoLevel = site.GeoLevel;
				if (cfg == null || !IsEligibleMissionForAskForHelp(mission, geoLevel, cfg))
					return;

				GeoFaction playerFaction = geoLevel?.PhoenixFaction;
				if (playerFaction == null)
					return;

				// Mutual exclusivity with a debug Pure/Forsaken hostile activity (explicit
				// request): if one is active on this site, TryAddDebugActivityAbilities
				// below owns showing its own status entry — don't also touch the normal
				// Ask-for-Help menu for this site at all.
				if (DebugActivityTracker.ActiveActivities.ContainsKey(site))
					return;

				// Once this site's one-favor-per-mission slot is used, show exactly one
				// informational entry ("Already Requested Help (X)") instead of all three
				// per-faction abilities (which would otherwise all just show individually
				// disabled with the same "already requested" reason — redundant and, with
				// other native context-menu entries alongside them, needlessly close to the
				// button-pool cap discussed in AskForHelpAbility's doc comment). Keyed by
				// site rather than mission so this (and the request itself) survives a
				// save/load — see AllyRequestTracker.ActiveRequests's own doc comment.
				if (AllyRequestTracker.ActiveRequests.TryGetValue(site, out PendingHelpRequest activeRequest))
				{
					GeoFaction requestedFrom = activeRequest.HelperKind.ToFaction(geoLevel);
					if (requestedFrom == null)
						return;
					double remainingHours = activeRequest.ExpiresAtHours - AllyRequestTracker.CurrentHours(geoLevel);
					AlreadyRequestedHelpAbility statusAbility = AskForHelpAbilityFactory.GetOrCreateAlreadyRequested(mission, requestedFrom, remainingHours);
					if (__result == null)
						__result = new List<GeoAbility>();
					__result.Add(statusAbility);
					return;
				}

				// Up to three potential helper factions — New Jericho, Anu, Synedrion — each
				// gets at most one context-menu entry (not one per payment method: see
				// AskForHelpAbility's doc comment for why that would risk overflowing the
				// site menu's fixed-size button pool). GeoFaction? candidates that end up
				// null (DLC/game-mode dependent), the player's own faction, or whichever
				// faction is actually attacking this site are all skipped.
				GeoFaction[] candidateHelperFactions =
				{
					geoLevel?.NewJerichoFaction,
					geoLevel?.AnuFaction,
					geoLevel?.SynedrionFaction,
				};
				IGeoFactionMissionParticipant enemyFaction = mission.GetEnemyFaction();
				foreach (GeoFaction helperFaction in candidateHelperFactions)
				{
					if (helperFaction == null || helperFaction == playerFaction)
						continue;
					// A candidate faction can itself be the one attacking the site (not just
					// Pandorans) — "ask the attacker to help fight themselves" makes no sense,
					// and on the Tactical side it would resolve to the SAME TacticalFaction as
					// the Intruder, corrupting its relations/participant kind. Refuse up front.
					if (enemyFaction?.PPFactionDef == helperFaction.PPFactionDef)
						continue;

					// EnableRequestForHelpForAnyMissionType's own per-candidate rule: against
					// an enemy that's itself New Jericho/Anu/Synedrion, only a helper faction
					// actually at war with that enemy is askable. No-op (always true) for the
					// 4 mission types IsEligibleMission already recognizes — see
					// IsHelperFactionEligibleAgainstEnemy's own doc comment.
					if (!IsHelperFactionEligibleAgainstEnemy(helperFaction, enemyFaction, mission, geoLevel, cfg))
						continue;

					// Explicit request: hide the entry entirely when relationship is below
					// RelationshipThreshold, instead of showing it disabled with a "Requirements
					// not met" reason. Reuses AllyRequestTracker.CanRequestHelp — the exact same
					// gate GetDisabledStateInternal already checks — rather than re-reading
					// Diplomacy directly, so there's one source of truth for "is this faction
					// actually askable right now." Its other two conditions (site not already
					// requested, no active debug activity) are both already guaranteed true by
					// this point in the method (handled by the early-return checks above), so in
					// practice this only ever filters on relationship here.
					if (!AllyRequestTracker.CanRequestHelp(mission, playerFaction, helperFaction, cfg))
						continue;

					FactionHelpOffer offer = FactionHelpOfferTracker.GetOffer(geoLevel, helperFaction, ResolveMissionType(mission, geoLevel), ResolveThreatLevel(mission), cfg);
					if (offer == null)
						continue;

					AskForHelpAbility ability = AskForHelpAbilityFactory.GetOrCreate(mission, playerFaction, helperFaction, cfg, offer);
					if (__result == null)
						__result = new List<GeoAbility>();
					__result.Add(ability);
				}
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to add Ask for Help ability to context menu: " + ex);
			}
		}

		/// <summary>
		/// Debug/testing only (chaos_factions_debug 1 — ChaosFactionDebugFlag.Enabled): a
		/// single "ChaosFactionsDebugOptions" entry on every mission type this mod already
		/// supports (same IsEligibleMission gate as Ask-for-Help). Originally one "Add X
		/// Activity" entry per DebugActivityKindExtensions.All (5, then 8 once Bandits/
		/// WildBeast/AnuExalted were added) — growing to 8 actually crashed the context
		/// menu in-game (ArgumentOutOfRangeException in UIModuleSiteContextualMenu.
		/// SetMenuItems, which indexes into a fixed-size button pool with no bounds check —
		/// see CLAUDE.md). Collapsed to this single entry instead, which opens a text-input
		/// popup (DebugActivityPickerAbility) listing every kind to choose from — the
		/// per-site context-menu button count this mod adds is now fixed at 1 regardless of
		/// how many debug activity kinds exist, so it can never hit that cap again no matter
		/// how many more kinds get added later. Mutually exclusive with a normal help
		/// request on the same site both ways — see TryAddAskForHelpAbility's own matching
		/// check — and with itself: once a debug activity is active on a site, this shows
		/// the disabled status entry instead of the picker entry, mirroring the "Already
		/// Requested Help" pattern.
		/// </summary>
		public static void TryAddDebugActivityAbilities(GeoSite site, ref List<GeoAbility> __result)
		{
			if (!ChaosFactionDebugFlag.Enabled)
				return;
			try
			{
				GeoMission mission = site.ActiveMission;
				if (mission == null)
					return;

				ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
				GeoLevelController geoLevel = site.GeoLevel;
				if (cfg == null || !IsEligibleMissionForActivity(mission, geoLevel, cfg))
					return;

				// A real Ask-for-Help request already active on this site takes priority —
				// don't offer or show anything debug-related here (mutual exclusivity).
				if (AllyRequestTracker.ActiveRequests.ContainsKey(site))
					return;

				if (DebugActivityTracker.ActiveActivities.TryGetValue(site, out PendingDebugActivity activeActivity))
				{
					// Reported in-game: reveal was firing from here, i.e. the moment the
					// player left-clicked the site at all — GetContextualAbilities fires on
					// any left-click, aircraft/squad presence not required, so this was
					// revealing activities the player hadn't actually "come to" in any
					// meaningful sense. RevealIfNeeded is no longer called from this
					// call site — Patch_CommonMissionDataController_SetData (the
					// mission-brief popup, only reachable once an aircraft/squad is actually
					// being sent to launch the mission) is the sole remaining trigger, a
					// genuine "come to mission and open it." This status entry's own
					// Kind/IsHostile display is unaffected either way — it always showed the
					// true values regardless of reveal state (see GetOrCreateStatus), reveal
					// only ever controlled the *other* display surfaces (marker/objective).
					double remainingHours = activeActivity.ExpiresAtHours - AllyRequestTracker.CurrentHours(geoLevel);
					GeoAbility statusAbility = DebugActivityAbilityFactory.GetOrCreateStatus(mission, activeActivity.Kind, activeActivity.IsHostile, remainingHours);
					if (__result == null)
						__result = new List<GeoAbility>();
					__result.Add(statusAbility);
					return;
				}

				GeoAbility pickerAbility = DebugActivityAbilityFactory.GetOrCreatePicker(mission);
				if (__result == null)
					__result = new List<GeoAbility>();
				__result.Add(pickerAbility);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to add debug activity ability to context menu: " + ex);
			}
		}

		/// <summary>
		/// Debug/testing only: an always-disabled, informational entry showing the site's
		/// mission's own internal C# type name/MissionDef asset name — explicit request, so
		/// a mission type can be identified for later mod-development reference without
		/// needing to already be one of the four eligible types. Shown for any mission at
		/// all (not gated on IsEligibleMission, unlike everything else this class adds) —
		/// that's the whole point, identifying mission types this mod doesn't necessarily
		/// support (yet). Not mutually-exclusive with anything else, since it's a separate
		/// lookup aid, not a competing action.
		/// </summary>
		public static void TryAddDebugMissionInfoAbility(GeoSite site, ref List<GeoAbility> __result)
		{
			if (!ChaosFactionDebugFlag.Enabled)
				return;
			try
			{
				GeoMission mission = site.ActiveMission;
				if (mission == null)
					return;

				GeoAbility infoAbility = DebugActivityAbilityFactory.GetOrCreateMissionInfo(mission);
				if (__result == null)
					__result = new List<GeoAbility>();
				__result.Add(infoAbility);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to add debug mission-info entry to context menu: " + ex);
			}
		}
	}

	[HarmonyPatch(typeof(UIStateNothingSelected), "GetContextualAbilities")]
	internal static class Patch_UIStateNothingSelected_GetContextualAbilities
	{
		private static void Postfix(GeoSite site, ref List<GeoAbility> __result)
		{
			ContextualAbilitiesPatchHelper.TryAddAskForHelpAbility(site, ref __result);
			ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities(site, ref __result);
			ContextualAbilitiesPatchHelper.TryAddDebugMissionInfoAbility(site, ref __result);
		}
	}

	[HarmonyPatch(typeof(UIStateVehicleSelected), "GetContextualAbilities")]
	internal static class Patch_UIStateVehicleSelected_GetContextualAbilities
	{
		private static void Postfix(GeoSite site, ref List<GeoAbility> __result)
		{
			ContextualAbilitiesPatchHelper.TryAddAskForHelpAbility(site, ref __result);
			ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities(site, ref __result);
			ContextualAbilitiesPatchHelper.TryAddDebugMissionInfoAbility(site, ref __result);
		}
	}

	/// <summary>
	/// Gives AskForHelpAbility a specific disabled-reason tooltip (relationship too low)
	/// instead of the generic shared "Requirements not met" text. Patched via Harmony
	/// rather than a GeoAbilityView subclass because GetDisabledStateText isn't virtual, so
	/// a subclass override wouldn't be called through the GeoAbilityView-typed call site in
	/// UIModuleSiteContextualMenu. Doesn't need an "already requested" branch: once a
	/// mission is already requested, ContextualAbilitiesPatchHelper stops adding
	/// AskForHelpAbility to the menu entirely (AlreadyRequestedHelpAbility replaces it, see
	/// below), so this only ever runs for missions still open to a request.
	/// </summary>
	[HarmonyPatch]
	internal static class Patch_GeoAbilityView_GetDisabledStateText
	{
		private static MethodBase TargetMethod()
		{
			return AccessTools.Method(typeof(GeoAbilityView), "GetDisabledStateText", new[] { typeof(GeoAbilityTarget) });
		}

		private static void Postfix(GeoAbilityView __instance, ref string __result)
		{
			if (!(__instance.GeoAbility is AskForHelpAbility ability))
				return;

			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null)
				return;

			int relationship = ability.HelperFaction.Diplomacy.GetDiplomacy(ability.PlayerFaction);
			if (relationship < cfg.RelationshipThreshold)
			{
				string helperName = ability.HelperFaction.Name.Localize(null);
				__result = ChaosFactionLocalization.Get("KEY_CHAOSFACTION_RELATIONSHIP_TOO_LOW", cfg.RelationshipThreshold, helperName, relationship);
			}
		}
	}

	/// <summary>
	/// Recolors the "Already Requested Help (X)" menu button's text light blue instead of
	/// the native disabled-grey, since it isn't really "unavailable" — it's informational
	/// (matches the request; user-visible confirmation of who was asked). Postfixed on
	/// UIModuleSiteContextualMenu.SetMenuItems (the method that actually assigns
	/// ItemText.text/color into the pooled SiteContextualMenuItems each time the menu
	/// opens) rather than reading __instance's private _menuItems field via reflection —
	/// ButtonsHolder (public) holds the same SiteContextualMenuItem components as direct
	/// children, so walking those achieves the same thing without reflection.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleSiteContextualMenu), "SetMenuItems")]
	internal static class Patch_UIModuleSiteContextualMenu_SetMenuItems
	{
		private static void Postfix(UIModuleSiteContextualMenu __instance)
		{
			try
			{
				if (__instance.ButtonsHolder == null)
					return;
				foreach (SiteContextualMenuItem item in __instance.ButtonsHolder.GetComponentsInChildren<SiteContextualMenuItem>(true))
				{
					if (item.Ability is AlreadyRequestedHelpAbility statusAbility && item.ItemText != null)
						item.ItemText.color = statusAbility.StatusColor;
				}
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to recolor Already Requested Help menu entry: " + ex);
			}
		}
	}

	/// <summary>
	/// Defensive fix, not tied to any specific spawned actor: TacticalActor.GetAimPoints()
	/// (PhoenixPoint/Tactical/Entities/TacticalActor.cs:1790) yields itemSlot.GetAimPoint()
	/// for every health slot with an attached addon, without filtering out null results —
	/// unlike CharacterTargetDummy.GetAimPoints(), which does filter. Some New Jericho
	/// generic-roster weapon/body combinations that vanilla content never actually spawns
	/// (but which our random ally-squad picker can) hit this gap ("Slot X has no aim point
	/// for Y" in the log), and the resulting null Transform crashes every caller that does
	/// `.Select(aimPoint => aimPoint.position)` without its own null check — observed via
	/// TacticalFactionVision.CheckVisibleLineBetweenActors, which is reachable from turn
	/// start, reinforcement deployment, and routine map-vision updates alike, each time
	/// taking down the whole coroutine it ran in ("stuck" turn). Filtering nulls here fixes
	/// it at the source for every caller, regardless of which one hits it next.
	/// </summary>
	[HarmonyPatch(typeof(TacticalActor), "GetAimPoints")]
	internal static class Patch_TacticalActor_GetAimPoints
	{
		private static void Postfix(ref IEnumerable<Transform> __result)
		{
			__result = __result.Where(t => t != null);
		}
	}

	/// <summary>
	/// Confirmed root cause via Player.log (a temporary diagnostic postfix on this same
	/// method, since replaced by this real fix): a Phoenix-base-defense reinforcement
	/// squad's actors were spawning with the ability trait "DoNotResetThisTurn" already
	/// applied — TacticalActor.ProcessInstanceData (called during SpawnActor, so before
	/// any post-spawn fixup ChaosFactionTactical could run) adds it whenever a spawned
	/// actor's own TacticalFaction already equals TacticalLevel.CurrentFaction at that
	/// exact moment, which is *guaranteed* true for this mod's reinforcement case — the
	/// helper faction was just Factions.Insert-ed at CurrentFaction's own list index
	/// (see ChaosFactionTactical.OnPlayerTurnAboutToStart) specifically so it plays immediately.
	/// TacticalActor.RestartAbilities() (called from StartTurn(), below) checks that
	/// trait first: if present, it *skips* ActionPoints.SetToMax() entirely and just
	/// consumes the trait — a real vanilla mechanic, but one that directly fights this
	/// mod's own explicit "full turn on arrival" request ("I want when ally reinforcement
	/// arrive to have full turn to do"). A pre-spawn fixup can't out-race this, since
	/// ProcessInstanceData (and the trait it adds) always runs first, inside SpawnActor
	/// itself — so this Postfix runs *after* StartTurn()'s own cascade instead, always
	/// getting the last word regardless of what vanilla did or didn't do to AP.
	/// Only acts on actors in ChaosFactionTactical.PendingArrivalActionPoints (populated
	/// by SpawnSquad, the one shared spawn loop every one of this mod's paths funnels
	/// through) — each entry maps the actor to the fraction (0-1) of its max AP it should
	/// have — and Remove()s each one the instant it's handled, so this only ever fires
	/// once per actor, on their actual arrival turn, and never overrides a legitimate
	/// *later* AP reduction (stun, fatigue, etc.) on any turn after that. A fraction of
	/// exactly 1 (the Ask-for-Help squad's own grant) also maxes Will Points, matching
	/// the original "full turn on arrival" request; anything less (debug/auto activity
	/// squads, ChaosFactionConfig.ActivityFirstTurnActionPointsPercent, explicit
	/// follow-up request: "For activity only (not for ally help)... 0 to 100 (default
	/// 25%) of AP at their first turn only") only touches Action Points, leaving Will
	/// Points at whatever vanilla's own StartTurn() cascade already gave them.
	/// </summary>
	[HarmonyPatch(typeof(TacticalActor), "StartTurn")]
	internal static class Patch_TacticalActor_StartTurn_GrantArrivalActionPoints
	{
		private static void Postfix(TacticalActor __instance)
		{
			if (__instance == null || !ChaosFactionTactical.PendingArrivalActionPoints.TryGetValue(__instance, out float fraction))
				return;
			ChaosFactionTactical.PendingArrivalActionPoints.Remove(__instance);
			if (__instance.CharacterStats == null)
				return;
			if (fraction >= 1f)
			{
				__instance.CharacterStats.ActionPoints.SetToMax();
				__instance.CharacterStats.WillPoints.SetToMax();
				Log.Debug($"Granted full arrival Action/Will Points to mod-spawned actor '{__instance.name}' "
					+ $"after StartTurn() (faction='{__instance.TacticalFaction?.TacticalFactionDef?.GetName()}').");
			}
			else
			{
				float ap = fraction * __instance.CharacterStats.ActionPoints.Max;
				__instance.CharacterStats.ActionPoints.Set(ap, true);
				Log.Debug($"Granted {fraction * 100f:0}% ({ap:0.0}/{__instance.CharacterStats.ActionPoints.Max:0.0}) arrival Action Points to mod-spawned actor '{__instance.name}' "
					+ $"after StartTurn() (faction='{__instance.TacticalFaction?.TacticalFactionDef?.GetName()}').");
			}
		}
	}

	/// <summary>
	/// Explicit request, later made config-toggleable (`ChaosFactionConfig.
	/// DisableLootDropForModSpawnedUnits`, default `true` — matches the original
	/// hardcoded-always-on behavior, so nothing changes unless a player flips it off in
	/// Mod Settings): soldiers this mod spawns (ally-help squads and debug-activity
	/// squads — see ChaosFactionTactical.NoLootActors, populated by the one shared
	/// SpawnSquad loop every one of this mod's spawn paths funnels through) shouldn't drop
	/// any items when they die. DieAbility.GetDroppableItems() (source,
	/// DieAbility.cs:244, protected but not virtual) is what DropItems() (the method
	/// DieAbility.Die()/RagdollDieAbility.Die() both call, the latter inheriting
	/// DropItems() unoverridden) filters its drop/destroy loop from — an empty result here
	/// means DropItems() has nothing to iterate at all, so nothing drops and nothing gets
	/// force-destroyed either, regardless of item-level DropOnActorDeath/
	/// DestroyOnActorDeathPerc flags. There's no per-actor "never drops loot" flag
	/// anywhere in code (loot suppression is normally an item-Def-level
	/// concern, not an actor one) — this Postfix is what makes it actor-scoped, checked
	/// against NoLootActors so no other actor's death loot (enemies, player squad,
	/// civilians, etc.) is affected regardless of this setting.
	/// </summary>
	[HarmonyPatch(typeof(DieAbility), "GetDroppableItems")]
	internal static class Patch_DieAbility_GetDroppableItems
	{
		private static void Postfix(DieAbility __instance, ref IEnumerable<TacticalItem> __result)
		{
			try
			{
				if (!(ChaosFactionMain.Cfg?.DisableLootDropForModSpawnedUnits ?? true))
					return;
				if (__instance?.TacticalActorBase != null && ChaosFactionTactical.NoLootActors.Contains(__instance.TacticalActorBase))
					__result = Enumerable.Empty<TacticalItem>();
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to suppress loot drop for a mod-spawned actor: " + ex);
			}
		}
	}

	/// <summary>
	/// Catches every GeoHavenDefenseMission instance so HavenDefenseRewardTracker
	/// (ChaosFactionState.cs) can hook its OnUpdateableMissionEnded — the event that fires
	/// for *both* an auto-resolved Haven defense (ticks down via GeoUpdateableMission's own
	/// Update loop while the player is elsewhere, never touches tactical) and a
	/// player-played-out one, unlike GeoMission.OnMissionCompleted, which only fires for
	/// the latter (see HavenDefenseRewardTracker's own doc comment for the full reasoning
	/// — this distinction mattered because "trigger even if auto resolved" was explicit).
	/// Patches StartUpdating/ResumeUpdating rather than either of GeoHavenDefenseMission's
	/// two constructors: a mission can come into existence via its normal public
	/// constructor (GeoSite.cs, two call sites) *or* a private one used only during
	/// save-data deserialization (GeoHavenDefenseMission.DeserializeCustomCreate) — both
	/// StartUpdating (called once, right after a fresh mission is created) and
	/// ResumeUpdating (called once, after a deserialized mission resumes updating post-load)
	/// are called exactly once per mission regardless of which constructor built it, so
	/// patching these two catches every instance without needing to patch either
	/// constructor overload individually. HookMission itself is idempotent (a HashSet
	/// guard), so no risk of double-subscribing if a mission's postfix somehow ran twice.
	/// </summary>
	[HarmonyPatch]
	internal static class Patch_GeoUpdateableMission_Started
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			yield return AccessTools.Method(typeof(GeoUpdateableMission), "StartUpdating");
			yield return AccessTools.Method(typeof(GeoUpdateableMission), "ResumeUpdating");
		}

		private static void Postfix(GeoUpdateableMission __instance)
		{
			if (__instance is GeoHavenDefenseMission havenDefense)
				HavenDefenseRewardTracker.HookMission(havenDefense);
		}
	}

	/// <summary>
	/// Marks/unmarks a GeoHavenDefenseMission as "currently inside GeoMission.Complete()"
	/// for HavenDefenseRewardTracker (ChaosFactionState.cs — see that class's own doc
	/// comment for the full reasoning). Explicit follow-up request: "roll only once, and
	/// in case if player helped in haven defense to roll after nest etc revealed" — a
	/// decisive player win fires OnUpdateableMissionEnded from inside this same Complete()
	/// call, before GenerateTacticalReward (and any RevealPandoranSiteMissionOutcomeDef
	/// reveal it applies) has run; this Prefix/Postfix pair lets the tracker tell that
	/// early, mid-Complete() firing apart from a genuine auto-resolved conclusion (which
	/// never goes through Complete() at all) and defer the former until after the whole
	/// Complete() call — reveal included — has finished.
	/// </summary>
	[HarmonyPatch(typeof(GeoMission), "Complete")]
	internal static class Patch_GeoMission_Complete
	{
		private static void Prefix(GeoMission __instance)
		{
			if (__instance is GeoHavenDefenseMission havenDefense)
				HavenDefenseRewardTracker.MarkCompleting(havenDefense);
		}

		private static void Postfix(GeoMission __instance)
		{
			if (__instance is GeoHavenDefenseMission havenDefense)
				HavenDefenseRewardTracker.OnCompleteFinished(havenDefense);
			// Explicit follow-up request: "increase same way counter for phoenix base
			// defense mission against pandoran enemy" — GeoPhoenixBaseDefenseMission
			// only ever concludes via this same Complete() call (no auto-resolve path
			// exists for it, unlike Haven defense), so no Prefix/MarkCompleting-style
			// double-fire guard is needed here — see OnPhoenixBaseDefenseCompleted's
			// own doc comment (ChaosFactionState.cs) for the full reasoning.
			else if (__instance is GeoPhoenixBaseDefenseMission pxDefense)
				HavenDefenseRewardTracker.OnPhoenixBaseDefenseCompleted(pxDefense);
		}
	}

	/// <summary>
	/// The other half of "increment [the Pandoran attack counter] in any case, even if
	/// player will not fight for base or lose it" — a loss is already covered by
	/// Patch_GeoMission_Complete's Postfix above (Complete() fires regardless of
	/// win/lose), but a genuine decline (the defense never gets launched at all) only
	/// ever goes through Cancel(), which Complete() never sees. See
	/// HavenDefenseRewardTracker.OnPhoenixBaseDefenseCancelled's own doc comment
	/// (ChaosFactionState.cs) for how a real decline is told apart from just closing the
	/// mission-brief popup (both fire this same Cancel() call).
	/// </summary>
	[HarmonyPatch(typeof(GeoPhoenixBaseDefenseMission), "Cancel")]
	internal static class Patch_GeoPhoenixBaseDefenseMission_Cancel
	{
		private static void Postfix(GeoPhoenixBaseDefenseMission __instance)
		{
			HavenDefenseRewardTracker.OnPhoenixBaseDefenseCancelled(__instance);
		}
	}

	/// <summary>
	/// Live "Xh remaining" countdown baked directly into the help-request objective's own
	/// row text in the top-left Geoscape objectives panel — explicit request, "similar
	/// thing was already done in HavenUnderAttackTimer" — this mirrors that sibling mod's
	/// technique (same repo, `HavenUnderAttackTimer/HavenUnderAttackTimerPatches.cs`)
	/// almost exactly, since it's an already-proven-working pattern for this exact
	/// problem: `GeoObjectiveElementController`'s own native per-objective countdown only
	/// activates for `EventGeoFactionObjective`s with a registered `GeoscapeEventDef`
	/// timer (`UIModuleGeoObjectives.InitObjective` only ever computes a real `TimeUnit`
	/// for that specific subclass) — `AskForHelpGeoFactionObjective` isn't one, so passing
	/// through the normal `SetObjective(..., endTime, ...)` path never shows a countdown
	/// no matter what `GetTitle()` returns. Instead of fighting that (subclassing
	/// `EventGeoFactionObjective` would need a real registered Geoscape event/timer this
	/// mod has no other reason to create), track which row currently represents one of
	/// our objectives (`InitObjective` prefix) and, once a frame, overwrite that row's
	/// `ObjectiveText.text` directly (`GeoObjectiveElementController.Update` postfix) using
	/// the exact same `TimedObjectiveFormatter`/`TimeFormatter.HoursText` the panel's own
	/// native countdown uses for other objectives — same wording/units as vanilla, not a
	/// hardcoded English string, and localized the same way vanilla's own timers are.
	/// </summary>
	internal static class AskForHelpObjectiveTimerState
	{
		internal class Entry
		{
			public GeoFactionObjective Objective;
			public IChaosTimedObjective Timed;
			public int LastHoursRemaining = int.MinValue;
		}

		public static readonly Dictionary<GeoObjectiveElementController, Entry> Tracked =
			new Dictionary<GeoObjectiveElementController, Entry>();
	}

	/// <summary>
	/// Live "Xh remaining" countdown baked directly into a mod-added objective's own row
	/// text — see IChaosTimedObjective's own doc comment for why AskForHelpGeoFactionObjective
	/// and DebugActivityGeoFactionObjective share this one patch instead of each needing
	/// their own.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleGeoObjectives), "InitObjective")]
	internal static class Patch_UIModuleGeoObjectives_InitObjective
	{
		private static void Prefix(GeoObjectiveElementController element, GeoFactionObjective objective)
		{
			if (objective is IChaosTimedObjective timed)
			{
				AskForHelpObjectiveTimerState.Tracked[element] = new AskForHelpObjectiveTimerState.Entry { Objective = objective, Timed = timed };
				return;
			}
			AskForHelpObjectiveTimerState.Tracked.Remove(element);
		}
	}

	[HarmonyPatch(typeof(GeoObjectiveElementController), "Update")]
	internal static class Patch_GeoObjectiveElementController_Update
	{
		private static void Postfix(GeoObjectiveElementController __instance)
		{
			if (!AskForHelpObjectiveTimerState.Tracked.TryGetValue(__instance, out AskForHelpObjectiveTimerState.Entry entry))
				return;

			if (!entry.Timed.TryGetRemainingHours(out double remainingHours))
			{
				// Resolved (launched/cancelled/expired) or the row got reused for something
				// else — InitObjective's own prefix will re-track it if it becomes one of
				// ours again later. Just forgetting the row here isn't enough on its own: a
				// scene reload (any mission launch, not just a true save/load) round-trips
				// GeoFaction._objectives through vanilla's own serializer, which can
				// resurrect a disconnected copy of a [SerializeType] objective like ours
				// that the owning tracker (AllyRequestTracker/DebugActivityTracker) never
				// sees and therefore never calls RemoveObjective on — left alone, that stray
				// copy sits in the top-left panel forever with whatever text was last
				// written to this row, frozen, for a request/activity that no longer exists
				// at its site. Removing it here, the moment its backing data is confirmed
				// gone, is what actually clears it regardless of how it got orphaned.
				AskForHelpObjectiveTimerState.Tracked.Remove(__instance);
				try
				{
					entry.Timed.ResolveOwningFaction()?.RemoveObjective(entry.Objective);
				}
				catch (Exception ex)
				{
					Log.Debug("Failed to remove an orphaned timed objective from the Geoscape panel: " + ex);
				}
				return;
			}

			int hoursRounded = Mathf.Max(0, Mathf.CeilToInt((float)remainingHours));
			if (hoursRounded == entry.LastHoursRemaining)
				return;
			entry.LastHoursRemaining = hoursRounded;

			string timeText = string.Format(__instance.TimeFormatter.HoursText.Localize(null), hoursRounded);
			string finalText = __instance.TimedObjectiveFormatter.Localize(null)
				.Replace("{Objective}", entry.Objective.GetTitle())
				.Replace("{time}", timeText);
			__instance.ObjectiveText.text = finalText;

			// UIModuleGeoObjectives.InitObjective only ever calls SetTooltip with a
			// non-null LocalizedTextBind for objectives tied to an active GeoscapeEventDef
			// encounter (GetObjectiveTooltip) — never for ours — so SetTooltip(null) leaves
			// element.Tooltip.gameObject fully SetActive(false) and it would otherwise never
			// show at all. UITooltipText.TipText is a plain public string, separate from
			// TipKey (only used to overwrite TipText *if* TipKey has a real, registered
			// LocalizationKey — ours never does, since it's literal in-code text, not a
			// registered localization entry) — so setting TipText directly and activating
			// the GameObject here works without needing a real localization key at all.
			if (__instance.Tooltip != null)
			{
				__instance.Tooltip.gameObject.SetActive(true);
				__instance.Tooltip.TipText = entry.Objective.GetDescription();
			}
		}
	}

	/// <summary>
	/// Adds a light-blue "X will help in this fight" row to the mission-brief popup shown
	/// before launching a mission (explicit request) — whenever the site being briefed has
	/// an active help request (player-paid or free from HavenDefenseRewardTracker).
	/// CommonMissionDataController is a sub-panel *shared* across every mission-brief
	/// modal (GeoPhoenixBaseDefenseBrief, GeoAlienBaseBrief, GeoPhoenixBaseInfestationBrief,
	/// InfestedHavenBrief — confirmed via GeoscapeView.GetMissionBriefModal's exact mapping
	/// for our four eligible mission classes), not tied to any one specific DataBind class
	/// — SetData already branches on mission type internally (e.g. hiding EnemyRow for
	/// GeoAmbushMission), confirming it's the real shared summary block, so patching this
	/// one method covers all four brief modals from a single patch instead of four.
	/// No pre-existing "ally/helper" row exists in vanilla's own layout to reuse directly,
	/// so this clones the existing EnemyRow (same Icon+Text shape already used for "who's
	/// attacking") rather than hand-building new UI — reuses vanilla's own row prefab/
	/// layout instead of guessing at RectTransform positioning from scratch.
	/// </summary>
	[HarmonyPatch(typeof(CommonMissionDataController), "SetData")]
	internal static class Patch_CommonMissionDataController_SetData
	{
		private const string HelpRowName = "ChaosFaction_HelpRow";
		private const string ActivityRowName = "ChaosFaction_ActivityRow";

		// Reuses the game's own real "ENEMY" tag (Missions.csv: KEY_MISSION_ENEMY) instead
		// of a hardcoded literal, so it localizes correctly and matches the exact casing
		// vanilla's own EnemyRow label already uses elsewhere on this same modal — explicit
		// follow-up request to reuse existing in-game translations where possible. No
		// equivalent standalone "ALLY" key exists anywhere in the game's localization
		// (checked ChaosUkrainian/SourceLocalization/PhoenixPoint/I2Languages), so the ALLY
		// tag below stays a literal, matching the HelpRowName row's own "ALLY" literal above.
		private static readonly Base.UI.LocalizedTextBind EnemyTagText = new Base.UI.LocalizedTextBind("KEY_MISSION_ENEMY");

		private static void Postfix(CommonMissionDataController __instance, ModalCommonResources resources, GeoMission mission)
		{
			try
			{
				if (__instance.EnemyRow == null || __instance.EnemyText == null)
					return;
				Transform parent = __instance.EnemyRow.transform.parent;
				if (parent == null)
					return;

				GeoSite site = mission?.Site;
				GeoFaction helperFaction = null;
				if (site != null && AllyRequestTracker.ActiveRequests.TryGetValue(site, out PendingHelpRequest request))
					helperFaction = request.HelperKind.ToFaction(site.GeoLevel);

				SetRow(__instance, resources, parent, HelpRowName,
					helperFaction != null,
					helperFaction != null ? resources.GetFactionInfo(helperFaction).Name.Localize(null) : null,
					ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ALLY_LABEL"),
					ChaosFactionColors.LightBlue,
					helperFaction != null ? resources.GetFactionInfo(helperFaction).Icon : null);

				PendingDebugActivity activity = site != null && DebugActivityTracker.ActiveActivities.TryGetValue(site, out PendingDebugActivity activeActivity)
					? activeActivity
					: null;
				// The sole reveal trigger (moved off the context-menu patch — reported
				// in-game as revealing on a bare left-click with no aircraft/squad ever
				// sent to the site, see TryAddDebugActivityAbilities' own doc comment).
				// Opening this mission-brief popup only happens once the player has
				// actually sent something to launch the mission — a genuine "come to
				// mission and open it," unlike a context-menu left-click.
				if (activity != null && site != null)
					DebugActivityTracker.RevealIfNeeded(site, site.GeoLevel);

				SetRow(__instance, resources, parent, ActivityRowName,
					activity != null,
					activity != null ? activity.Kind.DisplayName(site?.GeoLevel) : null,
					activity != null && activity.IsHostile ? EnemyTagText.Localize(null) : ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ALLY_LABEL"),
					activity != null && activity.IsHostile ? ChaosFactionColors.HostileRed : ChaosFactionColors.LightBlue,
					null);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to add help/activity row to mission brief: " + ex);
			}
		}

		/// <summary>
		/// Shared by the Ask-for-Help row and the debug-activity row (extracted once a
		/// second, structurally identical row was needed): clones EnemyRow (same Icon+Text
		/// shape vanilla already uses for "who's attacking") the first time, reuses/hides it
		/// on subsequent SetData calls — CommonMissionDataController instances are reused
		/// across repeated modal opens, not recreated each time, so a row from a previous
		/// open needs to be explicitly hidden when this call has nothing to show, not just
		/// left showing stale text for an unrelated mission.
		/// </summary>
		private static void SetRow(CommonMissionDataController instance, ModalCommonResources resources, Transform parent, string rowName, bool visible, string valueText, string labelText, Color color, Sprite icon)
		{
			Transform existingRow = parent.Find(rowName);
			if (!visible)
			{
				if (existingRow != null)
					existingRow.gameObject.SetActive(false);
				return;
			}

			GameObject rowGo;
			if (existingRow != null)
			{
				rowGo = existingRow.gameObject;
			}
			else
			{
				rowGo = UnityEngine.Object.Instantiate(instance.EnemyRow, parent);
				rowGo.name = rowName;
				rowGo.transform.SetSiblingIndex(instance.EnemyRow.transform.GetSiblingIndex() + 1);
			}
			rowGo.SetActive(true);

			// EnemyRow apparently isn't just the one EnemyText value — it also has its own
			// static "ENEMY" category-label Text sibling (confirmed in-game: the cloned row
			// showed "ENEMY" verbatim, since that label is baked into the prefab and
			// SetData never touches it, only EnemyText). Rather than guess at that label's
			// exact position, every Text found under the *original* EnemyRow other than
			// EnemyText itself is treated as a label and relabeled to labelText in the
			// clone — handles however many such labels actually exist without needing
			// Editor visibility into the prefab's exact hierarchy.
			foreach (Text originalText in instance.EnemyRow.GetComponentsInChildren<Text>(true))
			{
				Transform cloneTextTransform = FindCorrespondingChild(instance.EnemyRow.transform, originalText.transform, rowGo.transform);
				Text cloneText = cloneTextTransform != null ? cloneTextTransform.GetComponent<Text>() : null;
				if (cloneText == null)
					continue;
				cloneText.text = originalText == instance.EnemyText ? valueText : labelText;
				cloneText.color = color;
			}

			if (instance.EnemyIcon != null && icon != null)
			{
				Transform iconTransform = FindCorrespondingChild(instance.EnemyRow.transform, instance.EnemyIcon.transform, rowGo.transform);
				Image rowIcon = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
				if (rowIcon != null)
					rowIcon.sprite = icon;
			}
		}

		/// <summary>
		/// Locates originalTarget's sibling-index path relative to originalRoot, then walks
		/// the same index path under clonedRoot — a robust way to find "the same child" in
		/// a freshly-Instantiate()'d clone without relying on GetComponentInChildren&lt;T&gt;()
		/// (which would grab the *first* match and could be wrong if EnemyRow's hierarchy
		/// has more than one Text/Image, e.g. a label prefix versus the value).
		/// </summary>
		private static Transform FindCorrespondingChild(Transform originalRoot, Transform originalTarget, Transform clonedRoot)
		{
			List<int> path = new List<int>();
			Transform current = originalTarget;
			while (current != null && current != originalRoot)
			{
				path.Add(current.GetSiblingIndex());
				current = current.parent;
			}
			path.Reverse();

			Transform result = clonedRoot;
			foreach (int index in path)
			{
				if (result == null || index >= result.childCount)
					return null;
				result = result.GetChild(index);
			}
			return result;
		}
	}

}
