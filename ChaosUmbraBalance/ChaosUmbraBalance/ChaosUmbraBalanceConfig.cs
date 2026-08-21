using PhoenixPoint.Modding;

namespace ChaosUmbraBalance
{
	public class ChaosUmbraBalanceConfig : ModConfig
	{
		[ConfigField("Umbra First Turn AP",
			"Action points the Umbra gets on its very first turn after appearing. " +
			"0 = no actions at all, 4 = unrestricted (feature disabled). Default: 0.")]
		public int UmbraFirstTurnActionPoints = 0;

		[ConfigField("Disable Umbra",
			"When enabled, the Umbra will appear on the map but will never be able to act. Umbra dies on turn start. " +
			"Overrides First Turn AP if both are set.")]
		public bool DisableUmbra = false;

		[ConfigField("Enable Debug Log",
			"Write mod decisions to the Unity log. Useful for diagnosing issues.")]
		public bool EnableDebugLog = false;
	}
}