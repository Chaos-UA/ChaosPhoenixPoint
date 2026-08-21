using Base.Levels;
using HarmonyLib;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Modding;

namespace ChaosInventory
{
	/// <summary>
	/// This is the main mod class. Only one can exist per assembly.
	/// </summary>
	public class ChaosInventoryMain : ModMain
	{
		/// Config is accessible at any time, if any is declared.
		public new ChaosInventoryConfig Config => (ChaosInventoryConfig)base.Config;

		/// Static accessor so Harmony patches (which live outside this instance) can read config
		/// without reflection, mirroring ChaosMutoid/ChaosOverwatch's own Cfg pattern.
		public static ChaosInventoryConfig Cfg { get; private set; }

		/// Aircraft "storage" is a pure metadata overlay (ChaosVehicleAssignment) - every real item
		/// physically lives in the faction's own vanilla GeoFaction.ItemStorage the whole time,
		/// whether or not it's currently claimed by an aircraft - so DATA-wise, nothing would need
		/// to be flushed or migrated if the mod's own bookkeeping just stopped running. This was
		/// `true` (claiming a safe live disable) until direct user testing confirmed disabling the
		/// mod at runtime doesn't actually work - because `OnModDisabled()` below has never called
		/// `harmony.UnpatchAll()`, so every Harmony patch this mod applies (there are many, across
		/// several vanilla screens) stays fully active in the running process regardless of the
		/// game's own "disabled" bookkeeping. Per the game's own ModEntry.Disable logic, `false`
		/// here means the game never attempts a live unload at all - it just flags the mod as
		/// disabled-pending-restart, which is the honest, safe behavior given the live-unpatch path
		/// was never actually implemented. Genuinely enabling a real live-disable would mean adding
		/// an explicit `harmony.UnpatchAll(harmony.Id)` AND resetting every one of this mod's own
		/// static caches (ChaosVehicleAssignment, ChaosGeoscapeVehicleBadge, etc.) - not attempted
		/// here; requiring a restart is the simpler and safer choice.
		public override bool CanSafelyDisable => false;

		/// <summary>
		/// Callback for when mod is enabled. Called even on game startup.
		/// </summary>
		public override void OnModEnabled()
		{
			Cfg = Config;
			HarmonyLib.Harmony harmony = (HarmonyLib.Harmony)HarmonyInstance;
			harmony.PatchAll();
			ChaosInventoryLocalization.LoadOwnTranslations(Instance.Entry.Directory);
			Logger.LogInfo("ChaosInventory enabled - aircraft now carry their own isolated storage.");
		}

		/// <summary>
		/// Callback for when mod is disabled. This will be called even if mod cannot be safely disabled.
		/// </summary>
		public override void OnModDisabled()
		{
		}

		/// <summary>
		/// Callback for when any property from mod's config is changed.
		/// </summary>
		public override void OnConfigChanged()
		{
			Cfg = Config;
		}

		public override void OnLevelStateChanged(Level level, Level.State prevState, Level.State state)
		{
		}

		public override void OnLevelStart(Level level)
		{
		}

		public override void OnLevelEnd(Level level)
		{
		}
	}
}
