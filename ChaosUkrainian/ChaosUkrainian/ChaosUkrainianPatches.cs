using System;
using Base.Core;
using HarmonyLib;
using I2.Loc;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Modding;
using UnityEngine;

namespace ChaosUkrainian
{
	/// <summary>
	/// Runs every time the Options screen initializes (i.e. whenever the player opens it),
	/// which is safe regardless of whether this mod's Harmony patches got applied before or
	/// after the game's own OptionsManager.Init() ran during bootstrap.
	/// </summary>
	[HarmonyPatch(typeof(UIModuleInterfaceOptionsPanel), "Init", new Type[0])]
	internal static class UIModuleInterfaceOptionsPanel_Init_Patch
	{
		/// <summary>
		/// Wrapped in try/catch: an unhandled exception in a Harmony Prefix aborts the
		/// original method too, so any bug here (e.g. a locked/missing translation file)
		/// must never be allowed to also break the base game's own Options panel Init().
		/// </summary>
		private static void Prefix()
		{
			try
			{
				Debug.Log($"ChaosUkrainian: UIModuleInterfaceOptionsPanel.Init - CurrentLanguage={LocalizationManager.CurrentLanguage}, CurrentLanguageCode={LocalizationManager.CurrentLanguageCode}, modDirectory={GetModDirectory()}.");
				ChaosUkrainianLocalization.EnsureLanguageRegistered();
				ImportTranslationsIfModLoaded();
				ChaosUkrainianLocalization.RetryStartupLanguageIfNeeded();
				OptionsManager optionsManager = GameUtl.GameComponent<OptionsManager>();
				ChaosUkrainianLocalization.EnsureLanguageOption(optionsManager?.OptionsManagerDef, GetModDirectory());
				int optionCount = optionsManager?.OptionsManagerDef?.Languages?.Count ?? -1;
				Debug.Log($"ChaosUkrainian: UIModuleInterfaceOptionsPanel.Init finished - OptionsManagerDef.Languages.Count={optionCount}.");
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: UIModuleInterfaceOptionsPanel.Init patch failed: {ex}");
			}
		}

		internal static void ImportTranslationsIfModLoaded()
		{
			string modDirectory = GetModDirectory();
			if (modDirectory != null)
			{
				ChaosUkrainianLocalization.ImportShippedTranslations(modDirectory);
			}
		}

		internal static string GetModDirectory()
		{
			return ChaosUkrainianMain.Main?.Instance.Entry.Directory;
		}
	}

	/// <summary>
	/// Guarantees "Українська" is registered with LocalizationManager before
	/// OptionsManager.ChangeLanguageTo's own LocalizationManager.HasLanguage check runs,
	/// covering both the player picking it from the dropdown and it being restored from a
	/// saved setting on startup.
	/// </summary>
	[HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.ChangeLanguageTo))]
	internal static class OptionsManager_ChangeLanguageTo_Patch
	{
		/// <summary>
		/// Wrapped in try/catch: same reasoning as UIModuleInterfaceOptionsPanel_Init_Patch -
		/// a Prefix exception here would silently prevent ChangeLanguageTo's own body from
		/// running at all, i.e. the player would be unable to switch language away from
		/// Ukrainian (or to any language) until restart.
		/// </summary>
		private static void Prefix(string languageOrCode)
		{
			try
			{
				Debug.Log($"ChaosUkrainian: ChangeLanguageTo('{languageOrCode}') requested - CurrentLanguage(before)={LocalizationManager.CurrentLanguage}, CurrentLanguageCode(before)={LocalizationManager.CurrentLanguageCode}, HasLanguage={LocalizationManager.HasLanguage(languageOrCode, true, true, true)}.");
				ChaosUkrainianLocalization.EnsureLanguageRegistered();
				UIModuleInterfaceOptionsPanel_Init_Patch.ImportTranslationsIfModLoaded();
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: OptionsManager.ChangeLanguageTo prefix failed: {ex}");
			}
		}

		/// <summary>
		/// Logs the outcome after the base method ran, so a Player.log capture shows whether
		/// ChangeLanguageTo actually applied the switch or silently hit its own "not supported"
		/// guard clause (which only writes its own LogError, easy to miss in a long log).
		/// </summary>
		private static void Postfix(string languageOrCode)
		{
			try
			{
				Debug.Log($"ChaosUkrainian: ChangeLanguageTo('{languageOrCode}') finished - CurrentLanguage(after)={LocalizationManager.CurrentLanguage}, CurrentLanguageCode(after)={LocalizationManager.CurrentLanguageCode}.");
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: OptionsManager.ChangeLanguageTo postfix failed: {ex}");
			}
		}
	}

	/// <summary>
	/// Lets a mod-gated Data/Localization source (currently just TFTV/, see
	/// localization-sources.json) start showing translations the instant its required mod is
	/// enabled from the Mod Management screen, instead of waiting for the player to separately
	/// open the Options screen or switch language (the two paths ImportShippedTranslations was
	/// already reachable from). Neither Prefix nor Postfix ever touches __result or throws past
	/// their own try/catch, so this can never influence whether TryEnableMod itself succeeds.
	/// Both also call EnsureLanguageRegistered first, same as the other two patches in this
	/// file: a third-party mod's own OnModEnabled routinely registers brand-new I2 Loc terms
	/// (TFTV does, for its own English/etc. content) with a Languages/Flags array sized to
	/// whatever the source's language count was at that exact moment - if that's before this
	/// resize pass has run since the last new language got added, those terms are undersized
	/// and TermData.GetTranslation(idx) (I2/Loc/TermData.cs - a raw array index, no bounds
	/// check at all) throws IndexOutOfRangeException the moment anything looks up one of the
	/// missing languages for them. Observed concretely: TFTV content showing blank instead of
	/// its own real English/Russian/etc. text specifically when a non-Ukrainian language was
	/// active, because that exception aborted the whole translation lookup before it could
	/// reach TFTV's real source.
	/// </summary>
	[HarmonyPatch(typeof(ModManager), nameof(ModManager.TryEnableMod))]
	internal static class ModManager_TryEnableMod_Patch
	{
		/// <summary>
		/// Runs BEFORE the target mod's own OnModEnabled, and therefore before ModEntry.Enabled
		/// flips to true - closing a gap the Postfix alone can't: a third-party mod's own
		/// OnModEnabled can itself populate I2 Loc terms and trigger UI that immediately queries
		/// some of them for display, all synchronously, before our Postfix (which runs after the
		/// whole TryEnableMod call, including that) gets a turn. Observed concretely with TFTV:
		/// enabling it logged a burst of "[missing]" entries for TFTV's own keys at the exact
		/// frame it was enabled, even though the same directory's import always succeeded a
		/// moment later via the Postfix anyway - this Prefix imports it proactively instead, via
		/// ImportTranslationsForRequiredMod (which bypasses the normal ModManager.Enabled check,
		/// since that flag genuinely isn't true yet at this point). See its own doc comment for
		/// why proceeding before TryEnableMod's outcome is known is still safe.
		/// </summary>
		private static void Prefix(ModEntry mod)
		{
			try
			{
				if (string.IsNullOrEmpty(mod?.ID))
				{
					return;
				}
				Debug.Log($"ChaosUkrainian: ModManager.TryEnableMod('{mod.ID}') starting - preloading any localization source gated on this mod before its own OnModEnabled runs.");
				ChaosUkrainianLocalization.EnsureLanguageRegistered();
				ChaosUkrainianLocalization.ImportTranslationsForRequiredMod(UIModuleInterfaceOptionsPanel_Init_Patch.GetModDirectory(), mod.ID);
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: ModManager.TryEnableMod prefix failed: {ex}");
			}
		}

		/// <summary>
		/// General safety-net re-check after a successful enable, kept alongside the Prefix
		/// above rather than replaced by it: covers any other source whose eligibility could
		/// have changed as a side effect of this particular mod enabling (e.g. a future source
		/// gated on a mod that gets enabled recursively as a dependency), not just the directory
		/// tied directly to `mod.ID`. Cheap to no-op when nothing new is eligible, since
		/// ImportShippedTranslations is idempotent per directory (see _importedDirectories).
		/// Also re-runs EnsureLanguageRegistered here (not just in the Prefix): the target mod's
		/// own OnModEnabled - which runs between the Prefix and here - is precisely when it
		/// creates its own new I2 Loc terms, so this is the earliest point after that where a
		/// fresh resize pass can actually catch them.
		/// </summary>
		private static void Postfix(ModEntry mod, bool __result)
		{
			try
			{
				if (!__result)
				{
					return;
				}
				Debug.Log($"ChaosUkrainian: ModManager.TryEnableMod('{mod?.ID}') succeeded - re-checking mod-gated localization sources.");
				ChaosUkrainianLocalization.EnsureLanguageRegistered();
				UIModuleInterfaceOptionsPanel_Init_Patch.ImportTranslationsIfModLoaded();
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: ModManager.TryEnableMod postfix failed: {ex}");
			}
		}
	}

	/// <summary>
	/// Implements the configurable fallback chain (ChaosUkrainianConfig.FallbackLanguage):
	/// Ukrainian -> configured fallback -> English, all resolved explicitly (see
	/// ChaosUkrainianLocalization.TryResolveConfiguredFallback for why English can't be left
	/// to I2 Loc's own OnMissingTranslation mechanism). Only OVERRIDES results while Ukrainian
	/// is the active language, so switching to/from any of the base game's 8 languages is
	/// otherwise unaffected by this patch - but while some OTHER language is active, it still
	/// logs a diagnostic (LogNonUkrainianGapIfNeeded) whenever a term resolves empty/not-found,
	/// to track down reports of this mod's own translated content (e.g. TFTV) going blank
	/// specifically outside Ukrainian, which - being a real, if rare, base-game occurrence too -
	/// has no other diagnostic coverage today.
	/// </summary>
	[HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.TryGetTranslation))]
	internal static class LocalizationManager_TryGetTranslation_Patch
	{
		private static void Postfix(string Term, ref string Translation, string overrideLanguage, ref bool __result)
		{
			try
			{
				if (overrideLanguage != null || string.IsNullOrEmpty(Term))
				{
					return;
				}
				if (LocalizationManager.CurrentLanguage != ChaosUkrainianLocalization.LanguageName)
				{
					if (!__result || string.IsNullOrEmpty(Translation))
					{
						ChaosUkrainianLocalization.LogNonUkrainianGapIfNeeded(Term, LocalizationManager.CurrentLanguage);
					}
					return;
				}
				if (ChaosUkrainianLocalization.TryResolveConfiguredFallback(Term, out string fallbackTranslation))
				{
					Translation = fallbackTranslation;
					__result = true;
				}
				ChaosUkrainianLocalization.LogMissingTranslationIfNeeded(Term, Translation);
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: fallback-language patch failed: {ex}");
			}
		}
	}

	/// <summary>
	/// Almost every PhoenixPoint/TFTV key this mod translates already lives in a DIFFERENT
	/// I2 Loc source (ImportShippedTranslations always writes into Sources[0], but
	/// export_localization - what those CSVs were sourced from - only ever reads
	/// Sources.Skip(1)), so importing Ukrainian for such a key creates a brand-new TermData in
	/// Sources[0] with every OTHER language left empty. Since Sources[0] is checked first by
	/// LocalizationManager.TryGetTranslation, and every source (including Sources[0]) keeps
	/// I2 Loc's own default OnMissingTranslation=Fallback (see
	/// ChaosUkrainianLocalization.EnsureLanguageRegistered - it used to force Empty on
	/// Sources[0] specifically to dodge this, until that turned out to also suppress this
	/// source's own legitimate Fallback for terms it genuinely and solely owns, e.g. a base
	/// game key with a real official translation gap in some other language - reported as
	/// blank text after switching away from Ukrainian), this patch instead stops
	/// TryGetFallbackTranslation (the method Fallback mode actually calls to find a substitute
	/// language) from EVER offering Ukrainian's slot as that substitute, full stop - so a
	/// same-source lookup for some other active language, finding only Ukrainian populated in
	/// one of these shadow terms, correctly reports "nothing usable here" instead of wrongly
	/// handing back Ukrainian text, and the outer per-source loop moves on to whichever source
	/// actually holds the complete, real data for every language. Excluding Ukrainian's index
	/// is always safe even when langIndex already equals it (i.e. Ukrainian itself is the
	/// language being resolved for): the original method already excludes `i/j != langIndex`
	/// in that case, so adding "and not Ukrainian's index" is a no-op there and this patch
	/// changes nothing about Ukrainian's own fallback-to-English chain (further refined by
	/// ChaosUkrainianLocalization.TryResolveConfiguredFallback regardless).
	/// </summary>
	[HarmonyPatch(typeof(LanguageSourceData), "TryGetFallbackTranslation")]
	internal static class LanguageSourceData_TryGetFallbackTranslation_Patch
	{
		/// <summary>
		/// Full replacement (not a pre/post tweak) of I2/Loc/LanguageSourceData.cs's private
		/// TryGetFallbackTranslation, since Harmony can't selectively skip one candidate out of
		/// a loop inside the original method body without either a transpiler or reimplementing
		/// it - reimplementing is the more robust choice here since it doesn't depend on the
		/// original method's exact IL shape surviving a future game patch. Mirrors that method's
		/// two-pass logic (same-alphabet languages first via a shared code prefix, then any
		/// other populated language) exactly, with "and index != ukrainianIndex" added to both
		/// passes' conditions. Runs the original on any exception, so a mismatch with a future
		/// game version degrades back to the pre-patch (Empty-era) risk rather than breaking
		/// fallback entirely.
		/// </summary>
		private static bool Prefix(LanguageSourceData __instance, TermData termData, out string Translation, int langIndex, string overrideSpecialization, bool skipDisabled, ref bool __result)
		{
			Translation = null;
			try
			{
				int ukrainianIndex = __instance.GetLanguageIndex(ChaosUkrainianLocalization.LanguageName, false, false);
				if (ukrainianIndex < 0)
				{
					return true;
				}

				string code = __instance.mLanguages[langIndex].Code;
				if (!string.IsNullOrEmpty(code))
				{
					if (code.IndexOf('-') >= 0)
					{
						code = code.Substring(0, code.IndexOf('-'));
					}
					for (int i = 0; i < __instance.mLanguages.Count; i++)
					{
						if (i < termData.Languages.Length && i != langIndex && i != ukrainianIndex && __instance.mLanguages[i].Code.StartsWith(code) && (!skipDisabled || __instance.mLanguages[i].IsEnabled()))
						{
							Translation = termData.GetTranslation(i, overrideSpecialization, true);
							if (!string.IsNullOrEmpty(Translation))
							{
								__result = true;
								return false;
							}
						}
					}
				}
				for (int j = 0; j < __instance.mLanguages.Count; j++)
				{
					if (j < termData.Languages.Length && j != langIndex && j != ukrainianIndex && (!skipDisabled || __instance.mLanguages[j].IsEnabled()) && (code == null || !__instance.mLanguages[j].Code.StartsWith(code)))
					{
						Translation = termData.GetTranslation(j, overrideSpecialization, true);
						if (!string.IsNullOrEmpty(Translation))
						{
							__result = true;
							return false;
						}
					}
				}
				Translation = null;
				__result = false;
				return false;
			}
			catch (Exception ex)
			{
				Debug.LogError($"ChaosUkrainian: TryGetFallbackTranslation patch failed, running original: {ex}");
				return true;
			}
		}
	}
}