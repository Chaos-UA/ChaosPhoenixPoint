using Base.Levels;
using PhoenixPoint.Modding;

namespace ChaosUmbraBalance
{
	public class ChaosUmbraBalanceMain : ModMain
	{
		public new ChaosUmbraBalanceConfig Config => (ChaosUmbraBalanceConfig)base.Config;

		// Accessed by Harmony patch methods to read config without instance context.
		public new static ChaosUmbraBalanceMain Instance { get; private set; }

		public override bool CanSafelyDisable => false;

		public override void OnModEnabled()
		{
			Instance = this;
			Logger.LogInfo("[ChaosUmbraBalance] OnModEnabled: applying Harmony patches...");
			((HarmonyLib.Harmony)HarmonyInstance).PatchAll(typeof(ChaosUmbraBalanceMain).Assembly);
			Logger.LogInfo($"[ChaosUmbraBalance] OnModEnabled: patches applied. Config: DisableUmbra={Config.DisableUmbra}, SpawnAP={Config.UmbraFirstTurnActionPoints}");
		}

		public override void OnModDisabled()
		{
			Instance = null;
			UmbraTracker.Clear();
		}

		public override void OnConfigChanged()
		{
			if (Config.EnableDebugLog)
				Logger.LogInfo("[ChaosUmbraBalance] Config changed.");
		}

		public override void OnLevelStateChanged(Level level, Level.State prevState, Level.State state) { }
		public override void OnLevelStart(Level level) { }
		public override void OnLevelEnd(Level level) { }
	}
}
