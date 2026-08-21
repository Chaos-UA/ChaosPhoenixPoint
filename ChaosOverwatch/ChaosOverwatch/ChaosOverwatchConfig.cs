using PhoenixPoint.Modding;

namespace ChaosOverwatch
{
	public class ChaosOverwatchConfig : ModConfig
	{
		[ConfigField(
			text: "Manual Overwatch",
			description: "Pause and open first-person aim when an Overwatch shot triggers. " +
			             "Left-click to fire manually (enemy freezes during the shot), " +
			             "right-click to auto-fire at the original target (vanilla behaviour), " +
			             "ESC to skip the shot. " +
			             "During aim: Space toggles pause/max speed, 1 or + increases enemy speed, " +
			             "2 or - decreases.")]
		public bool ManualOverWatch = true;

		[ConfigField(
			text: "Manual Return Fire",
			description: "Pause and open first-person aim when a Return Fire shot triggers. " +
			             "Left-click to fire manually (enemy freezes during the shot), " +
			             "right-click to auto-fire at the original target (vanilla behaviour), " +
			             "ESC to skip the shot. " +
			             "During aim: Space toggles pause/max speed, 1 or + increases enemy speed, " +
			             "2 or - decreases.")]
		public bool ManualReturnFire = true;

		[ConfigField(
			text: "Pause Target On Regular Shot",
			description: "Freeze the enemy during regular player shots (not related to overwatch/return fire) so movement animations " +
			             "cannot cause misses. The enemy unpauses as soon as the shot resolves.")]
		public bool PauseTargetAnimationOnRegularShot = true;

		[ConfigField(
			text: "Debug Logging",
			description: "Write verbose ChaosOverwatch entries to the Unity log file. " +
			             "Useful for troubleshooting unexpected behaviour.")]
		public bool EnableDebugLog = false;
	}
}
