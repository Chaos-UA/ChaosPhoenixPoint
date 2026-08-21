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

		public static void Clear()
		{
			KnownUmbras.Clear();
			Processed.Clear();
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
	// For UmbraSpawnTurnActionPoints the restriction only applies on the first turn
	// (Processed set guards subsequent turns).
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

			// First-turn restriction only.
			if (!UmbraTracker.Processed.Add(actor))
				return;

			int apPoints = config.UmbraFirstTurnActionPoints;
			Log.Debug($"[ChaosUmbraBalance] First Umbra action: actor={actor.name}, action={actionName}, SpawnAP={apPoints}.");

			if (apPoints == 0)
			{
				__result = null;
				return;
			}

			if (apPoints >= 4) return;

			float maxAP = actor.CharacterStats.ActionPoints.Max;
			float newAP = (apPoints / 4f) * maxAP;
			Log.Debug($"[ChaosUmbraBalance] Restricting Umbra AP to {newAP} (config={apPoints}/4, max={maxAP}).");
			actor.CharacterStats.ActionPoints.Set(newAP, true);
		}
	}

	// ---------------------------------------------------------------------------
	// Safety net for standard TacticalActorYuggoth spawns via TacticalDeployZone.
	// The TFTV Umbral actors don't use this path, but the real Yuggoth does.
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
	}
}