using System;
using System.IO;
using System.Text;
using Base.UI;
using I2.Loc;
using UnityEngine;

namespace ChaosInventory
{
	/// <summary>
	/// Loads this mod's own KEY_CHAOSINVENTORY_* terms (Data/Localization/ChaosInventory.csv, same
	/// column shape ChaosFaction's own CSV uses - Key,Type,Desc,English,...) into I2 Loc, so
	/// player-facing text (context menu rows, per-slot hints, the Air Force tab's cargo filter/
	/// summary text) goes through the normal LocalizedTextBind/.Localize pipeline instead of
	/// hardcoded English string literals - explicit user request: "Add localization to the project
	/// for all languages," pointed at ChaosFaction.csv/ChaosFactionLocalization.cs as the pattern to
	/// follow. Debug-only text (every Log.Debug call in this mod) is deliberately left as plain C#
	/// string literals, matching ChaosFaction's own convention - none of it is ever shown to the
	/// player, only written to Player.log for troubleshooting.
	///
	/// Every key here is brand new and exclusively owned by this mod (the "KEY_CHAOSINVENTORY_"
	/// prefix guarantees no collision with vanilla or any other mod's own terms), so - same
	/// reasoning as ChaosFactionLocalization - the simple, direct approach is both correct and
	/// safe: parse this mod's CSV into its own dedicated LanguageSourceData and register that
	/// source directly with I2.Loc.LocalizationManager.Sources. Unlike ChaosUkrainian's own
	/// loader (a fundamentally different job - overriding translations for keys some OTHER system
	/// already owns), there's no need to defer/retry writes into a shared source here.
	/// </summary>
	internal static class ChaosInventoryLocalization
	{
		private const string CsvFileName = "ChaosInventory.csv";

		private static bool _attempted;

		/// <summary>
		/// Called once from ChaosInventoryMain.OnModEnabled(). A single-attempt, no-retry load
		/// (same reasoning as ChaosFactionLocalization.LoadOwnTranslations): nothing here depends
		/// on any other mod or on load order, so one successful import lasts the whole session, and
		/// a missing/malformed CSV would fail identically on every retry anyway.
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
					Log.Debug("ChaosInventoryLocalization: mod directory unknown; skipping localization CSV import.");
					return;
				}

				string csvPath = Path.Combine(Path.Combine(Path.Combine(modDirectory, "Data"), "Localization"), CsvFileName);
				if (!File.Exists(csvPath))
				{
					Log.Debug($"ChaosInventoryLocalization: no localization CSV found at '{csvPath}'.");
					return;
				}

				// Normalizes line endings before parsing, same as ChaosFactionLocalization/
				// ChaosUkrainianLocalization - LocalizationReader.ParseCSVline only ever treats
				// '\n' as a line terminator, never stripping a trailing '\r', so a CRLF-saved
				// file's last column on each line would otherwise parse with a stray '\r' attached.
				string csvText = File.ReadAllText(csvPath, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");

				LanguageSourceData source = new LanguageSourceData();
				string error = source.Import_CSV(string.Empty, csvText, eSpreadsheetUpdateMode.Replace, ',');
				if (!string.IsNullOrEmpty(error))
				{
					Debug.LogError($"[ChaosInventory] ChaosInventoryLocalization: failed to import '{csvPath}': {error}");
					return;
				}

				LocalizationManager.Sources.Add(source);
				Log.Debug($"ChaosInventoryLocalization: imported {source.mTerms.Count} localization term(s) from '{csvPath}'.");
			}
			catch (Exception ex)
			{
				Debug.LogError("[ChaosInventory] ChaosInventoryLocalization: failed to load localization CSV: " + ex);
			}
		}

		/// <summary>
		/// Looks up a KEY_CHAOSINVENTORY_* term and formats it with the given args - mirrors
		/// ChaosFactionLocalization.Get exactly (string.Format(someLocalizedTextBind.Localize(null),
		/// args)) rather than I2 Loc's own GameObject-bound parameter system, which needs live scene
		/// binding this mod's plain-string call sites (context menu labels, tooltip text, etc.)
		/// don't have. Falls back to the raw key itself if the term can't be found at all (e.g. the
		/// CSV failed to load) so a missing translation reads as an obviously-wrong key in-game
		/// rather than silently vanishing or throwing.
		/// </summary>
		internal static string Get(string key, params object[] args)
		{
			string template = new LocalizedTextBind(key).Localize(null);
			if (string.IsNullOrEmpty(template))
				return key;
			return args == null || args.Length == 0 ? template : string.Format(template, args);
		}
	}
}
