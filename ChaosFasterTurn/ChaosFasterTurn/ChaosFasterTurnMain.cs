using Base.Levels;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Modding;
using System.Linq;
using UnityEngine;

namespace ChaosFasterTurn
{
	/// <summary>
	/// This is the main mod class. Only one can exist per assembly.
	/// If no ModMain is detected in assembly, then no other classes/callbacks will be called.
	/// </summary>
	public class ChaosFasterTurnMain : ModMain
	{
		/// Config is accessible at any time, if any is declared.
		public new ChaosFasterTurnConfig Config => (ChaosFasterTurnConfig)base.Config;

		/// Static accessor so patches / other static-context classes (e.g. ChaosFasterTurnUpdater)
		/// can read config without reflection.
		public static ChaosFasterTurnConfig Cfg { get; private set; }

		/// This property indicates if mod can be Safely Disabled from the game.
		/// Safely sisabled mods can be reenabled again. Unsafely disabled mods will need game restart ot take effect.
		/// Unsafely disabled mods usually cannot revert thier changes in OnModDisabled
		/// ChaosFasterTurn doesn't Harmony-patch anything; it only touches a public event and a
		/// public property, both of which are cleanly reset in OnModDisabled below, so it's safe.
		public override bool CanSafelyDisable => false;

		/// <summary>
		/// Callback for when mod is enabled. Called even on game starup.
		/// </summary>
		public override void OnModEnabled() {
			Cfg = Config;
			Logger.LogInfo("ChaosFasterTurn: applying patches...");
			HarmonyLib.Harmony harmony = (HarmonyLib.Harmony)HarmonyInstance;
			harmony.PatchAll();
			/// The hotkey poller lives on the mod's own persistent GameObject so it keeps running
			/// across Tactical level loads/unloads without needing to be re-created each time.
			ModGO.AddComponent<ChaosFasterTurnUpdater>();
			Logger.LogInfo("ChaosFasterTurn: patched.");
		}

		/// <summary>
		/// Callback for when mod is disabled. This will be called even if mod cannot be safely disabled.
		/// Guaranteed to have OnModEnabled before.
		/// </summary>
		public override void OnModDisabled() {
			/// If a Tactical level is loaded and some actors are currently sped up, put them all
			/// back to normal before the mod goes away.
			FasterTurnState.ResetAllActors();
			FasterTurnState.Controller = null;
			PlayerMoveSpeedState.Reset();
			/// ModGO (and the ChaosFasterTurnUpdater component on it) will be destroyed after this returns.
		}

		/// <summary>
		/// Callback for when any property from mod's config is changed.
		/// </summary>
		public override void OnConfigChanged() {
			Cfg = Config;
		}


		/// <summary>
		/// In Phoenix Point there can be only one active level at a time.
		/// Levels go through different states (loading, unloaded, start, etc.).
		/// General puprose level state change callback.
		/// </summary>
		/// <param name="level">Level being changed.</param>
		/// <param name="prevState">Old state of the level.</param>
		/// <param name="state">New state of the level.</param>
		public override void OnLevelStateChanged(Level level, Level.State prevState, Level.State state) {
			/// Alternative way to access current level at any time.
			Level l = GetLevel();
		}

		/// <summary>
		/// Useful callback for when level is loaded, ready, and starts.
		/// Usually game setup is executed.
		/// </summary>
		/// <param name="level">Level that starts.</param>
		public override void OnLevelStart(Level level) {
		}

		/// <summary>
		/// Useful callback for when level is ending, before unloading.
		/// Usually game cleanup is executed.
		/// </summary>
		/// <param name="level">Level that ends.</param>
		public override void OnLevelEnd(Level level) {
		}
	}
}
