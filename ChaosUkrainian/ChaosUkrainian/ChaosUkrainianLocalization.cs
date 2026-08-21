using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Base.Core;
using Base.Defs;
using Base.Utils.GameConsole;
using I2.Loc;
using I2.Localization.Scripts;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Modding;
using UnityEngine;

namespace ChaosUkrainian
{
	/// <summary>
	/// Registers "Ukrainian" as a selectable language with I2 Loc and the Options screen, and
	/// imports any translation CSVs shipped in this mod's own Data/Localization folder.
	/// Keys with no Ukrainian text (either untranslated categories, or missing rows within a
	/// translated category) fall back to English automatically via I2 Loc's own per-source
	/// OnMissingTranslation=Fallback mechanism, left at its engine default on every source
	/// (see EnsureLanguageRegistered) - including the one this mod actually writes into, so a
	/// genuine gap in any of the OTHER 8 languages still falls back exactly as it did before
	/// this mod existed. The one risk that setup would otherwise carry - Ukrainian text
	/// leaking in as "the fallback" for some other active language - is prevented directly at
	/// the fallback-candidate level instead, see LanguageSourceData_TryGetFallbackTranslation_
	/// Patch in ChaosUkrainianPatches.
	/// </summary>
	/// <remarks>
	/// LanguageName is deliberately the English word "Ukrainian", not the native "Українська",
	/// even though every other base-game language is registered under its own native/English
	/// name (e.g. "Deutsch" isn't used - it's "German"). The reason is a quirk in I2 Loc's own
	/// Import_CSV (I2/Loc/LanguageSourceData.cs): SetupLanguages resolves a CSV header to an
	/// already-registered language BY CODE (via GoogleLanguages, so "Ukrainian" and
	/// "Українська" would both resolve to code "uk" and correctly avoid creating a duplicate
	/// language) - but the very next step, which decides which column index actually gets
	/// written into each TermData, re-resolves the SAME header text through GetLanguageIndex,
	/// which only matches by exact Name (no code fallback, no fuzzy match across alphabets).
	/// So the header text must equal LanguageName exactly, or every import throws
	/// IndexOutOfRangeException trying to write into language index -1. "Ukrainian" was chosen
	/// (over keeping "Українська") because it's also what GoogleLanguages' own name/code table
	/// recognizes, which is what makes chaos_export_localization's own CSV header (and the
	/// vanilla export_localization command's) read "Ukrainian" instead of "Українська [uk]" -
	/// see GetCodedLanguage in I2/Loc/GoogleLanguages.cs. None of this affects what a player
	/// actually sees: the language dropdown never renders LanguageName as text at all (see
	/// EnsureLanguageOption/LoadLanguageLabelIcon below - it only ever shows Data/Icons/
	/// Ukraine.png, a separate pre-rendered image of the word "Українська").
	/// </remarks>
	internal static class ChaosUkrainianLocalization
	{
		internal const string LanguageName = "Ukrainian";
		internal const string LanguageCode = "uk";

		/// <summary>
		/// Name of the manifest that gates and orders Data/Localization's top-level
		/// subdirectories (see LoadSourcesConfig/ImportShippedTranslations below). Lives
		/// alongside those subdirectories rather than under the mod's C# source so a
		/// translator can add/reorder a source without touching the DLL.
		/// </summary>
		private const string SourcesConfigFileName = "localization-sources.json";

		/// <summary>
		/// Parsed once per session (config content never changes at runtime, mirroring
		/// modDirectory in ImportShippedTranslations). Null until the first successful parse
		/// attempt; a parse FAILURE also leaves this null so every call keeps retrying (cheap)
		/// rather than permanently wedging the mod into "no translations imported" over one
		/// transient error - Import_CSV itself is the only genuinely expensive step, and that's
		/// separately guarded per-directory by _importedDirectories.
		/// </summary>
		private static LocalizationSourcesConfig _sourcesConfig;

		/// <summary>
		/// Directory names (relative to Data/Localization, e.g. "PhoenixPoint", "TFTV") already
		/// merged into the overlay source this session. Deliberately NOT a single "did we ever
		/// import" flag (unlike the old _translationsImported bool this replaces): a gated
		/// directory whose required mod isn't enabled yet is simply left out of this set, so a
		/// later call - this method already runs on every Options-panel Init and every
		/// ChangeLanguageTo, see ChaosUkrainianPatches - re-checks it and imports it the moment
		/// that mod becomes enabled later in the same session, regardless of which of the two
		/// mods the player enabled first.
		/// </summary>
		private static readonly HashSet<string> _importedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Shape of localization-sources.json. Public fields (not properties) so Newtonsoft.Json
		/// can populate them via reflection without needing [JsonProperty] attributes - its
		/// default contract resolver binds "directory"/"requiredModId" to Directory/RequiredModId
		/// case-insensitively.
		/// </summary>
		private sealed class LocalizationSourcesConfig
		{
			public LocalizationSourceEntry[] Sources = new LocalizationSourceEntry[0];
		}

		/// <summary>
		/// One Data/Localization subdirectory to import. Default load order is the manifest's
		/// own array order, but LoadAfterDirectory (another entry's Directory value) overrides
		/// that explicitly for that entry - see ResolveLoadOrder - so "which source wins a key
		/// collision" is stated directly on the entry that needs to win, rather than depending on
		/// whoever edits this file later preserving array position. RequiredModId null/empty
		/// means "always eligible" (e.g. this mod's own PhoenixPoint/ directory); otherwise the
		/// directory is skipped entirely unless ModManager reports a mod with that exact ID as
		/// Enabled (see IsModEnabled). UseFileNameAsCategory (default true) controls whether each
		/// CSV's filename becomes the I2 Loc Category prefix on import - see ImportCsvDirectory
		/// for why this must be false for TFTV.
		/// </summary>
		private sealed class LocalizationSourceEntry
		{
			public string Directory = string.Empty;
			public string RequiredModId = string.Empty;
			public string LoadAfterDirectory = string.Empty;
			public bool UseFileNameAsCategory = true;
		}

		/// <summary>
		/// Idempotent: LanguageSourceData.AddLanguage no-ops if the language already exists.
		/// Called from multiple patch points since load order between mod init and the
		/// game's own I2 Loc/Options bootstrapping isn't guaranteed.
		/// </summary>
		internal static void EnsureLanguageRegistered()
		{
			/// ImportShippedTranslations only ever writes into this exact source (Sources[0],
			/// see there) - every PhoenixPoint/TFTV key it translates already lives in a
			/// DIFFERENT source (export_localization itself only ever reads Sources.Skip(1)),
			/// so Import_CSV on Sources[0] almost always creates a brand-new TermData there
			/// with every language empty except Ukrainian's, shadowing the real, complete term
			/// in whichever other source actually owns it (checked later in
			/// LocalizationManager.Sources order, since Sources[0] is checked first). This
			/// USED to be handled by forcing Sources[0].OnMissingTranslation=Empty so that
			/// shadow-term could never resolve for a non-Ukrainian language and would
			/// correctly fall through - but that also broke this source's OWN legitimate
			/// Fallback behavior for the terms it genuinely and solely owns (e.g. a base-game
			/// key with a real official gap in, say, Polish, which should fall back to English
			/// from right here). Every source - including this one - now keeps the engine's
			/// own default (Fallback), and the shadow-term leak is instead prevented at its
			/// actual source: see LanguageSourceData_TryGetFallbackTranslation_Patch in
			/// ChaosUkrainianPatches, which excludes Ukrainian from ever being offered as
			/// another language's fallback candidate, everywhere, rather than disabling
			/// Fallback for an entire source.
			int sourceIndex = 0;
			foreach (LanguageSourceData source in LocalizationManager.Sources)
			{
				int indexBefore = source.GetLanguageIndex(LanguageName, false, false);
				source.AddLanguage(LanguageName, LanguageCode);
				int indexAfter = source.GetLanguageIndex(LanguageName, false, false);
				if (indexBefore < 0)
				{
					Debug.Log($"ChaosUkrainian: registered '{LanguageName}' on source #{sourceIndex} at index {indexAfter} (languages now: {source.GetLanguages(false).Count}), OnMissingTranslation={source.OnMissingTranslation}.");
				}
				ResizeUndersizedTerms(source, sourceIndex);
				sourceIndex++;
			}
		}

		/// <summary>
		/// AddLanguage only resizes the TermData.Languages/Flags arrays that existed in
		/// mTerms at the exact moment it runs (I2/Loc/LanguageSourceData.cs). Anything that
		/// creates a TermData later with a fixed-size array matching the OLD language count -
		/// e.g. other Harmony-patching mods, or game systems that build terms on the fly
		/// without knowing a language got added at runtime - ends up with an array too short
		/// to hold Ukrainian's slot, and Import_CSV's TermData.SetTranslation(idx, ...) throws
		/// IndexOutOfRangeException the moment it tries to write there (seen in practice: this
		/// only surfaced on a later re-enable of the mod within the same session, i.e. once
		/// something had a chance to add such a term after the first EnsureLanguageRegistered
		/// call, not on the very first enable at boot). Runs every time - cheap when nothing's
		/// undersized - rather than only right after AddLanguage first adds the language, so
		/// it also protects against terms created between an earlier and a later call.
		/// </summary>
		private static void ResizeUndersizedTerms(LanguageSourceData source, int sourceIndex)
		{
			int expectedLength = source.mLanguages.Count;
			int resizedCount = 0;
			foreach (TermData term in source.mTerms)
			{
				if (term.Languages.Length < expectedLength)
				{
					Array.Resize(ref term.Languages, expectedLength);
					resizedCount++;
				}
				if (term.Flags.Length < expectedLength)
				{
					Array.Resize(ref term.Flags, expectedLength);
				}
			}
			if (resizedCount > 0)
			{
				Debug.LogWarning($"ChaosUkrainian: resized {resizedCount} undersized term(s) on source #{sourceIndex} to {expectedLength} language slots.");
			}
		}

		private static bool _locSourcesDiagnosticsLogged;

		/// <summary>
		/// Diagnostic only (not part of normal operation): LocalizationManager.FindLanguageSources()
		/// (I2/Loc/LocalizationManager.cs) only ever reads
		/// DefRepository.GetAllDefs&lt;LocSourcesDef&gt;().FirstOrDefault() - i.e. if the build
		/// actually ships more than one LocSourcesDef (e.g. one per DLC content pack), every
		/// LanguageSourceAsset referenced by any Def other than the first one is silently never
		/// registered into LocalizationManager.Sources, for the entire session. Logs every
		/// LocSourcesDef DefRepository actually knows about and whether each of its
		/// LanguageSourceAsset entries made it into Sources, so a Player.log capture can confirm
		/// or rule out that theory without needing any asset-extraction tooling.
		/// </summary>
		internal static void LogLocSourcesDefsForDiagnostics()
		{
			if (_locSourcesDiagnosticsLogged)
			{
				return;
			}
			_locSourcesDiagnosticsLogged = true;
			try
			{
				DefRepository repo = GameUtl.GameComponent<DefRepository>();
				if (repo == null)
				{
					Debug.LogWarning("ChaosUkrainian: DefRepository not found, skipping LocSourcesDef diagnostics.");
					return;
				}
				var defs = repo.GetAllDefs<LocSourcesDef>().ToList();
				Debug.Log($"ChaosUkrainian: [diag] found {defs.Count} LocSourcesDef instance(s) in DefRepository.");
				foreach (LocSourcesDef def in defs)
				{
					LanguageSourceAsset[] assets = def.LanguageSources ?? Array.Empty<LanguageSourceAsset>();
					Debug.Log($"ChaosUkrainian: [diag] LocSourcesDef '{def.name}' (guid={def.Guid}) has {assets.Length} LanguageSourceAsset(s).");
					foreach (LanguageSourceAsset asset in assets)
					{
						if (asset == null)
						{
							Debug.Log("ChaosUkrainian: [diag]   - <null asset reference>");
							continue;
						}
						bool alreadyRegistered = LocalizationManager.Sources.Contains(asset.SourceData);
						Debug.Log($"ChaosUkrainian: [diag]   - '{asset.name}', alreadyRegistered={alreadyRegistered}.");
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: LocSourcesDef diagnostics failed: {ex}");
			}
		}

		/// <summary>
		/// OptionsManager.InitLanguage() (the game's own boot-time bootstrap) tries to restore
		/// the player's saved language choice before any mod - including this one - has been
		/// discovered or enabled, let alone had a chance to register "Українська". If the
		/// saved choice was Ukrainian, that restore attempt fails with its own
		/// "Language uk not supported!" LogError and the game silently starts in whatever
		/// language it falls back to instead (English, in practice). Call this once this mod
		/// has finished registering the language, so a genuinely-saved Ukrainian choice still
		/// takes effect this session instead of silently reverting until the player manually
		/// re-selects it.
		/// </summary>
		internal static void RetryStartupLanguageIfNeeded()
		{
			if (LocalizationManager.CurrentLanguageCode == LanguageCode)
			{
				return;
			}
			OptionsManager optionsManager = GameUtl.GameComponent<OptionsManager>();
			OptionsManagerDef def = optionsManager?.OptionsManagerDef;
			if (optionsManager?.OptionsComponent == null || def == null)
			{
				return;
			}
			string savedCode = optionsManager.OptionsComponent.Options.Get<string>(def.CurrentLanguageOption, string.Empty);
			if (string.IsNullOrEmpty(savedCode))
			{
				savedCode = PersistentStorage.GetSetting_String("I2 Language", string.Empty);
			}
			if (string.Equals(savedCode, LanguageCode, StringComparison.OrdinalIgnoreCase))
			{
				Debug.Log($"ChaosUkrainian: saved language '{savedCode}' failed to restore at boot (registered too late) - retrying now.");
				optionsManager.ChangeLanguageTo(LanguageCode);
			}
		}

		/// <summary>
		/// Reads "&lt;modDirectory&gt;/Data/Localization/localization-sources.json" and merges
		/// each configured, currently-eligible subdirectory's *.csv files into the global I2 Loc
		/// source, in the manifest's own order, mirroring how OptionsManager.InitLanguage() loads
		/// the game's own StreamingAssets/Localization override folder (same file format: see
		/// export_localization/!Readme.txt). File name (minus extension) is the term category,
		/// e.g. "Mods.csv" only fills in keys that belong to the "Mods" category; a "#"-prefixed
		/// file name means "no category, key column already contains Category/Key". A source
		/// entry with a RequiredModId is skipped entirely (no I2 Loc terms touched at all) unless
		/// that mod is currently enabled per ModManager - see IsModEnabled - which is how TFTV's
		/// translations stay gated behind the third-party TFTV mod itself rather than always
		/// loading. Safe to call every time (idempotent per-directory via _importedDirectories,
		/// not a single one-shot flag), which matters because this already runs on every
		/// Options-panel Init and ChangeLanguageTo (see ChaosUkrainianPatches): that means a mod
		/// enabled mid-session - in either order relative to this one - gets its directory
		/// imported the next time either of those fires, without needing a restart.
		/// </summary>
		internal static void ImportShippedTranslations(string modDirectory)
		{
			ImportEligibleSources(modDirectory, entry => IsModEnabled(entry.RequiredModId));
		}

		/// <summary>
		/// Proactively imports any configured source directory whose RequiredModId matches
		/// `modId`, bypassing the normal ModManager.Enabled gate (IsModEnabled) entirely. Called
		/// from a Harmony PREFIX on ModManager.TryEnableMod (see ChaosUkrainianPatches) - i.e.
		/// before the target mod's own OnModEnabled has run, and therefore before
		/// ModEntry.Enabled actually flips to true, unlike ImportShippedTranslations's own gate,
		/// which would still see this mod as disabled at this exact moment and skip it. This
		/// closes a real gap observed with TFTV: enabling it runs TFTV's own OnModEnabled, which
		/// itself populates its base (English) I2 Loc terms and can trigger UI that immediately
		/// queries several of them for display - all synchronously, before our own Postfix on
		/// the same TryEnableMod call gets a chance to run. Without this, those queries land on
		/// the not-yet-imported term for that one frame and get logged as [missing]/briefly
		/// shown in English, even though the exact same directory's import always succeeds a
		/// moment later via the existing Postfix anyway. Safe even if TryEnableMod ultimately
		/// fails (e.g. a missing dependency): the imported translations just sit unused, since
		/// nothing in the game ever creates or displays that mod's own terms without the mod
		/// itself actually ending up enabled.
		/// </summary>
		internal static void ImportTranslationsForRequiredMod(string modDirectory, string modId)
		{
			if (string.IsNullOrEmpty(modId))
			{
				return;
			}
			ImportEligibleSources(modDirectory, entry => string.Equals(entry.RequiredModId, modId, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Shared implementation behind ImportShippedTranslations (gated on ModManager.Enabled,
		/// as normal) and ImportTranslationsForRequiredMod (gated on a specific mod ID
		/// regardless of Enabled, for the TryEnableMod Prefix - see there). `isEligible` is the
		/// only difference between the two call sites; everything else (config loading/caching,
		/// per-directory idempotency via _importedDirectories, missing-directory handling) is
		/// identical.
		/// </summary>
		private static void ImportEligibleSources(string modDirectory, Func<LocalizationSourceEntry, bool> isEligible)
		{
			if (string.IsNullOrEmpty(modDirectory))
			{
				return;
			}
			string localizationDir = Path.Combine(Path.Combine(modDirectory, "Data"), "Localization");
			if (!Directory.Exists(localizationDir))
			{
				Debug.LogWarning($"ChaosUkrainian: localization directory not found: {localizationDir}");
				return;
			}
			if (LocalizationManager.Sources.Count == 0)
			{
				Debug.LogWarning("ChaosUkrainian: no LocalizationManager.Sources available yet, skipping import.");
				return;
			}

			if (_sourcesConfig == null)
			{
				_sourcesConfig = LoadSourcesConfig(Path.Combine(localizationDir, SourcesConfigFileName));
				if (_sourcesConfig?.Sources == null || _sourcesConfig.Sources.Length == 0)
				{
					Debug.LogError($"ChaosUkrainian: '{SourcesConfigFileName}' missing, empty, or unreadable under {localizationDir} - no translations will be imported.");
					_sourcesConfig = null;
					return;
				}
				WarnAboutUnlistedDirectories(localizationDir, _sourcesConfig);
				_sourcesConfig.Sources = ResolveLoadOrder(_sourcesConfig.Sources);
			}

			foreach (LocalizationSourceEntry entry in _sourcesConfig.Sources)
			{
				if (string.IsNullOrEmpty(entry.Directory) || _importedDirectories.Contains(entry.Directory))
				{
					continue;
				}
				if (!isEligible(entry))
				{
					continue;
				}
				string sourceDir = Path.Combine(localizationDir, entry.Directory);
				if (!Directory.Exists(sourceDir))
				{
					Debug.LogWarning($"ChaosUkrainian: configured localization source directory not found, will not retry: {sourceDir}");
					_importedDirectories.Add(entry.Directory);
					continue;
				}
				ImportCsvDirectory(sourceDir, entry.Directory, entry.UseFileNameAsCategory);
				_importedDirectories.Add(entry.Directory);
			}
			ReapplyAllTranslations();
		}

		/// <summary>
		/// One shipped CSV row (term + Ukrainian text), kept permanently once parsed - see
		/// _allTranslations for why this is never discarded even after a successful apply.
		/// </summary>
		private sealed class ShippedTranslation
		{
			public string Term = string.Empty;
			public string UkrainianText = string.Empty;
		}

		/// <summary>
		/// Every (term, Ukrainian text) row ever parsed from a shipped CSV this session,
		/// regardless of whether TryApplyTranslation already succeeded for it - reapplied in
		/// full on every subsequent ImportEligibleSources call (see ReapplyAllTranslations)
		/// instead of only rechecking terms that previously failed to find any source.
		/// A term can be found and successfully written to a real source's TermData on one call,
		/// then have that exact TermData stop being the one GetTermData/GetTranslation actually
		/// resolve to later, without ever becoming literally unreachable (so nothing here can
		/// detect the gap by re-checking "did this fail before"). Confirmed via Player.log for
		/// TFTV's own base-game overrides (e.g. "DLC4 Hypnos/KEY_DAMAGE_TYPE_CORRUPTION",
		/// Corruption/Delirium content that exists in vanilla Phoenix Point and that TFTV
		/// re-flavors): PhoenixPoint/'s import (during this mod's own OnModEnabled) applies
		/// Ukrainian to it successfully - source #0 ("I2Languages_Mod", one of the 7 real
		/// base-game LanguageSourceData objects, confirmed by LogLocSourcesDefsForDiagnostics)
		/// already has the term at that point. TFTV's own OnModEnabled then runs (after this
		/// mod's ModManager.TryEnableMod Prefix, before its Postfix) and registers its own English/
		/// Russian override text for many of the exact same term strings straight into that same
		/// source #0 (confirmed by the very next ResizeUndersizedTerms warning logging thousands
		/// of newly-undersized terms on source #0 right after TFTV's OnModEnabled) - for terms
		/// that already existed, this either replaces or duplicates the TermData GetTermData
		/// resolves to, orphaning whatever this mod already wrote into the old one. A once-per-
		/// session pending-only retry list can't catch this (the term wasn't pending - it already
		/// "succeeded"); always reapplying every known row on every check is the only reliable
		/// fix, since it re-resolves GetTermData fresh each time instead of trusting a stale
		/// success flag. Cheap in practice: this is only walked on Options-panel Init /
		/// ChangeLanguageTo / ModManager.TryEnableMod, not a hot path, and TryApplyTranslation is
		/// a no-op write when the right TermData already has the right text.
		/// </summary>
		private static readonly List<ShippedTranslation> _allTranslations = new List<ShippedTranslation>();

		/// <summary>
		/// Imports every *.csv under one configured source directory (recursive, in case a
		/// source ever grows subfolders of its own). Extracted from ImportShippedTranslations so
		/// each directory's import is independently retriable/skippable per
		/// entry.RequiredModId's eligibility, rather than one all-or-nothing pass over the whole
		/// Data/Localization tree.
		/// </summary>
		/// <remarks>
		/// Each CSV is parsed into a throwaway, unregistered LanguageSourceData ("scratch") via
		/// its own Import_CSV, purely to reuse I2 Loc's own CSV parsing (quoting, multi-line
		/// fields, "[touch]"-style specializations) without reimplementing it - scratch is never
		/// added to LocalizationManager.Sources and is discarded after this method returns.
		/// Each parsed (term, Ukrainian text) pair is then applied via TryApplyTranslation
		/// instead of importing directly into a real source (as this used to do, calling
		/// `source.Import_CSV(...)` on Sources[0] directly): Import_CSV's own behavior, when a
		/// term doesn't already exist in the target source, is to CREATE it there - and for
		/// PhoenixPoint/TFTV content that a third-party system (confirmed with TFTV: its own
		/// custom-game-options screen) registers lazily at runtime rather than having it baked in
		/// at build time, creating that term ourselves first, before the real owner does, means
		/// the real owner's own "does this term already exist?" check (a completely ordinary
		/// defensive pattern, not a bug on TFTV's end) sees our premature stub and skips its own
		/// registration entirely - leaving the term with ONLY Ukrainian populated, forever, in
		/// every OTHER language, which is exactly the bug reported (TFTV text blank in every
		/// language but Ukrainian, and only when this mod's TFTV translations were present -
		/// confirmed by removing them and seeing every other language work correctly again).
		/// Deferring instead (TryApplyTranslation/ShippedTranslation/ReapplyAllTranslations) means
		/// this mod only ever adds Ukrainian onto a term some other system already created, never
		/// the reverse.
		/// </remarks>
		private static void ImportCsvDirectory(string sourceDir, string directoryLabel, bool useFileNameAsCategory)
		{
			string[] csvPaths = Directory.GetFiles(sourceDir, "*.csv", SearchOption.AllDirectories);
			Debug.Log($"ChaosUkrainian: importing {csvPaths.Length} translation file(s) from '{directoryLabel}' ({sourceDir}) (CurrentLanguage={LocalizationManager.CurrentLanguage}, useFileNameAsCategory={useFileNameAsCategory}).");
			int importedCount = 0;
			int deferredCount = 0;
			foreach (string csvPath in csvPaths)
			{
				string category = useFileNameAsCategory ? Path.GetFileNameWithoutExtension(csvPath) : string.Empty;
				if (category.StartsWith("#"))
				{
					category = string.Empty;
				}
				try
				{
					/// LocalizationReader.ParseCSVline (I2/Loc/LocalizationReader.cs) only ever
					/// treats '\n' as a line terminator - it never strips a trailing '\r'. A
					/// CRLF-saved file's header row therefore parses its last column as
					/// "Ukrainian\r", which SetupLanguages can't resolve to our already-registered
					/// language (via GoogleLanguages' name/code lookup - see the shipped CSVs'
					/// "Key,Ukrainian" header), so it treats it as a brand-new language and
					/// creates a duplicate entry at a new index - which SetTranslation then throws
					/// IndexOutOfRangeException trying to write into on any pre-existing term.
					/// Normalizing here makes every import immune to this regardless of which line
					/// ending an editor happens to save with.
					string csvText = File.ReadAllText(csvPath, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
					LanguageSourceData scratch = new LanguageSourceData();
					string error = scratch.Import_CSV(category, csvText, eSpreadsheetUpdateMode.Merge, ',');
					if (!string.IsNullOrEmpty(error))
					{
						Debug.LogError($"ChaosUkrainian: failed to import '{csvPath}': {error}");
						continue;
					}
					int scratchUkrainianIndex = scratch.GetLanguageIndex(LanguageName, false, false);
					if (scratchUkrainianIndex < 0)
					{
						Debug.LogError($"ChaosUkrainian: '{csvPath}' has no '{LanguageName}' column after parsing, skipping.");
						continue;
					}
					foreach (TermData scratchTerm in scratch.mTerms)
					{
						string text = scratchTerm.GetTranslation(scratchUkrainianIndex, null, true);
						if (string.IsNullOrEmpty(text))
						{
							continue;
						}
						_allTranslations.Add(new ShippedTranslation { Term = scratchTerm.Term, UkrainianText = text });
						if (TryApplyTranslation(scratchTerm.Term, text))
						{
							importedCount++;
						}
						else
						{
							deferredCount++;
						}
					}
				}
				catch (Exception ex)
				{
					/// A single malformed/unexpected file must never abort the whole import
					/// (or, via the exception propagating out of OnModEnabled, the whole mod
					/// enable) - that would silently drop every file after it too. See
					/// ResizeUndersizedTerms above for one concrete cause already fixed;
					/// this catch is the safety net for that class of bug in general.
					Debug.LogError($"ChaosUkrainian: exception importing '{csvPath}' (category '{category}'), skipping this file: {ex}");
				}
			}
			Debug.Log($"ChaosUkrainian: import of '{directoryLabel}' finished - {importedCount} term(s) applied, {deferredCount} deferred (term not registered by its real owner yet) across {csvPaths.Length} file(s).");
		}

		/// <summary>
		/// Applies `ukrainianText` to `term`'s Ukrainian slot in whichever real (i.e. actually in
		/// LocalizationManager.Sources) source already contains that term, resizing its
		/// Languages/Flags arrays first if needed (same undersized-array situation
		/// ResizeUndersizedTerms guards against elsewhere). Never creates a new term in any
		/// source - see ImportCsvDirectory's remarks for why. Returns false (having changed
		/// nothing) if no source currently has this term.
		/// </summary>
		private static bool TryApplyTranslation(string term, string ukrainianText)
		{
			foreach (LanguageSourceData realSource in LocalizationManager.Sources)
			{
				TermData termData = realSource.GetTermData(term, true);
				if (termData == null)
				{
					continue;
				}
				int ukrainianIndex = realSource.GetLanguageIndex(LanguageName, false, false);
				if (ukrainianIndex < 0)
				{
					continue;
				}
				if (termData.Languages.Length <= ukrainianIndex)
				{
					Array.Resize(ref termData.Languages, realSource.mLanguages.Count);
				}
				if (termData.Flags.Length <= ukrainianIndex)
				{
					Array.Resize(ref termData.Flags, realSource.mLanguages.Count);
				}
				termData.SetTranslation(ukrainianIndex, ukrainianText);
				return true;
			}
			return false;
		}

		/// <summary>
		/// Reapplies every shipped translation row ever parsed (see _allTranslations for why this
		/// covers already-applied rows too, not just previously-unresolved ones) against the
		/// current LocalizationManager.Sources. Called at the end of ImportEligibleSources, so it
		/// runs on every existing trigger point (Options-panel Init, ChangeLanguageTo,
		/// ModManager.TryEnableMod pre/post) without needing its own hook.
		/// </summary>
		private static void ReapplyAllTranslations()
		{
			int resolvedCount = 0;
			foreach (ShippedTranslation translation in _allTranslations)
			{
				if (TryApplyTranslation(translation.Term, translation.UkrainianText))
				{
					resolvedCount++;
				}
			}
			Debug.Log($"ChaosUkrainian: reapplied translations - {resolvedCount}/{_allTranslations.Count} term(s) currently resolved.");
		}

		/// <summary>
		/// True if `modId` is null/empty (an ungated source, e.g. PhoenixPoint's own directory)
		/// or if ModManager currently reports a mod with that ID as Enabled. Re-run on every
		/// ImportShippedTranslations call rather than cached, since the whole point is picking up
		/// a mod the player enables mid-session (see ImportShippedTranslations) - this call is
		/// cheap (a linear scan of the discovered-mods list) compared to the actual Import_CSV
		/// work it gates.
		/// </summary>
		private static bool IsModEnabled(string modId)
		{
			if (string.IsNullOrEmpty(modId))
			{
				return true;
			}
			ModManager modManager = ModManager.GetInstance();
			ModEntry entry = modManager?.Mods.FirstOrDefault(m => string.Equals(m.ID, modId, StringComparison.OrdinalIgnoreCase));
			return entry != null && entry.Enabled;
		}

		/// <summary>
		/// Diagnostic only: warns (once, right after a fresh config parse) about any top-level
		/// Data/Localization subdirectory that isn't listed in localization-sources.json, so a
		/// translator who adds a new subfolder but forgets to register it gets an obvious log
		/// line instead of silently-never-imported translations.
		/// </summary>
		private static void WarnAboutUnlistedDirectories(string localizationDir, LocalizationSourcesConfig config)
		{
			try
			{
				HashSet<string> configured = new HashSet<string>(
					config.Sources.Select(s => s.Directory ?? string.Empty),
					StringComparer.OrdinalIgnoreCase);
				foreach (string dir in Directory.GetDirectories(localizationDir, "*", SearchOption.TopDirectoryOnly))
				{
					string name = Path.GetFileName(dir);
					if (!configured.Contains(name))
					{
						Debug.LogWarning($"ChaosUkrainian: localization directory '{name}' is not listed in {SourcesConfigFileName} and will not be imported.");
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: failed to scan for unlisted localization directories: {ex}");
			}
		}

		/// <summary>
		/// Reorders `entries` so each one comes after whatever its LoadAfterDirectory names,
		/// falling back to plain declaration order for entries that don't set it (so an existing
		/// config with no LoadAfterDirectory fields at all keeps behaving exactly as before this
		/// existed). A depth-first topological sort: visiting an entry first recursively visits
		/// (and thus places) whatever it must load after, then appends itself, so the earliest
		/// dependency in a chain ends up first. `visited` makes repeat visits (an entry reachable
		/// from more than one place, or the top-level loop revisiting something a deeper call
		/// already placed) a no-op instead of a duplicate. `visiting` catches a cycle (A after B,
		/// B after A, or a longer loop): rather than recursing forever, it logs the chain once and
		/// returns immediately without placing that entry a second time - the entry that closes
		/// the cycle just falls back to its position from the unwinding call instead, which is a
		/// safe, deterministic (if not perfectly meaningful) order rather than a hang or a crash.
		/// A LoadAfterDirectory naming a directory with no matching entry, or itself, is likewise
		/// logged and ignored rather than treated as fatal - a typo in this file should degrade to
		/// "that one ordering hint is ignored", not "no translations load at all".
		/// </summary>
		private static LocalizationSourceEntry[] ResolveLoadOrder(LocalizationSourceEntry[] entries)
		{
			Dictionary<string, LocalizationSourceEntry> byDirectory = new Dictionary<string, LocalizationSourceEntry>(StringComparer.OrdinalIgnoreCase);
			foreach (LocalizationSourceEntry entry in entries)
			{
				if (!string.IsNullOrEmpty(entry.Directory) && !byDirectory.ContainsKey(entry.Directory))
				{
					byDirectory[entry.Directory] = entry;
				}
			}

			List<LocalizationSourceEntry> resolved = new List<LocalizationSourceEntry>(entries.Length);
			HashSet<LocalizationSourceEntry> visited = new HashSet<LocalizationSourceEntry>();
			HashSet<LocalizationSourceEntry> visiting = new HashSet<LocalizationSourceEntry>();

			void Visit(LocalizationSourceEntry entry, List<string> chain)
			{
				if (visited.Contains(entry))
				{
					return;
				}
				if (visiting.Contains(entry))
				{
					Debug.LogError($"ChaosUkrainian: cyclic LoadAfterDirectory chain ({string.Join(" -> ", chain)} -> {entry.Directory}) in {SourcesConfigFileName} - ignoring the dependency that closes the loop.");
					return;
				}
				if (!string.IsNullOrEmpty(entry.LoadAfterDirectory))
				{
					if (string.Equals(entry.LoadAfterDirectory, entry.Directory, StringComparison.OrdinalIgnoreCase))
					{
						Debug.LogWarning($"ChaosUkrainian: source '{entry.Directory}' has LoadAfterDirectory pointing to itself in {SourcesConfigFileName} - ignoring.");
					}
					else if (byDirectory.TryGetValue(entry.LoadAfterDirectory, out LocalizationSourceEntry dependsOn))
					{
						visiting.Add(entry);
						chain.Add(entry.Directory);
						Visit(dependsOn, chain);
						chain.RemoveAt(chain.Count - 1);
						visiting.Remove(entry);
					}
					else
					{
						Debug.LogWarning($"ChaosUkrainian: source '{entry.Directory}' has LoadAfterDirectory '{entry.LoadAfterDirectory}', which isn't a configured source in {SourcesConfigFileName} - ignoring.");
					}
				}
				visited.Add(entry);
				resolved.Add(entry);
			}

			foreach (LocalizationSourceEntry entry in entries)
			{
				Visit(entry, new List<string>());
			}
			return resolved.ToArray();
		}

		/// <summary>
		/// Deserializes localization-sources.json via Newtonsoft.Json reached through
		/// reflection rather than a compile-time reference: ModSDK only ships 0Harmony/
		/// Assembly-CSharp/UnityEngine.CoreModule (see ChaosUkrainian.csproj), not
		/// Newtonsoft.Json.dll, but the game process already has it loaded - Assembly-CSharp's
		/// own ModMeta.FromDir uses JsonConvert.DeserializeObject to parse every mod's
		/// meta.json - so the type is resolvable at runtime by name. Same pattern as
		/// LoadLanguageLabelIcon below reaching UnityEngine.ImageConversion for the same reason.
		/// Returns null on any failure (missing file, malformed JSON, reflection failure) so the
		/// caller can log one clear error instead of a partially-populated config silently
		/// importing the wrong things.
		/// </summary>
		private static LocalizationSourcesConfig LoadSourcesConfig(string configPath)
		{
			if (!File.Exists(configPath))
			{
				Debug.LogError($"ChaosUkrainian: localization sources config not found: {configPath}");
				return null;
			}
			try
			{
				string json = File.ReadAllText(configPath, Encoding.UTF8);
				Type jsonConvertType = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json");
				MethodInfo deserializeMethod = jsonConvertType?.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) });
				if (deserializeMethod == null)
				{
					Debug.LogError("ChaosUkrainian: Newtonsoft.Json.JsonConvert not found at runtime, cannot read localization-sources.json.");
					return null;
				}
				return deserializeMethod.Invoke(null, new object[] { json, typeof(LocalizationSourcesConfig) }) as LocalizationSourcesConfig;
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: failed to read localization sources config '{configPath}': {ex}");
				return null;
			}
		}

		internal static void EnsureLanguageOption(OptionsManagerDef optionsManagerDef, string modDirectory)
		{
			if (optionsManagerDef == null || optionsManagerDef.Languages == null)
			{
				return;
			}
			if (optionsManagerDef.Languages.Any(option => option.Key == LanguageName))
			{
				return;
			}
			Debug.Log($"ChaosUkrainian: adding '{LanguageName}' dropdown option (had {optionsManagerDef.Languages.Count} language option(s)).");
			/// The language dropdown identifies entries only by icon sprite (see
			/// UIModuleInterfaceOptionsPanel.SetupLanguageDropdown - it never sets
			/// Dropdown.OptionData.text). The base game's own 8 entries look like plain
			/// text in-game because their "Icon" is itself a pre-rendered image of the
			/// language's name, not a flag - see LoadLanguageLabelIcon below, which does
			/// the same for "Українська" so it matches that style instead of showing a
			/// flag graphic or a blank slot.
			optionsManagerDef.Languages.Add(new OptionsManager.LanguageOption
			{
				Key = LanguageName,
				Icon = LoadLanguageLabelIcon(modDirectory)
			});
		}

		private static bool _labelIconLoadAttempted;
		private static Sprite _labelIcon;

		/// <summary>
		/// Loads Data/Icons/Ukraine.png (a pre-rendered "Українська" text label, matching
		/// how the base game's own language options are presented - see EnsureLanguageOption)
		/// into a Sprite at runtime. ModSDK doesn't ship UnityEngine.ImageConversionModule.dll
		/// (only CoreModule/UI are referenced), so ImageConversion.LoadImage is invoked via
		/// reflection against the copy already loaded in the game process (Assembly-CSharp
		/// itself calls ImageConversion.EncodeToPNG elsewhere, confirming the module is
		/// present at runtime) rather than a compile-time reference. Any failure here just
		/// leaves Icon null (blank slot), same as before - no functional regression.
		/// </summary>
		private static Sprite LoadLanguageLabelIcon(string modDirectory)
		{
			if (_labelIconLoadAttempted)
			{
				return _labelIcon;
			}
			_labelIconLoadAttempted = true;
			if (string.IsNullOrEmpty(modDirectory))
			{
				return null;
			}
			string iconPath = Path.Combine(modDirectory, "Data", "Icons", "Ukraine.png");
			if (!File.Exists(iconPath))
			{
				return null;
			}
			try
			{
				Type imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
				MethodInfo loadImageMethod = imageConversionType?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
				if (loadImageMethod == null)
				{
					return null;
				}
				byte[] bytes = File.ReadAllBytes(iconPath);
				Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
				bool loaded = (bool)loadImageMethod.Invoke(null, new object[] { texture, bytes, false });
				if (!loaded)
				{
					return null;
				}
				_labelIcon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
				return _labelIcon;
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: failed to load language label icon: {ex}");
				return null;
			}
		}

		/// <summary>
		/// Implements the full fallback chain the player configures via
		/// ChaosUkrainianConfig.FallbackLanguage: Ukrainian -> configured fallback -> English.
		/// Resolved explicitly here rather than left entirely to I2 Loc's own engine-default
		/// Fallback mechanism (OnMissingTranslation=Fallback, left at its default on every
		/// source - see EnsureLanguageRegistered): that mechanism just returns whichever
		/// language happens to come first in array order once Ukrainian's own slot is empty,
		/// which may not be the language the player actually configured as their preferred
		/// fallback - so this method re-derives the specific Ukrainian -> configured fallback
		/// -> English order explicitly instead of trusting whatever the engine already picked.
		/// Only called from a Postfix on
		/// LocalizationManager.TryGetTranslation while Ukrainian is the active language (see
		/// ChaosUkrainianPatches), so it never affects any of the base game's own 8 languages.
		/// Returns false (leaving whatever I2 Loc's own Postfix-preceding call already
		/// resolved untouched) only when a genuine Ukrainian translation already exists, or when
		/// neither the configured fallback nor English has anything for this term either.
		/// </summary>
		internal static bool TryResolveConfiguredFallback(string term, out string translation)
		{
			translation = null;
			ChaosUkrainianConfig config = ChaosUkrainianMain.Main?.Config;
			string fallbackLanguage = config != null ? ToI2LanguageName(config.FallbackLanguage) : null;
			bool useConfiguredFallback = !string.IsNullOrEmpty(fallbackLanguage) && fallbackLanguage != LanguageName;

			foreach (LanguageSourceData source in LocalizationManager.Sources)
			{
				TermData termData = source.GetTermData(term, true);
				if (termData == null)
				{
					continue;
				}
				int ukrainianIndex = source.GetLanguageIndex(LanguageName, false, false);
				if (ukrainianIndex >= 0 && !string.IsNullOrEmpty(termData.GetTranslation(ukrainianIndex, null, true)))
				{
					return false;
				}
				if (useConfiguredFallback)
				{
					int fallbackIndex = source.GetLanguageIndex(fallbackLanguage, false, false);
					if (fallbackIndex >= 0)
					{
						string fallbackText = termData.GetTranslation(fallbackIndex, null, true);
						if (!string.IsNullOrEmpty(fallbackText))
						{
							translation = fallbackText;
							return true;
						}
					}
				}
				int englishIndex = source.GetLanguageIndex("English", false, false);
				if (englishIndex >= 0)
				{
					string englishText = termData.GetTranslation(englishIndex, null, true);
					if (!string.IsNullOrEmpty(englishText))
					{
						translation = englishText;
						return true;
					}
				}
				return false;
			}
			return false;
		}

		private static string ToI2LanguageName(ChaosUkrainianConfig.FallbackLanguageOption option)
		{
			if (option == ChaosUkrainianConfig.FallbackLanguageOption.ChineseSimplified)
			{
				return "Chinese (Simplified)";
			}
			return option.ToString();
		}

		/// <summary>
		/// True if `term` has a genuine, non-empty Ukrainian entry in whichever source actually
		/// contains it (the first source LocalizationManager.Sources yields that ContainsTerm).
		/// False both for "term exists but Ukrainian's slot is empty" and "term doesn't exist in
		/// any source at all" - callers that need to tell those apart should check GetTermData
		/// themselves; this is only meant for the coarse "does the player see Ukrainian text for
		/// this term" question used by LogMissingTranslationIfNeeded.
		/// </summary>
		internal static bool HasUkrainianTranslation(string term)
		{
			foreach (LanguageSourceData source in LocalizationManager.Sources)
			{
				TermData termData = source.GetTermData(term, true);
				if (termData == null)
				{
					continue;
				}
				int ukrainianIndex = source.GetLanguageIndex(LanguageName, false, false);
				return ukrainianIndex >= 0 && !string.IsNullOrEmpty(termData.GetTranslation(ukrainianIndex, null, true));
			}
			return false;
		}

		private static readonly HashSet<string> _checkedTerms = new HashSet<string>();
		private static readonly HashSet<string> _checkedNonUkrainianGaps = new HashSet<string>();

		/// <summary>
		/// Diagnostic counterpart to LogMissingTranslationIfNeeded, for the opposite direction:
		/// called from a Postfix on LocalizationManager.TryGetTranslation (see
		/// ChaosUkrainianPatches) while some OTHER (non-Ukrainian) language is active, for every
		/// term that resolved empty/not-found. This exists specifically to root-cause reports of
		/// TFTV content (or anything else this mod translates) showing blank when a language
		/// other than Ukrainian is selected - unlike the Ukrainian-only diagnostic above, there's
		/// no static export to cross-reference against for "why is THIS language blank here", so
		/// this is the only way to see, from actually playing the game, which term failed and -
		/// critically - every source it exists in and whether that specific language's slot is
		/// populated there, to distinguish "found in the wrong/duplicate source", "found but the
		/// array's still undersized for this language's index", and "genuinely has no
		/// translation for this language anywhere" instead of guessing between them.
		/// </summary>
		internal static void LogNonUkrainianGapIfNeeded(string term, string currentLanguage)
		{
			if (string.IsNullOrEmpty(term) || !_checkedNonUkrainianGaps.Add(term))
			{
				return;
			}
			Debug.Log($"ChaosUkrainian: [gap:{currentLanguage}] '{term}' resolved empty - {DescribeAllContainingSources(term, currentLanguage)}");
		}

		/// <summary>
		/// Like DescribeContainingSource, but doesn't stop at the first source that contains the
		/// term (this mod's own Sources[0] shadow term is checked first and almost always
		/// contains SOMETHING for a translated key, which would otherwise mask whichever later
		/// source holds the real, complete data) - lists every source that has this term, and for
		/// each one, whether `language`'s slot is registered, undersized, empty, or populated.
		/// </summary>
		private static string DescribeAllContainingSources(string term, string language)
		{
			List<string> descriptions = new List<string>();
			int sourceIndex = 0;
			foreach (LanguageSourceData source in LocalizationManager.Sources)
			{
				TermData termData = source.GetTermData(term, true);
				if (termData != null)
				{
					int languageIndex = source.GetLanguageIndex(language, false, false);
					string slotState;
					if (languageIndex < 0)
					{
						slotState = "language not registered here";
					}
					else if (languageIndex >= termData.Languages.Length)
					{
						slotState = $"array undersized ({termData.Languages.Length} < {languageIndex + 1})";
					}
					else
					{
						string text = termData.GetTranslation(languageIndex, null, true);
						slotState = string.IsNullOrEmpty(text) ? "slot empty" : $"slot has text: {text}";
					}
					descriptions.Add($"#{sourceIndex} '{source.ownerObject?.name}' ({slotState})");
				}
				sourceIndex++;
			}
			return descriptions.Count > 0 ? string.Join("; ", descriptions) : "not found in any source";
		}

		/// <summary>
		/// Called from a Postfix on LocalizationManager.TryGetTranslation (see
		/// ChaosUkrainianPatches, after its own fallback-language override so `shownText` is
		/// whatever the player actually ends up seeing) while Ukrainian is the active language,
		/// for every term the game actually asks to display. Logs each distinct term exactly
		/// once the first time it's seen without a genuine Ukrainian translation - this is how
		/// in-game text that doesn't show up in export_localization's static snapshot (see
		/// chaos_export_missing_localization for the bulk equivalent) can still be identified by
		/// its real I2 Loc term/key and the text currently shown for it, from actually playing
		/// the game, rather than guessing from a screenshot.
		/// </summary>
		internal static void LogMissingTranslationIfNeeded(string term, string shownText)
		{
			if (string.IsNullOrEmpty(term) || !_checkedTerms.Add(term))
			{
				return;
			}
			if (!HasUkrainianTranslation(term))
			{
				Debug.Log($"ChaosUkrainian: [missing] '{term}' has no Ukrainian translation showing: {shownText} ({DescribeContainingSource(term)})");
			}
		}

		/// <summary>
		/// Diagnostic detail appended to every [missing] log line: which source (if any)
		/// actually holds this term right now, and whether chaos_export_localization /
		/// chaos_export_missing_localization would even see it - both skip Sources[0] (the
		/// overlay this mod imports into, "I2Languages_Mod"), matching the vanilla
		/// export_localization command's own Sources.Skip(1). A term can legitimately be
		/// completely absent from every source (shows up in-game as a raw
		/// "&lt;!-MISSING KEY [...]-!&gt;" string) if it's a stale/renamed reference in the
		/// game's own code with no corresponding I2 Loc data at all - nothing to export or
		/// translate from in that case, this just makes it obvious which situation is which
		/// instead of having to guess.
		/// </summary>
		private static string DescribeContainingSource(string term)
		{
			int sourceIndex = 0;
			foreach (LanguageSourceData source in LocalizationManager.Sources)
			{
				TermData termData = source.GetTermData(term, true);
				if (termData != null)
				{
					return $"found in source #{sourceIndex} '{source.ownerObject?.name}'";
				}
				sourceIndex++;
			}
			return "not found in any source";
		}

		private readonly struct CategoryExport
		{
			public readonly string Header;
			public readonly string Body;

			public CategoryExport(string header, string body)
			{
				Header = header;
				Body = body;
			}
		}

		/// <summary>
		/// Backs chaos_export_localization / chaos_export_missing_localization (see
		/// ChaosUkrainianConsoleCommands). Deliberately reimplements LanguageSourceData.
		/// Export_CSV's format (I2/Loc/LanguageSourceData.cs) rather than calling it, since that
		/// method has no parameter to exclude a single language column and its row-building
		/// helpers (AppendTerm/AppendString/AppendTranslation) are all private. Mirrors the
		/// vanilla export_localization console command (PhoenixPoint/Common/Core/
		/// OptionsManager.cs, ExportLocalization) in most respects: same Sources.Skip(1) (skip
		/// this mod's own overlay source), same Key,Type,Desc,&lt;language...&gt; column layout
		/// and CSV escaping rules - but writes each source into its own
		/// Localization/&lt;sourceName&gt;/&lt;category&gt;.csv subfolder rather than the
		/// vanilla command's flat Localization/&lt;category&gt;.csv. That's not just cosmetic:
		/// there are 6 non-overlay sources and nothing stops two of them using the same category
		/// name (e.g. "DLC5"), and the vanilla command's flat File.WriteAllText per (source,
		/// category) means whichever source is processed last for a shared name silently
		/// clobbers every earlier source's rows for it, with no error - which is exactly what
		/// made an earlier version of chaos_export_missing_localization show MORE rows for such
		/// a category than chaos_export_localization (the "only missing" filter could make the
		/// last-processed source contribute nothing and skip writing, leaving an earlier, larger
		/// contribution untouched, while "all keys" always overwrote it with a smaller one).
		/// Per-source subfolders make that collision structurally impossible instead of trying
		/// to detect and merge it after the fact.
		/// </summary>
		internal static void ExportLocalization(bool onlyMissingUkrainian, IConsole console)
		{
			int fileCount = 0;
			int skippedEmptyCount = 0;
			foreach (LanguageSourceData source in LocalizationManager.Sources.Skip(1))
			{
				string sourceName = source.ownerObject != null ? source.ownerObject.name : "UnknownSource";
				string sourceDir = Path.Combine(Application.streamingAssetsPath, "Localization", sourceName);
				foreach (string category in source.GetCategories(false, null))
				{
					CategoryExport? export = BuildCategoryCsv(source, category, onlyMissingUkrainian);
					if (export == null)
					{
						skippedEmptyCount++;
						continue;
					}
					Directory.CreateDirectory(sourceDir);
					File.WriteAllText(Path.Combine(sourceDir, category + ".csv"), export.Value.Header + export.Value.Body);
					fileCount++;
				}
			}

			string message = $"ChaosUkrainian: exported {fileCount} file(s) to {Application.streamingAssetsPath}/Localization (one subfolder per source)" +
				(onlyMissingUkrainian ? $", only keys missing Ukrainian; {skippedEmptyCount} categorie(s) had nothing missing." : ".");
			Debug.Log(message);
			console?.WriteLine(message, Array.Empty<object>());
		}

		private static void AppendCsvField(StringBuilder builder, string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			text = text.Replace("\\n", "\n");
			if (text.IndexOfAny(new[] { ',', '\n', '"' }) >= 0)
			{
				text = text.Replace("\"", "\"\"");
				builder.Append('"').Append(text).Append('"');
			}
			else
			{
				builder.Append(text);
			}
		}

		/// <summary>
		/// Returns null if this category has nothing to export under the given filter (so
		/// ExportLocalization can skip writing an empty/header-only file). Otherwise the header
		/// and body are returned separately (rather than one combined string) so
		/// ExportLocalization can merge contributions from multiple sources sharing the same
		/// category name (see there) without duplicating the header. Always excludes the
		/// Ukrainian column, and excludes any term whose Ukrainian slot is already non-empty
		/// when onlyMissingUkrainian is true.
		/// </summary>
		private static CategoryExport? BuildCategoryCsv(LanguageSourceData source, string category, bool onlyMissingUkrainian)
		{
			int ukrainianIndex = source.GetLanguageIndex(LanguageName, false, false);
			List<int> languageIndexes = new List<int>();
			StringBuilder headerBuilder = new StringBuilder();
			headerBuilder.Append("Key,Type,Desc");
			for (int i = 0; i < source.mLanguages.Count; i++)
			{
				if (i == ukrainianIndex)
				{
					continue;
				}
				languageIndexes.Add(i);
				LanguageData language = source.mLanguages[i];
				headerBuilder.Append(',');
				if (!language.IsEnabled())
				{
					headerBuilder.Append('$');
				}
				AppendCsvField(headerBuilder, GoogleLanguages.GetCodedLanguage(language.Name, language.Code));
			}
			headerBuilder.Append('\n');

			source.mTerms.Sort((a, b) => string.CompareOrdinal(a.Term, b.Term));
			StringBuilder bodyBuilder = new StringBuilder();
			bool anyRow = false;
			foreach (TermData term in source.mTerms)
			{
				string key;
				if (string.IsNullOrEmpty(category) || (category == LanguageSourceData.EmptyCategory && term.Term.IndexOfAny(LanguageSourceData.CategorySeparators) < 0))
				{
					key = term.Term;
				}
				else
				{
					if (!term.Term.StartsWith(category + "/") || category == term.Term)
					{
						continue;
					}
					key = term.Term.Substring(category.Length + 1);
				}

				if (onlyMissingUkrainian && HasUkrainianTranslation(term.Term))
				{
					continue;
				}

				foreach (string specialization in term.GetAllSpecializations())
				{
					AppendCsvRow(bodyBuilder, key, term, specialization, languageIndexes);
					anyRow = true;
				}
			}
			return anyRow ? new CategoryExport(headerBuilder.ToString(), bodyBuilder.ToString()) : (CategoryExport?)null;
		}

		private static void AppendCsvRow(StringBuilder builder, string key, TermData term, string specialization, List<int> languageIndexes)
		{
			AppendCsvField(builder, key);
			if (!string.IsNullOrEmpty(specialization) && specialization != "Any")
			{
				builder.AppendFormat("[{0}]", specialization);
			}
			builder.Append(',');
			builder.Append(term.TermType.ToString());
			builder.Append(',');
			AppendCsvField(builder, term.Description);
			foreach (int languageIndex in languageIndexes)
			{
				builder.Append(',');
				string text = languageIndex < term.Languages.Length ? term.GetTranslation(languageIndex, specialization, false) : null;
				AppendCsvField(builder, text);
			}
			builder.Append('\n');
		}

		private static bool _consoleCommandsRegistered;

		/// <summary>
		/// ConsoleCommandAttribute.LoadCommands() (Base/Utils/GameConsole/ConsoleCommandAttribute.cs)
		/// only ever scans Assembly.GetExecutingAssembly() - i.e. Assembly-CSharp.dll itself - for
		/// [ConsoleCommand] methods, never any mod assembly, and it already ran (GameConsole.Init(),
		/// well before any mod is discovered/enabled - confirmed via Player.log timing) by the time
		/// this mod gets a chance to do anything about it. So chaos_export_localization and
		/// chaos_export_missing_localization (ChaosUkrainianConsoleCommands) can't be picked up
		/// the normal way; this reflects them into the same private CommandToInfo dictionary
		/// LoadCommands() itself populates, using the exact same shape (Command/Description from
		/// the attribute, backing MethodInfo set via the same private field LoadCommands() sets),
		/// so the game's own console dispatch (ConsoleCommandAttribute.Invoke) treats them
		/// identically to a built-in command.
		/// </summary>
		internal static void RegisterConsoleCommands()
		{
			if (_consoleCommandsRegistered)
			{
				return;
			}
			_consoleCommandsRegistered = true;
			try
			{
				FieldInfo commandToInfoField = typeof(ConsoleCommandAttribute).GetField("CommandToInfo", BindingFlags.NonPublic | BindingFlags.Static);
				FieldInfo methodInfoField = typeof(ConsoleCommandAttribute).GetField("_methodInfo", BindingFlags.NonPublic | BindingFlags.Instance);
				var commandToInfo = (SortedList<string, ConsoleCommandAttribute>)commandToInfoField.GetValue(null);
				foreach (MethodInfo method in typeof(ChaosUkrainianConsoleCommands).GetMethods(BindingFlags.Static | BindingFlags.Public))
				{
					if (!(Attribute.GetCustomAttribute(method, typeof(ConsoleCommandAttribute)) is ConsoleCommandAttribute attribute))
					{
						continue;
					}
					methodInfoField.SetValue(attribute, method);
					string commandName = attribute.Command ?? method.Name;
					commandToInfo[commandName] = attribute;
					Debug.Log($"ChaosUkrainian: registered console command '{commandName}'.");
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: failed to register console commands: {ex}");
			}
		}
	}
}