using Base.Serialization.General;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Modding;
using System.Collections.Generic;

namespace ChaosFaction
{
	/// <summary>
	/// One pending help request, flattened to plain primitives/ints (never a live GeoSite/
	/// GeoFaction/GeoMission reference — none of those survive serialization as the same
	/// object, and Def/enum references add uncertainty this mod has no way to verify
	/// without a running game) so it round-trips through the save file safely. SiteId is
	/// GeoSite's own stable identity (GeoSite.SiteId, the same field GeoSite's own
	/// save/load code uses to reassociate itself); HelperKind/Method/ResourceType are all
	/// stored as int rather than their actual enum types, on the theory that plain ints are
	/// the least likely thing to trip up unfamiliar territory in this project's save
	/// serializer — see ChaosFactionGeoscape's own doc comment for the fuller reasoning.
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionSavedHelpRequest
	{
		public int SiteId;
		public int HelperKind;
		public int Method;
		public int ResourceType;
		public int ResourceAmount;
		public int RelationshipAmount;
		public double ExpiresAtHours;
	}

	/// <summary>
	/// One pending debug activity, flattened the same way ChaosFactionSavedHelpRequest is —
	/// explicit follow-up request: "activity state is not saved after save/load or after
	/// finishing another mission, make it to save and restore the same way as it is done
	/// for help request." No squad field, same reasoning ChaosFactionSavedHelpRequest has
	/// none either — TacCharacterDef references aren't safe to serialize, so
	/// DebugActivityTracker.RestorePendingActivities rebuilds the squad fresh at actual
	/// launch time instead, from Kind.
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionSavedActivity
	{
		public int SiteId;
		public int Kind;
		public bool IsHostile;
		public bool IsRevealed;
		public double ExpiresAtHours;
	}

	/// <summary>
	/// One faction's current haven-defense free-help "pity" streak (HavenDefenseFreeHelpPity,
	/// ChaosFactionState.cs) — HelperKind as a plain int for the same reason
	/// ChaosFactionSavedHelpRequest stores its own HelperKind as one, FailureStreak is the
	/// count of consecutive failed rolls (0 is never actually stored — see
	/// RecordGeoscapeInstanceData below).
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionSavedHavenPity
	{
		public int HelperKind;
		public int FailureStreak;
	}

	/// <summary>
	/// One Pandoran base's current "attack counter" for
	/// HavenSelfDefensePandoranSiteAttackTracker (ChaosFactionState.cs) — SiteId is the
	/// same stable GeoSite identity every other site-keyed saved entry in this file uses,
	/// AttackCount is how many times a nearby Haven has self-defended against it without
	/// it being revealed yet (0 is never actually stored — see RecordGeoscapeInstanceData
	/// below).
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionSavedPandoranSiteAttackCount
	{
		public int SiteId;
		public int AttackCount;
	}

	/// <summary>
	/// Mod's custom save data for geoscape — every currently-pending "ask for help" request
	/// (paid for, not yet launched/cancelled/expired), every currently-pending debug
	/// activity, every faction's current haven-defense free-help pity streak, and every
	/// Pandoran base's current self-defense reveal attack counter, so
	/// AllyRequestTracker.ActiveRequests/DebugActivityTracker.ActiveActivities/
	/// HavenDefenseFreeHelpPity/HavenSelfDefensePandoranSiteAttackTracker all survive a
	/// save/load instead of silently forgetting they exist (the pre-existing,
	/// explicitly-accepted limitation this replaces — see CLAUDE.md).
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFactionGSInstanceData
	{
		public List<ChaosFactionSavedHelpRequest> PendingHelpRequests = new List<ChaosFactionSavedHelpRequest>();
		public List<ChaosFactionSavedActivity> PendingActivities = new List<ChaosFactionSavedActivity>();
		public List<ChaosFactionSavedHavenPity> HavenPityStreaks = new List<ChaosFactionSavedHavenPity>();
		public List<ChaosFactionSavedPandoranSiteAttackCount> PandoranSiteAttackCounts = new List<ChaosFactionSavedPandoranSiteAttackCount>();
	}

	/// <summary>
	/// Represents a mod instance specific for Geoscape game.
	/// Each time Geoscape level is loaded, new mod's ModGeoscape is created.
	/// </summary>
	public class ChaosFactionGeoscape : ModGeoscape
	{
		/// <summary>
		/// Data handed to ProcessGeoscapeInstanceData on a load, held here until
		/// OnGeoscapeStart — ProcessGeoscapeInstanceData's own doc comment says the level
		/// is "already created" but GeoscapeStart hasn't fired yet at that point, which
		/// this mod takes to mean site/mission state (GeoLevelController.Map.AllSites,
		/// GeoSite.ActiveMission) isn't necessarily safe to read yet; deferring actual
		/// restoration to OnGeoscapeStart avoids relying on an unconfirmed assumption about
		/// exactly how "already created" this early stage really is. On a brand new game
		/// (no load), this simply stays null and RestorePendingRequests no-ops.
		/// </summary>
		private ChaosFactionGSInstanceData _pendingRestoreData;

		/// <summary>
		/// The live GeoLevelController for whichever Geoscape session is currently
		/// active, if any — so console commands (e.g. chaos_factions_state,
		/// ChaosFactionDebug.cs) that need to read live faction/relationship state have
		/// somewhere to get it from without their own scene lookup. Set at the top of
		/// OnGeoscapeStart, cleared in OnGeoscapeEnd, so it's never stale across a
		/// Geoscape teardown.
		/// </summary>
		public static GeoLevelController CurrentController { get; private set; }

		/// <summary>
		/// Called when Geoscape starts.
		/// </summary>
		public override void OnGeoscapeStart()
		{
			GeoLevelController gsController = Controller;
			CurrentController = gsController;
			try
			{
				AllyRequestTracker.RestorePendingRequests(gsController, _pendingRestoreData?.PendingHelpRequests);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to restore pending help requests from save data: " + ex);
			}

			// Debug activities are now persisted/restored the same way — explicit
			// follow-up request ("make it to save and restore the same way as it is done
			// for help request"), replacing what used to be an unconditional discard (a
			// "testing tool" limitation that no longer applies now that activities
			// auto-spawn as a normal, always-on part of the mod). Runs every
			// OnGeoscapeStart, not just a true save/load from the main menu — see
			// RestorePendingActivities' own doc comment for why.
			try
			{
				DebugActivityTracker.RestorePendingActivities(gsController, _pendingRestoreData?.PendingActivities);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to restore pending debug activities from save data: " + ex);
			}
			// Not a live GeoSite/GeoFaction/GeoMission reference (see
			// HavenDefenseFreeHelpPity's own doc comment), so this doesn't strictly need
			// to wait for OnGeoscapeStart the way the other two restores do — but doing
			// it here anyway, in the same place/order as those, keeps every piece of this
			// mod's save data restored from one single spot.
			try
			{
				HavenDefenseFreeHelpPity.RestoreStreaks(_pendingRestoreData?.HavenPityStreaks);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to restore haven-defense free-help pity streaks from save data: " + ex);
			}
			// Unlike HavenDefenseFreeHelpPity above, this one does need a live
			// GeoLevelController (to resolve each saved SiteId back to an actual GeoSite
			// and check whether it's since been revealed/destroyed — see RestoreCounts'
			// own doc comment), so it waits for OnGeoscapeStart the same way
			// RestorePendingRequests/RestorePendingActivities do.
			try
			{
				HavenSelfDefensePandoranSiteAttackTracker.RestoreCounts(gsController, _pendingRestoreData?.PandoranSiteAttackCounts);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to restore haven-defense self-defense reveal attack counters from save data: " + ex);
			}
			finally
			{
				_pendingRestoreData = null;
			}
			// Explicit follow-up request: after restoring this mod's own saved counters
			// above, reconcile them against vanilla's own native GeoAlienBase.
			// BaseAttacksCounter (which can grow independently of this mod's own
			// tracking — see SyncWithNativeCounters' own doc comment) so a base's
			// tracked count never under-represents its real attack history.
			try
			{
				HavenSelfDefensePandoranSiteAttackTracker.SyncWithNativeCounters(gsController);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to sync haven-defense self-defense attack counters with vanilla's own native counters: " + ex);
			}

			// Not debug-only (see AutoDebugActivitySpawner's own doc comment,
			// ChaosFactionDebug.cs, for why): rolls a chance for a Pure/Forsaken/etc.
			// activity to auto-spawn at an eligible site every in-game hour, controlled
			// purely by ActivitySpawnChancePercentPerDay — GeoLevelController.HourTicked
			// (a real, public event) is used instead of a Harmony patch since it's already
			// public. Subscribed unconditionally, not gated on anything here — safe to
			// subscribe every OnGeoscapeStart: ChaosFactionGeoscape is a fresh instance per
			// Geoscape level load (this class's own doc comment above), so there's no risk
			// of stacking duplicate subscriptions across sessions.
			if (gsController != null)
				gsController.HourTicked += hours => AutoDebugActivitySpawner.OnHourTicked(gsController, hours);
		}

		/// <summary>
		/// Called when Geoscape ends.
		/// </summary>
		public override void OnGeoscapeEnd()
		{
			if (CurrentController == Controller)
				CurrentController = null;
		}

		/// <summary>
		/// Called when Geoscape save is going to be generated, giving mod option for custom save data.
		/// Wrapped in try/catch so a serialization surprise degrades to "this save loses
		/// pending help requests" (the pre-existing, already-accepted behavior) rather than
		/// risking the whole save operation.
		/// </summary>
		/// <returns>Object to serialize or null if not used.</returns>
		public override object RecordGeoscapeInstanceData()
		{
			ChaosFactionGSInstanceData data = new ChaosFactionGSInstanceData();
			try
			{
				foreach (KeyValuePair<GeoSite, PendingHelpRequest> kv in AllyRequestTracker.ActiveRequests)
				{
					GeoSite site = kv.Key;
					PendingHelpRequest request = kv.Value;
					if (site == null || site.SiteId < 0 || request?.Offer == null)
						continue;
					data.PendingHelpRequests.Add(new ChaosFactionSavedHelpRequest
					{
						SiteId = site.SiteId,
						HelperKind = (int)request.HelperKind,
						Method = (int)request.Method,
						ResourceType = (int)request.Offer.ResourceType,
						ResourceAmount = request.Offer.ResourceAmount,
						RelationshipAmount = request.Offer.RelationshipAmount,
						ExpiresAtHours = request.ExpiresAtHours,
					});
				}
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to record pending help requests into save data: " + ex);
			}
			try
			{
				foreach (KeyValuePair<GeoSite, PendingDebugActivity> kv in DebugActivityTracker.ActiveActivities)
				{
					GeoSite site = kv.Key;
					PendingDebugActivity activity = kv.Value;
					if (site == null || site.SiteId < 0 || activity == null)
						continue;
					data.PendingActivities.Add(new ChaosFactionSavedActivity
					{
						SiteId = site.SiteId,
						Kind = (int)activity.Kind,
						IsHostile = activity.IsHostile,
						IsRevealed = activity.IsRevealed,
						ExpiresAtHours = activity.ExpiresAtHours,
					});
				}
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to record pending debug activities into save data: " + ex);
			}
			try
			{
				foreach (KeyValuePair<HelperFactionKind, int> kv in HavenDefenseFreeHelpPity.AllStreaks())
				{
					if (kv.Value <= 0)
						continue;
					data.HavenPityStreaks.Add(new ChaosFactionSavedHavenPity { HelperKind = (int)kv.Key, FailureStreak = kv.Value });
				}
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to record haven-defense free-help pity streaks into save data: " + ex);
			}
			try
			{
				foreach (KeyValuePair<int, int> kv in HavenSelfDefensePandoranSiteAttackTracker.AllCounts())
				{
					if (kv.Value <= 0)
						continue;
					data.PandoranSiteAttackCounts.Add(new ChaosFactionSavedPandoranSiteAttackCount { SiteId = kv.Key, AttackCount = kv.Value });
				}
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to record haven self-defense reveal attack counters into save data: " + ex);
			}
			return data;
		}

		/// <summary>
		/// Called when Geoscape save is being process. At this point level is already created, but GeoscapeStart is not called.
		/// </summary>
		/// <param name="instanceData">Instance data serialized for this mod. Cannot be null.</param>
		public override void ProcessGeoscapeInstanceData(object instanceData)
		{
			_pendingRestoreData = instanceData as ChaosFactionGSInstanceData;
		}

		/// <summary>
		/// Called when new Geoscape world is generating. This only happens on new game.
		/// Useful to modify initial spawned sites.
		/// </summary>
		/// <param name="setup">Main geoscape setup object.</param>
		/// <param name="worldSites">Sites to spawn and start simulating.</param>
		public override void OnGeoscapeNewWorldInit(GeoInitialWorldSetup setup, IList<GeoSiteSceneDef.SiteInfo> worldSites)
		{
		}

		/// <summary>
		/// Called when generated Geoscape world will pass through simulation step. This only happens on new game.
		/// Useful to modify game startup setup before simulation.
		/// </summary>
		/// <param name="setup">Main geoscape setup object.</param>
		/// <param name="context">Context for game setup.</param>
		public override void OnGeoscapeNewWorldSimulationStart(GeoInitialWorldSetup setup, GeoInitialWorldSetup.SimContext context)
		{
		}
	}
}
