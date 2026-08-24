using PhoenixPoint.Modding;

namespace ChaosUmbraBalance
{
	public class ChaosUmbraBalanceConfig : ModConfig
	{
		[ConfigField("Umbra First Turn AP %",
			"Percentage of max action points the Umbra gets on its very first turn after appearing. " +
			"0 = no actions at all, 100 = unrestricted, -1 or less = don't touch it at all (leave vanilla behavior, feature disabled). Default: 0.")]
		public int UmbraFirstTurnActionPoints = 0;

		[ConfigField("Reinforcements First Turn AP %",
			"Percentage of max action points AI-controlled reinforcements get on the turn they arrive from a map-edge " +
			"reinforcement zone (never the mission's initial squad; covers both scheduled reinforcement waves and " +
			"ability-triggered ones). " +
			"0 = no actions at all, 100 = unrestricted, -1 or less = don't touch it at all (leave vanilla behavior, feature disabled). Default: -1 (disabled).")]
		public int ReinforcementsFirstTurnActionPoints = -1;

		[ConfigField("Disable Umbra",
			"When enabled, the Umbra will appear on the map but will never be able to act. Umbra dies on turn start. " +
			"Overrides First Turn AP if both are set.")]
		public bool DisableUmbra = false;

		[ConfigField("Enable Debug Log",
			"Write mod decisions to the Unity log. Useful for diagnosing issues.")]
		public bool EnableDebugLog = false;
	}
}