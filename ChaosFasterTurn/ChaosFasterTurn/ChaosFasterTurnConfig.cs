using PhoenixPoint.Modding;

namespace ChaosFasterTurn
{
	/// <summary>
	/// ModConfig is mod settings that players can change from within the game.
	/// Config is only editable from players in main menu.
	/// Only one config can exist per mod assembly.
	/// Config is serialized on disk as json.
	/// </summary>
	public class ChaosFasterTurnConfig : ModConfig
	{
		/// The three speed hotkeys are hardcoded (Backspace/Minus/Equals - see
		/// ChaosFasterTurnSpeedControl.cs) rather than configurable here. They used to be
		/// ChaosFasterTurnHotkey config fields, but that let a player rebind onto a key already
		/// used by a vanilla function (F2, by default, before the defaults were changed - see
		/// CLAUDE.md "Incident"), so the hotkeys were fixed instead of exposing that footgun again.

		[ConfigField("Max Speed Multiplier", "Upper bound (whole x-steps) enemy turn speed can reach with the Increase Speed key. Whole number, 1 or higher. Default: 20")]
		public int MaxSpeedMultiplier = 20;

		[ConfigField("Reset Speed On Player Turn", "When enabled, enemy turn speed automatically resets to normal (x1) once the player's own turn starts, so every enemy turn begins at x1 again. When disabled, the last chosen speed carries over to the next enemy turn instead. Default: enabled")]
		public bool ResetSpeedOnPlayerTurn = true;

		[ConfigField("Reset Speed On Enemy Attack", "When enabled, enemy turn speed automatically drops to normal (x1) for the duration of any shot an AI-controlled unit fires at a player-controlled unit (or one of its overwatch/return-fire reactions), then restores automatically the instant that shot resolves. When disabled, enemy attacks on the player play at whatever speed is currently selected, same as every other enemy action. Default: enabled")]
		public bool ResetSpeedOnEnemyAttack = true;

		[ConfigField("Enable Debug Log", "Logs speed changes and turn transitions to the game log, useful for troubleshooting.")]
		public bool EnableDebugLog = false;
	}
}
