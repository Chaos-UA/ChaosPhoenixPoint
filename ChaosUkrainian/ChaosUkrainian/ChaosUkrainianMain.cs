using Base.Levels;
using PhoenixPoint.Modding;
using UnityEngine;

namespace ChaosUkrainian
{
	/// <summary>
	/// This is the main mod class. Only one can exist per assembly.
	/// If no ModMain is detected in assembly, then no other classes/callbacks will be called.
	/// </summary>
	public class ChaosUkrainianMain : ModMain
	{
		/// Config is accessible at any time, if any is declared.
		public new ChaosUkrainianConfig Config => (ChaosUkrainianConfig)base.Config;

		/// <summary>
		/// Static accessor so Harmony patches (static context) can reach this mod's install
		/// directory (Instance.Entry.Directory) without reflection.
		/// </summary>
		internal static ChaosUkrainianMain Main { get; private set; }

		/// <summary>
		/// False on purpose: this mod merges Ukrainian into shared I2 Loc singleton state
		/// (LanguageSourceData.mLanguages/mTerms across 7 sources, plus OptionsManagerDef's
		/// language dropdown list) via Harmony patches. Per ModEntry.SetEnabled, when
		/// CanSafelyDisable is false the framework skips calling OnModDisabled/UnloadMod
		/// entirely - it just marks the mod disabled in the list and sets ModEntry.
		/// UnsafelyDisabled, which flips on the Mod Management screen's built-in "Need
		/// Restart" banner (UIStateModManagment.NeedRestartRoot). That's deliberately safer
		/// here than trying to unpatch Harmony and unwind all of the above by hand: if a
		/// term ended up merged into this mod's data because another source didn't already
		/// have it (see EnsureLanguageRegistered), there's no reliable way to tell that apart
		/// from a term that source legitimately owns, so a clean, total revert isn't
		/// realistically possible - and a half-reverted state (patches gone but Ukrainian
		/// still selected, or vice versa) would be worse than asking for a restart.
		/// </summary>
		public override bool CanSafelyDisable => false;

		/// <summary>
		/// Callback for when mod is enabled. Called even on game starup.
		/// </summary>
		public override void OnModEnabled() {
			Main = this;
			Debug.Log("ChaosUkrainian: OnModEnabled - applying Harmony patches...");

			/// Game creates Harmony object for each mod. Accessible if needed.
			HarmonyLib.Harmony harmony = (HarmonyLib.Harmony)HarmonyInstance;
			harmony.PatchAll(typeof(ChaosUkrainianMain).Assembly);
			Debug.Log("ChaosUkrainian: Harmony patches applied.");

			/// Register "Українська" and import shipped translations immediately too, in case
			/// I2 Loc sources are already loaded by the time this mod is enabled (see
			/// ChaosUkrainianLocalization; the Harmony patches cover the case where they aren't yet).
			ChaosUkrainianLocalization.EnsureLanguageRegistered();
			ChaosUkrainianLocalization.ImportShippedTranslations(Instance.Entry.Directory);
			ChaosUkrainianLocalization.RetryStartupLanguageIfNeeded();
			ChaosUkrainianLocalization.LogLocSourcesDefsForDiagnostics();
			ChaosUkrainianLocalization.RegisterConsoleCommands();
			Debug.Log("ChaosUkrainian: OnModEnabled finished (mod directory: " + Instance.Entry.Directory + ").");
		}

		/// <summary>
		/// Callback for when mod is disabled. Per ModEntry.SetEnabled, this is only actually
		/// invoked when CanSafelyDisable is true - with it false (see above), the framework
		/// never calls this, so it's dead code here, kept only for documentation/parity with
		/// the template. ModGO would be destroyed after OnModDisabled if it were ever called.
		/// </summary>
		public override void OnModDisabled() {
		}

		/// <summary>
		/// Callback for when any property from mod's config is changed.
		/// </summary>
		public override void OnConfigChanged() {
			/// Config is accessible at any time.
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
