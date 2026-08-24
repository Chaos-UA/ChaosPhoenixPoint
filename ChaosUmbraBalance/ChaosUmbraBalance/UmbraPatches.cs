using Base.AI;
using Base.Core;
using Base.Entities;
using HarmonyLib;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.ActorDeployment;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ChaosUmbraBalance
{
	internal static class Log
	{
		public static void Debug(string msg)
		{
			if (ChaosUmbraBalanceMain.Instance?.Config?.EnableDebugLog == true)
				UnityEngine.Debug.Log(msg);
		}
	}

	// Tracks cross-turn state for Umbral actors. Cleared when the tactical level ends.
	public static class UmbraTracker
	{
		// Actors identified as Umbral via AI action name detection (persists all turns).
		public static readonly HashSet<TacticalActor> KnownUmbras = new HashSet<TacticalActor>();

		// Actors whose first-turn restriction has already been applied.
		public static readonly HashSet<TacticalActor> Processed = new HashSet<TacticalActor>();

		// AI reinforcement actors queued for a first-turn AP restriction, keyed to the
		// configured percentage of their max AP. Consumed (Remove'd) by
		// Patch_TacticalActor_StartTurn_ReinforcementAP the first time StartTurn() runs
		// for that actor, so only their actual arrival turn is affected.
		public static readonly Dictionary<TacticalActor, int> PendingReinforcementAP = new Dictionary<TacticalActor, int>();

		public static void Clear()
		{
			KnownUmbras.Clear();
			Processed.Clear();
			PendingReinforcementAP.Clear();
		}

		public static bool IsUmbra(TacticalActor actor) => actor != null && KnownUmbras.Contains(actor);
	}

	// ---------------------------------------------------------------------------
	// Primary patch: intercepts AIFaction.SelectTarget, the synchronous method
	// called after AI evaluation completes to pick the best-scored action.
	//
	// The TFTV "Umbral" enemies (Oilfish/Crabmen transformed by UmbralFishmen/
	// UmbralCrabmen_FactionEffectDef) are detected here by checking whether the
	// chosen action def name contains "Umbra". At that point we either:
	//   - Null the result (AP=0 or DisableUmbra): actor's AI loop sees null,
	//     breaks immediately, actor ends its turn with zero actions.
	//   - Set AP to the configured fraction and let the first action proceed:
	//     subsequent evaluations find no affordable action, loop exits naturally.
	//
	// For DisableUmbra the null is applied every turn (permanent suppression).
	// For UmbraFirstTurnActionPoints the restriction only applies on the first turn
	// (Processed set guards subsequent turns); a negative value skips this entirely,
	// leaving vanilla AP untouched.
	// ---------------------------------------------------------------------------
	[HarmonyPatch]
	static class Patch_AIFaction_SelectTarget
	{
		static MethodBase TargetMethod()
		{
			var method = AccessTools.Method(typeof(AIFaction), "SelectTarget");
			UnityEngine.Debug.Log($"[ChaosUmbraBalance] Patch_SelectTarget TargetMethod: {(method == null ? "NULL" : method.DeclaringType.Name + "." + method.Name)}");
			return method;
		}

		static void Postfix(ref AIScoredTarget __result)
		{
			if (__result == null) return;

			var actionName = __result.Action?.ActionDef?.name;
			if (actionName == null || actionName.IndexOf("Umbra", System.StringComparison.OrdinalIgnoreCase) < 0) return;

			var actor = __result.Actor as TacticalActor;
			if (actor == null) return;

			var config = ChaosUmbraBalanceMain.Instance?.Config;
			if (config == null) return;

			UmbraTracker.KnownUmbras.Add(actor);

			if (config.DisableUmbra)
			{
				Log.Debug($"[ChaosUmbraBalance] DisableUmbra: cancelling action '{actionName}' for {actor.name}.");
				__result = null;
				return;
			}

			int apPercent = config.UmbraFirstTurnActionPoints;
			if (apPercent < 0) return; // feature disabled, leave vanilla behavior untouched

			// First-turn restriction only.
			if (!UmbraTracker.Processed.Add(actor))
				return;

			Log.Debug($"[ChaosUmbraBalance] First Umbra action: actor={actor.name}, action={actionName}, SpawnAP%={apPercent}.");

			if (apPercent == 0)
			{
				__result = null;
				return;
			}

			if (apPercent >= 100) return;

			float maxAP = actor.CharacterStats.ActionPoints.Max;
			float newAP = (apPercent / 100f) * maxAP;
			Log.Debug($"[ChaosUmbraBalance] Restricting Umbra AP to {newAP} (config={apPercent}%, max={maxAP}).");
			actor.CharacterStats.ActionPoints.Set(newAP, true);
		}
	}

	// ---------------------------------------------------------------------------
	// Intercepts TacticalDeployZone.SpawnActor, the one method the game funnels
	// every actor spawn through — the mission's initial squad, scheduled
	// reinforcement waves, and ability-triggered ones (e.g. CallReinforcementsAbility)
	// alike. Its last parameter ("source") is the TacticalDeployZone the actor is
	// being placed by whenever the call comes from the normal deploy pipeline
	// (TacParticipantSpawn.DoSpawnActor always passes its zone there); it's null only
	// for a couple of scripted/debug spawn paths (tutorial script, console command)
	// that this mod has no reason to touch.
	//
	// Prefix — safety net for standard TacticalActorYuggoth spawns (DisableUmbra).
	// Postfix — queues AI reinforcements for a first-turn AP restriction.
	// ---------------------------------------------------------------------------
	[HarmonyPatch]
	static class Patch_TacticalDeployZone_SpawnActor
	{
		static MethodBase TargetMethod()
		{
			var method = AccessTools.Method(typeof(TacticalDeployZone), "SpawnActor", new[]
			{
				typeof(ComponentSetDef), typeof(ActorInstanceData), typeof(TacticalFactionDef),
				typeof(TacMissionParticipant), typeof(Vector3), typeof(Quaternion), typeof(object)
			});
			UnityEngine.Debug.Log($"[ChaosUmbraBalance] Patch_SpawnActor TargetMethod: {(method == null ? "NULL" : method.DeclaringType.Name + "." + method.Name)}");
			return method;
		}

		static bool Prefix(ComponentSetDef actorSetDef, ref TacticalActorBase __result)
		{
			var config = ChaosUmbraBalanceMain.Instance?.Config;
			if (config == null || !config.DisableUmbra) return true;

			bool hasYuggothDef = actorSetDef?.GetComponentDef<TacticalActorYuggothDef>() != null
			                  || (actorSetDef?.name?.ToLowerInvariant().Contains("yuggoth") ?? false);

			if (!hasYuggothDef) return true;

			Log.Debug($"[ChaosUmbraBalance] SpawnActor: blocking Yuggoth spawn (setDef={actorSetDef?.name}).");
			__result = null;
			return false;
		}

		// Feature: AP restriction for AI reinforcements on their arrival turn.
		// Only actors deployed through a designated reinforcement zone qualify —
		// TacticalDeployZone.IsReinforcementZone is the same flag the game itself uses
		// to draw the "reinforcements incoming" arrow marker at the edge of the map,
		// and vanilla deployment rules never use these zones for the initial squad
		// (turn 0), only for reinforcements (scheduled or ability-triggered alike) —
		// so this is a more direct signal than trying to infer "reinforcement" from a
		// turn number. The real Yuggoth is excluded; it has its own dedicated AP
		// setting (UmbraFirstTurnActionPoints) applied via Patch_AIFaction_SelectTarget.
		static void Postfix(object source, TacticalActorBase __result)
		{
			var actor = __result as TacticalActor;
			if (actor == null || actor is TacticalActorYuggoth) return;

			var config = ChaosUmbraBalanceMain.Instance?.Config;
			if (config == null) return;

			int apPercent = config.ReinforcementsFirstTurnActionPoints;
			if (apPercent < 0 || apPercent >= 100) return; // feature disabled, or unrestricted

			if (actor.TacticalFaction?.IsControlledByAI != true) return;

			var zone = source as TacticalDeployZone;
			if (zone == null || !zone.IsReinforcementZone) return;

			Log.Debug($"[ChaosUmbraBalance] AI reinforcement spawned: actor={actor.name}, zone={zone.name}, ReinforcementAP%={apPercent}.");
			UmbraTracker.PendingReinforcementAP[actor] = apPercent;
		}
	}

	[HarmonyPatch(typeof(TacticalActor), "StartTurn")]
	static class Patch_TacticalActor_StartTurn_ReinforcementAP
	{
		static void Postfix(TacticalActor __instance)
		{
			if (__instance == null || !UmbraTracker.PendingReinforcementAP.TryGetValue(__instance, out int apPercent))
				return;

			UmbraTracker.PendingReinforcementAP.Remove(__instance);
			if (__instance.CharacterStats == null) return;

			if (apPercent <= 0)
			{
				__instance.CharacterStats.ActionPoints.Set(0f, true);
				Log.Debug($"[ChaosUmbraBalance] Reinforcement {__instance.name}: zeroed arrival AP.");
				return;
			}

			float maxAP = __instance.CharacterStats.ActionPoints.Max;
			float newAP = (apPercent / 100f) * maxAP;
			__instance.CharacterStats.ActionPoints.Set(newAP, true);
			Log.Debug($"[ChaosUmbraBalance] Reinforcement {__instance.name}: restricted arrival AP to {newAP} ({apPercent}% of {maxAP}).");
		}
	}
}