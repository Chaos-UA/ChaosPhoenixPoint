using Base.Levels;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Modding;

namespace ChaosFaction
{
	/// <summary>
	/// This is the main mod class. Only one can exist per assembly.
	/// </summary>
	public class ChaosFactionMain : ModMain
	{
		/// Config is accessible at any time, if any is declared.
		public new ChaosFactionConfig Config => (ChaosFactionConfig)base.Config;

		/// Static accessor so Harmony patches (static context) can read config without reflection.
		public static ChaosFactionConfig Cfg { get; private set; }

		public override bool CanSafelyDisable => false;

		public override void OnModEnabled()
		{
			Cfg = Config;
			Logger.LogInfo("ChaosFaction: applying patches...");
			HarmonyLib.Harmony harmony = (HarmonyLib.Harmony)HarmonyInstance;
			harmony.PatchAll();
			ChaosFactionDebugFlag.RegisterConsoleCommands();
			ChaosFactionLocalization.LoadOwnTranslations(Instance.Entry.Directory);
			Logger.LogInfo("ChaosFaction: patched.");
		}

		public override void OnModDisabled()
		{
		}

		public override void OnConfigChanged()
		{
			Cfg = Config;
			// ShowUnrevealedAttacksCounter (or the debug flag, changed elsewhere via
			// chaos_factions_debug) can change what should currently be visible on the
			// map without any attack having just happened to trigger a refresh on its
			// own — re-evaluate every tracked Pandoran base's marker immediately so a
			// Mod Settings toggle takes effect right away instead of waiting for the
			// next attack.
			HavenSelfDefensePandoranSiteAttackTracker.RefreshAllMarkers(ChaosFactionGeoscape.CurrentController);
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
