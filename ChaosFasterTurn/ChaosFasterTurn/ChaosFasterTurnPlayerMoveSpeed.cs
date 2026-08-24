using System;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Independent speed control for a player-controlled unit's own movement action during the
	/// player's own turn - entirely separate from <see cref="FasterTurnState"/> (the enemy-turn
	/// speed-up), sharing only the <see cref="SpeedSteps"/> ladder/formatting and the four hotkeys
	/// (<see cref="ChaosFasterTurnUpdater"/> routes them here instead of to <see cref="FasterTurnState"/>
	/// whenever <see cref="Active"/> is true - see that class's Update()).
	/// <para/>
	/// <see cref="Active"/> is true only for the exact duration of one player movement action
	/// (<see cref="ChaosFasterTurnPlayerMoveStartPatch"/>/<see cref="ChaosFasterTurnPlayerMoveEndPatch"/>
	/// below, patching <c>TacticalNavigationComponent.OnNavigateStart</c>/<c>OnNavigateEnd</c> - the
	/// same start/end hook <c>ChaosFasterTurnNavigateStartPatch</c> in ChaosFasterTurnOverwatchPause.cs
	/// already uses for the AI-overshoot protection, applied here to the opposite case: a
	/// player-controlled mover on the player's own turn). Every movement action starts fresh at x1 -
	/// <see cref="Begin"/> always resets both <see cref="Multiplier"/> and
	/// <see cref="LastNonDefaultMultiplier"/>, so nothing is ever remembered from a previous move.
	/// </summary>
	internal static class PlayerMoveSpeedState
	{
		public static bool Active;
		public static TacticalActor Actor;
		public static float Multiplier = 1f;
		public static float LastNonDefaultMultiplier = 1f;

		/// <summary>Starts a fresh, x1 speed control for one player movement action.</summary>
		public static void Begin(TacticalActor actor)
		{
			Actor = actor;
			Active = true;
			Multiplier = 1f;
			LastNonDefaultMultiplier = 1f;
			Apply();
		}

		/// <summary>
		/// Ends the speed control for <paramref name="actor"/>'s movement action and puts its
		/// Timing back to normal. Ignores a stale end-callback for an actor that isn't the one
		/// currently tracked (e.g. a leftover call arriving after a new movement action already
		/// began), but still defensively normalizes that actor's own Timing either way.
		/// </summary>
		public static void End(TacticalActor actor)
		{
			if (actor?.Timing != null)
			{
				actor.Timing.Scale = 1f;
			}
			if (!Active || !ReferenceEquals(Actor, actor))
			{
				return;
			}
			Active = false;
			Actor = null;
			Multiplier = 1f;
			LastNonDefaultMultiplier = 1f;
		}

		/// <summary>Writes <see cref="Multiplier"/> onto the tracked actor's own Timing. No-op unless <see cref="Active"/>.</summary>
		public static void Apply()
		{
			if (!Active || Actor?.Timing == null)
			{
				return;
			}
			Actor.Timing.Scale = Multiplier;
		}

		/// <summary>Clamps and stores a new multiplier, then re-applies it immediately.</summary>
		public static void SetMultiplier(float value)
		{
			int max = Math.Max(1, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10);
			float clamped = UnityEngine.Mathf.Clamp(value, 1f, max);
			if (clamped == Multiplier)
			{
				return;
			}
			Multiplier = clamped;
			if (Multiplier != 1f)
			{
				LastNonDefaultMultiplier = Multiplier;
			}
			Apply();
			FasterTurnState.Log($"Player movement speed set to x{SpeedSteps.Format(Multiplier)}");
		}

		/// <summary>Increase/Decrease Speed keys - see <see cref="FasterTurnState.IncreaseSpeed"/>.</summary>
		public static void IncreaseSpeed()
		{
			int max = Math.Max(1, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10);
			SetMultiplier(SpeedSteps.Increase(Multiplier, max));
		}

		/// <summary>See <see cref="IncreaseSpeed"/>.</summary>
		public static void DecreaseSpeed()
		{
			int max = Math.Max(1, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10);
			SetMultiplier(SpeedSteps.Decrease(Multiplier, max));
		}

		/// <summary>
		/// Toggle Speed key - same x1 ↔ LastNonDefaultMultiplier behavior as
		/// <see cref="FasterTurnState.ToggleDefault"/>, including the same "default to x2 the first
		/// time" fallback, so the very first Space press during a movement action always does
		/// something (jumps straight to x2) instead of requiring a +/- press first.
		/// </summary>
		public static void ToggleDefault()
		{
			if (Multiplier == 1f && LastNonDefaultMultiplier == 1f)
			{
				LastNonDefaultMultiplier = Math.Min(2f, Math.Max(1f, ChaosFasterTurnMain.Cfg?.MaxSpeedMultiplier ?? 10));
			}
			SetMultiplier(Multiplier != 1f ? 1f : LastNonDefaultMultiplier);
		}

		/// <summary>
		/// Unconditionally ends any in-progress player-movement speed-up and forgets its state -
		/// used on Tactical end / mod disable, and defensively whenever an AI turn starts, so
		/// nothing is left sped up (or tracking a stale actor) outside the one movement action this
		/// is meant to cover.
		/// </summary>
		public static void Reset()
		{
			if (Actor?.Timing != null)
			{
				Actor.Timing.Scale = 1f;
			}
			Active = false;
			Actor = null;
			Multiplier = 1f;
			LastNonDefaultMultiplier = 1f;
		}
	}

	/// <summary>
	/// Starts <see cref="PlayerMoveSpeedState"/> the instant a player-controlled unit's own
	/// movement action begins on the player's own turn. Postfix on
	/// <c>TacticalNavigationComponent.OnNavigateStart(PlayingAction action)</c> - fires synchronously
	/// before the move's first step, same hook <c>ChaosFasterTurnNavigateStartPatch</c> uses for the
	/// AI side in ChaosFasterTurnOverwatchPause.cs. Deliberately the mirror-image gate of every
	/// enemy-turn trigger point in this mod: active only while it is NOT the AI's turn, and only for
	/// a player-controlled mover - during the AI's turn (or a non-player-controlled mover, e.g. a
	/// mind-controlled/allied AI unit moving on the player's faction's own turn) this does nothing at
	/// all, leaving <see cref="FasterTurnState"/> as the only speed control in effect.
	/// </summary>
	[HarmonyPatch(typeof(TacticalNavigationComponent), "OnNavigateStart")]
	internal static class ChaosFasterTurnPlayerMoveStartPatch
	{
		private static void Postfix(TacticalNavigationComponent __instance)
		{
			try
			{
				TacticalActor actor = __instance?.TacticalActor;
				if (actor == null || actor.Timing == null)
				{
					return;
				}
				if (FasterTurnState.IsAITurn || !actor.IsControlledByPlayer)
				{
					return;
				}
				PlayerMoveSpeedState.Begin(actor);
			}
			catch
			{
				// Never allowed to break vanilla's own navigation start-up.
			}
		}
	}

	/// <summary>
	/// Ends <see cref="PlayerMoveSpeedState"/> the instant that same movement action finishes.
	/// Postfix on <c>TacticalNavigationComponent.OnNavigateEnd(PlayingAction action)</c> - the exact
	/// counterpart callback to <c>OnNavigateStart</c> above, fired once the move's coroutine actually
	/// completes/stops. Unconditional (no IsAITurn/IsControlledByPlayer gate): <see cref="PlayerMoveSpeedState.End"/>
	/// is itself a no-op unless this is genuinely the actor it's currently tracking, so it's always
	/// safe to call and never risks leaving <see cref="PlayerMoveSpeedState.Active"/> stuck true.
	/// </summary>
	[HarmonyPatch(typeof(TacticalNavigationComponent), "OnNavigateEnd")]
	internal static class ChaosFasterTurnPlayerMoveEndPatch
	{
		private static void Postfix(TacticalNavigationComponent __instance)
		{
			try
			{
				TacticalActor actor = __instance?.TacticalActor;
				if (actor == null)
				{
					return;
				}
				PlayerMoveSpeedState.End(actor);
			}
			catch
			{
				// Never allowed to break vanilla's own navigation shutdown.
			}
		}
	}
}
