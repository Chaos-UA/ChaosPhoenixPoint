using Base.Serialization.General;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Modding;
using System.Collections.Generic;

namespace ChaosInventory
{
	/// <summary>
	/// Mod's custom save data for geoscape: which ItemDef quantities are claimed by which
	/// aircraft (ChaosVehicleAssignment). Note this is pure metadata now - the real items it
	/// refers to are already saved as part of vanilla's own GeoFaction.ItemStorage, so there is
	/// nothing here that, if lost, would make any real item vanish from the save; at worst a
	/// claim reverts to being untagged Global Inventory.
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosInventoryGSInstanceData
	{
		public Dictionary<int, List<ChaosVehicleAssignmentEntry>> VehicleAssignments;
	}

	/// <summary>
	/// Represents a mod instance specific for Geoscape game.
	/// </summary>
	public class ChaosInventoryGeoscape : ModGeoscape
	{
		/// <summary>
		/// Called when Geoscape starts - this fires on every Geoscape (re)entry, including returning
		/// from a tactical mission, not just on a genuine new game or a loaded save. Deliberately does
		/// NOT clear ChaosVehicleAssignment here - see OnGeoscapeNewWorldInit for the actual
		/// new-game-only hook (see the historical note in this mod's CLAUDE.md about why an earlier
		/// version wiping state here was a real, confirmed data-loss bug).
		/// </summary>
		public override void OnGeoscapeStart()
		{
			Log.Debug("OnGeoscapeStart called.");
		}

		/// <summary>
		/// Called when Geoscape ends.
		/// </summary>
		public override void OnGeoscapeEnd()
		{
		}

		/// <summary>
		/// Called when Geoscape save is going to be generated, giving mod option for custom save data.
		/// </summary>
		public override object RecordGeoscapeInstanceData()
		{
			Dictionary<int, List<ChaosVehicleAssignmentEntry>> snapshot = ChaosVehicleAssignment.Snapshot();
			Log.Debug($"RecordGeoscapeInstanceData: saving {snapshot.Count} vehicle assignment entr{(snapshot.Count == 1 ? "y" : "ies")}.");
			return new ChaosInventoryGSInstanceData { VehicleAssignments = snapshot };
		}

		/// <summary>
		/// Called when Geoscape save is being processed. At this point level is already created, but
		/// GeoscapeStart is not called yet.
		/// </summary>
		public override void ProcessGeoscapeInstanceData(object instanceData)
		{
			ChaosInventoryGSInstanceData data = (ChaosInventoryGSInstanceData)instanceData;
			int count = data?.VehicleAssignments?.Count ?? 0;
			Log.Debug($"ProcessGeoscapeInstanceData: restoring {count} vehicle assignment entr{(count == 1 ? "y" : "ies")}.");
			ChaosVehicleAssignment.Restore(data?.VehicleAssignments);
		}

		/// <summary>
		/// Called when new Geoscape world is generating. This only happens on new game - the correct,
		/// unambiguous place to clear any leftover state from a previous session (unlike
		/// OnGeoscapeStart, which fires far more often than just "new game").
		/// </summary>
		public override void OnGeoscapeNewWorldInit(GeoInitialWorldSetup setup, IList<GeoSiteSceneDef.SiteInfo> worldSites)
		{
			Log.Debug("OnGeoscapeNewWorldInit: brand new game - clearing any stale vehicle assignment state.");
			ChaosVehicleAssignment.Clear();
		}

		/// <summary>
		/// Called when generated Geoscape world will pass through simulation step. This only happens on new game.
		/// </summary>
		public override void OnGeoscapeNewWorldSimulationStart(GeoInitialWorldSetup setup, GeoInitialWorldSetup.SimContext context)
		{
		}
	}
}
