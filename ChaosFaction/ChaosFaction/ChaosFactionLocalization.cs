using System;
using System.IO;
using System.Text;
using Base.UI;
using I2.Loc;
using UnityEngine;

namespace ChaosFaction
{
	/// <summary>
	/// Loads this mod's own KEY_CHAOSFACTION_* terms (Data/Localization/ChaosFaction.csv, same
	/// column shape ChaosUkrainian's own I2Languages exports use — Key,Type,Desc,English, ...)
	/// into I2 Loc, so player-facing text can go through the normal LocalizedTextBind/.Localize
	/// pipeline instead of hardcoded English string literals — explicit request: "expose
	/// hardcoded not translated text to Localization files... add localization currently only
	/// for English." Debug-only text (chaos_factions_debug's own picker/status UI) and
	/// ChaosFactionConfig's [ConfigField] descriptions are deliberately left as plain C# string
	/// literals, per explicit instruction not to localize either.
	///
	/// Unlike ChaosUkrainian's own localization loader (ChaosUkrainianLocalization.cs), which
	/// only ever *overrides* translations for keys some other system (vanilla or a third-party
	/// mod like TFTV) already owns — and so has to defer/retry writes into a *real* shared
	/// source to avoid stub-creating a term before its real owner does (see that class's own
	/// long doc comment on TryApplyTranslation) — every key here is brand new and exclusively
	/// owned by this mod (the "KEY_CHAOSFACTION_" prefix guarantees no collision with vanilla or
	/// any other mod's own terms). That makes the simple, direct approach both correct and
	/// safe: parse this mod's CSV into its own dedicated LanguageSourceData and register that
	/// source directly with I2.Loc.LocalizationManager.Sources — LocalizationManager's own
	/// lookups (TryGetTranslation/GetTermData) already iterate every registered source, so this
	/// needs no interaction with vanilla's or any other mod's source objects at all.
	///
	/// There is no engine-native "just ship CSVs in a mod's own Data folder and they get picked
	/// up automatically" mechanism — confirmed by reading PhoenixPoint.Modding's own mod loader
	/// (PPModLoader only ever reads meta.json + the mod's own .dll) and OptionsManager.
	/// InitLanguage() (PhoenixPoint/Common/Core/OptionsManager.cs), the only place the game
	/// itself scans a folder of *.csv for I2 Loc — but it's hardcoded to the single shared
	/// Application.streamingAssetsPath + "/Localization" folder, not any individual mod's own
	/// directory. TFTV ships its own compiled DLL specifically to do its own CSV loading (its
	/// workshop copy has TFTV.dll sitting right next to its own Assets/Localization/*.csv) —
	/// same reasoning ChaosUkrainian's own loader already established for this codebase.
	/// </summary>
	internal static class ChaosFactionLocalization
	{
		private const string CsvFileName = "ChaosFaction.csv";

		private static bool _attempted;

		/// <summary>
		/// Called once from ChaosFactionMain.OnModEnabled(). Deliberately a single-attempt,
		/// no-retry load (unlike ChaosUkrainian's own re-checked-on-every-Options-panel-Init
		/// design): that mod has to retry because whether a *cross-mod* term already exists can
		/// change over the session as other mods enable later — nothing here depends on any
		/// other mod or on load order at all, so if this succeeds once it's done for the whole
		/// session, and if the CSV is missing/malformed it'll be missing/malformed every time,
		/// so retrying would only repeat the same failed disk read for no benefit.
		/// </summary>
		internal static void LoadOwnTranslations(string modDirectory)
		{
			if (_attempted)
				return;
			_attempted = true;

			try
			{
				if (string.IsNullOrEmpty(modDirectory))
				{
					Log.Debug("ChaosFactionLocalization: mod directory unknown; skipping localization CSV import.");
					return;
				}

				string csvPath = Path.Combine(Path.Combine(Path.Combine(modDirectory, "Data"), "Localization"), CsvFileName);
				if (!File.Exists(csvPath))
				{
					Log.Debug($"ChaosFactionLocalization: no localization CSV found at '{csvPath}'.");
					return;
				}

				// Normalizes line endings before parsing — the same CRLF-safety step
				// ChaosUkrainianLocalization.ImportCsvDirectory applies to its own shipped CSVs,
				// for the same reason: LocalizationReader.ParseCSVline only ever treats '\n' as a
				// line terminator, never stripping a trailing '\r', so a CRLF-saved file's last
				// column on each line would otherwise parse with a stray trailing '\r' attached.
				string csvText = File.ReadAllText(csvPath, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");

				LanguageSourceData source = new LanguageSourceData();
				string error = source.Import_CSV(string.Empty, csvText, eSpreadsheetUpdateMode.Replace, ',');
				if (!string.IsNullOrEmpty(error))
				{
					Debug.LogError($"[ChaosFaction] ChaosFactionLocalization: failed to import '{csvPath}': {error}");
					return;
				}

				LocalizationManager.Sources.Add(source);
				Log.Debug($"ChaosFactionLocalization: imported {source.mTerms.Count} localization term(s) from '{csvPath}'.");
			}
			catch (Exception ex)
			{
				Debug.LogError("[ChaosFaction] ChaosFactionLocalization: failed to load localization CSV: " + ex);
			}
		}

		/// <summary>
		/// Looks up a KEY_CHAOSFACTION_* term and formats it with the given args — mirrors the
		/// existing string.Format(someLocalizedTextBind.Localize(null), args) pattern already
		/// established elsewhere in this mod (e.g. the top-left-objective countdown patch,
		/// ChaosFactionPatches.cs, which formats __instance.TimeFormatter.HoursText.Localize(null)
		/// the same way) rather than relying on I2 Loc's own GameObject-bound parameter system,
		/// which needs live scene binding this mod's plain-string call sites don't have.
		/// </summary>
		internal static string Get(string key, params object[] args)
		{
			string template = new LocalizedTextBind(key).Localize(null);
			if (string.IsNullOrEmpty(template))
				return template;
			return args == null || args.Length == 0 ? template : string.Format(template, args);
		}
	}
}
