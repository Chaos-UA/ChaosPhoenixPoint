using Base.Utils.GameConsole;

namespace ChaosUkrainian
{
	/// <summary>
	/// Registered manually by ChaosUkrainianLocalization.RegisterConsoleCommands, not picked up
	/// by the game's own ConsoleCommandAttribute.LoadCommands() (see there for why). Both
	/// commands mirror the base game's own "export_localization" command (PhoenixPoint/Common/
	/// Core/OptionsManager.cs) - same output directory, same CSV format - except the Ukrainian
	/// column is always excluded, since these exist to compare against/regenerate our own
	/// Data/Localization/*.csv translations, not to re-import them.
	/// </summary>
	public static class ChaosUkrainianConsoleCommands
	{
		[ConsoleCommand(Command = "chaos_export_localization", Description = "Like export_localization, but excludes the Ukrainian column.")]
		public static void ExportLocalization(IConsole console)
		{
			ChaosUkrainianLocalization.ExportLocalization(false, console);
		}

		[ConsoleCommand(Command = "chaos_export_missing_localization", Description = "Like export_localization, but only keys with no Ukrainian translation, and excludes the Ukrainian column.")]
		public static void ExportMissingLocalization(IConsole console)
		{
			ChaosUkrainianLocalization.ExportLocalization(true, console);
		}
	}
}
