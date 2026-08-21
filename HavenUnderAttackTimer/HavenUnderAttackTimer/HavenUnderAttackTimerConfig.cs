using PhoenixPoint.Modding;

namespace HavenUnderAttackTimer
{
	/// <summary>
	/// ModConfig is mod settings that players can change from within the game.
	/// Config is only editable from players in main menu.
	/// Only one config can exist per mod assembly.
	/// Config is serialized on disk as json.
	/// </summary>
	public class HavenUnderAttackTimerConfig : ModConfig
	{
		/// When enabled, writes the mod's decisions (patch hits, estimated hours) to the Unity log.
		public bool EnableDebugLog;
	}
}
