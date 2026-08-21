using System;
using System.Collections.Generic;
using System.Linq;
using Base;
using Base.Core;
using Base.Defs;
using Base.Serialization.General;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Levels.ActorDeployment;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Modding;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.ActorsInstance;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.ActorDeployment;
using PhoenixPoint.Tactical.Levels.Missions;
using UnityEngine;

namespace ChaosFaction
{
	/// <summary>
	/// Mod's custom save data for tactical.
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionTacInstanceData
	{
		public int ExampleData;
	}

	/// <summary>
	/// Represents a mod instance specific for Tactical game.
	/// Each time Tactical level is loaded, new mod's ModTactical is created.
	/// </summary>
	public class ChaosFactionTactical : ModTactical
	{
		/// <summary>
		/// Snapshot of AllyRequestTracker.PendingReinforcement* taken once, at
		/// OnTacticalStart, and immediately cleared from the static fields — deliberately
		/// NOT read directly from the static fields in OnPlayerTurnAboutToStart. Those
		/// statics are only meaningful for the one specific mission that set them; if that
		/// mission ends (abandoned/failed) before its target player-turn-count arrives, the
		/// static fields would otherwise keep sitting there, and a *later, entirely
		/// unrelated* tactical mission's own turn count could eventually reach the same
		/// number and spawn phantom reinforcements into the wrong fight. Since
		/// ChaosFactionTactical is re-instantiated fresh per tactical level load (per the
		/// class's own doc comment above), owning a private copy on the instance sidesteps
		/// that entirely — the statics are consumed once, atomically, exactly like
		/// PendingAllyTemplates already was for the immediate-spawn case.
		/// </summary>
		private List<TacCharacterDef> _reinforcementTemplates;
		private PPFactionDef _reinforcementFactionDef;

		/// <summary>
		/// How many player turns must begin before the reinforcement spawns — re-rolled
		/// from PhoenixBaseDefenseReinforcementMinTurns/MaxTurns on the Geoscape side (still
		/// the same config fields/roll, just reinterpreted: "turns" counts player-turn-
		/// *beginnings* specifically, not the mission's raw/ambiguous global turn number).
		/// Renamed from _reinforcementAtTurn when the trigger mechanism switched from
		/// ModTactical.OnNewTurn(turnNumber) to TacticalLevelController.
		/// FactionEndedTurnEvent, then later moved from counting turn-*endings* to
		/// turn-*beginnings* — explicit follow-up request: "is it possible to spawn on the
		/// beginning of player turn?" — see OnPlayerTurnAboutToStart's own doc comment for
		/// both changes' reasoning.
		/// </summary>
		private int _reinforcementAfterPlayerTurns = -1;
		private int _playerTurnStartsSeen;

		/// <summary>
		/// Every actor this mod has spawned in the current tactical mission — ally-help
		/// squads (immediate and delayed-reinforcement) and debug-activity squads — so
		/// Patch_DieAbility_GetDroppableItems (ChaosFactionPatches.cs) can suppress their
		/// loot drop on death without touching any other actor's, gated on
		/// ChaosFactionConfig.DisableLootDropForModSpawnedUnits (originally hardcoded
		/// always-on, made config-toggleable per a later explicit follow-up request — "add
		/// global config property... to enable disable loot drop by the units spawned by
		/// this mod", default true so nothing changes unless a player opts out). Populated
		/// by SpawnSquad, the one shared per-template spawn loop every one of this mod's
		/// spawn paths already funnels through — unconditionally, regardless of the config
		/// value, since the patch itself is what actually reads the setting; this way
		/// flipping the setting mid-session doesn't need re-populating anything. Cleared at
		/// OnTacticalStart (fresh per tactical level load, same reasoning as the
		/// reinforcement fields above) rather than left to grow across missions — stale
		/// entries would be harmless (dead Unity object references), just unbounded memory
		/// over a long campaign.
		/// </summary>
		public static readonly HashSet<TacticalActorBase> NoLootActors = new HashSet<TacticalActorBase>();

		/// <summary>
		/// One-shot map of mod-spawned actors still waiting for their "arrival" Action
		/// Points grant, to what fraction of their max AP (0-1) they should be set to —
		/// consumed (Remove'd) by Patch_TacticalActor_StartTurn_GrantArrivalActionPoints
		/// (ChaosFactionPatches.cs) the first time TacticalActor.StartTurn() is called for
		/// that actor, then never touched again, so it never overrides a legitimate *later*
		/// AP reduction (stun, fatigue, etc.) — only the turn they actually arrive on. See
		/// that patch's own doc comment for why this has to happen as a Postfix on
		/// StartTurn() specifically, not at spawn time. 1f for the immediate Ask-for-Help
		/// spawn (always full AP + Will Points); ChaosFactionConfig.
		/// PhoenixBaseDefenseAllyReinforcementFirstTurnAP / 100 (default 1f, i.e. 100%) for
		/// the delayed Phoenix-base-defense reinforcement case;
		/// ChaosFactionConfig.ActivityFirstTurnActionPointsPercent / 100
		/// (default 0.25f, AP only — Will Points untouched) for debug/auto activity squads
		/// instead — explicit follow-up request: "For activity only (not for ally help)
		/// make spawned units to have configurable variable from 0 to 100 (default 25%) of
		/// AP at their first turn only, all next turns are unchanged." Populated by
		/// SpawnSquad alongside NoLootActors; cleared at OnTacticalStart for the same
		/// "fresh per tactical level load" reasoning as NoLootActors.
		/// </summary>
		public static readonly Dictionary<TacticalActorBase, float> PendingArrivalActionPoints = new Dictionary<TacticalActorBase, float>();

		public override void OnTacticalStart()
		{
			NoLootActors.Clear();
			PendingArrivalActionPoints.Clear();
			SpawnRequestedAllies();
			SpawnRequestedDebugActivity();

			_reinforcementTemplates = AllyRequestTracker.PendingReinforcementTemplates;
			_reinforcementFactionDef = AllyRequestTracker.PendingReinforcementFactionDef;
			_reinforcementAfterPlayerTurns = AllyRequestTracker.PendingReinforcementAtTurn;
			_playerTurnStartsSeen = 0;
			AllyRequestTracker.PendingReinforcementTemplates = null;
			AllyRequestTracker.PendingReinforcementFactionDef = null;
			AllyRequestTracker.PendingReinforcementAtTurn = -1;

			if (_reinforcementAfterPlayerTurns >= 0)
			{
				Controller.FactionEndedTurnEvent += OnPlayerTurnAboutToStart;
				// Explicit request: "use similar approach [to ChaosFasterTurn's own
				// top-left speed badge] to show text with how many turns left for
				// allied reinforcement in Phoenix defense mission." Built here (rather
				// than lazily on first countdown update) so it's already visible from
				// turn 1 - RefreshReinforcementBadge computes and shows the initial
				// "X turns" text immediately below.
				ReinforcementCountdownBadge.BuildFor(Controller);
				RefreshReinforcementBadge();
			}
		}

		public override void OnTacticalEnd()
		{
			Controller.FactionEndedTurnEvent -= OnPlayerTurnAboutToStart;
			ReinforcementCountdownBadge.Clear();
			base.OnTacticalEnd();
		}

		/// <summary>
		/// Shows/updates the top-left "Reinforcements in X turns" badge for however many
		/// player-turn-starts are still left before OnPlayerTurnAboutToStart's own
		/// _reinforcementAfterPlayerTurns threshold is reached - same countdown that method
		/// itself already tracks via _playerTurnStartsSeen, just surfaced as on-screen text
		/// instead of only a Player.log line. Hidden entirely once there's no pending
		/// reinforcement to count down (_reinforcementAfterPlayerTurns < 0), which covers
		/// both "not a Phoenix base defense mission" and "reinforcement already spawned" -
		/// OnPlayerTurnAboutToStart resets the field to -1 the moment it actually fires the
		/// spawn, so the next call here (if any) naturally hides it.
		/// </summary>
		private void RefreshReinforcementBadge()
		{
			if (_reinforcementAfterPlayerTurns < 0)
			{
				ReinforcementCountdownBadge.Hide();
				return;
			}
			int turnsLeft = Math.Max(0, _reinforcementAfterPlayerTurns - _playerTurnStartsSeen);
			ReinforcementCountdownBadge.Show(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_REINFORCEMENT_BADGE", turnsLeft));
		}

		public override void ProcessTacticalInstanceData(object instanceData)
		{
			ChaosFactionTacInstanceData data = (ChaosFactionTacInstanceData)instanceData;
		}

		public override object RecordTacticalInstanceData()
		{
			return new ChaosFactionTacInstanceData() { ExampleData = 5 };
		}

		/// <summary>
		/// Fires the delayed Phoenix-base-defense reinforcement spawn once the player's own
		/// turn is about to begin for the _reinforcementAfterPlayerTurns'th time — so the
		/// squad is already spawned and visible on the map the moment the player regains
		/// control, rather than only appearing partway through the *following* round.
		/// Reached via TacticalLevelController.FactionEndedTurnEvent (a real public C#
		/// event, confirmed via source — TacticalLevelController.cs's own
		/// NextTurnCrt fires it right after advancing _currentFactionIndex but *before* the
		/// newly-current faction's own PlayTurnCrt begins) — fires once per faction-turn
		/// transition, checked via nextFaction (not prevFaction) to detect "the player's
		/// turn is specifically about to begin."
		/// <para/>
		/// **Spawning here does NOT mean the squad also *acts* here — explicit follow-up
		/// request: "I want it to arrive on player start turn, but act after player turn...
		/// Spawn on player turn start but act after player turn end and before pandorans
		/// (between player and pandorans)."** `SpawnAllies` is called below with its
		/// `insertBeforeFaction` param left at its default (`null`), which makes it fall
		/// back to resolving the mission's own enemy `TacticalFaction` itself (same
		/// default every other ally-help spawn already uses) — so
		/// `GetOrCreateHelperFaction` inserts the freshly-created helper faction into
		/// `Factions` at the *Pandoran attacker's own current list index*, not the
		/// player's. Since the attacker's list position is (in the common 2-faction round)
		/// later than the player's, this insert doesn't disturb the player's own upcoming
		/// turn — the player still plays as scheduled — but it does guarantee the
		/// round-robin encounters the newly-inserted helper faction, and gives it a real
		/// turn, strictly *before* it reaches the (now one-slot-later) attacker. The actor
		/// *spawn* itself (SpawnActor, giving the squad a visible, positioned presence on
		/// the map) happens synchronously inside SpawnAllies/SpawnSquad regardless of when
		/// that faction's own first *turn* comes up — a faction can have live actors on the
		/// map well before its own turn in the round, same as every other faction already
		/// does from mission start. Net effect: the squad is visible throughout the
		/// player's own turn, then gets a genuine full turn of its own immediately after
		/// the player ends theirs, strictly before the Pandoran attacker acts.
		/// One-shot: unsubscribes itself the moment it actually fires (successfully or not),
		/// same as the old turn-number check's own single-fire guard. One consequence of
		/// counting turn-*beginnings* instead of turn-*endings*: the mission's very first
		/// player turn is the starting state, not a transition *into* a player turn, so
		/// FactionEndedTurnEvent never fires for it — the count effectively starts from the
		/// second player turn onward, a minor boundary shift not expected to matter given
		/// the multi-turn delay this represents anyway.
		/// </summary>
		private void OnPlayerTurnAboutToStart(TacticalFaction prevFaction, TacticalFaction nextFaction)
		{
			if (_reinforcementAfterPlayerTurns < 0)
				return;
			TacticalFaction playerFaction = Controller.GetTacticalFaction(TacMissionParticipant.Player);
			if (playerFaction == null || nextFaction != playerFaction)
				return;

			_playerTurnStartsSeen++;
			if (_playerTurnStartsSeen < _reinforcementAfterPlayerTurns)
			{
				RefreshReinforcementBadge();
				return;
			}

			List<TacCharacterDef> templates = _reinforcementTemplates;
			PPFactionDef helperDef = _reinforcementFactionDef;
			int playerTurnsWaited = _playerTurnStartsSeen;
			_reinforcementTemplates = null;
			_reinforcementFactionDef = null;
			_reinforcementAfterPlayerTurns = -1;
			Controller.FactionEndedTurnEvent -= OnPlayerTurnAboutToStart;
			// Reached the threshold - RefreshReinforcementBadge now hides the badge, since
			// _reinforcementAfterPlayerTurns was just reset to -1 above (same hide path a
			// mission with no pending reinforcement at all already takes).
			RefreshReinforcementBadge();
			if (templates == null || templates.Count == 0 || helperDef == null)
				return;

			Log.Debug($"Player's turn about to begin for the {playerTurnsWaited}. time — spawning delayed ally reinforcements from {helperDef.name}, visible now but inserted to act right after the player's own turn, before the attacker.");
			// insertBeforeFaction left at its default (null) — SpawnAllies falls back to
			// resolving the mission's own enemy TacticalFaction itself (the Pandoran
			// attacker), same default every other ally-help spawn already uses, so this
			// squad's own first turn lands after the player's, not before it.
			SpawnAllies(templates, helperDef, true, Math.Max(1, playerFaction.TurnNumber));
		}

		/// <summary>
		/// If the player requested a faction's help before launching this mission
		/// (ChaosFactionState.AllyRequestTracker.PendingAllyTemplates/PendingHelperFactionDef
		/// — whichever of New Jericho/Anu/Synedrion actually agreed), spawn the picked
		/// troops here as an allied faction. Only runs once: the pending list is always
		/// cleared, whether the spawn succeeds or not. Not used for the Phoenix-base-defense
		/// reinforcement case (see OnPlayerTurnAboutToStart) — PendingAllyTemplates is left
		/// empty for that case, so this is naturally a no-op then.
		/// </summary>
		private void SpawnRequestedAllies()
		{
			List<TacCharacterDef> templates = AllyRequestTracker.PendingAllyTemplates;
			AllyRequestTracker.PendingAllyTemplates = new List<TacCharacterDef>();
			if (templates == null || templates.Count == 0)
				return;

			PPFactionDef helperDef = AllyRequestTracker.PendingHelperFactionDef;
			if (helperDef == null)
			{
				Log.Debug("No helper faction recorded for this request; refusing to spawn allies.");
				return;
			}

			SpawnAllies(templates, helperDef, false, 0);
		}

		/// <summary>
		/// Shared by the immediate-spawn case (SpawnRequestedAllies, useReinforcementZones
		/// false — deploy anywhere in the player's own zones, same as before, turnNumber 0)
		/// and the delayed Phoenix-base-defense reinforcement case (OnPlayerTurnAboutToStart,
		/// useReinforcementZones true — deploy only at the map's own reinforcement-tagged
		/// entrance zones, falling back to the player's full zone set if the map has none,
		/// per explicit request: "they should come in entrance edge on map where is
		/// entrance to base"; turnNumber is a representative current-turn value, only its
		/// zero-vs-nonzero-ness actually matters — see SpawnSquad's own doc comment). Both
		/// call sites let this method resolve the mission's own enemy TacticalFaction itself
		/// and insert the helper faction to act immediately before it (see
		/// GetOrCreateHelperFaction for the actual reordering mechanism) — explicit
		/// follow-up request: "make those spawned units to act before pandorans, the same
		/// way as in base defense mission." An earlier version let the reinforcement case
		/// override this via an explicit insertBeforeFaction parameter (first the live
		/// "next faction about to go," later the player faction itself, to change *when*
		/// the squad's own first turn landed) — removed once neither call site needed to
		/// override it any more: "arrive on player start turn, but act after player turn
		/// end and before pandorans" only needed *when this method itself gets called*
		/// (OnPlayerTurnAboutToStart, at player-turn-start) to move, not which faction the
		/// insert targets — the Pandoran attacker was always the right target for both.
		/// </summary>
		private void SpawnAllies(List<TacCharacterDef> templates, PPFactionDef helperDef, bool useReinforcementZones, int turnNumber)
		{
			try
			{
				TacticalLevelController controller = Controller;
				Log.Debug($"Spawning requested allies into mission type '{controller.TacMission?.MissionData?.MissionType?.name}' (reinforcement={useReinforcementZones}).");

				// TacMissionParticipant.Residents is the only "third-party friendly" slot
				// the game's enum has, and plenty of mission types define a Residents
				// participant for something entirely unrelated to the helper faction (alien
				// base assaults, for instance, use it for captured civilians/hostages at the
				// site) — checking "does any Residents participant exist" was wrong, since
				// it refuses to spawn on those missions even though there's no actual
				// collision risk. The real risk is specifically the helper faction already
				// having an authored role in *this* mission (its own TacParticipantSpawn/
				// reinforcement pipeline, MissionData.MissionParticipants, read once in
				// TacMission.OnLevelStart) — reusing that would mix our spawns into a
				// pipeline that isn't ours and corrupt its later reinforcement turns
				// (observed in-game as malformed spawns with missing weapon aim-points,
				// crashing on the next turn). So check for the helper faction specifically.
				bool helperAlreadyParticipant = controller.TacMission.MissionData.MissionParticipants
					.Any(mp => mp.FactionDef == helperDef);
				if (helperAlreadyParticipant)
				{
					Log.Debug($"{helperDef.name} already has an authored role in this mission's participant data; refusing to spawn allies to avoid colliding with it.");
					return;
				}

				TacMissionParticipant enemyParticipant = ResolveEnemyParticipant(controller.TacMission.MissionData.MissionType);
				TacticalFaction attackerFaction = enemyParticipant != TacMissionParticipant.None
					? controller.GetTacticalFaction(enemyParticipant)
					: null;
				// Both call sites (immediate and delayed-reinforcement) insert the helper
				// faction to act immediately before the mission's own enemy — explicit
				// follow-up request: "make those spawned units to act before pandorans, the
				// same way as in base defense mission" — see GetOrCreateHelperFaction's own
				// doc comment for the actual reordering mechanism.
				TacticalFaction helperFaction = GetOrCreateHelperFaction(controller, helperDef, enemyParticipant, attackerFaction);
				if (helperFaction == null)
					return;

				TacticalFaction playerFaction = controller.GetTacticalFaction(TacMissionParticipant.Player);
				if (playerFaction != null)
					TacticalFaction.SetMutualRelation(helperFaction, playerFaction, FactionRelation.Friend);
				if (attackerFaction != null)
					TacticalFaction.SetMutualRelation(helperFaction, attackerFaction, FactionRelation.Enemy);
				else
					Log.Debug($"Could not resolve an enemy TacticalFaction (participant kind '{enemyParticipant}') to set hostile relations against; allies may spawn neutral.");

				List<TacticalDeployZone> deployZones = GetSpawnZones(playerFaction, useReinforcementZones);
				if (deployZones.Count == 0)
				{
					Log.Debug("No deploy zones found to spawn allies into.");
					return;
				}

				// Explicit follow-up request: the arrival AP grant is a configurable
				// percentage (0-100), but only for the Phoenix-base-defense delayed
				// reinforcement case (useReinforcementZones true) — the immediate ally-help
				// spawn always grants the full 1f fraction, unaffected by this setting.
				// "The logic must work only for first turn, other turns are unchanged" is
				// already guaranteed by PendingArrivalActionPoints/
				// Patch_TacticalActor_StartTurn_GrantArrivalActionPoints's own one-shot,
				// consumed-on-first-StartTurn() design — no extra logic needed here for that.
				float arrivalActionPointsFraction = useReinforcementZones
					? Mathf.Clamp01((ChaosFactionMain.Cfg?.PhoenixBaseDefenseAllyReinforcementFirstTurnAP ?? 100) / 100f)
					: 1f;

				// Explicit request: keep reinforcement squadmates spread out on arrival, same
				// as activity squads — but "for usual ally help support nothing to change", so
				// this only applies for the delayed reinforcement case (useReinforcementZones),
				// never the immediate spawn.
				float minDistanceBetweenSquadmates = useReinforcementZones
					? Mathf.Max(0, ChaosFactionMain.Cfg?.MinSquadmateSpawnDistanceTiles ?? 2) * WorldUnitsPerTile
					: 0f;

				HashSet<Vector3> usedPositions = new HashSet<Vector3>();
				int spawned = SpawnSquad(templates, helperFaction.TacticalFactionDef, helperFaction.ParticipantKind, deployZones, usedPositions, null, 0f, turnNumber, arrivalActionPointsFraction, minDistanceBetweenSquadmates);
				Log.Debug($"Spawned {spawned}/{templates.Count} requested ally troops.");
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to spawn requested allies: " + ex);
			}
		}

		/// <summary>
		/// Spawns the squad queued by DebugActivityTracker.AddActivity. Pure/Forsaken
		/// always spawn hostile to both the player and whichever faction the mission's own
		/// enemy already is. New Jericho/Synedrion/Anu instead spawn as an ally (friendly
		/// to the player, hostile only to the mission's own enemy) unless the diplomacy
		/// snapshot taken at DebugActivityTracker.ResolveIsHostile came back at
		/// PartyDiplomacyState.Conflict (or worse) — see
		/// DebugActivityTracker.PendingActivityIsHostile. The squad spawns clustered near a
		/// randomly-picked point within the inner half of the map (GetRandomAreaZones) —
		/// not pinned to the exact geometric center every time, and steered away from the
		/// player — rather than a raw NavMesh query; see CLAUDE.md for why
		/// GetFloorGridPositionsAroundPos was ruled out (needs a NavAreas value not readily
		/// available at this point).
		/// </summary>
		private void SpawnRequestedDebugActivity()
		{
			List<TacCharacterDef> squad = DebugActivityTracker.PendingSquadTemplates;
			PPFactionDef activityFactionDef = DebugActivityTracker.PendingActivityFactionDef;
			bool isHostile = DebugActivityTracker.PendingActivityIsHostile;
			DebugActivityTracker.PendingSquadTemplates = new List<TacCharacterDef>();
			DebugActivityTracker.PendingActivityFactionDef = null;
			DebugActivityTracker.PendingActivityIsHostile = true;
			if (squad == null || squad.Count == 0 || activityFactionDef == null)
				return;

			try
			{
				TacticalLevelController controller = Controller;
				Log.Debug($"Spawning debug activity ({squad.Count}) into mission type '{controller.TacMission?.MissionData?.MissionType?.name}'.");

				// Same collision guard as SpawnAllies: refuse if this sub-faction already
				// has an authored role in the mission's own participant data, rather than
				// risking corrupting its own reinforcement pipeline.
				bool activityFactionAlreadyParticipant = controller.TacMission.MissionData.MissionParticipants
					.Any(mp => mp.FactionDef == activityFactionDef);
				if (activityFactionAlreadyParticipant)
				{
					Log.Debug($"{activityFactionDef.name} already has an authored role in this mission's participant data; refusing to spawn the debug activity to avoid colliding with it.");
					return;
				}

				TacMissionParticipant enemyParticipant = ResolveEnemyParticipant(controller.TacMission.MissionData.MissionType);
				TacticalFaction attackerFaction = enemyParticipant != TacMissionParticipant.None
					? controller.GetTacticalFaction(enemyParticipant)
					: null;
				// Same "act before Pandorans" turn-order positioning as SpawnAllies now
				// applies here too, per explicit follow-up request — regardless of whether
				// this activity turns out hostile or allied (see GetOrCreateHelperFaction's
				// own doc comment for the actual reordering mechanism).
				TacticalFaction activityFaction = GetOrCreateHelperFaction(controller, activityFactionDef, enemyParticipant, attackerFaction);
				if (activityFaction == null)
					return;

				TacticalFaction playerFaction = controller.GetTacticalFaction(TacMissionParticipant.Player);
				// Hostile case (Pure/Forsaken always, or New Jericho/Synedrion/Anu at
				// PartyDiplomacyState.Conflict or worse): hostile to everyone but itself,
				// per explicit request ("hostile to player and pandoran") — the player, and
				// whichever faction the mission's own enemy already is. Ally case (New
				// Jericho/Synedrion/Anu above Conflict): friendly to the player instead,
				// same relation-setting as a normal Ask-for-Help grant — the "activity" still
				// turns into help, it just took the debug-tool path to get there.
				if (playerFaction != null)
					TacticalFaction.SetMutualRelation(activityFaction, playerFaction, isHostile ? FactionRelation.Enemy : FactionRelation.Friend);
				if (attackerFaction != null)
					TacticalFaction.SetMutualRelation(activityFaction, attackerFaction, FactionRelation.Enemy);
				else
					Log.Debug($"Could not resolve an enemy TacticalFaction (participant kind '{enemyParticipant}') to set relations against; debug activity may spawn neutral toward it.");

				// Reported in-game: activity squads sometimes spawned right next to the
				// player's own units. Live player positions are gathered first so both the
				// target-area pick below and FindSpawnPosition's own per-actor guard can
				// steer away from them, falling back to the nearest-available position/area
				// anyway if literally nothing on the map is far enough (better to spawn
				// somewhere than not at all).
				List<Vector3> playerPositions = GetPlayerUnitPositions(controller);
				float minDistanceFromPlayer = ChaosFactionMain.Cfg?.ActivityMinDistanceFromPlayerUnits ?? 30f;

				List<TacticalDeployZone> areaZones = GetRandomAreaZones(playerPositions, minDistanceFromPlayer);
				if (areaZones.Count == 0)
				{
					Log.Debug("No deploy zones found on the map to spawn the debug activity near.");
					return;
				}

				// Explicit follow-up request: activity squads (unlike ally-help, which
				// always gets the full 1f fraction when granted at all) get only a
				// *partial* Action Points grant on their first turn — "For activity only
				// (not for ally help) make spawned units to have configurable variable
				// from 0 to 100 (default 25%) of AP at their first turn only, all next
				// turns are unchanged." Every turn after the first is untouched — the
				// arrival fraction is a one-shot grant, consumed the first time StartTurn()
				// fires (see PendingArrivalActionPoints' own doc comment).
				float activityFirstTurnActionPointsFraction = Mathf.Clamp01((ChaosFactionMain.Cfg?.ActivityFirstTurnActionPointsPercent ?? 25) / 100f);

				// Explicit request: keep activity squadmates spread out too, so they don't all
				// clump onto the same/adjacent tile.
				float minDistanceBetweenSquadmates = Mathf.Max(0, ChaosFactionMain.Cfg?.MinSquadmateSpawnDistanceTiles ?? 2) * WorldUnitsPerTile;

				HashSet<Vector3> usedPositions = new HashSet<Vector3>();
				int spawned = SpawnSquad(squad, activityFaction.TacticalFactionDef, activityFaction.ParticipantKind, areaZones, usedPositions, playerPositions, minDistanceFromPlayer, 0, activityFirstTurnActionPointsFraction, minDistanceBetweenSquadmates);
				Log.Debug($"Spawned {spawned}/{squad.Count} debug activity troop(s) (hostile={isHostile}), kept at least {minDistanceFromPlayer} units from {playerPositions.Count} live player unit(s) where possible.");
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to spawn debug activity: " + ex);
			}
		}

		/// <summary>
		/// Explicit follow-up request: don't always cluster the squad at the exact
		/// geometric center — pick a random point somewhere within the inner half of the
		/// map instead (still biased toward the middle, just not pinned to one exact spot
		/// every time), avoid the player's current corner, and spawn the squad clustered
		/// near *that* point. Reuses the same centroid-of-every-TacticalDeployZone
		/// approximation for "the map's center" GetMapCenterZones (this method's
		/// predecessor) used — still without a raw NavMesh/NavAreas query, see this
		/// method's call site doc comment for why — but adds a "map radius" (the furthest
		/// any zone sits from that centroid) to scale the random pick against, and then
		/// re-anchors the same nearest-first zone sort FindSpawnPosition already expects
		/// around the random point instead of the literal centroid.
		/// </summary>
		private static List<TacticalDeployZone> GetRandomAreaZones(List<Vector3> avoidPositions, float minDistanceFromAvoid)
		{
			List<TacticalDeployZone> allZones = UnityEngine.Object.FindObjectsOfType<TacticalDeployZone>().ToList();
			if (allZones.Count == 0)
				return allZones;

			Vector3 centroid = Vector3.zero;
			foreach (TacticalDeployZone zone in allZones)
				centroid += zone.transform.position;
			centroid /= allZones.Count;

			float mapRadius = 0f;
			foreach (TacticalDeployZone zone in allZones)
				mapRadius = Mathf.Max(mapRadius, Vector3.Distance(zone.transform.position, centroid));

			Vector3 target = PickRandomAreaTarget(centroid, mapRadius, avoidPositions, minDistanceFromAvoid);
			return allZones.OrderBy(z => (z.transform.position - target).sqrMagnitude).ToList();
		}

		/// <summary>
		/// Explicit request: "select in area of radius like 50% from center radius of the
		/// map" — areaRadius is half of mapRadius (the furthest any TacticalDeployZone sits
		/// from the map's centroid, GetRandomAreaZones' own stand-in for "the map's overall
		/// size"), and the candidate point is sampled uniformly *by area* within that disc
		/// (sqrt(Random.value) before scaling by radius — a plain unscaled Random.value
		/// would bunch samples too close to the centroid, since area grows with the square
		/// of the radius). Offsets only X/Z (leaves Y at the centroid's own height) since
		/// this point is only ever used as a sorting anchor for GetRandomAreaZones, not an
		/// actual spawn position itself — matches how the centroid itself was already just
		/// an average of every zone's full position, Y included, without correcting for
		/// per-zone terrain height. Rerolls up to a fixed attempt cap to avoid landing
		/// inside minDistanceFromAvoid of a live player unit (same distance FindSpawnPosition
		/// itself steers individual actors away from); if every attempt still landed too
		/// close, keeps whichever attempt ended up farthest from the nearest player unit,
		/// rather than failing to pick a target at all.
		/// </summary>
		private static Vector3 PickRandomAreaTarget(Vector3 centroid, float mapRadius, List<Vector3> avoidPositions, float minDistanceFromAvoid)
		{
			float areaRadius = mapRadius * 0.5f;
			const int maxAttempts = 12;

			Vector3 bestCandidate = centroid;
			float bestDistanceFromAvoid = -1f;
			for (int attempt = 0; attempt < maxAttempts; attempt++)
			{
				float angle = UnityEngine.Random.value * Mathf.PI * 2f;
				float distance = Mathf.Sqrt(UnityEngine.Random.value) * areaRadius;
				Vector3 candidate = centroid + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);

				if (avoidPositions == null || avoidPositions.Count == 0)
					return candidate;

				float nearestAvoidDistance = avoidPositions.Min(p => Vector3.Distance(candidate, p));
				if (nearestAvoidDistance >= minDistanceFromAvoid)
					return candidate;

				if (nearestAvoidDistance > bestDistanceFromAvoid)
				{
					bestDistanceFromAvoid = nearestAvoidDistance;
					bestCandidate = candidate;
				}
			}
			Log.Debug($"Could not find a random spawn-area target at least {minDistanceFromAvoid} units from every player unit after {maxAttempts} attempts; using the farthest one found ({bestDistanceFromAvoid:0.0} units away).");
			return bestCandidate;
		}

		/// <summary>
		/// Shared per-template spawn loop, extracted so SpawnAllies (immediate/reinforcement
		/// ally spawns) and SpawnRequestedDebugActivity (Pure/Forsaken squad) don't
		/// duplicate it. Each template spawns in its own try/catch: SpawnActor
		/// calls straight into ActorSpawner.SpawnActor -> DoEnterPlay -> TacticalActor.
		/// OnEnterPlay synchronously, so an exception from any one actor (observed in-game:
		/// Weapon.ActivateEquipmentShootingClips NREs for a vehicle/Technician whose skin/
		/// prefab failed to resolve — the already-documented cosmetic content gap, see
		/// CLAUDE.md) would otherwise propagate up through the loop and abort every
		/// remaining template. Returns how many templates actually spawned.
		/// <paramref name="turnNumber"/> (explicit request — "be sure that you spawn ally
		/// helping soldiers after they arrive on the ground, especially for vehicle"):
		/// after each actor spawns, calls the exact same vanilla method the game's own
		/// reinforcement spawner (TacParticipantSpawn.DoSpawnActor) calls right after its
		/// own SpawnActor — TacParticipantSpawn.AdjustSpawned(Controller, actor,
		/// turnNumber) — which applies TacticalLevelControllerDef.
		/// MissionInitialOnSpawnEffect (turnNumber == 0) or ...ReinforcementOnSpawnEffect
		/// (turnNumber != 0), whichever the current mission's own Def has configured, as a
		/// VFX/sound cue at the actor's spawn position. Confirmed via source
		/// that vanilla reinforcements have no walk-in/landing-animation sequence at all —
		/// they're placed synchronously via the same static SpawnActor call we use, with
		/// this on-spawn effect as the only "arrival" cue that exists — so calling the
		/// real vanilla method (rather than inventing our own arrival sequence) is both
		/// the correct fix and the only one actually grounded in how the game itself
		/// signals "these troops have arrived," vehicles included.
		/// <paramref name="arrivalActionPointsFraction"/> (default 1f) — null skips
		/// queueing the actor into PendingArrivalActionPoints entirely (no arrival AP fix
		/// at all, i.e. vanilla's own partial/leftover AP is left as-is); otherwise the
		/// actor is queued with this fraction (0-1) of its max AP, consumed by
		/// Patch_TacticalActor_StartTurn_GrantArrivalActionPoints (ChaosFactionPatches.cs)
		/// the first time StartTurn() fires for it — i.e. only their actual arrival turn;
		/// every turn after runs through vanilla's own unmodified StartTurn() cascade,
		/// unaffected. SpawnAllies passes 1f (full AP + Will Points) for the immediate
		/// Ask-for-Help spawn always, or ChaosFactionConfig.
		/// PhoenixBaseDefenseAllyReinforcementFirstTurnAP / 100 (default 1f, i.e. 100%) for
		/// the Phoenix-base-defense delayed-reinforcement case specifically — explicit
		/// follow-up request: make the reinforcement case's first-turn AP grant a
		/// configurable 0-100% (not just an on/off toggle), default 100.
		/// SpawnRequestedDebugActivity instead passes
		/// ChaosFactionConfig.ActivityFirstTurnActionPointsPercent / 100 — a deliberately
		/// *partial* grant, AP only (Will Points left alone) — explicit follow-up request:
		/// "For activity only (not for ally help) make spawned units to have configurable
		/// variable from 0 to 100 (default 25%) of AP at their first turn only, all next
		/// turns are unchanged."
		/// <paramref name="minDistanceBetweenSquadmates"/> (default 0 = disabled) — explicit
		/// request: "try to spawn them at least 2 tiles far from each other... same for
		/// reinforcement for base defense mission, for usual ally help support nothing to
		/// change" — a running list of this squad's own already-placed positions is grown
		/// as each template spawns and fed into FindSpawnPosition's squadmatePositions
		/// avoid-group, so later squadmates steer away from earlier ones in the same call.
		/// SpawnAllies' immediate-spawn call site always passes 0 here (unaffected, per the
		/// "nothing to change" half of the request); the delayed reinforcement call site and
		/// SpawnRequestedDebugActivity both pass ChaosFactionConfig.
		/// MinSquadmateSpawnDistanceTiles (converted to world units via WorldUnitsPerTile).
		/// </summary>
		private int SpawnSquad(List<TacCharacterDef> templates, TacticalFactionDef factionDef, TacMissionParticipant participantKind, List<TacticalDeployZone> zones, HashSet<Vector3> usedPositions, List<Vector3> avoidPositions = null, float minDistanceFromAvoid = 0f, int turnNumber = 0, float? arrivalActionPointsFraction = 1f, float minDistanceBetweenSquadmates = 0f)
		{
			int spawned = 0;
			List<Vector3> squadmatePositions = new List<Vector3>();
			// Resolved once per squad (same factionDef for every template in it), not
			// per-template — see AssignRandomName's own doc comment.
			GeoFactionDef namePoolFaction = ResolveGeoFactionDef(factionDef);
			foreach (TacCharacterDef template in templates)
			{
				try
				{
					// A bare ComponentSetDef + null instance data (the original approach)
					// spawns an actor with no equipment/weapon data, which NREs deep in
					// vanilla code (Weapon.ActivateEquipmentShootingClips) as soon as it
					// enters play. GenerateActorDeployData()+InitializeInstanceData() is
					// the same pipeline the game's own "spawn" console command and mission
					// deployment use to build a fully-equipped instance from a TacCharacterDef.
					ActorDeployData deployData = template.GenerateActorDeployData();
					RandomizeLevelAndStats(deployData, template);
					AssignRandomName(deployData, template, namePoolFaction);
					deployData.InitializeInstanceData();
					if (deployData.ComponentSetDef == null)
					{
						Log.Debug($"Template {template.name} produced no ComponentSetDef; skipping.");
						continue;
					}

					// Reported in-game: vehicles/Mutogs could land on an upper floor with no
					// room to actually maneuver — GetDeploymentPositions casts down through
					// every floor stacked at each XZ grid point and GetOrderedSpawnPositions
					// only ever sorts by distance-from-zone-center (or shuffles), never by
					// floor, so a closer upper-floor candidate could beat a farther
					// ground-floor one purely by chance. Vehicles/Mutogs specifically now
					// prefer the lowest floor with room, where they're far more likely to
					// have space to move — regular infantry are unaffected.
					bool preferLowestFloor = template.IsVehicle || template.IsMutog;
					Vector3? spawnPos = FindSpawnPosition(zones, deployData, usedPositions, avoidPositions, minDistanceFromAvoid, squadmatePositions, minDistanceBetweenSquadmates, preferLowestFloor);
					if (spawnPos == null)
					{
						Log.Debug($"No valid spawn position found for template {template.name}; skipping.");
						continue;
					}
					TacticalActorBase actor = TacticalDeployZone.SpawnActor(deployData.ComponentSetDef, deployData.InstanceData, factionDef, participantKind, spawnPos.Value, Quaternion.identity, this);
					SetAlertedOnSpawn(actor, template);
					BoostVehicleAmmo(actor, template);
					if (actor != null)
					{
						NoLootActors.Add(actor);
						if (arrivalActionPointsFraction.HasValue)
							PendingArrivalActionPoints[actor] = Mathf.Clamp01(arrivalActionPointsFraction.Value);
						TacParticipantSpawn.AdjustSpawned(Controller, actor, turnNumber);
					}
					spawned++;
				}
				catch (Exception ex)
				{
					Log.Debug($"Failed to spawn template {template.name}; skipping it and continuing with the rest of the squad: " + ex);
				}
			}
			return spawned;
		}

		/// <summary>
		/// Maps a spawn's TacticalFactionDef back to New Jericho/Anu/Synedrion's own
		/// GeoFactionDef (the asset that actually carries the real per-faction name pool,
		/// GeoFactionDef.Countries — see AssignRandomName), by matching PPFactionDef —
		/// the same identity TacticalFactionDef.FactionDef and GeoFactionDef.PPFactionDef
		/// both carry.
		///
		/// Falls back to a GeoSubFactionDef lookup for anything that isn't one of the
		/// three main factions directly — explicit follow-up request: "roll names for
		/// Bandits same way as for New Jericho/Anu/Synedrion". A GeoSubFactionDef-backed
		/// debug activity (Pure/Forsaken/Bandits) has its own distinct PPFactionDef
		/// (e.g. NEU_Bandits), which never matches a GeoFactionDef directly — but
		/// GeoSubFactionDef.BaseFactionDef is the game's own "parent main faction"
		/// reference for a sub-faction, reused here as its name-pool source the same
		/// way a main faction's own Countries array already is. Written generically
		/// (matches any sub-faction with a BaseFactionDef assigned, not hardcoded to
		/// Bandits specifically) rather than special-casing one sub-faction by name —
		/// simpler, and naturally covers Pure/Forsaken too if their own BaseFactionDef
		/// is set. Returns null (no naming) if neither lookup resolves.
		/// </summary>
		private static GeoFactionDef ResolveGeoFactionDef(TacticalFactionDef factionDef)
		{
			if (factionDef?.FactionDef == null)
				return null;
			DefRepository defRepo = GameUtl.GameComponent<DefRepository>();
			if (defRepo == null)
				return null;

			GeoFactionDef mainFaction = defRepo.GetAllDefs<GeoFactionDef>().FirstOrDefault(f => f.PPFactionDef == factionDef.FactionDef);
			if (mainFaction != null)
				return mainFaction;

			GeoSubFactionDef subFaction = defRepo.GetAllDefs<GeoSubFactionDef>().FirstOrDefault(s => s.PPFactionDef == factionDef.FactionDef);
			return subFaction?.BaseFactionDef;
		}

		/// <summary>
		/// Random soldier names — explicit request: "when you spawn soldiers by this
		/// mod for help request and activity to randomly assign names to Anu/Synedrion/
		/// New Jericho soldiers (ignore vehicle/mutog)", later extended to Bandits (and,
		/// generically, any sub-faction with a BaseFactionDef — see ResolveGeoFactionDef).
		/// Reuses the game's own real per-faction name pool (GeoFactionDef.Countries, an
		/// array of CountryDef assets each with Male/FemaleFirstNames +
		/// Male/FemaleFamilyNames) via CharacterIdentity.GenerateRandomNameByFaction —
		/// the exact same static helper the normal recruit pipeline
		/// (FactionCharacterGenerator.RandomizeIdentity) itself calls — rather than
		/// inventing a name list of this mod's own. namePoolFaction is null for anything
		/// that doesn't resolve at all (see ResolveGeoFactionDef), and vehicles/Mutogs
		/// are skipped outright per the explicit "ignore vehicle/mutog" instruction — a
		/// display name has no real meaning for either the way it does for a person.
		/// Must run before InitializeInstanceData() — same requirement
		/// RandomizeLevelAndStats already has (see its own doc comment): that's the call
		/// that bakes ActorInstance into the actual instance data, so a write after it
		/// would be silently ignored.
		/// </summary>
		private static void AssignRandomName(ActorDeployData deployData, TacCharacterDef template, GeoFactionDef namePoolFaction)
		{
			if (namePoolFaction == null || template.IsVehicle || template.IsMutog)
				return;
			if (namePoolFaction.Countries == null || namePoolFaction.Countries.Length == 0)
				return;
			if (deployData?.ActorInstance == null)
				return;
			try
			{
				GeoCharacterSex sex = CharacterIdentity.GetRandomSex();
				string name = CharacterIdentity.GenerateRandomNameByFaction(namePoolFaction, sex);
				if (string.IsNullOrEmpty(name))
					return;
				deployData.ActorInstance.Name = name;
				deployData.ActorInstance.LocalizeName = false;
				Log.Debug($"Assigned random name '{name}' ({sex}) to a {namePoolFaction.name} {template.name}.");
			}
			catch (Exception ex)
			{
				Log.Debug($"Failed to assign a random name to {template.name}: " + ex);
			}
		}

		/// <summary>
		/// For an immediate spawn, unchanged from before: the player's own full deploy
		/// zone set (a runtime-created helper TacticalFaction has none of its own — see
		/// CLAUDE.md). For a delayed reinforcement, per explicit request ("they should come
		/// in entrance edge on map where is entrance to base"): only zones flagged
		/// TacticalDeployZone.IsReinforcementZone — the same flag vanilla's own
		/// TacParticipantSpawn reinforcement pipeline uses to mean "an off-map entrance
		/// point new forces can arrive at after turn 0" (confirmed via source,
		/// TacParticipantSpawn.cs — GetAllowedZonePositions filters zones by this exact
		/// flag). Whether Phoenix base defense maps actually flag any zones this way for
		/// the *player* side specifically (vanilla mainly uses it for enemy reinforcement
		/// waves) isn't verifiable from C# alone — that's authored per-map/
		/// per-mission-Def scene data — so this falls back to the player's full deploy zone
		/// set (same as the immediate-spawn case) if no reinforcement-flagged zone is found
		/// on the map at all, rather than failing to spawn the reinforcements outright.
		/// Uses UnityEngine.Object.FindObjectsOfType directly (not TacticalFaction.
		/// DeployZones or TacticalMap.GetTacActors, both of which only return zones already
		/// assigned to a specific faction) since reinforcement zones need to be found
		/// regardless of which faction's Def happens to reference them.
		/// </summary>
		private static List<TacticalDeployZone> GetSpawnZones(TacticalFaction playerFaction, bool useReinforcementZones)
		{
			if (useReinforcementZones)
			{
				List<TacticalDeployZone> reinforcementZones = UnityEngine.Object.FindObjectsOfType<TacticalDeployZone>()
					.Where(z => z.IsReinforcementZone)
					.ToList();
				if (reinforcementZones.Count > 0)
				{
					Log.Debug($"Found {reinforcementZones.Count} reinforcement-flagged deploy zone(s) on the map for the ally reinforcement spawn.");
					return reinforcementZones;
				}
				Log.Debug("No reinforcement-flagged deploy zones found on the map; falling back to the player's own deploy zones for the ally reinforcement spawn.");
			}
			return playerFaction?.DeployZones.ToList() ?? new List<TacticalDeployZone>();
		}

		/// <summary>
		/// Freshly spawned allies otherwise start unalerted and only react once their own
		/// AI perception actually spots a Pandoran, which can leave them standing idle for
		/// a turn or two right after landing in the middle of a fight. TacAIActor.IsAlerted
		/// (PhoenixPoint/Tactical/AI/TacAIActor.cs:62) is the same flag vanilla's own AI
		/// uses to gate reactive behavior; TacCharacterData.GenerateInstanceData() never
		/// populates TacActorInstanceData.AIActorData at all, so it's left at its default
		/// (AIActorData.IsAlerted = false) for every generic-pipeline spawn, ally or enemy.
		/// Setting it directly on the live TacAIActor component right after SpawnActor
		/// returns applies the same AlertedStatusDef status vanilla's own alert system would
		/// (TacAIActor.SetAlerted), immediately, rather than waiting for perception to catch
		/// up. Guarded by a null check since AIActor is null for any actor without an AI
		/// component (shouldn't happen for these templates, but SpawnActor can return null
		/// on its own failure paths too).
		/// </summary>
		private static void SetAlertedOnSpawn(TacticalActorBase actor, TacCharacterDef template)
		{
			if (actor?.AIActor == null)
			{
				Log.Debug($"Ally template {template.name} has no AIActor component to alert (or failed to spawn).");
				return;
			}
			actor.AIActor.IsAlerted = true;
		}

		// GrantFullActionPoints (the pre-spawn "call RestartAbilities()+SetToMax()
		// ourselves, right after SpawnActor" fix) used to live here — removed once a
		// Player.log capture (via the since-repurposed Patch_TacticalActor_StartTurn_Debug)
		// proved it wasn't enough: reported in-game, a Phoenix-base-defense reinforcement
		// squad still skipped its own turn on arrival, and every spawned infantry actor
		// showed a small non-max Action Points value (e.g. 4/19) *after*
		// TacticalActor.StartTurn() ran for their faction's own turn — only the vehicle
		// template was unaffected.
		//
		// Actual root cause: TacticalActor.ProcessInstanceData (called during SpawnActor,
		// so *before* any post-spawn fixup this mod could run) adds the ability trait
		// "DoNotResetThisTurn" to any actor whose TacticalFaction already equals
		// TacticalLevel.CurrentFaction at that exact moment — which, for this mod's
		// reinforcement case specifically, is *guaranteed* true, because
		// GetOrCreateHelperFaction had just Factions.Insert-ed the helper faction at
		// CurrentFaction's own list index (see OnPlayerTurnAboutToStart) *before* SpawnSquad's
		// SpawnActor calls run. TacticalActor.RestartAbilities() (called from StartTurn(),
		// and also by the old pre-spawn attempt) checks that trait first: if present, it
		// *skips* ActionPoints.SetToMax() entirely and just consumes the trait — a real
		// vanilla mechanic (presumably meant to stop a mid-turn-injected actor from getting
		// a full extra turn's worth of AP on the same turn it was added), but one that
		// fights this mod's own explicit "full turn on arrival" request ("I want when ally
		// reinforcement arrive to have full turn to do"). A pre-spawn SetToMax() call can't
		// out-race this, since ProcessInstanceData (and the trait it adds) always runs
		// *first*, inside SpawnActor, before this mod's own code gets the returned actor
		// reference back.
		//
		// Real fix: Patch_TacticalActor_StartTurn_GrantArrivalActionPoints
		// (ChaosFactionPatches.cs) is a Postfix on TacticalActor.StartTurn() itself —
		// running *after* vanilla's own StartTurn()/RestartAbilities() cascade (and
		// whatever it did or didn't do to AP) has fully finished — so it always gets the
		// last word. It only acts on actors in PendingArrivalActionPoints (populated here in
		// SpawnSquad, removed the instant the patch consumes it), so it fires exactly once
		// per actor — their actual arrival turn — and never overrides a legitimate *later*
		// AP reduction (stun, fatigue, etc.) on any turn after that.

		/// Flat per-level stat bonus ranges (inclusive), hardcoded rather than exposed to
		/// config: Strength +1-2/level, Will +0-1/level, Speed +0-1/level.
		private const int MinStrengthPerLevel = 1;
		private const int MaxStrengthPerLevel = 2;
		private const int MinWillPerLevel = 0;
		private const int MaxWillPerLevel = 1;
		private const int MinSpeedPerLevel = 0;
		private const int MaxSpeedPerLevel = 1;

		/// <summary>
		/// Rolls a random level (1..MaxLevel) for a freshly-generated ActorDeployData,
		/// re-grants abilities up to that level, and adds a flat per-level bonus to base
		/// stats (Strength/Will/Speed — TacCharacterData.BonusStats maps these to
		/// Endurance/Willpower/Speed, i.e. health/will/movement). Must run before
		/// InitializeInstanceData(), since that's what bakes ActorInstance into the actual
		/// spawned InstanceData (TacCharacterData.GenerateInstanceData() copies Strength/
		/// Will/Speed into BaseStrength/BaseWill/BaseSpeed at that point) — changes made
		/// after wouldn't take effect. Ability re-granting mirrors TacCharacterDef's own
		/// private ApplyPogression (called once already, at the template's un-rolled
		/// baseline level, inside GenerateActorDeployData()) since that method isn't
		/// accessible to call directly. Each of Strength/Will/Speed rolls its own
		/// independent set of per-level bonuses (level determined first, then one
		/// independent [min, max] roll per level, summed — see RollStat) rather than
		/// sharing one roll across all three, so a soldier can come out favoring one stat
		/// over another.
		/// </summary>
		private static void RandomizeLevelAndStats(ActorDeployData deployData, TacCharacterDef template)
		{
			TacCharacterData data = deployData.ActorInstance as TacCharacterData;
			LevelProgression progression = data?.LevelProgression;
			if (progression == null || !progression.IsValid)
				return;

			int level = UnityEngine.Random.Range(1, progression.Def.MaxLevel + 1);
			progression.SetLevel(level);

			SpecializationDef specialization = GameUtl.GameComponent<DefRepository>().GetAllDefs<SpecializationDef>()
				.FirstOrDefault(s => s.ClassTag == template.ClassTag);
			if (specialization != null)
			{
				List<TacticalAbilityDef> abilities = specialization.GetAbilitiesTillLevel(progression.Level).ToList();
				data.Abilites = data.Abilites.Concat(abilities).Distinct().ToArray();
			}

			AssignRandomPersonalAbilities(data, template, level);

			Log.Debug($"Ally template {template.name} rolled level {level}/{progression.Def.MaxLevel}:");
			data.Strength = RollStat("Strength", data.Strength, level, MinStrengthPerLevel, MaxStrengthPerLevel);
			data.Will = RollStat("Will", data.Will, level, MinWillPerLevel, MaxWillPerLevel);
			data.Speed = RollStat("Speed", data.Speed, level, MinSpeedPerLevel, MaxSpeedPerLevel);

			GrantSpareAmmo(data);
		}

		/// Level thresholds AssignRandomPersonalAbilities grants one more distinct
		/// random personal ability at — explicit follow-up requests: originally 1/4,
		/// then 1/3/5/7, then "I want it to be on lvl 1, 2, 3, 5, 7 (5 random
		/// abilities)".
		private static readonly int[] PersonalAbilityLevelThresholds = { 1, 2, 3, 5, 7 };

		/// <summary>
		/// Random class-independent "Personal Ability Track" abilities — explicit
		/// request: "There are pasive traits abilities not related to soldier
		/// classes... assign random ability to any soldier (not vehicle/mutog)". Level
		/// thresholds (PersonalAbilityLevelThresholds) were originally 1/4, then
		/// 1/3/5/7, now 1/2/3/5/7 (5 total). Confirmed via source this is a real,
		/// data-driven vanilla mechanic (AbilityTrackSource.Personal) rather than
		/// something invented for this feature: TacticalAbilityDefs tagged via
		/// CharacterProgressionData.PersonalTrackTags containing
		/// SharedGameTagsDataDef.PersonalProgressionTag are the exact same pool
		/// vanilla's own AbilityTrack.CreatePersonalAbilityTrack/
		/// FactionCharacterGenerator.GeneratePersonalAbilities draw from when
		/// generating a roster recruit's random Will-tree abilities — reused here
		/// rather than inventing a mod-owned ability list. Writes into the same
		/// TacCharacterData.Abilites array the class-ability grant above already
		/// targets (there's no separate "traits" list on the character — class
		/// abilities, personal-track abilities, and anything else all funnel into that
		/// one array). None of these thresholds are vanilla-significant for this
		/// system — real slot placement is fully random across all levels in vanilla —
		/// so they're purely this mod's own choice, applied against the level
		/// RandomizeLevelAndStats itself just rolled (not a separate roll): one more
		/// distinct random ability for every threshold the rolled level meets or
		/// exceeds (so a level 7+ soldier gets all five; a level 2 soldier gets two).
		/// </summary>
		private static void AssignRandomPersonalAbilities(TacCharacterData data, TacCharacterDef template, int level)
		{
			if (template.IsVehicle || template.IsMutog)
				return;

			List<TacticalAbilityDef> pool = GetPersonalAbilityPool();
			if (pool == null || pool.Count == 0)
				return;

			List<TacticalAbilityDef> granted = new List<TacticalAbilityDef>();
			foreach (int threshold in PersonalAbilityLevelThresholds)
			{
				if (level < threshold)
					break;
				List<TacticalAbilityDef> remaining = pool.Where(a => !granted.Contains(a)).ToList();
				TacticalAbilityDef picked = (remaining.Count > 0 ? remaining : pool).GetRandomElement(null);
				if (picked != null && !granted.Contains(picked))
					granted.Add(picked);
			}
			if (granted.Count == 0)
				return;

			data.Abilites = data.Abilites.Concat(granted).Distinct().ToArray();
			Log.Debug($"  Granted {granted.Count} random personal ability/abilities: {string.Join(", ", granted.Select(a => a.name))}");
		}

		/// <summary>
		/// The "Personal Ability Track" pool — every TacticalAbilityDef tagged as
		/// class-independent, the same filter vanilla's own AbilityTrack.
		/// CreatePersonalAbilityTrack/FactionCharacterGenerator.GeneratePersonalAbilities
		/// use (AbilityTrack.cs/FactionCharacterGenerator.cs). Recomputed on every call
		/// rather than cached — this mod's other Def lookups (SpecializationDef above,
		/// etc.) follow the same "just re-query DefRepository" pattern, and this only
		/// ever runs once per spawned soldier, not per frame.
		/// </summary>
		private static List<TacticalAbilityDef> GetPersonalAbilityPool()
		{
			DefRepository defRepo = GameUtl.GameComponent<DefRepository>();
			SharedData sharedData = GameUtl.GameComponent<SharedData>();
			var personalTag = sharedData?.SharedGameTags?.PersonalProgressionTag;
			if (defRepo == null || personalTag == null)
				return null;
			return defRepo.GetAllDefs<TacticalAbilityDef>()
				.Where(a => a.CharacterProgressionData != null
					&& a.CharacterProgressionData.PersonalTrackTags != null
					&& a.CharacterProgressionData.PersonalTrackTags.Contains(personalTag))
				.ToList();
		}

		/// <summary>
		/// Rolls the stat's bonus as `level` independent picks from [minPerLevel,
		/// maxPerLevel] (inclusive), summed — not one rate rolled once and multiplied by
		/// level, which was a bug: that made every level-worth of bonus use the exact same
		/// rolled rate, so a level 5 soldier's total was always precisely 5x whatever single
		/// value got rolled, with no independent variance between levels. Determine the
		/// unit's level first (done by the caller before this runs), then roll separately
		/// per level and sum, the same way rolling N dice and summing differs from rolling
		/// one die and multiplying by N.
		/// </summary>
		private static int RollStat(string statName, int baseValue, int level, int minPerLevel, int maxPerLevel)
		{
			int totalBonus = 0;
			for (int i = 0; i < level; i++)
				totalBonus += UnityEngine.Random.Range(minPerLevel, maxPerLevel + 1);
			int newValue = baseValue + totalBonus;
			Log.Debug($"  {statName}: {baseValue} -> {newValue} (+{totalBonus} total, {level} independent roll(s) in [{minPerLevel}, {maxPerLevel}])");
			return newValue;
		}

		/// Spare ammo clips granted per equipped weapon, so allies can actually reload.
		private const int SpareAmmoClipsPerWeapon = 4;

		/// Best-effort approximation used to convert ChaosFactionConfig.
		/// MinSquadmateSpawnDistanceTiles (a tile count, matching how a player thinks about
		/// grid spacing) into a world-space distance FindSpawnPosition can actually compare
		/// against — the game's own tile size (TacTileGraphBuilderDef.NodeSize) is per-map
		/// nav-mesh Def data, not a fixed constant readable from C#, so this is a
		/// documented assumption rather than a confirmed value.
		private const float WorldUnitsPerTile = 2f;

		/// <summary>
		/// Generic troopers otherwise carry zero spare ammo: TacCharacterData.InventoryItems
		/// defaults to an empty array, and TacCharacterData.FromItemDefs (called internally
		/// by GenerateInstanceData, not accessible directly) only ever bakes in the ONE
		/// magazine's worth of ammo that comes pre-loaded in the weapon itself
		/// (EquipmentItemsData[].Ammo) — nothing extra to reload with once that runs out.
		/// ReloadAbility is a fully generic TacticalAbility (not player-only): it finds
		/// spare TacticalItem ammo clips in Inventory/Equipments matching the weapon's own
		/// ItemDef.CompatibleAmmunition and reloads with them (ReloadAbility.cs,
		/// FindAmmoClipForEquipment). AI-controlled actors pick abilities through the same
		/// generic TacticalAbility framework as player-controlled ones, so once there's
		/// something to reload with, the AI uses it autonomously — no separate AI-behavior
		/// work needed, just giving them ammo to find. Mirrors TacCharacterData.
		/// FromItemDefs's own logic for picking which ammo type fits a weapon (its first
		/// CompatibleAmmunition entry). Must run before InitializeInstanceData(), same as
		/// the stat/level changes above, since that's what converts InventoryItems (ItemDef[])
		/// into the actual spawned instance's inventory.
		/// </summary>
		private static void GrantSpareAmmo(TacCharacterData data)
		{
			List<ItemDef> spareAmmo = new List<ItemDef>();
			foreach (ItemDef weaponDef in data.EquipmentItems.Where(d => d.CompatibleAmmunition != null && d.CompatibleAmmunition.Length > 0))
			{
				ItemDef ammoDef = weaponDef.CompatibleAmmunition[0];
				for (int i = 0; i < SpareAmmoClipsPerWeapon; i++)
					spareAmmo.Add(ammoDef);
			}
			if (spareAmmo.Count == 0)
				return;

			data.InventoryItems = data.InventoryItems.Concat(spareAmmo).ToArray();
			Log.Debug($"  Granted {spareAmmo.Count} spare ammo clip(s): {string.Join(", ", spareAmmo.Select(a => a.name).Distinct())}");
		}

		/// Extra charges added on top of each of a vehicle's already-loaded ammo magazines.
		private const int VehicleAmmoChargesPerMagazine = 900;

		/// <summary>
		/// Vehicle AI apparently never invokes ReloadAbility on its own turret weapon the
		/// way infantry AI does (GrantSpareAmmo's extra Inventory clips work for infantry —
		/// ReloadAbility is a fully generic TacticalAbility, see its own doc comment above —
		/// but reported in-game as not helping allied vehicles at all). Rather than depend
		/// on a reload ever happening for a vehicle, this inflates the ammo the vehicle's
		/// weapon already starts pre-loaded with, so it should never run dry within a
		/// single mission regardless of whether the AI ever reloads.
		///
		/// **v1 (reverted — caused 0 ammo in-game, confirmed by the player) tried to do
		/// this before spawn**, by setting each ItemData.Charges in the pre-generated
		/// TacActorInstanceData.EquipmentItems[*].Ammo list to a huge value. That data is
		/// converted into the actual spawned AmmoManager by CommonItemData.LoadFromData,
		/// which calls AmmoManager.LoadMagazine(...) once per Ammo entry —
		/// and LoadMagazine self-evicts: `if (ParentItem.ItemDef.ChargesMax <
		/// CurrentCharges) LoadedMagazines.RemoveAt(0)`, comparing the *cumulative* loaded
		/// total against the WEAPON's own (shared, Def-level) ChargesMax (its normal total
		/// ammo capacity, e.g. 60 — nowhere close to 999). Every inflated magazine
		/// immediately overflowed that check and got evicted the instant it was loaded —
		/// including the last one, leaving `LoadedMagazines` empty and the weapon's
		/// AmmoManager.CurrentCharges (sum of TotalCharges across LoadedMagazines) at 0.
		/// There's no way to feed an inflated amount through this pre-spawn path without
		/// tripping that same eviction, since it fires on every LoadMagazine call
		/// regardless of how the inflated total is split across entries.
		///
		/// **v2 (current) instead inflates the magazines that are already successfully
		/// loaded, post-spawn**, on the live actor's Equipment components — reached via
		/// `TacticalActor.Equipments.Equipments` (List&lt;Equipment&gt;, Weapon : Equipment)
		/// — by calling `CommonItemData.ModifyCharges` directly on each already-loaded
		/// magazine `ICommonItem` (`AmmoManager.LoadedMagazines`), instead of routing
		/// through `AmmoManager.LoadMagazine`/`ReloadCharges` at all. Both of those methods
		/// cap against the weapon's own ChargesMax (`ReloadCharges` explicitly refuses to
		/// exceed it; `LoadMagazine` evicts, as above) — but `ModifyCharges` called
		/// directly on a magazine item only checks *that magazine's own* ItemDef.ChargesMax
		/// (the per-clip size, e.g. 20), converting any overflow into that item's internal
		/// stack Count rather than refusing it, and never invokes LoadMagazine at all, so
		/// the eviction path is never reached. Must run after SpawnActor (unlike
		/// RandomizeLevelAndStats/GrantSpareAmmo, which edit pre-spawn TacCharacterData
		/// fields) since there's no pre-spawn structure that can express "add this much
		/// ammo without tripping LoadMagazine's cap" — only the live, already-loaded
		/// AmmoManager state can be edited this way. Only touches TacCharacterDef.IsVehicle
		/// actors (infantry keep the existing spare-clips/reload path) and only equipped
		/// items that actually have loaded magazines (i.e. ammo-consuming weapons —
		/// melee/unarmed equipment has none and is left untouched).
		/// </summary>
		private static void BoostVehicleAmmo(TacticalActorBase actor, TacCharacterDef template)
		{
			if (!template.IsVehicle)
				return;

			TacticalActor tacticalActor = actor as TacticalActor;
			if (tacticalActor?.Equipments == null)
			{
				Log.Debug($"Vehicle template {template.name} has no EquipmentComponent to boost ammo on (or failed to spawn).");
				return;
			}

			int boosted = 0;
			foreach (Equipment equipment in tacticalActor.Equipments.Equipments)
			{
				AmmoManager ammo = equipment.CommonItemData?.Ammo;
				if (ammo == null || ammo.LoadedMagazines.Count == 0)
					continue;

				foreach (ICommonItem magazine in ammo.LoadedMagazines)
					magazine.CommonItemData.ModifyCharges(VehicleAmmoChargesPerMagazine, false);
				boosted++;
				Log.Debug($"  Vehicle weapon {equipment.ItemDef.name}: added {VehicleAmmoChargesPerMagazine} charges to {ammo.LoadedMagazines.Count} already-loaded magazine(s) so it never needs to reload.");
			}
			if (boosted == 0)
				Log.Debug($"Vehicle template {template.name} has no ammo-consuming equipped weapon to boost.");
		}

		/// <summary>
		/// Which TacMissionParticipant kind is "the enemy of Player" varies by mission type:
		/// base defense missions tag the attacking hostile faction as Intruder, but alien
		/// base assaults tag the aliens (the site's own occupants, defending home turf) as
		/// Residents instead — mirroring the same Intruder-then-Residents priority order
		/// vanilla's own GeoMission.GetEnemyParticipantData() uses against the same
		/// MutualParticipantsRelations data. Hardcoding Intruder here was the root cause of
		/// allies spawning "neutral" instead of hostile toward Pandorans specifically on
		/// alien base assaults: GetTacticalFaction(Intruder) returned null (no Intruder
		/// participant on that mission type), so the Enemy relation was silently never set.
		/// </summary>
		private static TacMissionParticipant ResolveEnemyParticipant(TacMissionTypeDef missionType)
		{
			MutualParticipantsRelations[] candidates = missionType.ParticipantsRelations
				.Where(r => r.MutualRelation == FactionRelation.Enemy && r.HasParticipant(TacMissionParticipant.Player))
				.OrderBy(r => r.HasParticipant(TacMissionParticipant.Intruder) ? 0 : (r.HasParticipant(TacMissionParticipant.Residents) ? 1 : 2))
				.ToArray();
			if (candidates.Length == 0)
				return TacMissionParticipant.None;
			MutualParticipantsRelations relation = candidates[0];
			return relation.FirstParticipant == TacMissionParticipant.Player ? relation.SecondParticipant : relation.FirstParticipant;
		}

		/// <summary>
		/// <paramref name="insertBeforeFaction"/> — passed by every call site in this file
		/// now (SpawnAllies for both the immediate and delayed-reinforcement ally-help
		/// cases, and SpawnRequestedDebugActivity, all resolving it to the mission's own
		/// enemy TacticalFaction; explicit follow-up request: "make those spawned units to
		/// act before pandorans, the same way as in base defense mission") — places the
		/// freshly-created faction into Controller.Factions at insertBeforeFaction's own
		/// current list index, rather than appending it to the end. TacticalLevelController.
		/// CurrentFaction is computed as Factions[_currentFactionIndex] (a private int,
		/// unreadable from mod code, but we don't need it — inserting at Factions.
		/// IndexOf(insertBeforeFaction) shifts insertBeforeFaction itself to the next index
		/// and makes our own faction the one found at the *old* index instead) — so the
		/// very next time NextTurnCrt's loop reads CurrentFaction, it resolves to the
		/// newly-inserted faction instead of insertBeforeFaction, giving it a genuine turn
		/// immediately, ahead of whoever would otherwise have gone next (in practice, the
		/// mission's own Pandoran attacker). Falls back to a plain append (Factions.Add) if
		/// insertBeforeFaction is null (no resolvable enemy participant on this mission
		/// type) or not found in Factions. Only applies to a freshly-created faction — if an
		/// existing one is reused (the branch just below), its position is left exactly as
		/// it already is, since it may already be mid-round or have already played.
		/// </summary>
		private static TacticalFaction GetOrCreateHelperFaction(TacticalLevelController controller, PPFactionDef helperDef, TacMissionParticipant enemyParticipant, TacticalFaction insertBeforeFaction = null)
		{
			TacticalFaction existing = controller.Factions.FirstOrDefault(f => f.TacticalFactionDef.FactionDef == helperDef);
			if (existing != null)
			{
				// The helper faction is already a participant in this mission for some other
				// reason (e.g. they're the attacker on a base-defense raid — the Geoscape
				// side should already refuse to offer help in that case, but this is a
				// last-resort guard). Reusing that faction would silently repurpose
				// whatever role it already has (Player/the enemy's own slot/etc.) as "our
				// ally", corrupting its relations and mixing deploy pools — refuse instead.
				if (existing.ParticipantKind == TacMissionParticipant.Player || existing.ParticipantKind == enemyParticipant)
				{
					Log.Debug($"{helperDef.name} is already a '{existing.ParticipantKind}' participant in this mission; refusing to reuse it as the ally faction.");
					return null;
				}
				return existing;
			}

			TacticalFactionDef tacticalFactionDef = controller.TacticalLevelControllerDef.GetAllFactions().FirstOrDefault(tf => tf.FactionDef == helperDef);
			if (tacticalFactionDef == null)
			{
				Log.Debug($"No TacticalFactionDef found for {helperDef.name}; cannot spawn allies.");
				return null;
			}

			// Use whichever of the two "third-party" slots the enemy isn't already
			// occupying, so the helper faction's own ParticipantKind never collides with the
			// actual opposing faction's bucket (Residents is what the aliens hold on an
			// alien base assault, so the helper uses Intruder there instead; Intruder is
			// what attackers hold on a base defense, so the helper uses Residents there).
			TacMissionParticipant helperParticipantKind = enemyParticipant == TacMissionParticipant.Residents
				? TacMissionParticipant.Intruder
				: TacMissionParticipant.Residents;

			TacticalFaction faction = controller.Repo.Instantiate<TacticalFaction>(tacticalFactionDef, null, null, null, false);
			faction.SetTacticalLevel(controller);
			faction.ParticipantKind = helperParticipantKind;

			int insertIndex = insertBeforeFaction != null ? controller.Factions.IndexOf(insertBeforeFaction) : -1;
			if (insertIndex >= 0)
			{
				controller.Factions.Insert(insertIndex, faction);
				Log.Debug($"Inserted {helperDef.name}'s helper faction into the turn order immediately ahead of {insertBeforeFaction.TacticalFactionDef.GetName()}.");
			}
			else
			{
				controller.Factions.Add(faction);
			}

			// Vanilla's own level-start sequence calls this once for every faction that
			// exists at that point (TacticalLevelController's LevelCrt: "this.Factions.
			// ForEach(f => f.OnLevelStart())") — a faction created later, at runtime, never
			// gets it otherwise. Missing this is a likely root cause (found while
			// investigating a reported bug: a reinforcement squad spawned mid-mission
			// skipped its own turn entirely) — TacticalFactionVision.OnLevelStart wires up
			// the event subscriptions (ActorEnteredPlayEvent etc.) this faction's own
			// visibility tracking depends on and seeds it with every actor already on the
			// map; without it, an AI-controlled faction has no working perception of any
			// enemy, so PlayTurnCrt's AIUpdateCrt has nothing to react to and the turn
			// completes without any action — reads exactly like "skipped its turn". Called
			// here, before any of this mod's own SpawnActor calls for this faction, so the
			// freshly-wired ActorEnteredPlayEvent subscription also picks up the squad about
			// to be spawned into it.
			faction.OnLevelStart();
			return faction;
		}

		/// <summary>
		/// <paramref name="avoidPositions"/>/<paramref name="minDistanceFromAvoid"/> are only
		/// passed by the debug-activity spawn path (reported in-game: activity squads were
		/// sometimes appearing right next to the player's own units instead of near the
		/// map's center) — SpawnAllies' own immediate-spawn call site never passes them, so
		/// its behavior is unchanged (an empty/null avoid list always leaves `tooClose`
		/// false, so the first candidate in the existing center-sorted zone order is still
		/// picked immediately, same as before this parameter existed). When avoiding, a
		/// candidate within minDistanceFromAvoid of any position in avoidPositions is
		/// skipped in favor of one that isn't — but the very first skipped-for-being-too-
		/// close candidate is still remembered as a fallback, so a map where every valid
		/// position happens to be close to a player unit still spawns the actor there rather
		/// than not spawning it at all.
		///
		/// <paramref name="squadmatePositions"/>/<paramref name="minDistanceFromSquadmates"/>
		/// are a second, independent avoid-group with its own threshold — reported in-game:
		/// squadmates within one spawn call (activity squads, and Phoenix-base-defense
		/// delayed reinforcements) could land right next to each other. Same skip-then-
		/// fallback logic as the player-avoidance group above, evaluated separately so the
		/// two groups can use different distances; a candidate only counts as "good" if it
		/// clears *both* checks. SpawnSquad grows squadmatePositions as each actor in the
		/// same squad is placed, so later squadmates avoid earlier ones too, not just the
		/// original list's contents. SpawnAllies' immediate-spawn call site passes 0 for
		/// minDistanceFromSquadmates (disabled — explicit request: "for usual ally help
		/// support nothing to change"), so this is a pure no-op there.
		///
		/// <paramref name="preferLowestFloor"/> (default false, SpawnSquad passes true only
		/// for vehicle/Mutog templates) — reported in-game: a vehicle/Mutog could spawn on
		/// an upper floor with no room to maneuver. Tries FindSpawnPositionOnLowestFloor
		/// first; only falls through to the normal any-floor search below if that finds
		/// nothing usable (every ground-floor candidate too close to avoid/squadmates, or
		/// the deploy zones simply have none), so a vehicle still spawns somewhere rather
		/// than not spawning at all.
		/// </summary>
		private static Vector3? FindSpawnPosition(List<TacticalDeployZone> zones, ActorDeployData deployData, HashSet<Vector3> usedPositions, List<Vector3> avoidPositions = null, float minDistanceFromAvoid = 0f, List<Vector3> squadmatePositions = null, float minDistanceFromSquadmates = 0f, bool preferLowestFloor = false)
		{
			if (preferLowestFloor)
			{
				Vector3? lowestFloorPick = FindSpawnPositionOnLowestFloor(zones, deployData, usedPositions, avoidPositions, minDistanceFromAvoid, squadmatePositions, minDistanceFromSquadmates);
				if (lowestFloorPick.HasValue)
					return lowestFloorPick;
			}

			Vector3? fallback = null;
			float minDistanceSqr = minDistanceFromAvoid * minDistanceFromAvoid;
			float minSquadmateDistanceSqr = minDistanceFromSquadmates * minDistanceFromSquadmates;
			foreach (TacticalDeployZone zone in zones)
			{
				IReadOnlyCollection<Vector3> candidates = zone.GetValidSpawnPosition(deployData.ComponentSetDef, deployData.DeploymentTags, zone.GetOrderedSpawnPositions(false));
				foreach (Vector3 candidate in candidates)
				{
					if (usedPositions.Contains(candidate))
						continue;
					bool tooCloseToAvoid = avoidPositions != null && avoidPositions.Count > 0
						&& avoidPositions.Any(p => (candidate - p).sqrMagnitude < minDistanceSqr);
					bool tooCloseToSquadmate = squadmatePositions != null && squadmatePositions.Count > 0
						&& squadmatePositions.Any(p => (candidate - p).sqrMagnitude < minSquadmateDistanceSqr);
					if (!tooCloseToAvoid && !tooCloseToSquadmate)
					{
						usedPositions.Add(candidate);
						squadmatePositions?.Add(candidate);
						return candidate;
					}
					if (fallback == null)
						fallback = candidate;
				}
			}
			if (fallback.HasValue)
			{
				usedPositions.Add(fallback.Value);
				squadmatePositions?.Add(fallback.Value);
			}
			return fallback;
		}

		/// Positions within this much of the lowest Y found across every candidate still
		/// count as "the same (lowest) floor" — a documented best-effort tolerance for
		/// terrain irregularities (stairs, slopes) within one floor, not a confirmed exact
		/// per-floor height read from game data (TacticalDeployZone.GetDeploymentPositions
		/// casts down through every floor stacked at a given XZ point via
		/// BaseMap.CastAllFloorsAt, but exposes no floor-index/height-per-floor value this
		/// mod can read directly).
		private const float FloorHeightTolerance = 1.5f;

		/// <summary>
		/// Gathers every valid candidate across all zones up front (rather than the normal
		/// per-zone lazy walk) so "the lowest floor" can be determined across the whole
		/// deploy area at once, then only considers candidates within FloorHeightTolerance
		/// of that minimum Y — the closest thing to "the default/ground floor" this mod can
		/// determine without a real floor-index API. Within that filtered set, still tries
		/// candidates in their original per-zone order (nearest-to-zone-center, or shuffled
		/// — same ordering FindSpawnPosition's own any-floor search uses), applying the same
		/// avoid/squadmate checks. Returns null (not a fallback) if no ground-floor
		/// candidate clears those checks, letting the caller fall through to the unrestricted
		/// any-floor search instead of forcing a bad position.
		/// </summary>
		private static Vector3? FindSpawnPositionOnLowestFloor(List<TacticalDeployZone> zones, ActorDeployData deployData, HashSet<Vector3> usedPositions, List<Vector3> avoidPositions, float minDistanceFromAvoid, List<Vector3> squadmatePositions, float minDistanceFromSquadmates)
		{
			List<Vector3> allCandidates = new List<Vector3>();
			foreach (TacticalDeployZone zone in zones)
				allCandidates.AddRange(zone.GetValidSpawnPosition(deployData.ComponentSetDef, deployData.DeploymentTags, zone.GetOrderedSpawnPositions(false)));
			if (allCandidates.Count == 0)
				return null;

			float minY = allCandidates.Min(c => c.y);
			float minDistanceSqr = minDistanceFromAvoid * minDistanceFromAvoid;
			float minSquadmateDistanceSqr = minDistanceFromSquadmates * minDistanceFromSquadmates;
			foreach (Vector3 candidate in allCandidates)
			{
				if (candidate.y > minY + FloorHeightTolerance)
					continue;
				if (usedPositions.Contains(candidate))
					continue;
				bool tooCloseToAvoid = avoidPositions != null && avoidPositions.Count > 0
					&& avoidPositions.Any(p => (candidate - p).sqrMagnitude < minDistanceSqr);
				bool tooCloseToSquadmate = squadmatePositions != null && squadmatePositions.Count > 0
					&& squadmatePositions.Any(p => (candidate - p).sqrMagnitude < minSquadmateDistanceSqr);
				if (tooCloseToAvoid || tooCloseToSquadmate)
					continue;
				usedPositions.Add(candidate);
				squadmatePositions?.Add(candidate);
				return candidate;
			}
			return null;
		}

		/// <summary>Live positions of the player's currently-alive tactical units, for the
		/// debug-activity spawn path's "don't spawn near the player" distance check —
		/// TacticalMap.GetTacActors reads the live map actor registry (spawned, positioned
		/// actors), not deploy-zone/spawn data, so this only ever reflects units actually on
		/// the field right now.</summary>
		private static List<Vector3> GetPlayerUnitPositions(TacticalLevelController controller)
		{
			TacticalFaction playerFaction = controller.GetTacticalFaction(TacMissionParticipant.Player);
			if (playerFaction == null)
				return new List<Vector3>();
			return playerFaction.TacticalActors.Where(a => a != null && a.IsAlive).Select(a => a.Pos).ToList();
		}
	}
}
