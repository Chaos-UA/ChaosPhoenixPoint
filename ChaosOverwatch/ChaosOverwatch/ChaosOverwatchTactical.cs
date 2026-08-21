using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Cameras;
using Base.Core;
using Base.Input;
using Base.Entities;
using Base.Serialization.General;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Modding;
using PhoenixPoint.Tactical.Cameras;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels.PathProcessors;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;
using PhoenixPoint.Tactical.View.ViewModules;
using PhoenixPoint.Tactical.View.ViewStates;
using UnityEngine;

namespace ChaosOverwatch
{
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public class ChaosOverwatchTacInstanceData { public int ExampleData; }

    internal static class Log
    {
        public static void Debug(string msg)
        {
            if (ChaosOverwatchMain.Cfg?.EnableDebugLog == true)
                UnityEngine.Debug.Log(msg);
        }
    }

    internal static class OverwatchState
    {
        public static bool WaitingForPlayer;
        public static bool FireConfirmed;
        public static TacticalAbilityTarget ConfirmedTarget;
        public static AttackType PendingAttackType;
        public static UIStateFreeCam ActiveFreeCam;
        public static Timing PausedTlcTiming;
        public static bool WasManualAim; // true = left-click fire; false = right-click auto-fire

        // Speed control during manual aim (1/+ to increase, 2/- to decrease)
        public static TacticalActor SpeedTargetActor;
        public static float OriginalEnemyTimingScale;
        public static float EnemySpeedLevel; // 0 = paused, 1 = normal, 2 = double, etc.

        public static void UnpauseTlc()
        {
            if (PausedTlcTiming != null)
            {
                PausedTlcTiming.Paused = false;
                Log.Debug("[ChaosOverwatch] TLC Timing unpaused.");
                PausedTlcTiming = null;
            }
        }

        public static void AdjustEnemySpeed(float delta)
        {
            if (SpeedTargetActor == null) return;
            float newLevel = Mathf.Clamp(EnemySpeedLevel + delta, 0f, 3f);
            if (Mathf.Approximately(newLevel, EnemySpeedLevel)) return;
            EnemySpeedLevel = newLevel;
            if (newLevel <= 0f)
            {
                SpeedTargetActor.Timing.Scale = 0f;
                if (PausedTlcTiming != null) PausedTlcTiming.Paused = true;
            }
            else
            {
                if (PausedTlcTiming != null && PausedTlcTiming.Paused)
                    PausedTlcTiming.Paused = false;
                SpeedTargetActor.Timing.Scale = newLevel;
            }
            Log.Debug($"[ChaosOverwatch] Enemy speed level: {newLevel:F2}x");
        }

        public static void ResetEnemySpeed()
        {
            if (SpeedTargetActor != null)
            {
                SpeedTargetActor.Timing.Scale = OriginalEnemyTimingScale;
                SpeedTargetActor = null;
            }
            EnemySpeedLevel = 0f;
            OriginalEnemyTimingScale = 1f;
        }

        public static void Reset()
        {
            UnpauseTlc();
            ResetEnemySpeed();
            WaitingForPlayer = false;
            FireConfirmed = false;
            WasManualAim = false;
            ConfirmedTarget = null;
            PendingAttackType = AttackType.Regular;
            ActiveFreeCam = null;
        }
    }

    // Intercepts ShootAbility.Activate for player-controlled overwatch/return-fire.
    // Blocks the auto-shot, opens UIStateFreeCam for manual aiming, then fires once the player decides.
    [HarmonyPatch(typeof(ShootAbility), "Activate")]
    public static class OverwatchActivatePatch
    {
        private static readonly FieldInfo _statesStackField =
            typeof(TacticalView).GetField("_statesStack", BindingFlags.NonPublic | BindingFlags.Instance);

        // TravelType.Aim = 7 in the animator — makes CurrentlyAiming return true so
        // FireWeaponAtTargetCrt skips the ~2-second aim-start animation when stepOutNeeded=false.
        private static readonly PropertyInfo _animatorProp =
            typeof(TacticalActorBase).GetProperty("Animator");
        private static readonly MethodInfo _animSetIntegerMethod =
            Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule")
                ?.GetMethod("SetInteger", new Type[] { typeof(string), typeof(int) });

        private static readonly MethodInfo _checkShootTargetMethod =
            typeof(Weapon).GetMethod("CheckShootTarget", BindingFlags.NonPublic | BindingFlags.Instance, null,
                new Type[] { typeof(TacticalAbilityTarget), typeof(Vector3), typeof(bool) }, null);

        // Used in PushFreeAimState to set _freeFirstPersonShootingModule.SelectedAbility.
        // Normally UIStateShoot.EnterState does this, but we skip UIStateShoot.EnterState by
        // pushing UIStateFreeCam directly — so the accuracy circle would get a null SelectedAbility
        // and stay at scale (1,1,1) = "very big".
        private static readonly PropertyInfo _fpsModuleProp =
            typeof(UIStateShoot).GetProperty("_freeFirstPersonShootingModule", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _selectedAbilityField =
            typeof(UIModuleFreeFirstPersonShooting).GetField("SelectedAbility", BindingFlags.Public | BindingFlags.Instance);

        public static bool Prefix(ShootAbility __instance, object parameter)
        {
            Log.Debug($"[ChaosOverwatch] ShootAbility.Activate called. WaitingForPlayer={OverwatchState.WaitingForPlayer} parameter={parameter?.GetType().Name ?? "null"}");

            if (OverwatchState.WaitingForPlayer)
            {
                Log.Debug("[ChaosOverwatch] Already waiting for player — letting original run.");
                return true;
            }

            TacticalAbilityTarget target = parameter as TacticalAbilityTarget;
            if (target == null)
            {
                Log.Debug("[ChaosOverwatch] parameter is not TacticalAbilityTarget — skipping.");
                return true;
            }

            TacticalActor actor = __instance.TacticalActor;
            if (actor == null)
            {
                Log.Debug("[ChaosOverwatch] TacticalActor is null — skipping.");
                return true;
            }

            Log.Debug($"[ChaosOverwatch] Actor={actor.DisplayName} IsControlledByPlayer={actor.IsControlledByPlayer} AttackType={target.AttackType}");

            if (!actor.IsControlledByPlayer)
            {
                Log.Debug("[ChaosOverwatch] Not player-controlled — skipping.");
                return true;
            }

            bool interceptOverwatch  = target.AttackType == AttackType.Overwatch   && (ChaosOverwatchMain.Cfg?.ManualOverWatch  ?? false);
            bool interceptReturnFire = target.AttackType == AttackType.ReturnFire  && (ChaosOverwatchMain.Cfg?.ManualReturnFire ?? false);
            if (!interceptOverwatch && !interceptReturnFire)
            {
                Log.Debug($"[ChaosOverwatch] AttackType={target.AttackType} not intercepted (config or type mismatch) — skipping.");
                return true;
            }

            Log.Debug($"[ChaosOverwatch] Intercepting {target.AttackType} for {actor.DisplayName} — opening FreeAim UI.");

            OverwatchState.WaitingForPlayer = true;
            OverwatchState.PendingAttackType = target.AttackType;
            OverwatchState.FireConfirmed = false;
            OverwatchState.ConfirmedTarget = null;

            // Overwatch only: freeze the world and enable speed scrub keys.
            // Return Fire: enemy is mid-attack — pausing TLC here corrupts their animation
            // and causes ESC to break the game when UnpauseTlc fires into a bad state.
            if (target.AttackType == AttackType.Overwatch)
            {
                OverwatchState.SpeedTargetActor = target.Actor as TacticalActor;
                OverwatchState.OriginalEnemyTimingScale = OverwatchState.SpeedTargetActor?.Timing?.Scale ?? 1f;
                OverwatchState.EnemySpeedLevel = 0f;
            }

            Log.Debug("[ChaosOverwatch] Starting WaitAndFire coroutine via PlayAction.");
            __instance.PlayAction(
                action => WaitAndFire(action, __instance, target),
                parameter,
                null
            );

            Log.Debug("[ChaosOverwatch] Returning false — original Activate blocked.");
            return false;
        }

        private static void PushFreeAimState(ShootAbility ability, TacticalAbilityTarget originalTarget, TacticalActor actor)
        {
            UIStateFreeCam freeCam = null;
            try
            {
                TacticalView view = actor.TacticalLevel.View;
                Log.Debug($"[ChaosOverwatch] PushFreeAimState: view={view != null} _statesStackField={_statesStackField != null}");

                view.SelectedActor = actor;
                actor.FPSMode = true;

                List<TacticalAbilityTarget> validTargets = new List<TacticalAbilityTarget>(ability.GetTargets());
                Log.Debug($"[ChaosOverwatch] PushFreeAimState: {validTargets.Count} valid targets.");

                // Phantom UIStateShoot is required as sendingState — UIStateFreeCam.ExitState calls
                // TransferSelectedShootTarget/ShouldTransitionToFreeCam/SetExitImmediately on it.
                UIStateShoot phantom = new UIStateShoot(ability, actor, originalTarget.Actor as TacticalActor, validTargets, false);

                freeCam = new UIStateFreeCam(ability, actor, originalTarget.Actor, validTargets, phantom);
                OverwatchState.ActiveFreeCam = freeCam;

                Log.Debug("[ChaosOverwatch] PushFreeAimState: CameraDirector state BEFORE push: " + view.CameraDirector.DirectorState.ToString());

                object stack = _statesStackField.GetValue(view);
                stack.GetType()
                     .GetMethod("SwitchToState")
                     .Invoke(stack, new object[] { freeCam, StateStackAction.PushOnTop });

                Log.Debug("[ChaosOverwatch] PushFreeAimState: UIStateFreeCam pushed. CameraDirector state AFTER push: " + view.CameraDirector.DirectorState.ToString());

                // UIStateShoot.EnterState (which normally sets _freeFirstPersonShootingModule.SelectedAbility)
                // is bypassed when we push UIStateFreeCam directly. Without it UpdateAccuracyIndicator
                // gets SelectedAbility=null → NullReferenceException → scale never set → circle stays (1,1,1).
                var fpsModule = _fpsModuleProp?.GetValue(freeCam);
                if (fpsModule != null)
                    _selectedAbilityField?.SetValue(fpsModule, ability as ShootAbility);

                // During AI turn the director state contains hints that override ManualAim.
                // Match([AbilityActivated, ManualAim]) picks AbilityActivated camera, then
                // ClearUnmanagedParams removes ManualAim (Unmanaged=true, didn't win).
                // Remove all AI hints, then re-push ManualAim as the sole hint so
                // Match([ManualAim]) picks the FirstPersonCamera node and switches camera.
                view.CameraDirector.RemoveHints(item => item.Hint != CameraDirectorHint.ManualAim);
                view.CameraDirector.Hint(CameraDirectorHint.ManualAim, new TacCamDirectorParams { Actor = actor });

                Log.Debug("[ChaosOverwatch] PushFreeAimState: CameraDirector state AFTER ManualAim: " + view.CameraDirector.DirectorState.ToString());
                Log.Debug($"[ChaosOverwatch] PushFreeAimState: CameraDirector.Blending={view.CameraDirector.Blending} Busy={view.CameraDirector.Busy}");

                // If the camera switched instantly (Blending=false), FirstPersonCamera._position
                // is still zero, which causes UpdateState to crash in UpdateInfoAtRay.
                // Call TurnFPSCamToActor to give the FPS camera a valid position/orientation.
                if (originalTarget.Actor != null)
                {
                    MethodInfo turnToActor = typeof(UIStateFreeCam).GetMethod(
                        "TurnFPSCamToActor",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (turnToActor != null)
                    {
                        turnToActor.Invoke(freeCam, new object[] { originalTarget.Actor, false });
                        Log.Debug("[ChaosOverwatch] PushFreeAimState: TurnFPSCamToActor called.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChaosOverwatch] PushFreeAimState FAILED: {ex}");
                // Fallback: fire at the auto-target.
                OverwatchState.WaitingForPlayer = false;
                OverwatchState.FireConfirmed = true;
                OverwatchState.ConfirmedTarget = originalTarget;
                return;
            }

            // Separate try-catch: if _freeFirstPersonShootingModule isn't ready, the GUI won't
            // show but WaitingForPlayer stays true so the left-click path still fires the shot.
            try
            {
                // If the switch was instant there will be no Cinemachine blend, so OnCameraBlendOver
                // never fires naturally.  Force it to set _isBlendingOver=true and show the AIM GUI.
                MethodInfo onBlendOver = typeof(UIStateFreeCam).GetMethod(
                    "OnCameraBlendOver",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (onBlendOver != null)
                {
                    onBlendOver.Invoke(freeCam, null);
                    Log.Debug("[ChaosOverwatch] PushFreeAimState: OnCameraBlendOver forced.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChaosOverwatch] PushFreeAimState: OnCameraBlendOver force failed (AIM GUI may not show): {ex}");
            }
        }

        private static IEnumerator<NextUpdate> WaitAndFire(PlayingAction action, ShootAbility ability, TacticalAbilityTarget originalTarget)
        {
            Log.Debug("[ChaosOverwatch] WaitAndFire coroutine started.");

            TacticalActor actor = ability.TacticalActor;
            bool didStepOut = false;
            Vector3 stepBackPos = Vector3.zero;

            // === Pre-navigation step-out (Overwatch only) ===
            // The soldier physically steps out of cover before the FreeCam opens so the player
            // sees the character in position before they aim. Return Fire step-out is blocked by
            // the game engine (FireWeaponAtTargetCrt yields break at line 2015 for that case).
            if (originalTarget.AttackType == AttackType.Overwatch)
            {
                // C# forbids yield inside try/catch, so compute the step-out parameters first,
                // then yield outside the try block.
                Vector3 shootFromPos = Vector3.zero;
                NavigationSettings stepOutNavSettings = null;
                bool stepOutNeeded = false;
                try
                {
                    // Populate ShootFromPos the same way FireWeaponAtTargetCrt does at line 2003.
                    _checkShootTargetMethod?.Invoke(ability.Weapon, new object[] { originalTarget, actor.Pos, false });
                    shootFromPos = originalTarget.ShootFromPos;
                    bool posValid = !float.IsNaN(shootFromPos.x) && !float.IsNaN(shootFromPos.y) && !float.IsNaN(shootFromPos.z);
                    stepOutNeeded = posValid && Vector3.SqrMagnitude(actor.Pos - shootFromPos) > 0.01f;
                    if (stepOutNeeded)
                        stepOutNavSettings = ability.Weapon.GetFireStartNavigationSettings(originalTarget, true);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChaosOverwatch] WaitAndFire: step-out setup failed ({ex.Message}) — skipping step-out.");
                    stepOutNeeded = false;
                }

                if (stepOutNeeded)
                {
                    stepBackPos = actor.Pos;
                    Log.Debug($"[ChaosOverwatch] WaitAndFire: stepping out from {stepBackPos} to {shootFromPos}.");
                    yield return action.Timing.Call(DoStepOut(actor, shootFromPos, stepOutNavSettings), null);
                    didStepOut = true;
                    Log.Debug("[ChaosOverwatch] WaitAndFire: step-out complete.");
                }
                else
                {
                    Log.Debug("[ChaosOverwatch] WaitAndFire: no step-out needed.");
                }
            }

            // === Open FreeCam ===
            PushFreeAimState(ability, originalTarget, actor);

            // === Freeze enemy (Overwatch only, after step-out) ===
            if (originalTarget.AttackType == AttackType.Overwatch)
            {
                var tlc = UnityEngine.Object.FindObjectOfType<TacticalLevelController>();
                if (tlc != null)
                {
                    OverwatchState.PausedTlcTiming = tlc.Timing;
                    tlc.Timing.Paused = true;
                    Log.Debug("[ChaosOverwatch] TLC Timing paused — enemy frozen during manual aim.");
                }
            }

            // === Wait for player decision ===
            int frameCount = 0;
            UIStateFreeCam freeCam = OverwatchState.ActiveFreeCam;
            FieldInfo isBlendingOverField = typeof(UIStateFreeCam).GetField("_isBlendingOver", BindingFlags.NonPublic | BindingFlags.Instance);

            while (OverwatchState.WaitingForPlayer)
            {
                frameCount++;
                if (frameCount % 60 == 0)
                {
                    bool blending = freeCam != null && isBlendingOverField != null && (bool)isBlendingOverField.GetValue(freeCam);
                    TacticalView view = actor?.TacticalLevel?.View;
                    Log.Debug($"[ChaosOverwatch] WaitAndFire: frame {frameCount}, _isBlendingOver={blending}, CamBlending={view?.CameraDirector?.Blending}");
                }
                yield return NextUpdate.NextFrame;
            }

            Log.Debug($"[ChaosOverwatch] WaitAndFire: player decided. FireConfirmed={OverwatchState.FireConfirmed} ConfirmedTarget={OverwatchState.ConfirmedTarget != null}");

            if (!OverwatchState.FireConfirmed)
            {
                Log.Debug("[ChaosOverwatch] WaitAndFire: not confirmed — aborting.");
                if (didStepOut)
                {
                    Log.Debug("[ChaosOverwatch] WaitAndFire: stepping back to cover after abort.");
                    yield return action.Timing.Call(DoStepBack(actor, stepBackPos, BuildStepBackNavSettings(originalTarget, ability.Weapon)), null);
                }
                OverwatchState.Reset();
                yield break;
            }

            TacticalAbilityTarget fireTarget = OverwatchState.ConfirmedTarget ?? originalTarget;
            fireTarget.AttackType = OverwatchState.PendingAttackType;

            // For Return Fire manual aim (left-click): GetAbilityTarget() sets PositionToApply =
            // camera.ScreenPointToRay(...).GetPoint(1000f) — a point 1000 units along the FPS
            // camera ray, typically outside the map.  FireWeaponAtTargetCrt sends two camera hints
            // that both resolve the orbit target from PositionToApply, so the camera flies there.
            //   • CameraDirectorHint.Shoot  uses AbilityTarget.Actor → TargetActor.GetAimPoint()
            //     when Actor is set, otherwise falls back to PositionToApply.
            //   • CameraDirectorHint.ShootingStarted sets TargetPosition = GetWorkingPosition()
            //     = PositionToApply directly, with no Actor override path.
            // Fix: set Actor = enemy on the fireTarget (Shoot hint uses enemy.GetAimPoint() and
            // ignores PositionToApply), and set PositionToApply = enemy.GetAimPoint().position
            // (ShootingStarted reads it directly, so it also stays on screen).
            if (OverwatchState.WasManualAim && fireTarget.AttackType == AttackType.ReturnFire)
            {
                TacticalActorBase rfActor = fireTarget.Actor ?? originalTarget.Actor;
                if (rfActor != null)
                {
                    if (fireTarget.Actor == null)
                        fireTarget.Actor = rfActor;
                    UnityEngine.Transform aimPt = rfActor.GetAimPoint();
                    fireTarget.PositionToApply = aimPt != null ? aimPt.position : rfActor.Pos;
                    Log.Debug($"[ChaosOverwatch] WaitAndFire: RF camera fix applied, PositionToApply={fireTarget.PositionToApply}");
                }
                else if (originalTarget.HasPositionToApply)
                    fireTarget.PositionToApply = originalTarget.PositionToApply;
            }

            Log.Debug($"[ChaosOverwatch] WaitAndFire: firing. AttackType={fireTarget.AttackType} target={fireTarget.Actor?.DisplayName ?? "terrain"}");

            if (!ability.Weapon.IsAttackSilent(actor, null))
            {
                TacticalFactionVision.IncrementKnownCounterToAll(actor, KnownState.Located, 1, true);
                foreach (TacticalFaction faction in actor.TacticalLevel.Factions)
                {
                    if (faction != actor.TacticalFaction)
                        faction.Vision.UpdateVisibilityOfAllTowardsActor(actor, float.PositiveInfinity, true);
                }
            }

            // Freeze the moving enemy so they don't keep walking while we shoot at them.
            // Player's shot runs on player.Timing (child of TLC.Timing); enemy.Timing is a sibling,
            // so pausing it only stops the enemy without affecting our shot coroutine.
            TacticalActor shotTarget = originalTarget.Actor as TacticalActor;
            bool pausedShotTarget = OverwatchState.WasManualAim && shotTarget != null
                && OverwatchState.PendingAttackType == AttackType.Overwatch;
            if (pausedShotTarget)
            {
                shotTarget.Timing.Paused = true;
                Log.Debug($"[ChaosOverwatch] WaitAndFire: paused {shotTarget.DisplayName} Timing for shot.");
            }

            // After pre-navigation, the aim-start animation ran as part of the step-out.
            // FireWeaponAtTargetCrt replays it when stepOutNeeded=false && !CurrentlyAiming,
            // adding ~2 s. Set TravelType=7 (Aim) on the animator so CurrentlyAiming=true
            // and the block is skipped. UnityEngine.AnimationModule isn't in ModSDK so we
            // reach the animator via reflection.
            if (didStepOut && _animSetIntegerMethod != null)
            {
                object anim = _animatorProp?.GetValue(actor);
                if (anim != null)
                {
                    _animSetIntegerMethod.Invoke(anim, new object[] { "TravelType", 7 });
                    Log.Debug("[ChaosOverwatch] WaitAndFire: set TravelType=Aim to skip aim-start delay.");
                }
            }

            Log.Debug("[ChaosOverwatch] WaitAndFire: calling FireWeaponAtTargetCrt...");
            yield return action.Timing.Call(
                actor.TacticalLevel.FireWeaponAtTargetCrt(ability.Weapon, fireTarget, ability),
                null
            );

            if (pausedShotTarget)
            {
                shotTarget.Timing.Paused = false;
                Log.Debug($"[ChaosOverwatch] WaitAndFire: unpaused {shotTarget.DisplayName} Timing after shot.");
            }

            // === Step back after shot ===
            // FireWeaponAtTargetCrt sees stepOutNeeded=false (actor is already at ShootFromPos)
            // so it skips its own step-back. We do it here instead.
            if (didStepOut)
            {
                Log.Debug("[ChaosOverwatch] WaitAndFire: stepping back to cover after shot.");
                yield return action.Timing.Call(DoStepBack(actor, stepBackPos, BuildStepBackNavSettings(originalTarget, ability.Weapon)), null);
            }

            Log.Debug("[ChaosOverwatch] WaitAndFire: complete.");
            OverwatchState.Reset();
        }

        private static IEnumerator<NextUpdate> DoStepOut(TacticalActor actor, Vector3 shootFromPos, NavigationSettings navSettings)
        {
            Timing timing = actor.Timing;
            yield return timing.Call(actor.TacticalNav.NavObstacleEnable(false), null);
            yield return timing.Call(actor.TacticalNav.NavigateAndWaitUntilFinished(shootFromPos, navSettings), null);
        }

        private static IEnumerator<NextUpdate> DoStepBack(TacticalActor actor, Vector3 targetPos, NavigationSettings navSettings)
        {
            Timing timing = actor.Timing;
            yield return timing.Call(actor.TacticalNav.NavigateAndWaitUntilFinished(targetPos, navSettings), null);
        }

        private static NavigationSettings BuildStepBackNavSettings(TacticalAbilityTarget target, Equipment equipment)
        {
            NavigationSettings nav = new NavigationSettings();
            nav.CostsAPToActor = false;
            nav.UpdateNavigationPerceptionRange = false;
            nav.TriggerOverwatch = true;
            nav.TeleportOnInvalidPath = true;
            nav.SetPathProcessors(new TacticalPathProcessor[] { new StepBackPathProcessor(target, equipment) });
            return nav;
        }
    }

    // Log when Cinemachine blend to FPS camera completes.
    [HarmonyPatch(typeof(UIStateFreeCam), "OnCameraBlendOver")]
    public static class FreeCamBlendOverPatch
    {
        public static void Prefix(UIStateFreeCam __instance)
        {
            Log.Debug($"[ChaosOverwatch] UIStateFreeCam.OnCameraBlendOver fired! WaitingForPlayer={OverwatchState.WaitingForPlayer}");
        }
    }

    // Intercept UIStateFreeCam.ConfirmShoot (left-click / fire button).
    // Forces _isBlendingOver=true so the shot fires even if the FPS camera blend is still in
    // progress (which happens during AI turn when CameraDirector hints conflict), then captures
    // the player's chosen aim position and signals WaitAndFire instead of calling ActivateAbility.
    [HarmonyPatch(typeof(UIStateFreeCam), "ConfirmShoot")]
    public static class FreeAimConfirmPatch
    {
        private static readonly MethodInfo _getAbilityTarget =
            typeof(UIStateFreeCam).GetMethod(
                "GetAbilityTarget",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);

        private static readonly FieldInfo _isBlendingOver =
            typeof(UIStateFreeCam).GetField("_isBlendingOver", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _switchToPrev =
            typeof(TacticalViewState).GetMethod("SwitchToPreviousState", BindingFlags.NonPublic | BindingFlags.Instance);

        static FreeAimConfirmPatch()
        {
            Log.Debug($"[ChaosOverwatch] FreeAimConfirmPatch reflections: _getAbilityTarget={_getAbilityTarget != null} _isBlendingOver={_isBlendingOver != null} _switchToPrev={_switchToPrev != null}");
        }

        public static bool Prefix(UIStateFreeCam __instance)
        {
            Log.Debug($"[ChaosOverwatch] UIStateFreeCam.ConfirmShoot called. WaitingForPlayer={OverwatchState.WaitingForPlayer}");

            if (!OverwatchState.WaitingForPlayer)
                return true;

            bool currentlyBlending = _isBlendingOver != null && !(bool)_isBlendingOver.GetValue(__instance);
            if (currentlyBlending)
            {
                // FPS camera blend not done yet (e.g., AI-turn camera conflict).
                // Force the flag so the original code won't gate on it, then proceed.
                Log.Debug("[ChaosOverwatch] ConfirmShoot: _isBlendingOver=false — forcing true to allow fire.");
                _isBlendingOver?.SetValue(__instance, true);
            }

            TacticalAbilityTarget chosen = _getAbilityTarget != null
                ? (TacticalAbilityTarget)_getAbilityTarget.Invoke(__instance, null)
                : null;

            Log.Debug($"[ChaosOverwatch] ConfirmShoot: aim target={chosen?.Actor?.DisplayName ?? (chosen != null ? "terrain" : "null")}");

            OverwatchState.ConfirmedTarget = chosen;
            OverwatchState.FireConfirmed = true;
            OverwatchState.WasManualAim = true;
            OverwatchState.WaitingForPlayer = false;
            OverwatchState.UnpauseTlc();

            try
            {
                _switchToPrev?.Invoke(__instance, null);
                Log.Debug("[ChaosOverwatch] ConfirmShoot: FreeCam state popped.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChaosOverwatch] ConfirmShoot: failed to pop state: {ex}");
            }

            return false;
        }
    }

    // Intercept UIStateFreeCam.OnCancel (right-click / ESC).
    // ESC → skip return fire entirely. Right-click → auto-shot at the original target.
    [HarmonyPatch(typeof(UIStateFreeCam), "OnCancel")]
    public static class FreeAimCancelPatch
    {
        private static readonly MethodInfo _switchToPrev =
            typeof(TacticalViewState).GetMethod("SwitchToPreviousState", BindingFlags.NonPublic | BindingFlags.Instance);

        // Input is in UnityEngine.InputLegacyModule which isn't in the ModSDK references.
        // Cache the method via reflection so we get a compile-time-clean call.
        private static readonly MethodInfo _inputGetKey =
            Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
                ?.GetMethod("GetKey", new[] { typeof(KeyCode) });

        private static bool IsEscapePressed() =>
            _inputGetKey != null && (bool)_inputGetKey.Invoke(null, new object[] { KeyCode.Escape });

        public static bool Prefix(UIStateFreeCam __instance)
        {
            Log.Debug($"[ChaosOverwatch] UIStateFreeCam.OnCancel called. WaitingForPlayer={OverwatchState.WaitingForPlayer}");

            if (!OverwatchState.WaitingForPlayer)
                return true;

            bool escPressed = IsEscapePressed();
            if (escPressed)
            {
                Log.Debug("[ChaosOverwatch] OnCancel: ESC — skipping return fire.");
                OverwatchState.WaitingForPlayer = false;
                OverwatchState.FireConfirmed = false;
                OverwatchState.ConfirmedTarget = null;
            }
            else
            {
                Log.Debug("[ChaosOverwatch] OnCancel: right-click — firing at auto-target.");
                OverwatchState.WaitingForPlayer = false;
                OverwatchState.FireConfirmed = true;
                OverwatchState.ConfirmedTarget = null;
            }
            OverwatchState.UnpauseTlc();

            try
            {
                _switchToPrev?.Invoke(__instance, null);
                Log.Debug("[ChaosOverwatch] OnCancel: FreeCam state popped.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChaosOverwatch] OnCancel: failed to pop state: {ex}");
            }

            return false;
        }
    }

    // Block all keyboard/gamepad input while waiting for the player to manually aim.
    // OnInputEvent is the single funnel for weapon switch (TacSelectItem), ability hotkeys
    // (ActivateAbility, SelectAbility), cycle weapons, zoom-to-FPS, etc.
    // We pass through only "Select" (left-click → sets _mouseButtonDown → ConfirmShoot) and
    // InputTypeChanged (harmless device-type notification).
    // OnCancel (right-click / ESC) arrives via a separate base-class mechanism and is NOT
    // filtered here — it still reaches our FreeAimCancelPatch.
    [HarmonyPatch(typeof(UIStateShoot), "OnInputEvent")]
    public static class ShootInputBlockPatch
    {
        public static bool Prefix(InputEvent ev)
        {
            if (!OverwatchState.WaitingForPlayer) return true;
            if (ev.Type == InputEventType.InputTypeChanged) return true;
            if (ev.Name == "Select") return true;
            Log.Debug($"[ChaosOverwatch] OnInputEvent blocked: {ev.Name} ({ev.Type})");
            return false;
        }
    }

    // UIStateFreeCam.InitCamera always calls TurnFPSCamToActor(actor, animate=true) which sends
    // CameraHint.Rotate with FreeCameraParams. This happens inside SwitchToState, before our
    // RemoveHints fix can run. When AI-turn hints beat ManualAim, PlanarScrollCamera remains
    // current and crashes on (int)param cast. Skip the hint silently — our post-SwitchToState
    // RemoveHints + Hint(ManualAim) + TurnFPSCamToActor(false) handles camera positioning.
    [HarmonyPatch(typeof(PlanarScrollCamera), "HandleHint")]
    public static class PlanarScrollCameraHandleHintPatch
    {
        public static bool Prefix(CameraHint hint, object param)
        {
            if (hint == CameraHint.Rotate && !(param is int))
            {
                Log.Debug($"[ChaosOverwatch] PlanarScrollCamera.HandleHint: CameraHint.Rotate with {param?.GetType().Name} — skipping to prevent InvalidCastException.");
                return false;
            }
            return true;
        }
    }

    // Safety net: if UIStateFreeCam exits via any path that didn't go through ConfirmShoot or
    // OnCancel (e.g. scroll-out triggering ExitCall → SwitchToPreviousState directly), WaitAndFire
    // would hang forever. Signal an auto-shot so the turn can complete.
    [HarmonyPatch(typeof(UIStateFreeCam), "ExitState")]
    public static class FreeCamExitStatePatch
    {
        private static readonly FieldInfo _controlledActorField =
            typeof(UIStateFreeCam).GetField("_controlledActor", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void Prefix()
        {
            if (!OverwatchState.WaitingForPlayer) return;
            Log.Debug("[ChaosOverwatch] ExitState: still waiting — triggering auto-shot (scroll-out or unexpected exit).");
            OverwatchState.WaitingForPlayer = false;
            OverwatchState.FireConfirmed = true;
            OverwatchState.ConfirmedTarget = null;
            OverwatchState.UnpauseTlc();
        }

        // ExitState calls RemoveHint(ManualAim) which only pops the LAST occurrence (Pop =
        // FindLastIndex + RemoveAt). We push TWO ManualAim hints in PushFreeAimState (one from
        // InitCamera, one explicitly). After ExitState removes the last one, the first survives,
        // keeping FirstPersonCamera alive → its CamOffset callback fires on dead FreeCam → crash.
        // Remove any remaining ManualAim hints after ExitState has done its own cleanup.
        public static void Postfix(UIStateFreeCam __instance)
        {
            try
            {
                var actor = _controlledActorField?.GetValue(__instance) as TacticalActor;
                actor?.TacticalLevel?.View?.CameraDirector
                      ?.RemoveHints(item => item.Hint == CameraDirectorHint.ManualAim);
                Log.Debug("[ChaosOverwatch] ExitState Postfix: remaining ManualAim hints cleared.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChaosOverwatch] ExitState Postfix: ManualAim cleanup failed: {ex.Message}");
            }
        }
    }

    // Poll Unity key state each frame to scrub enemy speed during manual aim.
    //   1 or +/=        → +0.25x (max 3x)
    //   2 or -          → −0.25x (min 0)
    //   1 held + +/=    → +0.75x (triple step)
    //   2 held + -      → −0.75x (triple step)
    //   Space           → pause immediately (set to 0)
    // At speed 0 TLC.Timing is re-paused; above 0 TLC is unpaused and enemy.Timing.Scale sets rate.
    [HarmonyPatch(typeof(UIStateFreeCam), "UpdateState")]
    public static class FreeCamSpeedControlPatch
    {
        private static readonly Type _inputType =
            Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule");

        private static readonly MethodInfo _getKeyDown =
            _inputType?.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });

        private static readonly MethodInfo _getKey =
            _inputType?.GetMethod("GetKey", new[] { typeof(KeyCode) });

        private static bool IsKeyDown(KeyCode key) =>
            _getKeyDown != null && (bool)_getKeyDown.Invoke(null, new object[] { key });

        private static bool IsKey(KeyCode key) =>
            _getKey != null && (bool)_getKey.Invoke(null, new object[] { key });

        public static void Postfix()
        {
            if (!OverwatchState.WaitingForPlayer || OverwatchState.SpeedTargetActor == null) return;

            // Space → toggle: pause if moving, resume at max if already paused
            if (IsKeyDown(KeyCode.Space))
            {
                if (Mathf.Approximately(OverwatchState.EnemySpeedLevel, 0f))
                    OverwatchState.AdjustEnemySpeed(+100f); // clamps to 3 (max)
                else
                    OverwatchState.AdjustEnemySpeed(-100f); // clamps to 0
                return;
            }

            bool held1    = IsKey(KeyCode.Alpha1);
            bool held2    = IsKey(KeyCode.Alpha2);
            bool heldPlus = IsKey(KeyCode.Plus) || IsKey(KeyCode.Equals) || IsKey(KeyCode.KeypadPlus);
            bool heldMinus= IsKey(KeyCode.Minus) || IsKey(KeyCode.KeypadMinus);

            bool down1    = IsKeyDown(KeyCode.Alpha1);
            bool down2    = IsKeyDown(KeyCode.Alpha2);
            bool downPlus = IsKeyDown(KeyCode.Plus) || IsKeyDown(KeyCode.Equals) || IsKeyDown(KeyCode.KeypadPlus);
            bool downMinus= IsKeyDown(KeyCode.Minus) || IsKeyDown(KeyCode.KeypadMinus);

            // Combo: 1 + Plus pressed together → triple step increase
            if ((down1 && heldPlus) || (downPlus && held1))
                OverwatchState.AdjustEnemySpeed(+0.75f);
            // Combo: 2 + Minus pressed together → triple step decrease
            else if ((down2 && heldMinus) || (downMinus && held2))
                OverwatchState.AdjustEnemySpeed(-0.75f);
            // Single: 1 or Plus → normal step increase
            else if (down1 || downPlus)
                OverwatchState.AdjustEnemySpeed(+0.25f);
            // Single: 2 or Minus → normal step decrease
            else if (down2 || downMinus)
                OverwatchState.AdjustEnemySpeed(-0.25f);
        }
    }

    // Pause the target actor during regular (non-overwatch/return-fire) player shots so enemy
    // movement animations cannot cause misses. Wraps FireWeaponAtTargetCrt in a generator that
    // pauses target.Timing before the coroutine starts and unpauses it when it finishes.
    // [ThreadStatic] _reentrant prevents the wrapper from wrapping itself when it re-calls
    // FireWeaponAtTargetCrt to get the original enumerator.
    [HarmonyPatch(typeof(TacticalLevelController), "FireWeaponAtTargetCrt")]
    public static class FireWeaponPauseTargetPatch
    {
        [ThreadStatic]
        private static bool _reentrant;

        public static bool Prefix(TacticalLevelController __instance, Weapon weapon, TacticalAbilityTarget abilityTarget, ShootAbility shootAbility, ref IEnumerator<NextUpdate> __result)
        {
            if (_reentrant) return true;
            if (!(ChaosOverwatchMain.Cfg?.PauseTargetAnimationOnRegularShot ?? false)) return true;
            if (abilityTarget.AttackType != AttackType.Regular) return true;

            TacticalActor shooter = shootAbility?.TacticalActor;
            if (shooter == null || !shooter.IsControlledByPlayer) return true;

            // During the player's own turn the target enemy may have return fire queued.
            // Pausing their Timing blocks it from completing, causing a permanent deadlock.
            if (shooter.DuringOwnTurn) return true;

            TacticalActor shotTarget = abilityTarget.Actor as TacticalActor;
            if (shotTarget == null) return true;

            Log.Debug($"[ChaosOverwatch] FireWeaponPauseTargetPatch: pausing {shotTarget.DisplayName} for regular shot.");
            __result = WrapWithPause(__instance, weapon, abilityTarget, shootAbility, shotTarget);
            return false;
        }

        private static IEnumerator<NextUpdate> WrapWithPause(TacticalLevelController level, Weapon weapon, TacticalAbilityTarget abilityTarget, ShootAbility shootAbility, TacticalActor shotTarget)
        {
            shotTarget.Timing.Paused = true;
            _reentrant = true;
            IEnumerator<NextUpdate> original = level.FireWeaponAtTargetCrt(weapon, abilityTarget, shootAbility);
            _reentrant = false;
            while (true)
            {
                bool stepped;
                try { stepped = original.MoveNext(); }
                catch { shotTarget.Timing.Paused = false; throw; }
                if (!stepped) break;
                yield return original.Current;
            }
            shotTarget.Timing.Paused = false;
            Log.Debug($"[ChaosOverwatch] FireWeaponPauseTargetPatch: unpaused {shotTarget.DisplayName} after shot.");
        }
    }

    public class ChaosOverwatchTactical : ModTactical
    {
        public override void OnTacticalStart()
        {
            base.OnTacticalStart();
        }

        public override void OnTacticalEnd()
        {
            OverwatchState.Reset();
            base.OnTacticalEnd();
        }

        public override void ProcessTacticalInstanceData(object instanceData)
        {
            ChaosOverwatchTacInstanceData data = (ChaosOverwatchTacInstanceData)instanceData;
        }

        public override object RecordTacticalInstanceData()
        {
            return new ChaosOverwatchTacInstanceData() { ExampleData = 5 };
        }

        public override void OnNewTurn(int turnNumber)
        {
        }
    }
}