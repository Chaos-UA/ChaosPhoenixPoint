using System;
using System.Reflection;
using Base.Entities;
using Base.Utils.Maths;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Live state for the enemy-turn speed control. <see cref="Controller"/> is set/cleared by
	/// <see cref="ChaosFasterTurnTactical"/> for whichever Tactical level is currently loaded;
	/// <see cref="Multiplier"/> is the player-chosen x-speed, changed by <see cref="ChaosFasterTurnUpdater"/>.
	/// </summary>
	internal static class FasterTurnState
	{
		public static TacticalLevelController Controller;
		public static int Multiplier = 1;

		/// <summary>
		/// True only while an AI faction's turn is actually in progress. Every trigger point in
		/// this mod that can start suppressing speed (<see cref="BeginSuppressed"/>) or otherwise
		/// touch actor state now checks this first and passes through untouched when it's false -
		/// see CLAUDE.md "Player-turn lockout" - so the mod is fully inert the instant the player's
		/// own turn starts, regardless of what triggered the check (a player shot, an enemy
		/// overwatch/return-fire reaction against the player, anything). This is stricter than
		/// checking <c>Suppressed</c> alone: it stops this mod from ever wrapping/touching a shot's
		/// coroutine in the first place during the player's turn, rather than wrapping it and then
		/// relying on the wrapped suppression having no *visible* effect.
		/// </summary>
		public static bool IsAITurn => Controller?.CurrentFaction?.IsControlledByAI == true;

		/// <summary>
		/// The most recent non-1 value <see cref="Multiplier"/> was set to, kept even after
		/// <see cref="Multiplier"/> itself gets reset back to 1 (by the Reset Speed key, or by
		/// <c>ResetSpeedOnPlayerTurn</c> at the start of the player's turn) - this is what the
		/// Toggle Speed key restores. Only <see cref="SetMultiplier"/> writes to it, and only when
		/// the new value isn't 1, so resetting to 1 (from anywhere) never touches it.
		/// </summary>
		public static int LastNonDefaultMultiplier = 1;

		/// <summary>
		/// True while a player-controlled overwatch/return-fire shot is in flight
		/// (<see cref="ChaosFasterTurnOverwatchPausePatch"/>). While set, <see cref="ApplyForCurrentFaction"/>
		/// forces everyone back to 1x regardless of whose turn it is or what <see cref="Multiplier"/>
		/// is, so the reacting AI actor plays that one shot at normal speed instead of fighting
		/// whatever dramatic-pause/camera timing the shot sequence wants to apply to it.
		/// </summary>
		public static bool Suppressed;

		/// <summary>
		/// How long (real, unscaled seconds) <see cref="NotifyPlayerDamageTaken"/> forces every actor
		/// to 1x for, starting from the most recent hit. Deliberately a hardcoded constant rather than
		/// a config field - same reasoning as the hardcoded hotkeys - easy to retune directly in
		/// source if a different duration is ever wanted.
		/// </summary>
		private const float DamageFlashSeconds = 1.5f;

		/// <summary>
		/// Time.realtimeSinceStartup deadline written by <see cref="NotifyPlayerDamageTaken"/>; while
		/// still in the future, <see cref="IsDamageFlashActive"/> is true. Real, unscaled time is used
		/// deliberately - it can never be affected by anything this mod (or vanilla) does to any
		/// Timing.Scale, so the deadline always means the same wall-clock duration regardless of the
		/// chosen multiplier.
		/// </summary>
		private static float _damageFlashUntilRealtime;

		/// <summary>
		/// True for <see cref="DamageFlashSeconds"/> after the most recent player-controlled unit took
		/// damage during the AI's turn. Read live by <see cref="ApplyForCurrentFaction"/> exactly like
		/// <see cref="Suppressed"/> and <see cref="IsUnderVanillaDramaticPause"/> already are - unlike
		/// <see cref="Suppressed"/>, nothing ever sets this back to false explicitly: it just stops
		/// being true once real time passes the deadline, so the very next reapply (every frame, see
		/// "Continuous reapply" in CLAUDE.md) naturally resumes the chosen speed with no dedicated "end"
		/// call needed anywhere, and it can never get stuck on the way <see cref="Suppressed"/> could if
		/// a matching EndSuppressed() were ever missed.
		/// </summary>
		public static bool IsDamageFlashActive => Time.realtimeSinceStartup < _damageFlashUntilRealtime;

		/// <summary>
		/// Called (see <c>ChaosFasterTurnDamageTakenPatch</c> in ChaosFasterTurnOverwatchPause.cs)
		/// whenever a player-controlled unit actually takes damage while it's the AI's turn - the
		/// broadest possible catch-all for "Reset Speed On Enemy Attack" (CLAUDE.md), independent of
		/// which specific ability caused it (splash/AOE that never directly targeted the unit, a
		/// status/DoT tick, fire spreading, anything). Repeated hits simply keep pushing the deadline
		/// further out, so a burst of damage holds speed at 1x continuously rather than flickering.
		/// </summary>
		public static void NotifyPlayerDamageTaken()
		{
			if (!(ChaosFasterTurnMain.Cfg?.ResetSpeedOnEnemyAttack ?? true))
			{
				return;
			}
			_damageFlashUntilRealtime = Time.realtimeSinceStartup + DamageFlashSeconds;
			ApplyForCurrentFaction();
			Log("Enemy turn speed forced to x1 - a player unit just took damage.");
		}

		/// <summary>
		/// Sets <see cref="Multiplier"/> on every currently-acting AI actor's own Timing (or 1f on
		/// all of them while <see cref="Suppressed"/>), and 1f on everyone else. Deliberately does
		/// NOT touch TacticalLevelController.Timing (the level's root) - that Timing is shared with
		/// the game's own UI state-machine coroutines (TacticalView/UIStateInitial etc. all run
		/// their waits via "levelController.Timing.Call"), so scaling it at up to
		/// Cfg.MaxSpeedMultiplier risked desyncing those coroutines badly enough to leave the whole
		/// HUD stuck (this is what actually happened in testing - see CLAUDE.md). Working per-actor
		/// instead keeps the effect scoped to enemy Animator/movement timing, the same layer
		/// vanilla's own Animation Speed option (TacTimeScaleRegulator) already operates on, and
		/// never touches anything the UI depends on.
		/// </summary>
		public static void ApplyForCurrentFaction()
		{
			TacticalLevelController controller = Controller;
			if (controller == null || controller.Map == null)
			{
				return;
			}
			TacticalFaction currentFaction = controller.CurrentFaction;
			bool speedUpCurrentFaction = !Suppressed && !IsDamageFlashActive && currentFaction != null && currentFaction.IsControlledByAI;

			foreach (TacticalActor actor in controller.Map.GetActors<TacticalActor>(null))
			{
				if (actor == null || actor.Timing == null)
				{
					continue;
				}
				if (IsUnderVanillaDramaticPause(actor, controller))
				{
					continue;
				}
				bool isActingActor = speedUpCurrentFaction && actor.TacticalFaction == currentFaction;
				if (isActingActor && IsNavigatingUnderArmedOverwatch(actor, controller))
				{
					isActingActor = false;
				}
				actor.Timing.Scale = isActingActor ? Multiplier : 1f;
			}
		}

		/// <summary>
		/// Extra distance (world units - 1 unit == 1 tactical tile, see <c>TacticalMap.TileSize</c>)
		/// added around a hostile overwatch's actual cone reach before this mod starts treating a
		/// mover as "at risk" and capping it to 1x. Exists to absorb one-frame-ish detection slop
		/// (see CLAUDE.md "Incident 6") while still being a small, local radius around each individual
		/// watcher - not "anywhere on the map" (an earlier version used exactly that and slowed down
		/// every enemy on the level any time a single player unit anywhere had overwatch up).
		/// </summary>
		private const float OverwatchProximityMarginTiles = 8f;

		/// <summary>
		/// True while at least one hostile unit is currently holding armed overwatch against
		/// <paramref name="actor"/> and <paramref name="actor"/> is within that watcher's cone reach
		/// plus <see cref="OverwatchProximityMarginTiles"/> - i.e. an overwatch reaction could
		/// plausibly trigger against this actor on literally any subsequent frame. Mirrors the
		/// relation/status half of the exact query TacticalLevelController.TriggerOverwatch itself
		/// runs (`status != null &amp;&amp; !actor.DuringOwnTurn &amp;&amp; actor.RelationTo(target)
		/// == FactionRelation.Enemy`), plus a proximity gate TriggerOverwatch itself doesn't have
		/// (see CLAUDE.md "Incident 6" for why that gate is needed here even though vanilla doesn't
		/// need one for its own purposes). Shared by <see cref="IsNavigatingUnderArmedOverwatch"/>
		/// (the per-frame poll in <see cref="ApplyForCurrentFaction"/>) and
		/// <c>ChaosFasterTurnTriggerOverwatchPatch</c> (the zero-latency hook directly on
		/// <c>TriggerOverwatch</c> itself, see CLAUDE.md "Incident 7").
		/// </summary>
		internal static bool IsNearArmedOverwatch(TacticalActor actor, TacticalLevelController controller)
		{
			if (actor == null || controller == null || controller.Map == null)
			{
				return false;
			}
			foreach (TacticalActor watcher in controller.Map.GetActors<TacticalActor>(null))
			{
				if (watcher == null || watcher == actor || watcher.DuringOwnTurn)
				{
					continue;
				}
				OverwatchStatus status = watcher.Status?.GetStatus<OverwatchStatus>();
				if (status == null)
				{
					continue;
				}
				if (watcher.RelationTo(actor) != FactionRelation.Enemy)
				{
					continue;
				}
				Cone cone = status.GetCone();
				float reach = Mathf.Max(cone.Height, cone.Radius) + OverwatchProximityMarginTiles;
				if ((actor.Pos - watcher.Pos).sqrMagnitude <= reach * reach)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// True while <paramref name="actor"/> is actively navigating (<c>NavigationComponent.IsNavigating</c>)
		/// AND <see cref="IsNearArmedOverwatch"/>. Gated on IsNavigating (not just "AI turn + overwatch
		/// armed nearby") so idle/shooting/etc. AI actors - which can never hit the overshoot bug this
		/// protects against, since only movement calls TriggerOverwatch at all - still get sped up
		/// normally even while some other unit's overwatch is armed nearby. See CLAUDE.md "Incident 6".
		/// </summary>
		private static bool IsNavigatingUnderArmedOverwatch(TacticalActor actor, TacticalLevelController controller)
		{
			if (actor.NavigationComponent == null || !actor.NavigationComponent.IsNavigating)
			{
				return false;
			}
			return IsNearArmedOverwatch(actor, controller);
		}

		/// <summary>
		/// True while vanilla itself is actively holding this actor's Timing.Scale down for a
		/// dramatic-pause effect via the proper TimingScaleCombiner mechanism -
		/// TacticalLevelController.ExecuteOverwatch/ReturnFire both do
		/// `actor.TimingScale.AddScale(OverwatchTimeScale / ReturnFireTimeScale, this)` (`this`
		/// being the TacticalLevelController itself) the instant an overwatch/return-fire reaction
		/// triggers, specifically so the cone/line-of-sight check moments later still catches the
		/// mover at roughly the position it triggered at - see CLAUDE.md "Incident 4". If this mod
		/// overwrites Timing.Scale on top of that (which the direct-write design here normally does,
		/// deliberately, to exceed whatever TacTimeScaleRegulator's own combiner contribution allows -
		/// see the TimingScaleCombiner row in CLAUDE.md), the mover keeps moving at full speed right
		/// through that check instead of slowing down for it, so by the time the check actually runs
		/// the mover is already far past where the reaction should have caught it - late trigger,
		/// inaccurate shot. Skipping this actor entirely while the level controller has a live
		/// contribution on it costs nothing (this is what the check is for) and leaves vanilla's own
		/// bracketing (AddScale at the start, RemoveScale when the reaction ends) in full, undisturbed
		/// control for exactly as long as it needs it.
		/// </summary>
		internal static bool IsUnderVanillaDramaticPause(TacticalActor actor, TacticalLevelController controller)
		{
			TimingScaleCombiner combiner = actor.TimingScale;
			if (combiner == null)
			{
				return false;
			}
			foreach (TimingScaleCombiner.ScaleApplication application in combiner.ScaleApplications)
			{
				if (ReferenceEquals(application.Source, controller))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Puts every actor's own Timing back to normal. Used on Tactical end / mod disable so
		/// nothing is left sped up if the level goes away mid-AI-turn.
		/// </summary>
		public static void ResetAllActors()
		{
			TacticalLevelController controller = Controller;
			if (controller == null || controller.Map == null)
			{
				return;
			}
			foreach (TacticalActor actor in controller.Map.GetActors<TacticalActor>(null))
			{
				if (actor != null && actor.Timing != null)
				{
					actor.Timing.Scale = 1f;
				}
			}
		}

		/// <summary>
		/// Clamps and stores a new multiplier, then re-applies it immediately (so a key press
		/// mid-AI-turn is felt right away instead of waiting for the next turn change).
		/// </summary>
		public static void SetMultiplier(int value)
		{
			int max = Math.Max(1, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10);
			int clamped = Mathf.Clamp(value, 1, max);
			if (clamped == Multiplier)
			{
				return;
			}
			Multiplier = clamped;
			if (Multiplier != 1)
			{
				LastNonDefaultMultiplier = Multiplier;
			}
			ApplyForCurrentFaction();
			Log($"Enemy turn speed set to x{Multiplier}");
		}

		/// <summary>
		/// Toggle Speed key: x1 → LastNonDefaultMultiplier → x1 → ... Pressing while at any non-1
		/// speed always goes to x1 (dropping whatever the exact value was, but LastNonDefaultMultiplier
		/// already captured it via SetMultiplier). Pressing while at x1 restores
		/// LastNonDefaultMultiplier - unless that's still 1 (never set this session), in which case
		/// there'd be nothing to restore and the press would otherwise do nothing at all; defaulting
		/// it to 2 first means Space always does *something* the first time it's pressed, instead of
		/// requiring an Increase Speed press before it becomes useful.
		/// </summary>
		public static void ToggleDefault()
		{
			if (Multiplier == 1 && LastNonDefaultMultiplier == 1)
			{
				LastNonDefaultMultiplier = Math.Min(2, Math.Max(1, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10));
			}
			SetMultiplier(Multiplier != 1 ? 1 : LastNonDefaultMultiplier);
		}

		/// <summary>
		/// Begins suppressing the speed-up (see <see cref="Suppressed"/>) and applies the resulting
		/// 1x immediately, so it's in effect before the shot's first frame rather than one frame late.
		/// </summary>
		public static void BeginSuppressed()
		{
			Suppressed = true;
			ApplyForCurrentFaction();
			Log("Enemy turn speed suppressed to x1 for a player overwatch/return-fire shot.");
		}

		/// <summary>
		/// Ends suppression and immediately restores whatever Multiplier was set to.
		/// </summary>
		public static void EndSuppressed()
		{
			Suppressed = false;
			ApplyForCurrentFaction();
			Log($"Enemy turn speed restored to x{Multiplier} after the shot.");
		}

		public static void Log(string message)
		{
			if (ChaosFasterTurnMain.Cfg?.EnableDebugLog == true)
			{
				Debug.Log($"[ChaosFasterTurn] {message}");
			}
		}
	}

	/// <summary>
	/// Polls the four (hardcoded - see ChaosFasterTurnConfig.cs) hotkeys every frame. Added once
	/// to the mod's own persistent GameObject (<see cref="ChaosFasterTurnMain.ModGO"/>) in
	/// <c>OnModEnabled</c>, so it keeps running across level loads/unloads; it simply no-ops
	/// whenever <see cref="FasterTurnState.Controller"/> is null (i.e. outside a Tactical level).
	///
	/// UnityEngine.Input lives in UnityEngine.InputLegacyModule.dll, which isn't among the ModSDK
	/// reference DLLs, so it can't be called directly - it's resolved via reflection instead, same
	/// as ChaosOverwatch's FreeCamSpeedControlPatch does.
	/// </summary>
	internal sealed class ChaosFasterTurnUpdater : MonoBehaviour
	{
		private static readonly Type InputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule");
		private static readonly MethodInfo GetKeyDownMethod = InputType?.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });

		private static bool IsKeyDown(KeyCode key) =>
			GetKeyDownMethod != null && (bool)GetKeyDownMethod.Invoke(null, new object[] { key });

		// Hardcoded rather than configurable - see ChaosFasterTurnConfig.cs's comment on why.
		private const KeyCode ResetSpeedKey = KeyCode.Backspace;
		private const KeyCode DecreaseSpeedKey = KeyCode.Minus;
		private const KeyCode IncreaseSpeedKey = KeyCode.Equals;
		private const KeyCode ToggleSpeedKey = KeyCode.Space;

		private void Update()
		{
			if (FasterTurnState.Controller == null)
			{
				return;
			}

			// Fully hands-off while a player overwatch/return-fire reaction is in flight
			// (FasterTurnState.Suppressed - see ChaosFasterTurnOverwatchPause.cs): no hotkeys, no
			// continuous reapply, not even the badge (explicitly hidden instead of left showing a
			// stale value) - this mod does *nothing* at all until suppression ends. An earlier,
			// narrower version only skipped the hotkey block but still called
			// ApplyForCurrentFaction() unconditionally every frame, which - while Suppressed -
			// forced every actor back to 1f on every single frame, fighting anything else that
			// legitimately wants to control an actor's Timing.Scale during that window (most
			// notably ChaosOverwatch's own Space/+/- speed-scrub controls during its manual-aim
			// FreeCam) and reportedly affecting shot accuracy too - see CLAUDE.md "Incident 3".
			// The one-time resets inside BeginSuppressed/EndSuppressed already do the actual job
			// this mod needs (holding speed at 1x when a reaction starts, restoring it when
			// suppression ends), so bailing out completely here costs nothing.
			if (FasterTurnState.Suppressed)
			{
				FasterTurnSpeedBadge.Refresh(false, FasterTurnState.Multiplier);
				return;
			}

			if (IsKeyDown(ResetSpeedKey))
			{
				FasterTurnState.SetMultiplier(1);
			}
			else if (IsKeyDown(DecreaseSpeedKey))
			{
				FasterTurnState.SetMultiplier(FasterTurnState.Multiplier - 1);
			}
			else if (IsKeyDown(IncreaseSpeedKey))
			{
				FasterTurnState.SetMultiplier(FasterTurnState.Multiplier + 1);
			}
			else if (IsKeyDown(ToggleSpeedKey))
			{
				FasterTurnState.ToggleDefault();
			}

			// Player-turn lockout (see CLAUDE.md): this was the last spot in the mod that still
			// touched every actor's Timing every single frame regardless of whose turn it was -
			// every trigger-point patch got gated on IsAITurn already, but this continuous reapply
			// kept running unconditionally straight through the player's own turn, including
			// writing Timing.Scale on whichever enemy the player is currently shooting at, at the
			// same time ChaosOverwatch (or anything else) is trying to hold that same actor's
			// Timing.Paused for the shot. Only reassert while it's actually the AI's turn now.
			bool isAITurn = FasterTurnState.IsAITurn;
			if (isAITurn)
			{
				/// Continuously reassert the chosen multiplier rather than relying solely on the
				/// NewTurnEvent hook. FasterTurnState.Multiplier already survives faction-turn changes
				/// on its own - this is about the applied per-actor Timing.Scale getting stomped back
				/// to normal mid-turn by something else (observed in practice; TacTimeScaleRegulator
				/// re-applying the vanilla Animation Speed option per-actor on ActorEnteredPlayEvent is
				/// the likeliest vanilla suspect, since that fires per unit). Timing.Scale's setter
				/// already no-ops when the value is unchanged, so this is a cheap per-frame safety net,
				/// not a real per-frame cost, and it guarantees the last-set speed is back in effect
				/// within one frame of anything else touching it.
				FasterTurnState.ApplyForCurrentFaction();
			}

			FasterTurnSpeedBadge.Refresh(isAITurn, FasterTurnState.Multiplier);
		}
	}
}
