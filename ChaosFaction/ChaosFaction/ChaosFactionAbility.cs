using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Defs;
using Base.UI;
using Base.UI.MessageBox;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.UI;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace ChaosFaction
{
	/// <summary>
	/// Def for AskForHelpAbility. Built purely in code via ScriptableObject.CreateInstance
	/// (no Unity Editor asset) — see AskForHelpAbilityFactory.
	/// </summary>
	internal class AskForHelpAbilityDef : GeoAbilityDef
	{
		public override GeoAbilityTargetType TargetType => GeoAbilityTargetType.Self;
	}

	/// <summary>
	/// How the player pays for a help request: Resource (whichever one this faction's
	/// current FactionHelpOffer rolled — Supplies/Materials/Tech, not always Materials
	/// anymore), deducted from Wallet, or Relationship, deducted from HelperFaction's
	/// diplomacy toward PlayerFaction (not a ResourceType, so no native Wallet/Cost
	/// support — handled by hand). Chosen by the player from a MessageBox popup shown by
	/// AskForHelpAbility.ActivateInternal, not baked into the ability/Def itself — see the
	/// type's own doc comment for why.
	/// </summary>
	internal enum AskForHelpPaymentMethod
	{
		Resource,
		Relationship,
	}

	/// <summary>
	/// The "ask a faction for help in this fight" action, shown in the site's left-click
	/// context menu (alongside "Move"/"Enter Base"/"Assault"/etc.) whenever the site has
	/// an active mission ContextualAbilitiesPatchHelper recognizes as eligible — a Phoenix
	/// base under attack, or a Pandoran lair/nest the player is about to assault. One
	/// instance exists per (mission, helper faction) pair — up to three simultaneously
	/// (New Jericho/Anu/Synedrion, see ContextualAbilitiesPatchHelper), each offering
	/// either payment method rather than each payment method getting its own menu entry:
	/// UIModuleSiteContextualMenu.SetMenuItems indexes into a fixed-size, pre-instantiated
	/// button pool with no bounds check (`this._menuItems[i]` for `i &lt; abilities.Count`)
	/// — six entries (3 factions × 2 payment methods, an earlier design) risked overflowing
	/// that pool and crashing the menu outright, not just looking cluttered; three is far
	/// safer. Clicking this ability shows a payment-method choice popup instead of costing
	/// anything itself — see ActivateInternal.
	/// </summary>
	internal class AskForHelpAbility : GeoAbility
	{
		public GeoMission Mission;
		public GeoFaction PlayerFaction;
		public GeoFaction HelperFaction;

		/// <summary>
		/// This faction's current FactionHelpOffer, copied onto the ability so
		/// ActivateInternal/OnPaymentChoice use exactly what was last displayed to the
		/// player (AskForHelpAbilityFactory.GetOrCreate refreshes this — and the menu
		/// text — every time the context menu opens, since the underlying offer can
		/// change between opens via FactionHelpOfferTracker's daily/after-use rerolls).
		/// </summary>
		public ResourceType ResourceType;
		public int ResourceAmount;
		public int RelationshipAmount;

		protected override GeoAbilityDisabledState GetDisabledStateInternal()
		{
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null)
				return GeoAbilityDisabledState.InvalidAbilityActor;
			if (!AllyRequestTracker.CanRequestHelp(Mission, PlayerFaction, HelperFaction, cfg))
				return GeoAbilityDisabledState.RequirementsNotMet;
			return GeoAbilityDisabledState.NotDisabled;
		}

		/// <summary>
		/// Doesn't pay anything itself (GeoscapeAbilityDef.Cost is always an empty
		/// ResourcePack — see AskForHelpAbilityFactory) — instead shows a MessageBox asking
		/// the player which way they'd rather pay, and only deducts/marks the mission as
		/// requested once they've actually picked one (OnPaymentChoice). Cancelling the
		/// popup leaves everything untouched, same as never having clicked the ability.
		/// </summary>
		protected override void ActivateInternal(GeoAbilityTarget target)
		{
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			MessageBox messageBox = GameUtl.GetMessageBox();
			if (cfg == null || messageBox == null)
				return;

			// HelperFaction.Name (the live faction's own localized display name, e.g.
			// "New Jericho") rather than PPFactionDef.GetName() (the raw internal Def
			// identifier, e.g. "NewJericho") — explicit request: reuse the game's own
			// localization instead of an internal name/hardcoded literal wherever possible.
			string helperName = HelperFaction.Name.Localize(null);
			Dictionary<MessageBoxButtons, string> labels = new Dictionary<MessageBoxButtons, string>
			{
				{ MessageBoxButtons.Yes, ChaosFactionLocalization.Get("KEY_CHAOSFACTION_PAY_RESOURCE_LABEL", ResourceAmount, ResourceType) },
				{ MessageBoxButtons.No, ChaosFactionLocalization.Get("KEY_CHAOSFACTION_PAY_RELATIONSHIP_LABEL", RelationshipAmount) },
			};
			messageBox.ShowSimplePrompt(
				ChaosFactionLocalization.Get("KEY_CHAOSFACTION_PAYMENT_PROMPT", helperName),
				MessageBoxIcon.Question,
				MessageBoxButtons.YesNoCancel,
				labels,
				OnPaymentChoice,
				this,
				null);
		}

		private void OnPaymentChoice(MessageBoxCallbackResult result)
		{
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null)
				return;

			FactionHelpOffer offer = new FactionHelpOffer
			{
				ResourceType = ResourceType,
				ResourceAmount = ResourceAmount,
				RelationshipAmount = RelationshipAmount,
			};
			if (result.DialogResult == MessageBoxResult.Yes)
			{
				ResourcePack cost = new ResourcePack(new ResourceUnit(ResourceType, ResourceAmount));
				if (!PlayerFaction.Wallet.HasResources(cost))
				{
					GameUtl.GetMessageBox()?.ShowSimplePrompt(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_NOT_ENOUGH_RESOURCE", ResourceType, ResourceAmount), MessageBoxIcon.Error, MessageBoxButtons.OK, null, null, null);
					return;
				}
				PlayerFaction.Wallet.Take(cost, OperationReason.Purchase);
				AllyRequestTracker.MarkRequested(Mission, PlayerFaction, HelperFaction, cfg, AskForHelpPaymentMethod.Resource, offer);
			}
			else if (result.DialogResult == MessageBoxResult.No)
			{
				HelperFaction.Diplomacy.ModifyDiplomacy(PlayerFaction, -RelationshipAmount);
				AllyRequestTracker.MarkRequested(Mission, PlayerFaction, HelperFaction, cfg, AskForHelpPaymentMethod.Relationship, offer);
			}
			// MessageBoxResult.Cancel (or anything else): player backed out, nothing to do.
		}
	}

	/// <summary>
	/// Status-only placeholder shown instead of the per-faction AskForHelpAbility entries
	/// once a mission's one-favor-per-mission slot is already used — see
	/// ContextualAbilitiesPatchHelper.TryAddAskForHelpAbility, which adds exactly one of
	/// these (never alongside the normal per-faction abilities) once
	/// AllyRequestTracker.ActiveRequests already contains the site. Always disabled;
	/// its DisplayName1 ("Already Requested Help (X)") carries the message, with the
	/// remaining-time countdown in its Description tooltip. Patch_SiteContextualMenuItem_Color (ChaosFactionPatches.cs)
	/// recolors its menu button text light blue instead of the native disabled grey, since
	/// this isn't really "disabled/unavailable", it's informational.
	/// </summary>
	internal class AlreadyRequestedHelpAbility : GeoAbility
	{
		/// <summary>
		/// Which tint Patch_UIModuleSiteContextualMenu_SetMenuItems (ChaosFactionPatches.cs)
		/// applies to this entry's menu text — light blue for "already requested help"
		/// (friendly, informational), red for "debug activity already added" (hostile,
		/// informational) — set by whichever factory built this instance
		/// (AskForHelpAbilityFactory/DebugActivityAbilityFactory), defaulting to the
		/// original light-blue so existing call sites don't need to change.
		/// </summary>
		public Color StatusColor = ChaosFactionColors.LightBlue;

		protected override GeoAbilityDisabledState GetDisabledStateInternal()
		{
			return GeoAbilityDisabledState.RequirementsNotMet;
		}

		protected override void ActivateInternal(GeoAbilityTarget target)
		{
		}
	}

	/// <summary>
	/// Builds (and caches, one per active mission *per helper faction*) the
	/// AskForHelpAbility + its Def/ViewElementDef. Defs are ScriptableObjects — created
	/// once per mission/faction pair and destroyed when the mission ends (launched or
	/// cancelled) to avoid leaking native Unity objects across repeated context-menu opens.
	/// </summary>
	internal static class AskForHelpAbilityFactory
	{
		private static readonly Dictionary<(GeoMission, GeoFaction), AskForHelpAbility> Cache = new Dictionary<(GeoMission, GeoFaction), AskForHelpAbility>();
		private static readonly Dictionary<GeoMission, AlreadyRequestedHelpAbility> AlreadyRequestedCache = new Dictionary<GeoMission, AlreadyRequestedHelpAbility>();

		/// <summary>
		/// One per mission (not per faction — there's only ever one "already requested"
		/// entry shown at a time). helperFaction here is whichever faction actually agreed
		/// to help (AllyRequestTracker.ActiveRequests), purely for the display text.
		/// remainingHours (the "24 hours which should decrease" the player asked for) is
		/// refreshed into the tooltip text on *every* call, cache hit or not — mirrors
		/// GetOrCreate's own "never let cached text go stale" pattern below, since this
		/// value visibly ticks down and a reopened menu should always show the current one.
		/// </summary>
		public static AlreadyRequestedHelpAbility GetOrCreateAlreadyRequested(GeoMission mission, GeoFaction helperFaction, double remainingHours)
		{
			string helperName = helperFaction.Name.Localize(null);
			string timeText = ChaosFactionLocalization.Get("KEY_CHAOSFACTION_HOURS_REMAINING", $"{Mathf.Max(0f, (float)remainingHours):0.0}");

			if (AlreadyRequestedCache.TryGetValue(mission, out AlreadyRequestedHelpAbility existing))
			{
				if (existing.GeoscapeAbilityDef is AskForHelpAbilityDef existingDef && existingDef.ViewElementDef != null)
					existingDef.ViewElementDef.Description = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ALREADY_HELPING_DESC", helperName, timeText), true);
				return existing;
			}

			GeoscapeAbilityViewElementDef viewElementDef = ScriptableObject.CreateInstance<GeoscapeAbilityViewElementDef>();
			viewElementDef.name = "ChaosFaction_AlreadyRequestedHelpViewElementDef";
			viewElementDef.DisplayName1 = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ALREADY_REQUESTED_TITLE", helperName), true);
			viewElementDef.Description = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ALREADY_HELPING_DESC", helperName, timeText), true);
			viewElementDef.DisplayPriority = 0;
			viewElementDef.ShowContextMenuTooltip = true;
			viewElementDef.ContextMenuShowOnlyIfViewFaction = true;
			viewElementDef.ContextMenuVisibility = NewVisibilityCondition();
			viewElementDef.ActionBarVisibility = NewVisibilityCondition();
			viewElementDef.CallToActionButtonVisibility = NewVisibilityCondition();

			AskForHelpAbilityDef abilityDef = ScriptableObject.CreateInstance<AskForHelpAbilityDef>();
			abilityDef.name = "ChaosFaction_AlreadyRequestedHelpAbilityDef";
			abilityDef.ViewElementDef = viewElementDef;
			abilityDef.Cost = new ResourcePack();

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			AlreadyRequestedHelpAbility ability = new AlreadyRequestedHelpAbility();
			ability.Init(abilityDef, repo);
			ability.SetActor(mission.Site);

			AlreadyRequestedCache[mission] = ability;
			mission.OnMissionActivated += (m, result) => CleanupAlreadyRequested(m);
			mission.OnMissionCancel += CleanupAlreadyRequested;

			return ability;
		}

		private static void CleanupAlreadyRequested(GeoMission mission)
		{
			if (!AlreadyRequestedCache.TryGetValue(mission, out AlreadyRequestedHelpAbility ability))
				return;
			AlreadyRequestedCache.Remove(mission);
			try
			{
				AskForHelpAbilityDef def = ability.GeoscapeAbilityDef as AskForHelpAbilityDef;
				if (def != null)
				{
					if (def.ViewElementDef != null)
						UnityEngine.Object.Destroy(def.ViewElementDef);
					UnityEngine.Object.Destroy(def);
				}
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to clean up Already Requested Help ability defs: " + ex);
			}
		}

		/// <summary>
		/// offer is whatever FactionHelpOfferTracker currently has rolled for this faction
		/// — since that can change between context-menu opens (daily reroll, or a reroll
		/// from this same faction being used on a different mission), the ability's display
		/// text and stored Resource*/RelationshipAmount fields are refreshed here on *every*
		/// call, not just when first created — a cache hit still gets updated before
		/// returning, so a stale price never lingers on a cached-but-reopened menu entry.
		/// </summary>
		public static AskForHelpAbility GetOrCreate(GeoMission mission, GeoFaction playerFaction, GeoFaction helperFaction, ChaosFactionConfig cfg, FactionHelpOffer offer)
		{
			(GeoMission, GeoFaction) key = (mission, helperFaction);
			string helperName = helperFaction.Name.Localize(null);
			string costText = ChaosFactionLocalization.Get("KEY_CHAOSFACTION_COST_TEXT", offer.ResourceAmount, offer.ResourceType, offer.RelationshipAmount);
			// This mission's own per-threat-level squad-size range (originally split per
			// mission type, replaced with per-DifficultyThreatLevel per explicit follow-up
			// request) — not a global range anymore, so the tooltip has to resolve which
			// threat level this specific mission is actually at.
			MissionHelpSettings settings = MissionHelpSettingsResolver.Get(ContextualAbilitiesPatchHelper.ResolveThreatLevel(mission), cfg);

			if (Cache.TryGetValue(key, out AskForHelpAbility existing))
			{
				existing.ResourceType = offer.ResourceType;
				existing.ResourceAmount = offer.ResourceAmount;
				existing.RelationshipAmount = offer.RelationshipAmount;
				if (existing.GeoscapeAbilityDef is AskForHelpAbilityDef existingDef && existingDef.ViewElementDef != null)
					existingDef.ViewElementDef.DisplayName1 = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ASK_FOR_HELP_TITLE", helperName, costText), true);
				return existing;
			}

			GeoscapeAbilityViewElementDef viewElementDef = ScriptableObject.CreateInstance<GeoscapeAbilityViewElementDef>();
			viewElementDef.name = $"ChaosFaction_AskForHelpViewElementDef_{helperFaction.PPFactionDef.name}";
			viewElementDef.DisplayName1 = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ASK_FOR_HELP_TITLE", helperName, costText), true);
			viewElementDef.Description = new LocalizedTextBind(ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ASK_FOR_HELP_DESC", helperName, settings.MinSquadSize, settings.MaxSquadSize, costText), true);
			viewElementDef.DisplayPriority = 0;
			viewElementDef.ShowContextMenuTooltip = true;
			viewElementDef.ContextMenuShowOnlyIfViewFaction = true;
			viewElementDef.ContextMenuVisibility = NewVisibilityCondition();
			viewElementDef.ActionBarVisibility = NewVisibilityCondition();
			viewElementDef.CallToActionButtonVisibility = NewVisibilityCondition();

			AskForHelpAbilityDef abilityDef = ScriptableObject.CreateInstance<AskForHelpAbilityDef>();
			abilityDef.name = $"ChaosFaction_AskForHelpAbilityDef_{helperFaction.PPFactionDef.name}";
			abilityDef.ViewElementDef = viewElementDef;
			// GeoAbility.Activate/GetTargetDisabledState both do resourceCost.Count without a
			// null check (GetResourceCost just returns GeoscapeAbilityDef.Cost directly), so
			// this must be a real (empty) ResourcePack, never null — actual payment is chosen
			// and applied by hand after the ability fires, see AskForHelpAbility.ActivateInternal.
			abilityDef.Cost = new ResourcePack();

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			AskForHelpAbility ability = new AskForHelpAbility
			{
				Mission = mission,
				PlayerFaction = playerFaction,
				HelperFaction = helperFaction,
				ResourceType = offer.ResourceType,
				ResourceAmount = offer.ResourceAmount,
				RelationshipAmount = offer.RelationshipAmount,
			};
			ability.Init(abilityDef, repo);
			ability.SetActor(mission.Site);

			Cache[key] = ability;
			mission.OnMissionActivated += (m, result) => Cleanup(m);
			mission.OnMissionCancel += Cleanup;

			return ability;
		}

		/// <summary>
		/// Removes every helper-faction variant cached for this mission — doesn't need to
		/// know the candidate faction list itself, just scans the cache for matching
		/// missions. Subscribed once per GetOrCreate call (so up to once per faction
		/// actually created for this mission) — safe to call more than once, already-removed
		/// entries are just skipped on the next pass.
		/// </summary>
		private static void Cleanup(GeoMission mission)
		{
			List<(GeoMission, GeoFaction)> keys = Cache.Keys.Where(k => k.Item1 == mission).ToList();
			foreach ((GeoMission, GeoFaction) key in keys)
			{
				if (!Cache.TryGetValue(key, out AskForHelpAbility ability))
					continue;
				Cache.Remove(key);
				try
				{
					AskForHelpAbilityDef def = ability.GeoscapeAbilityDef as AskForHelpAbilityDef;
					if (def != null)
					{
						if (def.ViewElementDef != null)
							UnityEngine.Object.Destroy(def.ViewElementDef);
						UnityEngine.Object.Destroy(def);
					}
				}
				catch (Exception ex)
				{
					Log.Debug("Failed to clean up Ask for Help ability defs: " + ex);
				}
			}
		}

		private static GeoscapeAbilityViewElementDef.VisibilityCondition NewVisibilityCondition()
		{
			return new GeoscapeAbilityViewElementDef.VisibilityCondition
			{
				Visible = true,
				DisabledStateFilters = new List<GeoAbilityDisabledState>(),
				TargetDisabledStateFilters = new List<GeoAbilityTargetDisabledState>(),
			};
		}
	}
}
