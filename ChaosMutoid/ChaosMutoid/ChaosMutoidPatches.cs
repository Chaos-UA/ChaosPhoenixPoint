using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Eventus;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Addons;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.Entities.GameTags;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Requirement;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewControllers.AugmentationScreen;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace ChaosMutoid
{
	/// <summary>
	/// Gated by ChaosMutoidConfig.EnableDebugLog - mirrors ChaosOverwatch's own Log.Debug
	/// helper so Player.log entries can be turned on from the in-game mod config without
	/// a rebuild.
	/// </summary>
	internal static class Log
	{
		public static void Debug(string msg)
		{
			if (ChaosMutoidMain.Cfg?.EnableDebugLog == true)
				UnityEngine.Debug.Log(msg);
		}
	}

	/// <summary>
	/// Tracks every Personal-track slot currently blanked out for re-picking (every
	/// already-decided level, not just the one clicked - see
	/// Patch_SpecializedAbilityTrackPopupElement_Init), bridging the popup-build patch
	/// and the purchase patch (which needs to remove the old ability once a new one is
	/// bought).
	/// </summary>
	internal static class MutoidRelearnState
	{
		internal struct PendingPick
		{
			internal AbilityTrackSlot Slot;

			internal TacticalAbilityDef OldAbility;
		}

		internal static readonly List<PendingPick> Pending = new List<PendingPick>();

		internal static CharacterProgression PendingProgression;

		/// <summary>
		/// Set only while a SpecializedAbilityTrackPopupElement.Init call is in progress
		/// for a non-Personal track (e.g. PrimaryClass) - see
		/// Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect for what consumes it.
		/// Cleared at the end of that same Init call's Postfix, independently of Pending/
		/// PendingProgression (which need to survive until BuyAbility's postfix runs).
		/// </summary>
		internal static AbilityTrack RedirectTrack;

		internal static void Clear()
		{
			Pending.Clear();
			PendingProgression = null;
			RedirectTrack = null;
		}

		private static readonly FieldInfo AbilitiesField =
			typeof(CharacterProgression).GetField("_abilities", BindingFlags.NonPublic | BindingFlags.Instance);

		/// <summary>
		/// CharacterProgression exposes AddAbility but no public way to remove a single
		/// already-learned ability - reflection into the private backing list is the
		/// only option (see ChaosMutoid/CLAUDE.md for why BuyAbility itself never needs
		/// to remove one in vanilla).
		/// </summary>
		internal static void RemoveAbility(CharacterProgression progression, TacticalAbilityDef ability)
		{
			if (progression == null || ability == null || AbilitiesField == null)
			{
				return;
			}
			if (AbilitiesField.GetValue(progression) is List<TacticalAbilityDef> abilities)
			{
				abilities.Remove(ability);
			}
		}
	}

	/// <summary>
	/// Lets the player click an already-learned Mutoid ability-track slot at all, on any
	/// of its three tracks: Personal (mutation), and - if the Mutoid was human before
	/// mutating - the retained PrimaryClass and SecondaryClass ("soldier") tracks.
	/// Vanilla's MutoidAbilityTrackContainerElement.SetAbilitySlot renders any slot with
	/// an assigned Ability as fixed/owned with isBuyable=false forever, so
	/// AbilityTrackSkillEntryElement.OnPointerClick's own "if (!IsBuyableSkill) return;"
	/// guard never lets the click reach the container's OnTrackSlotPointerClicked at
	/// all. Only that one bool needs to flip; the empty-slot branch (isLocked/
	/// isAvailable computed the normal way) is untouched.
	/// SpecializedAbilityTrackPopupElement.Init - the popup this eventually opens - has
	/// exactly one caller in the whole game, MutoidAbilityTrackContainerElement itself,
	/// so this is Mutoid-only regardless of source; no separate "is this a real human"
	/// gate is needed. PrimaryClass/SecondaryClass each need different handling once the
	/// popup opens - see Patch_SpecializedAbilityTrackPopupElement_Init.
	/// </summary>
	[HarmonyPatch(typeof(MutoidAbilityTrackContainerElement), "SetAbilitySlot")]
	internal static class Patch_MutoidAbilityTrackContainerElement_SetAbilitySlot
	{
		private static bool Prefix(AbilityTrackSource abilitySource, AbilityTrackSlot slot, AbilityTrackSkillEntryElement element)
		{
			if (slot.Ability == null)
			{
				return true;
			}
			Log.Debug($"[ChaosMutoid] SetAbilitySlot: making learned {abilitySource} slot (ability={slot.Ability.name}) clickable again.");
			element.SetSkill(abilitySource, slot, slot.Ability.ViewElementDef.SmallIcon, false, false, true, true);
			element.SetTooltip(new LocalizedTextBind("KEY_CHAOSMUTOID_RELEARN_TOOLTIP"));
			return false;
		}
	}

	/// <summary>
	/// The re-learn picker itself. SpecializedAbilityTrackPopupElement.Init always
	/// builds the *whole* ability-track grid (every specialization column, every level
	/// row) in one call, and locks out every candidate at a given level the instant that
	/// level's real AbilityTrackSlot has any Ability at all - the same lockout vanilla
	/// relies on to stop a slot being bought twice. Temporarily nulling out EVERY
	/// already-decided AbilityTrackSlot on the CLICKED slot's own track
	/// (`slot.AbilityTrack`, not a hardcoded track - restored again the moment Init
	/// returns, before the popup is even shown) makes every row's candidates evaluate
	/// exactly like a fresh, never-bought level - reusing vanilla's own selection/cost/
	/// affordability logic unmodified. Unlocking the whole track (not just the row the
	/// player actually clicked) lets one popup open pick a different ability for ANY
	/// level, instead of forcing a separate close-and-reclick-the-icon round trip per
	/// level.
	/// This popup has exactly one caller in the whole game -
	/// MutoidAbilityTrackContainerElement.OnTrackSlotPointerClicked, which passes
	/// isHuman = (source == AbilityTrackSource.SecondaryClass) - so despite the
	/// parameter's name, every call here is for a Mutoid, on one of its three tracks:
	/// isHuman=false covers Personal AND PrimaryClass, isHuman=true covers
	/// SecondaryClass. `Init` itself hardcodes `abilityTrack2 = progression.
	/// GetAbilityTrack(isHuman ? SecondaryClass : Personal)` for its own candidate-row
	/// lookup, completely ignoring `slot.AbilityTrack` - this is CORRECT for Personal and
	/// SecondaryClass (that's exactly what `isHuman` picks), but wrong for PrimaryClass
	/// (vanilla apparently never anticipated a Pandoran character with a real
	/// PrimaryClass track, only Personal + optionally SecondaryClass). When the clicked
	/// slot's track is PrimaryClass specifically, this sets `MutoidRelearnState.
	/// RedirectTrack` so `Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect` can
	/// correct that ONE call for the duration of this Init call, without touching Init's
	/// own logic. Confirmed via Player.log that vanilla's SecondSpecializationLevel skip
	/// (`LevelProgressionDef.SecondSpecializationLevel`, default 4 - `Init` removes
	/// index 3 from its level list to make room for the dual-class pick) is a single
	/// game-wide constant applied identically to every track, so this redirect doesn't
	/// risk misaligning PrimaryClass's own per-level structure against it.
	/// The already-decided-slot nulling only runs once the player has clicked an
	/// already-learned slot (slot.Ability != null) - a fresh, never-decided pick still
	/// gets the redirect (needed either way, for PrimaryClass) but has nothing to null.
	/// </summary>
	[HarmonyPatch(typeof(SpecializedAbilityTrackPopupElement), "Init")]
	internal static class Patch_SpecializedAbilityTrackPopupElement_Init
	{
		private static void Prefix(GeoCharacter character, AbilityTrackSlot slot, bool isHuman)
		{
			MutoidRelearnState.Clear();
			if (slot == null)
			{
				return;
			}
			AbilityTrack realTrack = slot.AbilityTrack;
			if (realTrack == null)
			{
				return;
			}
			MutoidRelearnState.PendingProgression = character.Progression;
			if (!isHuman && realTrack.Source != AbilityTrackSource.Personal)
			{
				Log.Debug($"[ChaosMutoid] SpecializedAbilityTrackPopupElement.Init: clicked slot belongs to the {realTrack.Source} track - redirecting vanilla's hardcoded GetAbilityTrack(Personal) lookup to this track for this Init call.");
				MutoidRelearnState.RedirectTrack = realTrack;
			}
			if (slot.Ability == null)
			{
				return;
			}
			foreach (AbilityTrackSlot trackSlot in realTrack.AbilitiesByLevel)
			{
				if (trackSlot?.Ability == null)
				{
					continue;
				}
				Log.Debug($"[ChaosMutoid] SpecializedAbilityTrackPopupElement.Init: temporarily nulling slot.Ability (was {trackSlot.Ability.name}) so this level is pickable in this popup session too.");
				MutoidRelearnState.Pending.Add(new MutoidRelearnState.PendingPick { Slot = trackSlot, OldAbility = trackSlot.Ability });
				trackSlot.Ability = null;
			}
		}

		private static void Postfix()
		{
			foreach (MutoidRelearnState.PendingPick pick in MutoidRelearnState.Pending)
			{
				Log.Debug($"[ChaosMutoid] SpecializedAbilityTrackPopupElement.Init: restoring slot.Ability={pick.OldAbility.name} now that the grid has been built. Note: this ability is still in CharacterProgression.Abilities, so vanilla's OnTrackSlotPointerClicked will silently ignore a click on this exact same ability (Abilities.Contains guard) until a different ability is bought and BuyAbility's postfix removes it.");
				pick.Slot.Ability = pick.OldAbility;
			}
			MutoidRelearnState.RedirectTrack = null;
		}
	}

	/// <summary>
	/// Surgical, narrowly-scoped redirect for the one vanilla bug described in
	/// Patch_SpecializedAbilityTrackPopupElement_Init: while MutoidRelearnState.
	/// RedirectTrack is set (only during the synchronous duration of the Init call it
	/// was set up for), a request for AbilityTrackSource.Personal on the matching
	/// CharacterProgression instance returns the clicked slot's real (non-Personal)
	/// track instead. Every other call - any other progression, any other source, or
	/// any call once RedirectTrack has been cleared - is completely unaffected; this is
	/// not a general-purpose GetAbilityTrack override.
	/// </summary>
	[HarmonyPatch(typeof(CharacterProgression), "GetAbilityTrack")]
	internal static class Patch_CharacterProgression_GetAbilityTrack_MutoidRedirect
	{
		private static bool Prefix(CharacterProgression __instance, AbilityTrackSource tracksource, ref AbilityTrack __result)
		{
			AbilityTrack redirect = MutoidRelearnState.RedirectTrack;
			if (redirect == null || tracksource != AbilityTrackSource.Personal || MutoidRelearnState.PendingProgression != __instance)
			{
				return true;
			}
			Log.Debug($"[ChaosMutoid] GetAbilityTrack: redirecting Personal lookup to the {redirect.Source} track for this popup Init call.");
			__result = redirect;
			return false;
		}
	}

	/// <summary>
	/// Vanilla's Pandoran/Mutoid purchase path (UIModuleCharacterProgression.BuyAbility)
	/// only ever adds the newly bought ability and overwrites the slot's Ability field -
	/// it never removes whatever ability the slot held before, since in vanilla a slot
	/// is only ever bought once. Left alone, a re-learn would leave the old ability
	/// permanently active (still in the character's learned Abilities list) alongside
	/// the new one. This postfix removes it - checking every level that was unlocked for
	/// this popup session (see Patch_SpecializedAbilityTrackPopupElement_Init), but only
	/// for whichever one's Ability actually changed, so cancelling the popup or buying
	/// a level that was already unfilled removes nothing.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleCharacterProgression), "BuyAbility")]
	internal static class Patch_UIModuleCharacterProgression_BuyAbility
	{
		private static void Postfix()
		{
			if (MutoidRelearnState.Pending.Count == 0)
			{
				return;
			}
			CharacterProgression progression = MutoidRelearnState.PendingProgression;
			foreach (MutoidRelearnState.PendingPick pick in MutoidRelearnState.Pending)
			{
				if (pick.Slot.Ability == pick.OldAbility)
				{
					continue;
				}
				Log.Debug($"[ChaosMutoid] BuyAbility: slot.Ability changed to {pick.Slot.Ability?.name ?? "null"} - removing old ability {pick.OldAbility.name} from CharacterProgression.");
				MutoidRelearnState.RemoveAbility(progression, pick.OldAbility);
			}
			MutoidRelearnState.Clear();
		}
	}

	/// <summary>
	/// Diagnostic-only, no behaviour change (prefix always returns true / runs original).
	/// UIModuleCharacterProgression.OnTrackSlotPointerClicked is the method that decides,
	/// after a candidate is clicked inside the popup, whether to actually fire
	/// AbilityBoughtConfirmation (which is what shows the purchase-confirmation dialog).
	/// It only proceeds past "else if (!this._character.Progression.Abilities.Contains
	/// (tacticalAbilityDef))" - i.e. it silently does nothing if the character already
	/// has that exact TacticalAbilityDef anywhere in CharacterProgression.Abilities.
	/// Because Patch_SpecializedAbilityTrackPopupElement_Init only nulls slot.Ability
	/// (restored immediately after the grid is built) and the old ability is only removed
	/// from CharacterProgression AFTER a successful purchase (see BuyAbility's postfix
	/// above), re-clicking the exact same ability the slot already holds hits this guard
	/// and produces exactly the reported "click does nothing" symptom. Logs the decision
	/// inputs so this can be confirmed from Player.log without guessing.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleCharacterProgression), "OnTrackSlotPointerClicked")]
	internal static class Patch_UIModuleCharacterProgression_OnTrackSlotPointerClicked_Diagnostics
	{
		private static readonly FieldInfo HasPandoranProgressionField =
			typeof(UIModuleCharacterProgression).GetField("_hasPandoranProgression", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo CurrentMutagensField =
			typeof(UIModuleCharacterProgression).GetField("_currentMutagens", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo CharacterField =
			typeof(UIModuleCharacterProgression).GetField("_character", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Prefix(UIModuleCharacterProgression __instance, AbilityTrackSkillEntryElement button, AbilityTrackSlot slot, bool isDualClassSlot)
		{
			if (ChaosMutoidMain.Cfg?.EnableDebugLog != true)
			{
				return;
			}
			bool hasPandoranProgression = HasPandoranProgressionField != null && (bool)HasPandoranProgressionField.GetValue(__instance);
			if (!hasPandoranProgression)
			{
				return;
			}
			TacticalAbilityDef clicked = button.AbilityDef;
			GeoCharacter character = CharacterField?.GetValue(__instance) as GeoCharacter;
			bool alreadyKnown = clicked != null && character != null && character.Progression.Abilities.Contains(clicked);
			int currentMutagens = CurrentMutagensField != null ? (int)CurrentMutagensField.GetValue(__instance) : -1;
			int cost = clicked?.CharacterProgressionData?.MutagenCost ?? -1;
			Log.Debug($"[ChaosMutoid] OnTrackSlotPointerClicked: clicked={clicked?.name ?? "null"} level={button.AbilityLevel} isBuyable={button.IsBuyableSkill} isLearnable={button.LearnableSkill} alreadyInAbilities={alreadyKnown} mutagens={currentMutagens} cost={cost} charLevel={character?.Progression.LevelProgression.Level}");
			if (alreadyKnown)
			{
				Log.Debug("[ChaosMutoid] OnTrackSlotPointerClicked: vanilla will do NOTHING for this click - the clicked ability is still in CharacterProgression.Abilities (Abilities.Contains guard), most likely because it's the same ability this slot already held and it hasn't been removed yet (that only happens after a successful purchase of a DIFFERENT ability). Pick a different ability to re-learn this slot.");
			}
		}
	}

	/// <summary>
	/// Gated independently by ChaosMutoidConfig.EnableMutoidBodyPartMutation (default true, for
	/// the Mutation/Anu button) and EnableMutoidBodyPartAugmentation (default false, for the
	/// Bionics/New Jericho button). Vanilla hides both circular buttons on the roster/Edit
	/// Soldier screen entirely for a Mutoid character - EditUnitButtonsController.
	/// SetContextButtonVisibility ANDs both button visibilities with "!_isCurrentActorMutoid".
	/// This is Gate A of two independent gates (see Patch_GeoscapeView_ToMutateState/
	/// ToBionicsState for Gate B) - both need to be bypassed for a given button, since even with
	/// the button visible, the matching state-transition method would still refuse to open for a
	/// Mutoid-only selection.
	/// Reimplements the method's own logic via reflection into its private members rather than a
	/// transpiler, since the fix is a single boolean conjunct dropped (per button, independently)
	/// from two straightforward method calls - SetEditUnitButtonsBasedOnType() is still invoked
	/// for real afterwards (not reimplemented), so its own per-UI-state overrides (e.g. always
	/// hiding these buttons while browsing the roster list, not just when Mutoid) are preserved
	/// unchanged. Only intercepts (skips original) when at least one of the two flags is on; a
	/// human character's buttons are computed identically to vanilla either way, since
	/// "!isCurrentActorMutoid" is already true for them regardless of the config bypass.
	/// </summary>
	[HarmonyPatch(typeof(EditUnitButtonsController), "SetContextButtonVisibility")]
	internal static class Patch_EditUnitButtonsController_SetContextButtonVisibility
	{
		private static readonly FieldInfo IsAugmentationOnField =
			typeof(EditUnitButtonsController).GetField("_isAugmentationOn", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo BionicsAvailableField =
			typeof(EditUnitButtonsController).GetField("_bionicsAvailable", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo MutationAvailableField =
			typeof(EditUnitButtonsController).GetField("_mutationAvailable", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly PropertyInfo IsCurrentActorMutoidProperty =
			typeof(EditUnitButtonsController).GetProperty("_isCurrentActorMutoid", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly MethodInfo SetCircularButtonVisibilityMethod =
			typeof(EditUnitButtonsController).GetMethod("SetCircularButtonVisibility", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly MethodInfo SetEditUnitButtonsBasedOnTypeMethod =
			typeof(EditUnitButtonsController).GetMethod("SetEditUnitButtonsBasedOnType", BindingFlags.Public | BindingFlags.Instance);

		private static bool Prefix(EditUnitButtonsController __instance, bool isAugmentationModeOn)
		{
			bool bypassMutation = ChaosMutoidMain.Cfg?.EnableMutoidBodyPartMutation == true;
			bool bypassBionics = ChaosMutoidMain.Cfg?.EnableMutoidBodyPartAugmentation == true;
			if (!bypassMutation && !bypassBionics)
			{
				return true;
			}
			if (IsAugmentationOnField == null || BionicsAvailableField == null || MutationAvailableField == null
				|| IsCurrentActorMutoidProperty == null || SetCircularButtonVisibilityMethod == null || SetEditUnitButtonsBasedOnTypeMethod == null)
			{
				return true;
			}
			IsAugmentationOnField.SetValue(__instance, isAugmentationModeOn);
			bool bionicsAvailable = (bool)BionicsAvailableField.GetValue(__instance);
			bool mutationAvailable = (bool)MutationAvailableField.GetValue(__instance);
			bool isCurrentActorMutoid = (bool)IsCurrentActorMutoidProperty.GetValue(__instance);
			bool bionicsVisible = isAugmentationModeOn && bionicsAvailable && (!isCurrentActorMutoid || bypassBionics);
			bool mutationVisible = isAugmentationModeOn && mutationAvailable && (!isCurrentActorMutoid || bypassMutation);
			if (isCurrentActorMutoid)
			{
				Log.Debug($"[ChaosMutoid] SetContextButtonVisibility: Mutoid character - mutationVisible={mutationVisible} (EnableMutoidBodyPartMutation={bypassMutation}) bionicsVisible={bionicsVisible} (EnableMutoidBodyPartAugmentation={bypassBionics}).");
			}
			SetCircularButtonVisibilityMethod.Invoke(__instance, new object[] { __instance.BionicsButton, bionicsVisible });
			SetCircularButtonVisibilityMethod.Invoke(__instance, new object[] { __instance.MutationButton, mutationVisible });
			SetEditUnitButtonsBasedOnTypeMethod.Invoke(__instance, null);
			return false;
		}
	}

	/// <summary>
	/// Gate B (see Patch_EditUnitButtonsController_SetContextButtonVisibility for Gate A):
	/// GeoscapeView.ToMutateState/ToBionicsState filter the character list down to
	/// "CheckIsHuman() &amp;&amp; !CheckIsMutoid()" before opening the Mutation/Bionics screen - for
	/// a Mutoid-only selection the filtered list is empty and the method just silently returns,
	/// so even with Gate A's button visible, clicking it would do nothing.
	/// Reimplements both methods via a Prefix that skips the original (return false) - identical
	/// to vanilla except the "&amp;&amp; !CheckIsMutoid()" conjunct is dropped, so Mutoids pass the
	/// same "CheckIsHuman()" filter regular soldiers do (Mutoids are still human-tagged
	/// underneath - TacCharacterDef.CheckIsHuman() is true for them, only CheckIsMutoid() singles
	/// them out). _statesStack is private with no public accessor, so it's read once via
	/// reflection and then used normally (StateStack&lt;GeoscapeViewContext&gt; and its
	/// SwitchToState/CurrentState are both public) - everything else is a straight copy of
	/// vanilla's own control flow, not a new implementation.
	/// </summary>
	[HarmonyPatch(typeof(GeoscapeView), "ToMutateState")]
	internal static class Patch_GeoscapeView_ToMutateState
	{
		private static bool Prefix(GeoscapeView __instance, GeoCharacter initCharacter, IEnumerable<GeoCharacter> characters, StateStackAction action)
		{
			if (ChaosMutoidMain.Cfg?.EnableMutoidBodyPartMutation != true)
			{
				return true;
			}
			StateStack<GeoscapeViewContext> statesStack = ChaosMutoidGeoscapeViewUtl.GetStatesStack(__instance);
			if (statesStack == null)
			{
				return true;
			}
			if (initCharacter != null && !initCharacter.TemplateDef.CheckIsHuman())
			{
				return false;
			}
			IEnumerable<GeoCharacter> allowedCharacters = (characters == null)
				? __instance.GetFactionCharacters((GeoCharacter c) => c.TemplateDef.CheckIsHuman())
				: characters.Where((GeoCharacter c) => c.TemplateDef.CheckIsHuman());
			allowedCharacters = allowedCharacters.ToList();
			if (!allowedCharacters.Any())
			{
				return false;
			}
			Log.Debug($"[ChaosMutoid] ToMutateState: opening Mutation screen including Mutoid character(s) ({allowedCharacters.Count()} eligible).");
			if (statesStack.CurrentState is UIStateGeoRoster || statesStack.CurrentState is UIStateRosterDeployment || statesStack.CurrentState is UIStateEditSoldier)
			{
				statesStack.SwitchToState(new UIStateMutate(allowedCharacters, initCharacter), action);
				return false;
			}
			statesStack.SwitchToState(new UIStateMutate(allowedCharacters, initCharacter), StateStackAction.ReplaceTop);
			return false;
		}
	}

	[HarmonyPatch(typeof(GeoscapeView), "ToBionicsState")]
	internal static class Patch_GeoscapeView_ToBionicsState
	{
		private static bool Prefix(GeoscapeView __instance, GeoCharacter initCharacter, IEnumerable<GeoCharacter> characters, bool preselectRepair, StateStackAction action)
		{
			if (ChaosMutoidMain.Cfg?.EnableMutoidBodyPartAugmentation != true)
			{
				return true;
			}
			StateStack<GeoscapeViewContext> statesStack = ChaosMutoidGeoscapeViewUtl.GetStatesStack(__instance);
			if (statesStack == null)
			{
				return true;
			}
			if (initCharacter != null && !initCharacter.TemplateDef.CheckIsHuman())
			{
				return false;
			}
			IEnumerable<GeoCharacter> allowedCharacters = (characters == null)
				? __instance.GetFactionCharacters((GeoCharacter c) => c.TemplateDef.CheckIsHuman())
				: characters.Where((GeoCharacter c) => c.TemplateDef.CheckIsHuman());
			allowedCharacters = allowedCharacters.ToList();
			if (!allowedCharacters.Any())
			{
				return false;
			}
			Log.Debug($"[ChaosMutoid] ToBionicsState: opening Bionics screen including Mutoid character(s) ({allowedCharacters.Count()} eligible).");
			if (statesStack.CurrentState is UIStateGeoRoster || statesStack.CurrentState is UIStateRosterDeployment || statesStack.CurrentState is UIStateEditSoldier)
			{
				statesStack.SwitchToState(new UIStateBionics(allowedCharacters, initCharacter, preselectRepair), action);
				return false;
			}
			statesStack.SwitchToState(new UIStateBionics(allowedCharacters, initCharacter, preselectRepair), StateStackAction.ReplaceTop);
			return false;
		}
	}

	/// <summary>
	/// GeoscapeView._statesStack is private with no public accessor (only a typed CurrentViewState
	/// wrapper that loses the SwitchToState API the Mutate/Bionics patches need) - shared reflection
	/// lookup for both state-transition patches above.
	/// </summary>
	internal static class ChaosMutoidGeoscapeViewUtl
	{
		private static readonly FieldInfo StatesStackField =
			typeof(GeoscapeView).GetField("_statesStack", BindingFlags.NonPublic | BindingFlags.Instance);

		internal static StateStack<GeoscapeViewContext> GetStatesStack(GeoscapeView view)
		{
			return StatesStackField?.GetValue(view) as StateStack<GeoscapeViewContext>;
		}
	}

	/// <summary>
	/// Bypasses class-restricted Mutation/Bionics candidates (e.g. Priest-only heads) for Mutoid
	/// characters. UIModuleMutationSection.CanApplyAugumentation(ItemDef) - shared by both the
	/// Mutation and Bionics screens (both drive the same UIModuleMutationSection component per
	/// body-part row) - ANDs two independent checks: the faction's UnlockedAugmentations research
	/// gate, and augumentation.UnlockRequirements.IsRequirementMet(context). Every class-locked
	/// candidate (head/torso/bionic) uses a ClassRequirementDef for that second check
	/// ("RequiredClass" - ItemDef.UnlockRequirements' own dev tooltip literally says "This is
	/// really Equip Requirement for priest heads and similar"), which resolves the currently
	/// viewed GeoCharacter's GameTags and checks for the required class tag - Mutoids have no
	/// class/specialization tag at all, so every class-gated candidate fails this check
	/// regardless of which class it requires, which is exactly the "3 of 6 heads locked" the user
	/// saw. ClassRequirementDef.IsRequirementMet has no other caller anywhere in the game (single
	/// choke point, confirmed via search), so patching it here only affects this one mechanism -
	/// normal weapon/armor equip-slot proficiency checks are a completely different, tag-based
	/// system (CommonCharacterUtils.IsActorProficientWithItem) untouched by this patch.
	/// Bypassed for Mutoid characters only, gated on EITHER EnableMutoidBodyPartMutation or
	/// EnableMutoidBodyPartAugmentation being on - by the time a Mutoid reaches this check at all,
	/// it already got past Gate A/B for whichever one of the two screens it's actually in, so
	/// checking "either" here is sufficient without needing to know which screen invoked it.
	/// Regular human soldiers are completely unaffected - they still can't pick a class's
	/// restricted parts without that class, same as vanilla.
	/// </summary>
	[HarmonyPatch(typeof(ClassRequirementDef), "IsRequirementMet")]
	internal static class Patch_ClassRequirementDef_IsRequirementMet
	{
		private static bool Prefix(BaseEventContext context, ref bool __result)
		{
			ChaosMutoidConfig cfg = ChaosMutoidMain.Cfg;
			if (cfg == null || (!cfg.EnableMutoidBodyPartMutation && !cfg.EnableMutoidBodyPartAugmentation))
			{
				return true;
			}
			if (!(context is ICommonActorProvider provider))
			{
				return true;
			}
			ICommonActor actor = provider.GetCommonActor();
			if (!(actor is GeoCharacter character) || !character.IsMutoid)
			{
				return true;
			}
			Log.Debug($"[ChaosMutoid] ClassRequirementDef.IsRequirementMet: bypassing class requirement for Mutoid character {character.DisplayName}.");
			__result = true;
			return false;
		}
	}

	/// <summary>
	/// Gated by ChaosMutoidConfig.EnableReplacementForMutationAndAugmentation (default false).
	/// Vanilla only lets a body part (Head/Torso/Legs) be replaced with another item of the SAME
	/// augment category - a Mutation-occupied slot can only be swapped for another Mutation, a
	/// Bionic-occupied slot only for another Bionic, never across categories. Confirmed via
	/// source this is a hard structural one-slot-per-body-part design (Mutation and
	/// Bionic ItemDefs for a given body part both bind to the exact same AddonSlotDef, and the
	/// runtime attachment object only ever holds one occupant), reinforced by ONE narrow, genuine
	/// UI-level rule on top: UIModuleMutate/UIModuleBionics.InitCharacterInfo lock the section
	/// entirely (AugumentSlotState.BlockedByPermenantAugument) whenever the current occupant is
	/// tagged with the OTHER category's GameTag. That rule is what these two patches bypass -
	/// this only re-enables the normal swap/eviction flow (the same one used for same-category
	/// replacements), it can never result in a body part holding both at once, since the
	/// structural one-slot design underneath is untouched.
	/// Implemented as a Postfix (not a Prefix reimplementation) because InitCharacterInfo also
	/// does bookkeeping unrelated to this one rule (the 2-augment global cap display, marking
	/// which candidate is "currently applied" via a second pass over ArmourItems). Recomputing
	/// just the affected sections' correct state afterwards and calling the same public
	/// ResetContainer(...) the original already uses is safe specifically because a section
	/// blocked by the OTHER category's occupant is - by construction, since an item can't carry
	/// both categories' tags - never one of the sections the original's own second pass (which
	/// only touches same-category occupied sections) already set MutationUsed on, so this
	/// doesn't clobber anything the original just finished setting up correctly.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleMutate), "InitCharacterInfo")]
	internal static class Patch_UIModuleMutate_InitCharacterInfo_AllowReplacement
	{
		private static readonly FieldInfo AugmentSectionsField =
			typeof(UIModuleMutate).GetField("_augmentSections", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo BionicsTagField =
			typeof(UIModuleMutate).GetField("_bionicsTag", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Postfix(UIModuleMutate __instance)
		{
			if (ChaosMutoidMain.Cfg?.EnableReplacementForMutationAndAugmentation != true)
			{
				return;
			}
			if (AugmentSectionsField == null || BionicsTagField == null)
			{
				return;
			}
			GeoCharacter character = __instance.CurrentCharacter;
			GameTagDef bionicsTag = BionicsTagField.GetValue(__instance) as GameTagDef;
			if (character == null || bionicsTag == null
				|| !(AugmentSectionsField.GetValue(__instance) is Dictionary<AddonSlotDef, UIModuleMutationSection> sections))
			{
				return;
			}
			bool underLimit = ChaosMutoidMain.Cfg?.EnableFullAugmentationOrMutation == true
				|| AugmentScreenUtilities.GetNumberOfAugments(character) < 2;
			foreach (KeyValuePair<AddonSlotDef, UIModuleMutationSection> section in sections)
			{
				ItemDef augmentAtSlot = AugmentScreenUtilities.GetAugmentAtSlot(character, section.Key);
				if (augmentAtSlot == null || !augmentAtSlot.Tags.Contains(bionicsTag))
				{
					continue;
				}
				AugumentSlotState state = underLimit ? AugumentSlotState.Available : AugumentSlotState.AugumentationLimitReached;
				string text = underLimit ? null : __instance.LockedDueToLimitKey.LocalizationKey;
				Log.Debug($"[ChaosMutoid] UIModuleMutate.InitCharacterInfo: allowing replacement of Bionic-occupied slot ({augmentAtSlot.name}) with a Mutation - state={state}.");
				section.Value.ResetContainer(state, text);
			}
		}
	}

	/// <summary>
	/// Gated by ChaosMutoidConfig.EnableFullAugmentationOrMutation (default false). Vanilla's
	/// InitCharacterInfo (both UIModuleMutate and UIModuleBionics - see MAX_AUGMENTATIONS = 2 in
	/// each) locks any EMPTY body-part section with AugumentationLimitReached once the character
	/// already has 2 augmented body parts, regardless of which 2 of the 3 (Head/Torso/Legs) those
	/// are - so the 3rd body part can never be touched. Same-category-occupied sections are never
	/// affected by this cap in vanilla (re-picking an already-mutated/augmented slot doesn't raise
	/// the count), and cross-category-occupied sections are a separate concern already handled by
	/// the AllowReplacement patches above (whose own cap check now also respects this flag). This
	/// patch only targets genuinely empty sections that vanilla's own loop marked as limit-reached.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleMutate), "InitCharacterInfo")]
	internal static class Patch_UIModuleMutate_InitCharacterInfo_FullAugmentation
	{
		private static readonly FieldInfo AugmentSectionsField =
			typeof(UIModuleMutate).GetField("_augmentSections", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Postfix(UIModuleMutate __instance)
		{
			if (ChaosMutoidMain.Cfg?.EnableFullAugmentationOrMutation != true)
			{
				return;
			}
			if (AugmentSectionsField == null)
			{
				return;
			}
			GeoCharacter character = __instance.CurrentCharacter;
			if (character == null
				|| !(AugmentSectionsField.GetValue(__instance) is Dictionary<AddonSlotDef, UIModuleMutationSection> sections))
			{
				return;
			}
			foreach (KeyValuePair<AddonSlotDef, UIModuleMutationSection> section in sections)
			{
				if (AugmentScreenUtilities.GetAugmentAtSlot(character, section.Key) != null)
				{
					continue;
				}
				Log.Debug($"[ChaosMutoid] UIModuleMutate.InitCharacterInfo: lifting 2-augment cap for empty slot ({section.Key.name}).");
				section.Value.ResetContainer(AugumentSlotState.Available, null);
			}
		}
	}

	[HarmonyPatch(typeof(UIModuleBionics), "InitCharacterInfo")]
	internal static class Patch_UIModuleBionics_InitCharacterInfo_AllowReplacement
	{
		private static readonly FieldInfo AugmentSectionsField =
			typeof(UIModuleBionics).GetField("_augmentSections", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo MutationTagField =
			typeof(UIModuleBionics).GetField("_mutationTag", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Postfix(UIModuleBionics __instance)
		{
			if (ChaosMutoidMain.Cfg?.EnableReplacementForMutationAndAugmentation != true)
			{
				return;
			}
			if (AugmentSectionsField == null || MutationTagField == null)
			{
				return;
			}
			GeoCharacter character = __instance.CurrentCharacter;
			GameTagDef mutationTag = MutationTagField.GetValue(__instance) as GameTagDef;
			if (character == null || mutationTag == null
				|| !(AugmentSectionsField.GetValue(__instance) is Dictionary<AddonSlotDef, UIModuleMutationSection> sections))
			{
				return;
			}
			bool underLimit = ChaosMutoidMain.Cfg?.EnableFullAugmentationOrMutation == true
				|| AugmentScreenUtilities.GetNumberOfAugments(character) < 2;
			foreach (KeyValuePair<AddonSlotDef, UIModuleMutationSection> section in sections)
			{
				ItemDef augmentAtSlot = AugmentScreenUtilities.GetAugmentAtSlot(character, section.Key);
				if (augmentAtSlot == null || !augmentAtSlot.Tags.Contains(mutationTag))
				{
					continue;
				}
				AugumentSlotState state = underLimit ? AugumentSlotState.Available : AugumentSlotState.AugumentationLimitReached;
				string text = underLimit ? null : __instance.LockedDueToLimitKey.LocalizationKey;
				Log.Debug($"[ChaosMutoid] UIModuleBionics.InitCharacterInfo: allowing replacement of Mutation-occupied slot ({augmentAtSlot.name}) with a Bionic - state={state}.");
				section.Value.ResetContainer(state, text);
			}
		}
	}

	/// <summary>
	/// Mirror of Patch_UIModuleMutate_InitCharacterInfo_FullAugmentation for the Bionics screen -
	/// see that patch's doc comment for the full rationale.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleBionics), "InitCharacterInfo")]
	internal static class Patch_UIModuleBionics_InitCharacterInfo_FullAugmentation
	{
		private static readonly FieldInfo AugmentSectionsField =
			typeof(UIModuleBionics).GetField("_augmentSections", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void Postfix(UIModuleBionics __instance)
		{
			if (ChaosMutoidMain.Cfg?.EnableFullAugmentationOrMutation != true)
			{
				return;
			}
			if (AugmentSectionsField == null)
			{
				return;
			}
			GeoCharacter character = __instance.CurrentCharacter;
			if (character == null
				|| !(AugmentSectionsField.GetValue(__instance) is Dictionary<AddonSlotDef, UIModuleMutationSection> sections))
			{
				return;
			}
			foreach (KeyValuePair<AddonSlotDef, UIModuleMutationSection> section in sections)
			{
				if (AugmentScreenUtilities.GetAugmentAtSlot(character, section.Key) != null)
				{
					continue;
				}
				Log.Debug($"[ChaosMutoid] UIModuleBionics.InitCharacterInfo: lifting 2-augment cap for empty slot ({section.Key.name}).");
				section.Value.ResetContainer(AugumentSlotState.Available, null);
			}
		}
	}
}
