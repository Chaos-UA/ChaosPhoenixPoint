using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Defs;
using Base.Serialization.General;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace ChaosInventory
{
	/// <summary>
	/// Replaces the old per-vehicle real ItemStorage design (ChaosVehicleStorage) with a pure
	/// metadata overlay: every real item physically lives in the faction's own vanilla
	/// GeoFaction.ItemStorage (the same pool "Global Inventory" always was) - this class only
	/// tracks how many units of each ItemDef are currently "claimed" by each aircraft. Nothing
	/// here is a container of real GeoItem objects; disabling this mod simply means the claims
	/// stop being read/enforced, and every previously-claimed unit is already sitting in the
	/// normal, vanilla-visible Global Inventory where it always physically was.
	///
	/// Invariant (defensively enforced, not just assumed): for a given ItemDef, the sum of every
	/// vehicle's claimed count for that faction can never exceed the real count actually sitting
	/// in that faction's ItemStorage - claims are clamped down at read time if something outside
	/// this mod's awareness (e.g. scrapping an item from an unrelated screen) reduced the real
	/// total out from under a claim.
	///
	/// Second invariant, per explicit user request (simplifying away an earlier, more complex
	/// "track which vehicle holds the genuinely partial unit" design): whenever the real pool for a
	/// pooled-charge item (ammo/magazines/batteries - ChargesMax>0 && CombineWhenStacking) currently
	/// has a partial remainder, exactly ONE unit's worth of claim capacity is reserved and can never
	/// be claimed by any vehicle (see Adjust's own cap-reduction) - every OTHER unit in that same
	/// real stack is still guaranteed genuinely full and freely claimable. This guarantees every
	/// claimed unit of such an item is always genuinely full-charge - the one partially-charged unit
	/// can therefore never end up claimed by any vehicle; it always stays untagged Global Inventory
	/// instead, automatically, with no separate tracking/map needed to know WHICH bucket holds the
	/// true partial charge (see ChaosInventoryPatchUtl.ChargesOverrideFor, which can now
	/// unconditionally show ChargesMax for any vehicle claim of a pooled-charge item instead of
	/// guessing).
	/// </summary>
	// v90 fix: this entry used to hold the live ItemDef reference directly (`public ItemDef
	// ItemDef;`) - confirmed via Player.log this crashed mod save entirely
	// (`JsonSerializationException: Self referencing loop detected with type
	// 'PhoenixPoint.Tactical.Entities.Equipments.TacticalItemDef'. Path
	// 'VehicleAssignments.1[0].ItemDef'`). Mod instance data is serialized via plain
	// Newtonsoft.Json reflection (not the game's own binary Base.Serialization.General reader/
	// writer this [SerializeType] attribute would otherwise imply - Newtonsoft.Json isn't even
	// shipped in this mod's ModSDK references, so its own [JsonIgnore] can't be used here either),
	// which tries to serialize the ENTIRE ItemDef object graph - but ItemDefs cross-reference each
	// other in cycles (compatible ammo, addons, etc.), which Json.NET can't serialize at all by
	// default. Fixed by storing the def's own stable `Guid` (a plain string - BaseDef.Guid,
	// inherited by every ItemDef) instead of the live object, resolved back to the real ItemDef via
	// DefRepository.GetDef(guid) on load (see ChaosVehicleAssignment.ResolveItemDef below).
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosVehicleAssignmentEntry
	{
		public string ItemDefGuid;
		public int Count;
	}

	public static class ChaosVehicleAssignment
	{
		// vehicleID -> (ItemDef -> claimed count). VehicleID is only faction-unique, same caveat
		// as the old ChaosVehicleStorage design - every call site here only ever operates on the
		// viewer's own Phoenix faction's vehicles.
		private static readonly Dictionary<int, Dictionary<ItemDef, int>> _assignments =
			new Dictionary<int, Dictionary<ItemDef, int>>();

		// Lazily populated from live GeoVehicle references (vehicle.Owner) whenever this mod
		// touches a vehicle - lets capacity/trim patches find "which faction's ItemStorage does
		// this vehicle's claim actually sit in" without needing a UI context. Not persisted -
		// harmless to rebuild lazily post-load the first time each vehicle is touched again.
		private static readonly Dictionary<int, GeoFaction> _owners = new Dictionary<int, GeoFaction>();

		private static Dictionary<ItemDef, int> GetOrCreate(int vehicleId)
		{
			Dictionary<ItemDef, int> dict;
			if (!_assignments.TryGetValue(vehicleId, out dict))
			{
				dict = new Dictionary<ItemDef, int>();
				_assignments[vehicleId] = dict;
			}
			return dict;
		}

		private static int RealCount(GeoFaction faction, ItemDef itemDef)
		{
			GeoItem existing;
			return faction?.ItemStorage != null && faction.ItemStorage.Items.TryGetValue(itemDef, out existing)
				? existing.CommonItemData.Count
				: 0;
		}

		/// Adjusts (increments if positive, decrements if negative) how many units of itemDef are
		/// claimed by this vehicle. Clamped to [0, effective cap for that ItemDef in the faction's
		/// real storage] - a claim can never exceed what's actually physically there. Zero-count
		/// entries are pruned so save data and diagnostics don't accumulate stale zeros.
		public static void Adjust(GeoVehicle vehicle, ItemDef itemDef, int delta)
		{
			if (vehicle == null || itemDef == null || delta == 0)
			{
				return;
			}
			// Per explicit user request/report: growing a claim used to be refused OUTRIGHT whenever
			// the real pool for a pooled-charge item had ANY partial remainder, even if the specific
			// units being claimed were genuinely full ones sitting alongside that remainder (e.g. "2
			// full magazines + 1 at 7/8" - moving just the 2 full ones to an aircraft was silently
			// rejected, the UI showed them moved anyway until the next refresh silently snapped
			// everything back to Global). Fixed: instead of refusing all growth, reserve exactly ONE
			// unit's worth of cap for the pool's partial remainder (if any) - that one unit's identity
			// is unknowable, so it must never be claimable by any vehicle, but every OTHER unit in the
			// same real stack is still guaranteed genuinely full (vanilla only ever tracks one shared
			// charge value for the whole stack, so at most one physical unit can ever be partial) and
			// can be freely claimed. This keeps "any vehicle claim of a pooled-charge item is always
			// genuinely full-charge" true (see ChargesOverrideFor) while no longer blocking the normal
			// "move the full ones, leave the partial one behind" flow the user actually wants.
			int cap = RealCount(vehicle.Owner, itemDef);
			if (delta > 0 && itemDef.ChargesMax > 0 && itemDef.CombineWhenStacking)
			{
				GeoItem real;
				if (vehicle.Owner?.ItemStorage != null && vehicle.Owner.ItemStorage.Items.TryGetValue(itemDef, out real)
					&& real.CommonItemData.Count > 0 && real.CommonItemData.CurrentCharges != itemDef.ChargesMax)
				{
					cap = Math.Max(0, cap - 1);
					Log.Debug($"ChaosVehicleAssignment.Adjust: '{itemDef.name}' pool currently has a partial-charge remainder ({real.CommonItemData.CurrentCharges}/{itemDef.ChargesMax}) - reserving 1 unit for Global Inventory, effective cap for vehicle #{vehicle.VehicleID} is now {cap}.");
				}
			}
			_owners[vehicle.VehicleID] = vehicle.Owner;
			Dictionary<ItemDef, int> dict = GetOrCreate(vehicle.VehicleID);
			int current;
			dict.TryGetValue(itemDef, out current);
			int updated = current + delta;
			if (updated < 0)
			{
				updated = 0;
			}
			if (updated > cap)
			{
				Log.Debug($"ChaosVehicleAssignment.Adjust: '{itemDef.name}' claim for vehicle #{vehicle.VehicleID} would be {updated}, clamping to effective cap {cap}.");
				updated = cap;
			}
			Log.Debug($"ChaosVehicleAssignment.Adjust: '{itemDef.name}' vehicle #{vehicle.VehicleID} claim {current} + ({delta}) -> {updated} (effective cap {cap}).");
			if (updated == 0)
			{
				dict.Remove(itemDef);
			}
			else
			{
				dict[itemDef] = updated;
			}
		}

		/// Every (ItemDef, count) this vehicle currently claims, clamped against the real total so
		/// a stale/over-claimed entry (e.g. the item was scrapped elsewhere) never DISPLAYS more
		/// than what's actually real. Deliberately non-mutating (unlike an earlier version of this
		/// method) - see the big comment below for why.
		public static List<KeyValuePair<ItemDef, int>> GetAll(GeoVehicle vehicle)
		{
			if (vehicle == null)
			{
				return new List<KeyValuePair<ItemDef, int>>();
			}
			_owners[vehicle.VehicleID] = vehicle.Owner;
			Dictionary<ItemDef, int> dict;
			if (!_assignments.TryGetValue(vehicle.VehicleID, out dict))
			{
				return new List<KeyValuePair<ItemDef, int>>();
			}
			// Read-only clamp, NOT a mutation of `dict` - confirmed via Player.log to be the actual
			// root cause of a real, reported claim loss: this method is called from GetDisplayItems
			// (i.e. from ordinary UI refreshes - the cargo badge, the merged Storage list, etc.), any
			// of which can run at a moment when real storage is only TRANSIENTLY below a valid claim
			// (e.g. between two related but separate commits - one that temporarily reduces the real
			// stack before a very next one restores/increases it again). The previous version of this
			// method treated "claim currently exceeds real total" as "this claim is stale garbage,
			// delete it" (`dict.Remove`/`dict[key] = clamped`) - permanently destroying a perfectly
			// valid claim just because it was queried at an unlucky instant, with no way to recover
			// once deleted. `Adjust` (above) is the only method that should ever durably change a
			// claim - it already has its own, correctly-scoped clamp for exactly this purpose. This
			// method now only ever affects what's RETURNED (skipping/clamping a momentarily-over-cap
			// entry for display), never what's STORED - a transient mismatch self-corrects on the
			// next real `Adjust` call instead of being permanently baked in here.
			List<KeyValuePair<ItemDef, int>> result = new List<KeyValuePair<ItemDef, int>>();
			foreach (KeyValuePair<ItemDef, int> kv in dict)
			{
				int cap = RealCount(vehicle.Owner, kv.Key);
				int clamped = Math.Min(kv.Value, cap);
				if (clamped > 0)
				{
					result.Add(new KeyValuePair<ItemDef, int>(kv.Key, clamped));
				}
			}
			return result;
		}

		/// Sum, across every vehicle of this faction, of how many units of itemDef are claimed -
		/// used both to compute the "untagged remainder" shown as Global Inventory, and to protect
		/// claimed quantity from capacity/trim accounting.
		public static int GetTotalAssignedAcrossVehicles(GeoFaction faction, ItemDef itemDef)
		{
			if (faction == null)
			{
				return 0;
			}
			int total = 0;
			foreach (GeoVehicle v in faction.Vehicles)
			{
				Dictionary<ItemDef, int> dict;
				int count;
				if (_assignments.TryGetValue(v.VehicleID, out dict) && dict.TryGetValue(itemDef, out count))
				{
					total += Math.Min(count, RealCount(faction, itemDef));
				}
			}
			return total;
		}

		/// Same as GetTotalAssignedAcrossVehicles, but keyed off a real ItemStorage instance
		/// rather than a GeoFaction - used by capacity/trim patches that only see the storage
		/// object (e.g. ItemStorage.GetStorageUsed's own __instance), not a faction reference.
		public static int GetTaggedStorageUsedForStorage(ItemStorage storage)
		{
			if (storage == null)
			{
				return 0;
			}
			int total = 0;
			foreach (KeyValuePair<int, Dictionary<ItemDef, int>> entry in _assignments)
			{
				GeoFaction owner;
				if (!_owners.TryGetValue(entry.Key, out owner) || owner?.ItemStorage != storage)
				{
					continue;
				}
				foreach (KeyValuePair<ItemDef, int> kv in entry.Value)
				{
					int cap = RealCount(owner, kv.Key);
					total += Math.Min(kv.Value, cap) * kv.Key.StorageUsed;
				}
			}
			return total;
		}

		public static int GetTaggedCountForStorage(ItemStorage storage, ItemDef itemDef)
		{
			if (storage == null)
			{
				return 0;
			}
			int total = 0;
			foreach (KeyValuePair<int, Dictionary<ItemDef, int>> entry in _assignments)
			{
				GeoFaction owner;
				if (!_owners.TryGetValue(entry.Key, out owner) || owner?.ItemStorage != storage)
				{
					continue;
				}
				int count;
				if (entry.Value.TryGetValue(itemDef, out count))
				{
					total += Math.Min(count, RealCount(owner, itemDef));
				}
			}
			return total;
		}

		/// Total StorageUsed this vehicle's own claims represent - used for the "unlimited
		/// aircraft storage" indicator (shown against int.MaxValue, not the real faction total).
		public static int GetVehicleStorageUsed(GeoVehicle vehicle)
		{
			int total = 0;
			foreach (KeyValuePair<ItemDef, int> kv in GetAll(vehicle))
			{
				total += kv.Value * kv.Key.StorageUsed;
			}
			return total;
		}

		/// Drops this vehicle's claims entirely - used when the aircraft is destroyed/dismantled.
		/// Unlike the old real-storage design, this does NOT delete any real items: everything
		/// this vehicle claimed simply becomes untagged Global Inventory again, automatically,
		/// the next time anything reads the faction's real storage. No item loss on destruction.
		public static void Remove(GeoVehicle vehicle)
		{
			if (vehicle != null)
			{
				_assignments.Remove(vehicle.VehicleID);
				_owners.Remove(vehicle.VehicleID);
			}
		}

		public static void Clear()
		{
			_assignments.Clear();
			_owners.Clear();
		}

		public static Dictionary<int, List<ChaosVehicleAssignmentEntry>> Snapshot()
		{
			Dictionary<int, List<ChaosVehicleAssignmentEntry>> snapshot = new Dictionary<int, List<ChaosVehicleAssignmentEntry>>();
			foreach (KeyValuePair<int, Dictionary<ItemDef, int>> entry in _assignments)
			{
				snapshot[entry.Key] = entry.Value
					.Where(kv => !string.IsNullOrEmpty(kv.Key?.Guid))
					.Select(kv => new ChaosVehicleAssignmentEntry { ItemDefGuid = kv.Key.Guid, Count = kv.Value })
					.ToList();
			}
			return snapshot;
		}

		public static void Restore(Dictionary<int, List<ChaosVehicleAssignmentEntry>> saved)
		{
			_assignments.Clear();
			_owners.Clear();
			if (saved == null)
			{
				return;
			}
			foreach (KeyValuePair<int, List<ChaosVehicleAssignmentEntry>> entry in saved)
			{
				Dictionary<ItemDef, int> dict = new Dictionary<ItemDef, int>();
				foreach (ChaosVehicleAssignmentEntry e in entry.Value)
				{
					ItemDef itemDef = ResolveItemDef(e?.ItemDefGuid);
					if (itemDef != null && e.Count > 0)
					{
						dict[itemDef] = e.Count;
					}
					else if (e != null && !string.IsNullOrEmpty(e.ItemDefGuid))
					{
						Log.Debug($"ChaosVehicleAssignment.Restore: couldn't resolve ItemDef guid '{e.ItemDefGuid}' - dropping this claim entry.");
					}
				}
				_assignments[entry.Key] = dict;
			}
		}

		// Resolves a saved def Guid back to the live ItemDef instance - the same DefRepository
		// every def in the game is already loaded through (confirmed via the source,
		// e.g. Base.Entities.ActorSpawner's own `GameUtl.Game().GetComponent<DefRepository>()`
		// idiom), so this works for any ItemDef regardless of which mod/DLC actually defined it.
		private static ItemDef ResolveItemDef(string guid)
		{
			if (string.IsNullOrEmpty(guid))
			{
				return null;
			}
			DefRepository repo = GameUtl.Game()?.GetComponent<DefRepository>();
			return repo?.GetDef(guid) as ItemDef;
		}
	}
}
