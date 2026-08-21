using System.Collections.Generic;
using Base.Core;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels.Objectives;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace HavenUnderAttackTimer
{
	internal static class Log
	{
		public static void Debug(string msg)
		{
			if (HavenUnderAttackTimerMain.Cfg?.EnableDebugLog == true)
				UnityEngine.Debug.Log("[HavenUnderAttackTimer] " + msg);
		}
	}

	/// <summary>
	/// Tracks which Geoscape objective-panel rows currently represent a haven-under-attack
	/// objective, so the per-frame Update patch knows what to (re)compute without needing a
	/// tooltip hover. Re-keyed every time the panel (re)initializes a row via InitObjective.
	/// </summary>
	internal static class HavenAttackTimerState
	{
		public class Entry
		{
			public GeoHavenDefenseMission Mission;
			public string BaseTitle;
			public int LastAttackerDeployment = int.MinValue;
			public int LastDefenderDeployment = int.MinValue;
		}

		public static readonly Dictionary<GeoObjectiveElementController, Entry> Tracked =
			new Dictionary<GeoObjectiveElementController, Entry>();
	}

	/// <summary>
	/// Whenever the objectives panel (re)builds a row, remember whether it's a haven-under-attack
	/// objective and its live mission reference, so we can append a live-updating time-remaining
	/// suffix straight onto the row's own text (no tooltip involved).
	/// </summary>
	[HarmonyPatch(typeof(UIModuleGeoObjectives), "InitObjective")]
	internal static class InitObjectivePatch
	{
		private static void Prefix(GeoObjectiveElementController element, GeoFactionObjective objective)
		{
			if (objective is MissionGeoFactionObjective missionObjective &&
				missionObjective.Mission is GeoHavenDefenseMission havenMission)
			{
				HavenAttackTimerState.Tracked[element] = new HavenAttackTimerState.Entry
				{
					Mission = havenMission,
					BaseTitle = objective.GetTitle(),
				};
				return;
			}
			HavenAttackTimerState.Tracked.Remove(element);
		}
	}

	/// <summary>
	/// Runs every frame alongside the row's own Update(). Only recomputes the battle projection
	/// when the mission's deployments have actually changed (roughly once per in-game hour tick),
	/// then writes the final text directly - independent of the vanilla endTime/tooltip machinery.
	/// Reuses the same TimeRemainingFormatterDef + TimedObjectiveFormatter the game already ships
	/// with (and already has translated into every supported language) for other timed objectives,
	/// so our text matches vanilla wording/units instead of hardcoded English.
	/// </summary>
	[HarmonyPatch(typeof(GeoObjectiveElementController), "Update")]
	internal static class ObjectiveElementUpdatePatch
	{
		private static void Postfix(GeoObjectiveElementController __instance)
		{
			if (!HavenAttackTimerState.Tracked.TryGetValue(__instance, out HavenAttackTimerState.Entry entry))
			{
				return;
			}
			GeoHavenDefenseMission mission = entry.Mission;
			if (mission.Status != GeoscapeMissionStatus.InProgress)
			{
				HavenAttackTimerState.Tracked.Remove(__instance);
				return;
			}
			int attacker = mission.AttackerDeployment;
			int defender = mission.DefenderDeployment;
			if (attacker == entry.LastAttackerDeployment && defender == entry.LastDefenderDeployment)
			{
				return;
			}
			entry.LastAttackerDeployment = attacker;
			entry.LastDefenderDeployment = defender;

			HavenAttackTimerEstimator.HourRange? range = HavenAttackTimerEstimator.EstimateHoursRemaining(mission);
			string timeText = range.HasValue
				? HavenAttackTimerEstimator.FormatHoursRange(range.Value, __instance.TimeFormatter)
				: __instance.TimeFormatter.InfiniteText.Localize(null);

			string finalText = __instance.TimedObjectiveFormatter.Localize(null)
				.Replace("{Objective}", entry.BaseTitle)
				.Replace("{time}", timeText);
			__instance.ObjectiveText.text = finalText;
			Log.Debug("Updated " + mission.Site.LocalizedSiteName + " objective text: " + finalText);
		}
	}

	internal static class HavenAttackTimerEstimator
	{
		/// Safety cap so an unresolved (near-stalemate) projection can't loop forever.
		private const int MaxProjectedHours = 240;

		public struct HourRange
		{
			public int MinHours;
			public int MaxHours;
		}

		/// <summary>
		/// The real battle applies an independent Random.Range(0.9, 1.1) damage roll to each side
		/// every hourly tick, so there's no single exact time-to-resolution - only a range. We
		/// project GeoHavenDefenseMission.SimulateBattle's damage formula hour-by-hour twice: once
		/// with a flat 1.1x multiplier (best case for whichever side is currently losing, resolves
		/// soonest) and once with 0.9x (worst case, resolves latest). Never mutates the real
		/// mission, only reads its public state and the def's public HavenDefenceSettings.
		/// </summary>
		public static HourRange? EstimateHoursRemaining(GeoHavenDefenseMission mission)
		{
			int? soonest = SimulateBattleHours(mission, 1.1f);
			int? latest = SimulateBattleHours(mission, 0.9f);
			if (!soonest.HasValue || !latest.HasValue)
			{
				return null;
			}
			int prepHours = GetPrepHoursRemaining(mission);
			return new HourRange
			{
				MinHours = Mathf.Min(soonest.Value, latest.Value) + prepHours,
				MaxHours = Mathf.Max(soonest.Value, latest.Value) + prepHours,
			};
		}

		private static int? SimulateBattleHours(GeoHavenDefenseMission mission, float noiseMultiplier)
		{
			HavenDefenceSettings settings = mission.Site.GeoLevel.MissionTypeSettingsDef.HavenDefenceSettings;
			int attacker = mission.AttackerDeployment;
			int defender = mission.DefenderDeployment;
			int hours = 0;
			while (attacker > 0 && defender > 0 && hours < MaxProjectedHours)
			{
				float higher = Mathf.Max(attacker, defender);
				float lower = Mathf.Min(attacker, defender);
				float asymmetry = 1f + Mathf.Log(higher / lower);
				float attackerDamage;
				float defenderDamage;
				if (attacker <= defender)
				{
					attackerDamage = attacker * settings.DeploymentDamageScale * asymmetry;
					defenderDamage = defender * settings.DeploymentDamageScale / asymmetry;
				}
				else
				{
					attackerDamage = attacker * settings.DeploymentDamageScale / asymmetry;
					defenderDamage = defender * settings.DeploymentDamageScale * asymmetry;
				}
				attackerDamage *= noiseMultiplier;
				defenderDamage *= noiseMultiplier;
				if (defenderDamage > 0f)
				{
					defender -= Mathf.Min((int)defenderDamage, defender);
				}
				if (attackerDamage > 0f)
				{
					attacker -= Mathf.Min((int)attackerDamage, attacker);
				}
				if (attacker < settings.MinimalMissionDeployment)
				{
					attacker = 0;
				}
				if (defender < settings.MinimalMissionDeployment)
				{
					defender = 0;
				}
				hours++;
			}
			if (attacker > 0 && defender > 0)
			{
				return null;
			}
			return hours;
		}

		/// <summary>
		/// GeoHavenDefenseMission keeps its prep-time countdown (_waitTime) in a private field;
		/// the battle doesn't start ticking until it reaches zero, so it's added on top of the
		/// projected battle duration. Usually zero unless a faction has researched extra prep time.
		/// </summary>
		private static int GetPrepHoursRemaining(GeoHavenDefenseMission mission)
		{
			TimeUnit waitTime = Traverse.Create(mission).Field("_waitTime").GetValue<TimeUnit>();
			if (waitTime <= TimeUnit.Zero)
			{
				return 0;
			}
			return Mathf.CeilToInt((float)waitTime.TimeSpan.TotalHours);
		}

		/// <summary>
		/// Formats a hint range using the game's own TimeRemainingFormatterDef (same asset the
		/// panel already uses for other timed objectives), so the hour/day unit text is pulled
		/// from the existing localization instead of a hardcoded English suffix. When both bounds
		/// land within the same day, they're combined into one localized "{min}-{max}h" style
		/// string; otherwise each bound is formatted independently and joined with a dash.
		/// </summary>
		public static string FormatHoursRange(HourRange range, TimeRemainingFormatterDef formatter)
		{
			string hoursFormat = formatter.HoursText.Localize(null);
			if (range.MinHours == range.MaxHours)
			{
				return string.Format(hoursFormat, range.MinHours);
			}
			return string.Format(hoursFormat, range.MinHours + "-" + range.MaxHours);
		}
	}
}
