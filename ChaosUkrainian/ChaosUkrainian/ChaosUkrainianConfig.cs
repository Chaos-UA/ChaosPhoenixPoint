using PhoenixPoint.Modding;

namespace ChaosUkrainian
{
	/// <summary>
	/// ModConfig is mod settings that players can change from within the game.
	/// Config is only editable from players in main menu.
	/// Only one config can exist per mod assembly.
	/// Config is serialized on disk as json.
	/// </summary>
	public class ChaosUkrainianConfig : ModConfig
	{
		/// <summary>
		/// One of the 8 languages the base game ships (matches the header row of
		/// StreamingAssets/Localization/*.csv, see CLAUDE.md "Where translation strings
		/// live"). Used as the second link in the fallback chain: Ukrainian -> this ->
		/// English (see ChaosUkrainianLocalization.TryResolveConfiguredFallback).
		/// </summary>
		public enum FallbackLanguageOption
		{
			English,
			ChineseSimplified,
			French,
			German,
			Italian,
			Polish,
			Russian,
			Spanish,
		}

		/// <summary>
		/// Language to prefer over English when a key has no Ukrainian translation yet.
		/// If that language is also missing the key, resolution falls through to English
		/// explicitly (see ChaosUkrainianLocalization.TryResolveConfiguredFallback).
		/// </summary>
		/// <remarks>
		/// ConfigFieldAttribute's Text/Description only accept compile-time literal
		/// strings (no I2 Loc term lookup - see PhoenixPoint.Modding.ConfigFieldAttribute),
		/// so this tooltip is hardcoded in Ukrainian rather than tracking the player's
		/// selected game language.
		/// </remarks>
		[ConfigField("Резервна мова", "Мова, якою показуються рядки, ще не перекладені українською. Якщо ключа немає і в цій мові, буде використано англійську.")]
		public FallbackLanguageOption FallbackLanguage = FallbackLanguageOption.English;
	}
}