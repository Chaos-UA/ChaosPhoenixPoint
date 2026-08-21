using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Entities;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View.ViewStates;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Suppresses the enemy speed-up to x1 for the duration of a single shot, for two distinct
	/// cases, both funneling into the same <see cref="FasterTurnState.Suppressed"/> flag (idempotent
	/// - safe for more than one trigger to fire for the same shot):
	/// <list type="bullet">
	/// <item>A player-controlled overwatch/return-fire reaction - from three independent,
	/// redundant-by-design trigger points: the moment the reaction is requested
	/// (<see cref="ChaosFasterTurnShootActivatePatch"/>, earliest and most reliable), the manual-aim
	/// waiting period that follows with ChaosOverwatch installed
	/// (<see cref="ChaosFasterTurnFreeCamEnterPatch"/>/<see cref="ChaosFasterTurnFreeCamExitPatch"/>),
	/// and the shot itself (<see cref="ChaosFasterTurnOverwatchPausePatch"/>, fires with or without
	/// ChaosOverwatch since it patches the core vanilla method every shot funnels through regardless
	/// of how it was aimed). See CLAUDE.md "Overwatch/return-fire suppression" for the full timeline.</item>
	/// <item>An AI-controlled unit firing at a player-controlled unit (<c>Reset Speed On Enemy
	/// Attack</c>, default on) - <see cref="ChaosFasterTurnOverwatchPausePatch"/> alone is
	/// sufficient here: unlike the player-reaction case there's no manual-aim window or async
	/// trigger-then-delayed-check gap to race ahead of (see CLAUDE.md "Incident 4") - the AI fires
	/// directly within this one coroutine, so bracketing just this method already covers the whole
	/// window reliably. See CLAUDE.md "Reset Speed On Enemy Attack" for details.</item>
	/// </list>
	/// </summary>
	[HarmonyPatch(typeof(TacticalLevelController), "FireWeaponAtTargetCrt")]
	internal static class ChaosFasterTurnOverwatchPausePatch
	{
		// Prevents the wrapper from wrapping itself when it re-calls FireWeaponAtTargetCrt to get
		// the original enumerator - same guard ChaosOverwatch's own equivalent patch uses.
		[ThreadStatic]
		private static bool _reentrant;

		private static bool Prefix(TacticalLevelController __instance, Weapon weapon, TacticalAbilityTarget abilityTarget, ShootAbility shootAbility, ref IEnumerator<NextUpdate> __result)
		{
			if (_reentrant)
			{
				return true;
			}
			// Player-turn lockout (see CLAUDE.md): this mod must be fully hands-off during the
			// player's own turn, not just a no-op-in-effect - so it never even wraps the shot's
			// coroutine (with its own reentrant MoveNext pump) while it's the player's turn,
			// leaving 100% of that shot to vanilla and whatever other mods are patching it.
			if (!FasterTurnState.IsAITurn)
			{
				return true;
			}
			if (abilityTarget == null)
			{
				return true;
			}

			TacticalActor shooter = shootAbility?.TacticalActor;
			if (shooter == null)
			{
				return true;
			}

			bool isReactionShot = abilityTarget.AttackType == AttackType.Overwatch || abilityTarget.AttackType == AttackType.ReturnFire;
			bool isPlayerReaction = isReactionShot && shooter.IsControlledByPlayer;
			bool isEnemyAttackOnPlayer = IsEnemyAttackOnPlayer(shooter, abilityTarget);
			if (!isPlayerReaction && !isEnemyAttackOnPlayer)
			{
				return true;
			}

			__result = WrapWithSuppressedSpeed(__instance, weapon, abilityTarget, shootAbility);
			return false;
		}

		/// <summary>
		/// True when <c>Reset Speed On Enemy Attack</c> is enabled, <paramref name="shooter"/> is
		/// AI-controlled, and the shot's actual target (<see cref="TacticalAbilityTarget.GetTargetActor"/>
		/// - resolves through <c>Actor</c>, then <c>DamageReceiver</c>, then <c>ShootTargetActor</c>,
		/// covering every way a shot can carry its target) is a player-controlled unit. Deliberately
		/// not restricted to a particular <see cref="AttackType"/> - an AI unit's regular attack and an
		/// AI unit's own overwatch/return-fire trigger against the player should both count as "an
		/// enemy attacking the player" for this option. Not restricted to the currently-acting faction
		/// either: a shooter that isn't part of <see cref="FasterTurnState"/>'s current AI faction is
		/// already forced to 1x by <see cref="FasterTurnState.ApplyForCurrentFaction"/> regardless, so
		/// suppressing here too is simply a no-op in that case rather than something that needs its own
		/// guard.
		/// </summary>
		internal static bool IsEnemyAttackOnPlayer(TacticalActor shooter, TacticalAbilityTarget abilityTarget)
		{
			if (!(ChaosFasterTurnMain.Cfg?.ResetSpeedOnEnemyAttack ?? true))
			{
				return false;
			}
			if (shooter == null || abilityTarget == null || shooter.IsControlledByPlayer)
			{
				return false;
			}
			TacticalActor target = abilityTarget.GetTargetActor() as TacticalActor;
			return target != null && target.IsControlledByPlayer;
		}

		private static IEnumerator<NextUpdate> WrapWithSuppressedSpeed(TacticalLevelController level, Weapon weapon, TacticalAbilityTarget abilityTarget, ShootAbility shootAbility)
		{
			FasterTurnState.BeginSuppressed();
			_reentrant = true;
			IEnumerator<NextUpdate> original = level.FireWeaponAtTargetCrt(weapon, abilityTarget, shootAbility);
			_reentrant = false;
			while (true)
			{
				bool stepped;
				try
				{
					stepped = original.MoveNext();
				}
				catch
				{
					FasterTurnState.EndSuppressed();
					throw;
				}
				if (!stepped)
				{
					break;
				}
				yield return original.Current;
			}
			FasterTurnState.EndSuppressed();
		}
	}

	/// <summary>
	/// The melee counterpart to <see cref="ChaosFasterTurnOverwatchPausePatch"/>: an AI unit's melee
	/// ("bash") attack on a player-controlled unit never touches `FireWeaponAtTargetCrt` at all - it
	/// runs entirely through <c>BashAbility</c>'s own private <c>BashCrt</c> coroutine instead (Bash
	/// covers claws, blunt weapon strikes, etc.; also used for non-attack "bashes" like breaking down
	/// doors, which this patch never engages for since <see cref="TacticalAbilityTarget.GetTargetActor"/>
	/// returns null for those). Without this patch, Reset Speed On Enemy Attack silently did nothing
	/// for melee attacks - a Pandoran (or any other melee-capable enemy) closing in and striking a
	/// player unit kept playing at the full enemy-turn multiplier. Same
	/// `[ThreadStatic] _reentrant`-guarded manual `MoveNext()` pump as
	/// <see cref="ChaosFasterTurnOverwatchPausePatch"/>, same <see cref="FasterTurnState.BeginSuppressed"/>/
	/// <see cref="FasterTurnState.EndSuppressed"/> bracket, and reuses
	/// <see cref="ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer"/> unchanged for the
	/// AI-shooter/player-target check. The only difference is mechanical: `BashCrt` is `private`, so
	/// the reentrant call that fetches the original enumerator goes through a cached `MethodInfo`
	/// instead of a direct call - same private-method-invocation pattern `ChaosOverwatch` already uses
	/// throughout `ChaosOverwatchTactical.cs`. Harmony patches the method itself (not a particular call
	/// site), so this reflected re-invocation still runs the patched version and is caught by the same
	/// reentrant guard, exactly like the direct call in `ChaosFasterTurnOverwatchPausePatch` is.
	/// </summary>
	[HarmonyPatch(typeof(BashAbility), "BashCrt")]
	internal static class ChaosFasterTurnBashPatch
	{
		[ThreadStatic]
		private static bool _reentrant;

		private static readonly MethodInfo BashCrtMethod =
			typeof(BashAbility).GetMethod("BashCrt", BindingFlags.NonPublic | BindingFlags.Instance);

		private static bool Prefix(BashAbility __instance, PlayingAction action, ref IEnumerator<NextUpdate> __result)
		{
			if (_reentrant || BashCrtMethod == null)
			{
				return true;
			}
			// Player-turn lockout - see the matching guard (and CLAUDE.md) on
			// ChaosFasterTurnOverwatchPausePatch.Prefix above.
			if (!FasterTurnState.IsAITurn)
			{
				return true;
			}

			TacticalActor shooter = __instance?.TacticalActor;
			TacticalAbilityTarget target = action?.Param as TacticalAbilityTarget;
			if (!ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer(shooter, target))
			{
				return true;
			}

			__result = WrapWithSuppressedSpeed(__instance, action);
			return false;
		}

		private static IEnumerator<NextUpdate> WrapWithSuppressedSpeed(BashAbility instance, PlayingAction action)
		{
			FasterTurnState.BeginSuppressed();
			_reentrant = true;
			IEnumerator<NextUpdate> original = (IEnumerator<NextUpdate>)BashCrtMethod.Invoke(instance, new object[] { action });
			_reentrant = false;
			while (true)
			{
				bool stepped;
				try
				{
					stepped = original.MoveNext();
				}
				catch
				{
					FasterTurnState.EndSuppressed();
					throw;
				}
				if (!stepped)
				{
					break;
				}
				yield return original.Current;
			}
			FasterTurnState.EndSuppressed();
		}
	}

	/// <summary>
	/// Universal generalization of "Reset Speed On Enemy Attack" to ANY ability, not just Shoot/Bash
	/// (see CLAUDE.md). <c>TacticalAbility.StartPlayingAction</c>/<c>ClearPlayingAction</c> are the
	/// two callbacks every ability funnels through when its <c>PlayAction</c> begins/ends
	/// (<c>Base.Entities.PlayingAction.SetState</c> invokes <c>OnCoroutineStart</c>/
	/// <c>OnCoroutineEnd</c>; <c>TacticalAbility.PlayAction</c> wires
	/// <c>StartPlayingAction</c>/<c>ClearPlayingAction</c> as exactly those two callbacks) -
	/// regardless of ability type: Shoot, Bash, ApplyStatus (gas/fire grenades), MindControl,
	/// SpawnActor, anything. This pair fires no later than (and typically earlier than, since it
	/// wraps the whole ability rather than just its internal damage-dealing coroutine) the existing
	/// Shoot/Bash-specific patches, and is kept alongside them rather than replacing them - same
	/// "each is a fallback for the others, not a single point of failure" philosophy already used for
	/// the player-reaction Phase 0/1/2 triggers above.
	///
	/// Both Start and Clear independently re-check the same
	/// <see cref="ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer"/> condition, rather than
	/// Clear firing unconditionally the way <see cref="ChaosFasterTurnFreeCamExitPatch"/> does for
	/// FreeCam - a Move or Idle ability's own Clear must never end a suppression some *other*,
	/// still-in-flight ability legitimately started. This doesn't perfectly handle two *concurrent*
	/// qualifying abilities (the underlying <see cref="FasterTurnState.Suppressed"/> flag is a simple
	/// bool, not reference-counted, same as everywhere else in this file), but avoids the far more
	/// common case of an unrelated ability's Clear ending suppression early.
	///
	/// Both are Postfixes wrapped in a hard `try`/`catch` that can never rethrow: they run for
	/// literally every ability activation in the entire game (not just enemy-vs-player ones), and
	/// `ClearPlayingAction` in particular runs in the middle of `PlayingAction`'s own completion
	/// chain (`AddExecutingAbility`/`RemoveExecutingAbility`, `TacticalLevel.AbilityExecuted`, etc.) -
	/// an uncaught exception here would corrupt vanilla's own ability bookkeeping for every mod and
	/// every ability, not just this one, so this is held to a stricter standard than this file's usual
	/// "fall back to vanilla" prefix convention.
	/// </summary>
	[HarmonyPatch(typeof(TacticalAbility), "StartPlayingAction")]
	internal static class ChaosFasterTurnAbilityStartPatch
	{
		private static void Postfix(TacticalAbility __instance, PlayingAction action)
		{
			try
			{
				// Player-turn lockout - see the matching guard (and CLAUDE.md) on
				// ChaosFasterTurnOverwatchPausePatch.Prefix.
				if (!FasterTurnState.IsAITurn)
				{
					return;
				}
				TacticalActor shooter = __instance?.TacticalActor;
				TacticalAbilityTarget target = action?.Param as TacticalAbilityTarget;
				if (ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer(shooter, target))
				{
					FasterTurnState.BeginSuppressed();
				}
			}
			catch
			{
				// Never allowed to break TacticalAbility's own PlayingAction lifecycle - see summary above.
			}
		}
	}

	[HarmonyPatch(typeof(TacticalAbility), "ClearPlayingAction")]
	internal static class ChaosFasterTurnAbilityClearPatch
	{
		private static void Postfix(TacticalAbility __instance, PlayingAction action)
		{
			try
			{
				TacticalActor shooter = __instance?.TacticalActor;
				TacticalAbilityTarget target = action?.Param as TacticalAbilityTarget;
				if (ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer(shooter, target))
				{
					// Unlike the Start postfix above, Clear is NOT gated on IsAITurn: if Start
					// began a suppression while it genuinely was the AI's turn, Clear must still
					// be able to end it even if the turn has since changed - never risk leaving
					// Suppressed stuck true (see CLAUDE.md "Incident 3"). EndSuppressed is
					// idempotent, so this is harmless when nothing was actually suppressed.
					FasterTurnState.EndSuppressed();
				}
			}
			catch
			{
				// Never allowed to break TacticalAbility's own PlayingAction lifecycle - see summary above.
			}
		}
	}

	/// <summary>
	/// Broadest possible safety net for "Reset Speed On Enemy Attack" (see CLAUDE.md): rather than
	/// depending on recognizing which specific ability caused it, this reacts directly to a
	/// player-controlled unit's health actually going down during the AI's turn - covers splash/AOE
	/// damage that never targeted the player unit directly, status/DoT ticks, fire spreading, or any
	/// other source this mod hasn't (and can't practically) enumerate ability-by-ability.
	/// <c>TacticalActor.ApplyDamageInternal</c> is the one method every one of those funnels through
	/// (<c>DamageAccumulation.ApplyAddedDamage_Default</c>/<c>_Fire</c> →
	/// <c>IDamageReceiver.ApplyDamage</c> → <c>TacticalActorBase.ApplyDamage</c> → this override) -
	/// patched here as a Postfix reading its own `bool` return value (`false` = no-op, e.g. a dead
	/// actor or god mode - nothing actually happened, nothing to react to).
	///
	/// Unlike every other trigger in this file, this does NOT call
	/// <see cref="FasterTurnState.BeginSuppressed"/>/<see cref="FasterTurnState.EndSuppressed"/>:
	/// damage application is a single synchronous call, not a coroutine with a natural "this is
	/// finished" moment to bracket, and (per CLAUDE.md "Incident 3") nothing here may ever risk
	/// leaving <see cref="FasterTurnState.Suppressed"/> stuck true. Instead it bumps
	/// <see cref="FasterTurnState.NotifyPlayerDamageTaken"/>'s own live wall-clock deadline - see that
	/// method and <see cref="FasterTurnState.IsDamageFlashActive"/> for why no explicit "end" is
	/// needed and nothing can get stuck this way.
	///
	/// Only reacts to real health or armor loss (`damageResult.HealthDamageAndHealDelta > 0f` -
	/// `HealthDamage` minus any simultaneous `HealValue` - `|| damageResult.ArmorDamage > 0f`), not
	/// pure heals or buffs a friendly source might apply to a player unit mid-AI-turn. Wrapped in the
	/// same hard `try`/`catch` as the ability-lifecycle patches above, for the same reason: this runs
	/// inside `TacticalActorBase.ApplyDamage`'s own call chain for every damage event on every actor,
	/// and must never be able to corrupt that.
	/// </summary>
	[HarmonyPatch(typeof(TacticalActor), "ApplyDamageInternal")]
	internal static class ChaosFasterTurnDamageTakenPatch
	{
		private static void Postfix(TacticalActor __instance, DamageResult damageResult, bool __result)
		{
			try
			{
				if (!__result || __instance == null || !__instance.IsControlledByPlayer)
				{
					return;
				}
				if (damageResult.HealthDamageAndHealDelta <= 0f && damageResult.ArmorDamage <= 0f)
				{
					return;
				}
				bool isAITurn = FasterTurnState.Controller?.CurrentFaction?.IsControlledByAI == true;
				if (!isAITurn)
				{
					return;
				}
				FasterTurnState.NotifyPlayerDamageTaken();
			}
			catch
			{
				// Never allowed to break TacticalActor's own damage-application pipeline - see summary above.
			}
		}
	}

	/// <summary>
	/// Earliest, most reliable suppression trigger: fires the instant a player-controlled
	/// overwatch/return-fire reaction is *requested*, before ChaosOverwatch's own interception logic
	/// (state-stack pushing via reflection, camera director hints, etc.) has done anything at all -
	/// added after <see cref="ChaosFasterTurnFreeCamEnterPatch"/> alone proved to have a window
	/// (root cause unconfirmed, but reproducible) where the first player overwatch/return-fire in a
	/// mission didn't suppress correctly, letting this mod's per-frame Timing writes collide with
	/// ChaosOverwatch's own Space/+/- speed-scrub controls and, per report, affect shot accuracy -
	/// see CLAUDE.md "Incident 3". A Harmony **postfix** (never a prefix) specifically so this can
	/// never affect whether the original `Activate` call - or any other mod's prefix on it, like
	/// ChaosOverwatch's own interception - runs: postfixes always run after every prefix on a
	/// patched method, whether or not one of those prefixes returned false and skipped the original
	/// body, so this fires reliably regardless of what ChaosOverwatch (or vanilla) does with the call.
	/// Uses the exact same directly-typed `shooter.IsControlledByPlayer` check
	/// <see cref="ChaosFasterTurnOverwatchPausePatch"/> already uses (no reflection into a private
	/// field needed here, unlike the FreeCam patch below). Deliberately stays scoped to player
	/// reactions only - it does NOT also trigger for "Reset Speed On Enemy Attack"
	/// (<see cref="ChaosFasterTurnOverwatchPausePatch.IsEnemyAttackOnPlayer"/>): that case has no
	/// manual-aim window or async trigger-then-delayed-check gap for an earlier trigger to close (see
	/// the class doc above <see cref="ChaosFasterTurnOverwatchPausePatch"/>), so the
	/// <c>FireWeaponAtTargetCrt</c> prefix alone is already sufficient there.
	/// </summary>
	[HarmonyPatch(typeof(ShootAbility), "Activate")]
	internal static class ChaosFasterTurnShootActivatePatch
	{
		private static void Postfix(ShootAbility __instance, object parameter)
		{
			// Player-turn lockout - see the matching guard (and CLAUDE.md) on
			// ChaosFasterTurnOverwatchPausePatch.Prefix. A player overwatch/return-fire reaction
			// can only structurally fire during the AI's turn anyway, so this is a defensive,
			// belt-and-suspenders check rather than one expected to ever actually trigger.
			if (!FasterTurnState.IsAITurn)
			{
				return;
			}
			TacticalAbilityTarget target = parameter as TacticalAbilityTarget;
			if (target == null)
			{
				return;
			}
			bool isReactionShot = target.AttackType == AttackType.Overwatch || target.AttackType == AttackType.ReturnFire;
			if (!isReactionShot)
			{
				return;
			}
			TacticalActor shooter = __instance.TacticalActor;
			if (shooter == null || !shooter.IsControlledByPlayer)
			{
				return;
			}
			FasterTurnState.BeginSuppressed();
		}
	}

	/// <summary>
	/// Covers the ChaosOverwatch manual-aim waiting window that comes *before* the shot: ChaosOverwatch
	/// intercepts a player-controlled overwatch/return-fire trigger by pushing the vanilla
	/// UIStateFreeCam state (normally used for the player's own free-aim shots) and waiting - for as
	/// long as the player takes to left-click/right-click/ESC - before ever calling
	/// FireWeaponAtTargetCrt. "It's the AI's turn" alone isn't a safe enough signal on its own -
	/// missions can have multiple hostile AI factions, and one AI faction's unit can hold overwatch
	/// against another AI faction's mover during the same AI turn (a legitimate case this mod must
	/// NOT suppress for, and initially wrongly did - see CLAUDE.md "Incident 2") - so this also reads
	/// UIStateFreeCam's own private "_controlledActor" field (the actor actually being aimed with,
	/// same field ChaosOverwatch's own ExitState patch already reads) and requires it to be
	/// player-controlled. Without ChaosOverwatch installed (or with its manual-aim options off),
	/// UIStateFreeCam is never entered during the enemy's turn at all regardless (there's no vanilla
	/// mechanism for the player to open free-aim UI during an AI turn otherwise), so this stays a
	/// ChaosOverwatch-version-agnostic check - no hard dependency on ChaosOverwatch being installed,
	/// and no need to read its internal (and otherwise inaccessible - separate assembly)
	/// OverwatchState.WaitingForPlayer.
	/// </summary>
	[HarmonyPatch(typeof(UIStateFreeCam), "EnterState")]
	internal static class ChaosFasterTurnFreeCamEnterPatch
	{
		private static readonly FieldInfo ControlledActorField =
			typeof(UIStateFreeCam).GetField("_controlledActor", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Postfix(UIStateFreeCam __instance)
		{
			bool isAITurn = FasterTurnState.Controller?.CurrentFaction?.IsControlledByAI == true;
			if (!isAITurn)
			{
				return;
			}
			TacticalActor controlledActor = ControlledActorField?.GetValue(__instance) as TacticalActor;
			if (controlledActor == null || !controlledActor.IsControlledByPlayer)
			{
				return;
			}
			FasterTurnState.BeginSuppressed();
		}
	}

	/// <summary>
	/// Ends the suppression <see cref="ChaosFasterTurnFreeCamEnterPatch"/> began, whenever FreeCam
	/// closes - confirmed shot (ConfirmShoot), cancelled (OnCancel/ESC), or any other exit path
	/// (e.g. ChaosOverwatch's own FreeCamExitStatePatch safety net) all funnel through this same
	/// ExitState. Unconditional (not gated on whose turn it is) since EndSuppressed is idempotent -
	/// harmless if this FreeCam session was never actually suppressing anything.
	/// </summary>
	[HarmonyPatch(typeof(UIStateFreeCam), "ExitState")]
	internal static class ChaosFasterTurnFreeCamExitPatch
	{
		private static void Postfix()
		{
			// Player-turn lockout (see CLAUDE.md): UIStateFreeCam is also vanilla's own everyday
			// free-aim camera for the player's normal manual shots on their own turn, so this
			// fires far more often than just the ChaosOverwatch-driven AI-turn case this patch
			// actually exists for. Skip entirely - no FasterTurnState.EndSuppressed() call, no
			// ApplyForCurrentFaction() sweep over every actor's Timing - whenever there's nothing
			// to clean up (Suppressed already false) and it isn't the AI's turn, so this mod never
			// touches actor state on an ordinary player-turn free-aim shot. Still unconditional
			// (not just IsAITurn-gated) whenever Suppressed IS true, as a safety net: if a
			// suppression genuinely started during an AI turn, it must still be released even if
			// this fires a moment after the turn changed - never risk leaving Suppressed stuck
			// true (see CLAUDE.md "Incident 3"). EndSuppressed is idempotent either way.
			if (!FasterTurnState.Suppressed && !FasterTurnState.IsAITurn)
			{
				return;
			}
			FasterTurnState.EndSuppressed();
		}
	}

	/// <summary>
	/// Zero-latency companion to <c>IsNavigatingUnderArmedOverwatch</c> (CLAUDE.md "Incident 6") -
	/// see "Incident 7". <c>FasterTurnState</c>'s own per-frame poll (from
	/// <see cref="ChaosFasterTurnUpdater"/>'s <c>MonoBehaviour.Update()</c>) can still run either
	/// before or after the game's own custom Timing-scheduler tick that actually drives movement
	/// within the same real engine frame - Unity gives no ordering guarantee between two independent
	/// per-frame update sources - so on whichever frame a mover first comes into overwatch range,
	/// there was still a real chance our own reapply lost that race and let one more elevated-speed
	/// step through before catching up, reported as "enemy moved a few steps at increased speed
	/// right as/after the shot triggered" even with the Incident 6 fix in place.
	/// <para/>
	/// This patches <c>TacticalLevelController.TriggerOverwatch(TacticalActor target)</c> itself -
	/// the exact public method vanilla calls once per real frame, for as long as the mover has any
	/// hostile overwatch watching it, from <c>TacticalNavigationComponent.UpdateActorTransformFromPathSample</c>
	/// (see the <c>TacticalLevelController</c> row in CLAUDE.md's "Key game classes" table) - as a
	/// **prefix**, so it runs synchronously, in-line, on the exact same call stack vanilla itself
	/// uses to decide whether an overwatch check is about to happen this frame. There is no separate
	/// update source to race against here: by the time vanilla's own code in this same method (and
	/// <c>ExecuteOverwatch</c> immediately after it) reads <c>target.Timing</c> to compute this
	/// frame's movement, this prefix has already forced it to <c>1f</c>. Always returns true
	/// (<c>Prefix</c> has no bool return here) - this never replaces or skips vanilla's own
	/// `TriggerOverwatch` logic, it only ever writes `Timing.Scale` before that logic runs.
	/// <para/>
	/// Reuses <see cref="FasterTurnState.IsNearArmedOverwatch"/> unchanged (same proximity-gated
	/// check <c>IsNavigatingUnderArmedOverwatch</c> uses) rather than vanilla's own undistanced
	/// watcher query, for the same reason Incident 6 needed the gate in the first place: vanilla
	/// gets away with no distance filter because it only decides "should a check run", not "should
	/// speed be capped" - blindly forcing `1f` for literally every enemy any time any overwatch
	/// exists anywhere (matching vanilla's own query exactly) reintroduces the "all enemies moved
	/// slowly" regression from Incident 6 renamed for a different call site.
	/// <para/>
	/// Deliberately does **not** call <see cref="FasterTurnState.BeginSuppressed"/>/`EndSuppressed`
	/// or set <see cref="FasterTurnState.Suppressed"/> - `TriggerOverwatch` fires every frame
	/// regardless of whether a shot ends up happening (see CLAUDE.md "Incident 6"), so there's no
	/// single well-defined "end" moment to release a flag at; this only ever writes `1f` directly to
	/// the one actor's own `Timing.Scale`, the same narrow, self-correcting kind of write
	/// <see cref="FasterTurnState.ApplyForCurrentFaction"/>'s own per-frame reapply already makes -
	/// if this actor turns out not to be at risk on some later frame, that same reapply naturally
	/// raises it back to <see cref="FasterTurnState.Multiplier"/> on its own, no explicit "end" needed.
	/// </summary>
	[HarmonyPatch(typeof(TacticalLevelController), "TriggerOverwatch")]
	internal static class ChaosFasterTurnTriggerOverwatchPatch
	{
		private static void Prefix(TacticalActor target, TacticalLevelController __instance)
		{
			try
			{
				// Player-turn lockout - see CLAUDE.md and the matching guard on
				// ChaosFasterTurnOverwatchPausePatch.Prefix. TriggerOverwatch fires for ANY mover
				// exposed to a hostile overwatch, including a player-controlled unit walking near
				// an enemy's overwatch on the player's own turn - this mod must not write to that
				// unit's Timing.Scale at all in that case.
				if (!FasterTurnState.IsAITurn)
				{
					return;
				}
				if (target == null || target.Timing == null || __instance == null)
				{
					return;
				}
				// Critical: TriggerOverwatch keeps getting called every real frame for as long as
				// the mover is exposed to a hostile OverwatchStatus - including all through the
				// actual shot windup once the cone has already matched (TriggerOverwatch only
				// early-returns *inside itself* while OverwatchTarget != null; the movement loop
				// that calls it keeps ticking regardless). During that windup vanilla has its own
				// TimingScale.AddScale(OverwatchTimeScale = 0.1f, controller) contribution live on
				// this exact actor (see FasterTurnState.IsUnderVanillaDramaticPause / "Incident 4").
				// The first version of this patch didn't check for that and unconditionally forced
				// Timing.Scale to 1f on every one of those frames too - directly overwriting
				// vanilla's intended 0.1x creep with a full-speed 1x, every single frame of the
				// windup, so the mover covered *more* ground during the reaction than even vanilla
				// alone would have allowed. Reported back as the fix making things worse, not
				// better - see CLAUDE.md "Incident 7". Skipping here whenever vanilla has already
				// claimed this actor leaves that 0.1x creep completely undisturbed, exactly like
				// ApplyForCurrentFaction()'s own per-frame reapply already does.
				if (FasterTurnState.IsUnderVanillaDramaticPause(target, __instance))
				{
					return;
				}
				if (FasterTurnState.IsNearArmedOverwatch(target, __instance))
				{
					target.Timing.Scale = 1f;
				}
			}
			catch
			{
				// Never allowed to break vanilla's own overwatch trigger check.
			}
		}
	}

	/// <summary>
	/// Closes the last known gap in the overwatch-overshoot protection - see CLAUDE.md "Incident 8".
	/// Both <see cref="FasterTurnState.ApplyForCurrentFaction"/>'s per-frame poll (Incident 6) and
	/// <see cref="ChaosFasterTurnTriggerOverwatchPatch"/> (Incident 7) only ever react *while the
	/// actor is already navigating* - `TacticalNavigationComponent.IsNavigating`/`TriggerOverwatch`
	/// both only fire once movement is actually under way. That leaves exactly one small, bounded
	/// gap: the very first movement step of a move that starts *already* within
	/// <see cref="FasterTurnState.IsNearArmedOverwatch"/> range (e.g. the mover's very next tile is
	/// already inside an overwatcher's reach) still plays at whatever `Timing.Scale` was in effect
	/// *before* the move began, since neither of those checks has had a chance to run yet.
	/// <para/>
	/// This patches `TacticalNavigationComponent.OnNavigateStart(PlayingAction action)` - the
	/// `onStart` callback `NavigationComponent.NavigateRoutine`'s enclosing `PlayAction` wires up
	/// (same `PlayingAction.SetState(Playing)`-driven "action begins" hook as
	/// `TacticalAbility.StartPlayingAction`, see the "Universal ability coverage" note on the
	/// `TacticalAbility` / `PlayingAction` row in "Key game classes") - so it fires synchronously,
	/// before `NavigateRoutine`'s coroutine (and therefore every `SamplePath`/
	/// `UpdateActorTransformFromPathSample` step) has run even once. A postfix here that finds the
	/// mover already at risk can force `Timing.Scale = 1f` before a single frame of movement has
	/// happened, fully closing the gap the other two triggers structurally can't reach.
	/// <para/>
	/// Same `IsUnderVanillaDramaticPause` guard as <see cref="ChaosFasterTurnTriggerOverwatchPatch"/>
	/// for the same reason (see "Incident 7") - though in practice a move can't *start* already
	/// mid-vanilla-dramatic-pause (that only exists once a reaction has already triggered on a
	/// stationary check), it costs nothing to stay consistent with every other write path in this
	/// file rather than assume that can never change.
	/// </summary>
	[HarmonyPatch(typeof(TacticalNavigationComponent), "OnNavigateStart")]
	internal static class ChaosFasterTurnNavigateStartPatch
	{
		private static void Postfix(TacticalNavigationComponent __instance)
		{
			try
			{
				// Player-turn lockout - see CLAUDE.md and the matching guard on
				// ChaosFasterTurnTriggerOverwatchPatch.Prefix above (same reasoning: a move that
				// starts near an armed overwatch can just as easily be a player-controlled unit
				// moving on the player's own turn).
				if (!FasterTurnState.IsAITurn)
				{
					return;
				}
				TacticalActor actor = __instance?.TacticalActor;
				TacticalLevelController controller = FasterTurnState.Controller;
				if (actor == null || actor.Timing == null || controller == null)
				{
					return;
				}
				if (FasterTurnState.IsUnderVanillaDramaticPause(actor, controller))
				{
					return;
				}
				if (FasterTurnState.IsNearArmedOverwatch(actor, controller))
				{
					actor.Timing.Scale = 1f;
				}
			}
			catch
			{
				// Never allowed to break vanilla's own navigation start-up.
			}
		}
	}
}
