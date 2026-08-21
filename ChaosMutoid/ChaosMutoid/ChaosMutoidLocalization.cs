using System;
using System.IO;
using System.Text;
using Base.UI;
using I2.Loc;
using UnityEngine;

namespace ChaosMutoid
{
	/// <summary>
	/// Loads this mod's own KEY_CHAOSMUTOID_* terms (Data/Localization/ChaosMutoid.csv, same
	/// column shape as ChaosFaction's own ChaosFactionLocalization.cs / ChaosUkrainian's
	/// I2Languages exports: Key,Type,Desc,English, ...) into I2 Loc, so player-facing text goes
	/// through the normal LocalizedTextBind/.Localize pipeline instead of a hardcoded English
	/// string literal.
	///
	/// Every key here is brand new and exclusively owned by this mod (the "KEY_CHAOSMUTOID_"
	/// prefix guarantees no collision with vanilla or any other mod's own terms), so - same as
	/// ChaosFactionLocalization - this can parse the CSV into its own dedicated
	/// LanguageSourceData and register that source directly with I2.Loc.LocalizationManager.
	/// Sources; no need to defer/retry against a shared source someone else might own, unlike
	/// ChaosUkrainian's own loader (which overrides vanilla/TFTV-owned terms).
	/// </summary>
	internal static class ChaosMutoidLocalization
	{
		private const string CsvFileName = "ChaosMutoid.csv";

		private static bool _attempted;

		/// <summary>
		/// Called once from ChaosMutoidMain.OnModEnabled(). Single-attempt, no-retry load -
		/// nothing here depends on any other mod or on load order, so if this succeeds once
		/// it's done for the whole session.
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
					Log.Debug("[ChaosMutoid] ChaosMutoidLocalization: mod directory unknown; skipping localization CSV import.");
					return;
				}

				string csvPath = Path.Combine(Path.Combine(Path.Combine(modDirectory, "Data"), "Localization"), CsvFileName);
				if (!File.Exists(csvPath))
				{
					Log.Debug($"[ChaosMutoid] ChaosMutoidLocalization: no localization CSV found at '{csvPath}'.");
					return;
				}

				// Normalizes line endings before parsing, since LocalizationReader.ParseCSVline
				// only ever treats '\n' as a line terminator, never stripping a trailing '\r' -
				// a CRLF-saved file's last column on each line would otherwise parse with a
				// stray trailing '\r' attached.
				string csvText = File.ReadAllText(csvPath, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");

				LanguageSourceData source = new LanguageSourceData();
				string error = source.Import_CSV(string.Empty, csvText, eSpreadsheetUpdateMode.Replace, ',');
				if (!string.IsNullOrEmpty(error))
				{
					Debug.LogError($"[ChaosMutoid] ChaosMutoidLocalization: failed to import '{csvPath}': {error}");
					return;
				}

				LocalizationManager.Sources.Add(source);
				Log.Debug($"[ChaosMutoid] ChaosMutoidLocalization: imported {source.mTerms.Count} localization term(s) from '{csvPath}'.");
			}
			catch (Exception ex)
			{
				Debug.LogError("[ChaosMutoid] ChaosMutoidLocalization: failed to load localization CSV: " + ex);
			}
		}

		/// <summary>
		/// Looks up a KEY_CHAOSMUTOID_* term and formats it with the given args.
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
