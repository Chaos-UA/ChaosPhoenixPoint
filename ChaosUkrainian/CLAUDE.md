# ChaosUkrainian Mod — Developer Reference

## Project overview
Phoenix Point mod that adds **Ukrainian language support** to the game, for both
Vanilla and Terror From The Void. `meta.json` has the mod identity set
(`com.chaos.Ukrainian`, "Chaos Ukrainian").

Implemented: `ChaosUkrainianLocalization.cs` registers the language internally as
`LanguageName = "Ukrainian"` (`LanguageSourceData.AddLanguage`, code `"uk"`) — deliberately
the English name, not the native "Українська"; see the `<remarks>` on
`ChaosUkrainianLocalization` for why `Import_CSV`'s own column-index resolution requires an
exact `Name` match with no code fallback, unlike the language-creation check that precedes it.
This has no player-visible effect: the Options dropdown never renders `LanguageName` as text,
only the pre-rendered `Data/Icons/Ukraine.png` icon (see "Extensibility implications" below),
so the selector still shows "Українська". `OnMissingTranslation = Fallback`
so any key without a Ukrainian translation falls back to English automatically, and
imports translation CSVs shipped under `Data/Localization/` at runtime, per the ordered,
mod-gated manifest `Data/Localization/localization-sources.json` (see "Translation data
workflow" below) — so the `PhoenixPoint/`/`TFTV/` split is no longer purely organizational:
`TFTV/` only loads (and only after `PhoenixPoint/`, so it can override on key collision)
while the third-party TFTV mod itself is enabled.
`ChaosUkrainianPatches.cs` Harmony-patches `UIModuleInterfaceOptionsPanel.Init()` and
`OptionsManager.ChangeLanguageTo` to add the language + dropdown option regardless of
mod/game load order (see "How Phoenix Point's localization actually works" below for
why both a data source and an `OptionsManagerDef.Languages` entry are needed).
`ChaosUkrainianTactical.cs`/`ChaosUkrainianGeoscape.cs` remain unused template hooks
(not needed for a pure localization mod).

## Directory rules
- **`ChaosUkrainian/`** (this file's directory) — solution root, **writable**
  - **`ChaosUkrainian/ChaosUkrainian/`** — mod source, the only folder meant to be edited
  - **`ChaosUkrainian/ModSDK/`** — read-only SDK DLLs (0Harmony, Assembly-CSharp, UnityEngine, UnityEngine.UI)
  - **`ChaosUkrainian/Data/`** — loose files copied to `Dist/` on build (`<Content Include="..\Data\**">` in the csproj); put any CSV/localization assets a released mod needs to ship here
  - **`ChaosUkrainian/Dist/`** — build output (`ChaosUkrainian.dll` + `meta.json` + `Data/*`)
- **`../Assembly-CSharp/`** — Phoenix Point sources (dnSpy v6.6.0), **read only**
- **`../ChaosOverwatch/`, `../ChaosUmbraBalance/`, `../HavenUnderAttackTimer/`** — sibling mods, **read only**, useful as reference examples (Harmony patch conventions, config patterns, csproj structure)
- All other directories and files — **read only**

## Build & deploy
- Open `ChaosUkrainian.sln` in Visual Studio 2019+.
- Debug/Release both compile to `Dist/`.
- Post-build event (in the `.csproj`) wipes and recreates
  `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\WorkshopTool\TestMod`
  and copies `Dist/*` there — this is how a build gets picked up by the game for testing.
- Target framework: .NET 4.7.2 (matches the game's Unity/Mono runtime).

## Mod framework basics
- `ModMain` (`ChaosUkrainianMain`) — loaded once per session; where `harmony.PatchAll()`
  would be called once localization patches exist.
- `CanSafelyDisable => false` (deliberately, not the template default): this mod merges
  Ukrainian into shared I2 Loc singleton state (`LanguageSourceData.mLanguages`/`mTerms`
  across all 7 sources, plus `OptionsManagerDef.Languages`) via Harmony patches, and there's
  no reliable way to tell "a term that only exists in our overlay source because we merged
  it there" apart from "a term that source legitimately owns" — so a clean revert in
  `OnModDisabled` isn't realistically possible. Per `ModEntry.SetEnabled`, returning `false`
  here makes the framework skip `OnModDisabled`/unpatching entirely and instead flip on the
  Mod Management screen's built-in "Need Restart" banner (`UIStateModManagment.
  NeedRestartRoot`) — before this, disabling the mod mid-session left the language, the
  dropdown entry, and all Harmony patches fully intact and working until the next restart.
- `ModTactical` / `ModGeoscape` (`ChaosUkrainianTactical` / `ChaosUkrainianGeoscape`) —
  per-level-load instances; currently unused template hooks (not expected to be needed
  for a pure localization mod, but left as-is since removing them isn't necessary).
- `ModConfig` (`ChaosUkrainianConfig`) — one real setting: `FallbackLanguage`
  (`FallbackLanguageOption` enum, one of the base game's 8 languages, default
  `English`). Read by `ChaosUkrainianLocalization.TryResolveConfiguredFallback`, called
  from a Postfix on `LocalizationManager.TryGetTranslation` (see
  `ChaosUkrainianPatches.LocalizationManager_TryGetTranslation_Patch`) to implement a
  3-level fallback chain while Ukrainian is active: Ukrainian -> configured
  `FallbackLanguage` -> I2 Loc's own default fallback (in practice always English, since
  `OnMissingTranslation = Fallback` just returns the first non-empty language it finds,
  and English is always first). The Postfix only touches results when
  `LocalizationManager.CurrentLanguage` is Ukrainian, so switching between any of the
  base game's 8 languages is unaffected.
- `meta.json`'s `Name`/`Author`/`Description` are `LocalizationEntry` arrays matched by
  simple string equality against `I2.Loc.LocalizationManager.CurrentLanguage` — **this is
  a separate mechanism from the game's I2 Loc translation data**, just for the mod
  manager's own UI text.

## How Phoenix Point's localization actually works (read-only Assembly-CSharp)
The game uses **I2 Localization** ("I2 Loc"), a third-party Unity asset — not a simple
enum-keyed system. Understanding this is the crux of implementing this mod.

| Class | Namespace/path | Role |
|---|---|---|
| `LocalizationManager` | `I2.Loc` (`I2/Loc/LocalizationManager.cs`) | Static service: `CurrentLanguage`, `GetTranslation(term, ...)`, `Sources: List<LanguageSourceData>` |
| `LanguageSourceData` | `I2.Loc` (`I2/Loc/LanguageSourceData.cs`) | Holds all terms/translations for one source; `GetLanguageIndex(name)` matches by `LanguageData.Name` string (case-insensitive); `Import_CSV(...)` merges CSV data in, and can add a brand-new `LanguageData` if the CSV header name doesn't match an existing one |
| `LanguageData` | `I2/Loc/LanguageData.cs` | A language is just a `Name`/`Code` string pair — **not a fixed enum** |
| `LocalizedTextBind` | `Base.UI` (`Base/UI/LocalizedTextBind.cs`) | Game-side wrapper around a `LocalizationKey`; calls `LocalizationManager.GetTranslation` |
| `OptionsManager` | `PhoenixPoint.Common.Core` (`PhoenixPoint/Common/Core/OptionsManager.cs`) | `InitLanguage()` (~line 173) reads `StreamingAssets/Localization/*.csv` at startup and calls `Sources[0].Import_CSV(category, csvText, eSpreadsheetUpdateMode.Merge, ',')` — this is the game's own supported override mechanism |
| `OptionsManagerDef` | `PhoenixPoint.Common.Core` | `Languages: List<OptionsManager.LanguageOption>` — a **fixed, hand-authored** list (`{Sprite Icon, string Key}`) baked into a Def asset; this is what actually populates the in-game language dropdown |
| `UIModuleInterfaceOptionsPanel.SetupLanguageDropdown` | `PhoenixPoint/Common/View/ViewModules/` | Builds the dropdown from `OptionsManagerDef.Languages` only — **not** from `LocalizationManager.GetAllLanguages()`, so CSV data alone never becomes selectable |
| `Base.Localization.Language` | `Base/Localization/Language.cs` | Near-vestigial enum with only `English`; belongs to a separate, minor `Localizer` MonoBehaviour, **not** the I2 Loc system used for game text |

### Where translation strings live
- Base game strings ship embedded in `LanguageSourceAsset` ScriptableObjects (compiled
  into the game, not editable by a mod).
- The **documented, supported override path** is CSV files at
  `PhoenixPointWin64_Data/StreamingAssets/Localization/*.csv`, loaded once at startup.
  The folder's own `!Readme.txt` specifies the header row:
  `Keys,English,Chinese (Simplified),French,German,Italian,Polish,Russian,Spanish`
  — matching exactly the 8 languages the game ships with. That Readme explicitly states
  **you can only override existing language columns**, not add new ones through this path.

### Extensibility implications for this mod (resolved)
Adding a true, selectable "Ukrainian" language (rather than overwriting an existing
language's text) needed **more than a CSV**, since `LanguageSourceData.Import_CSV` can
technically create a new `LanguageData` entry, but the language dropdown reads from the
separate, fixed `OptionsManagerDef.Languages` list — a CSV-only approach would load
Ukrainian term data that's simply never exposed as a selectable option. Implemented both:
1. Ukrainian translation data merged into `LocalizationManager.Sources` via
   `AddLanguage` + this mod's own `Import_CSV` calls over every CSV found recursively
   under `Data/Localization/` (see "Translation data workflow" below) — not the
   StreamingAssets path, since a Workshop mod can't write into the game's own install
   folder.
2. A Harmony patch adding a `LanguageOption{ Key = "Ukrainian", Icon = ... }` entry to
   `OptionsManagerDef.Languages` so it appears as a dropdown choice. The dropdown
   identifies entries only by icon sprite (`UIModuleInterfaceOptionsPanel.
   SetupLanguageDropdown` never sets `Dropdown.OptionData.text`) — the base game's own 8
   entries only look like plain text in-game because their "Icon" is itself a
   pre-rendered image of the language's name, not a flag. This mod ships
   `Data/Icons/Ukraine.png` (a white-on-transparent, black-outlined render of the word
   "Українська", generated once via .NET `System.Drawing`, not at runtime) to match that
   convention, loaded into a `Sprite` at runtime by `ChaosUkrainianLocalization.
   LoadLanguageLabelIcon` via reflection (ModSDK doesn't ship
   `UnityEngine.ImageConversionModule.dll`).

No prior mod in this repo (`ChaosOverwatch`, `ChaosUmbraBalance`, `HavenUnderAttackTimer`)
touched localization before this — there was no existing pattern here to copy.

## Translation data workflow
- Source of truth for what needs translating: `export_localization/*.csv` (84 files,
  ~37k lines), produced by running the game's own `export_localization` console command
  (see `export_localization/!Readme.txt`) — one CSV per I2 Loc "category", header row
  `Key,Type,Desc,English,Chinese (Simplified),French,German,Italian,Polish,Russian,Spanish,UI_Tester`
  (column order/set can vary slightly per file; locate columns by header name, not index).
  **Read-only** — this is reference data, never edit it.
- Ukrainian translations are shipped as `Data/Localization/PhoenixPoint/<same filename>.csv`
  (base game/DLC content, matching `export_localization/*.csv`) and
  `Data/Localization/TFTV/<filename>.csv` (Terror From The Void community-mod content,
  sourced from `SourceLocalization/TFTV/*.csv` rather than `export_localization`, since the
  vanilla export command doesn't know about TFTV's own content — all 18 TFTV files are fully
  translated). Each file uses a minimal 2-column format: header `Key,Ukrainian` (plain
  English column name, matching the vanilla `export_localization` convention rather than the
  language's own native name — `Import_CSV` matches this header to the registered language by
  resolving "Ukrainian" to code `uk` via I2 Loc's own Google-language table and then matching
  on that code, not on the literal header text, so this header string doesn't need to equal
  `ChaosUkrainianLocalization.LanguageName` ("Українська") to import correctly), then one row
  per key. Only keys that are actually translated need a row — anything else (or an empty
  cell) falls back to English automatically (see `ChaosUkrainianLocalization` above).
  Standard CSV quoting rules apply (quote fields containing a comma, a literal `"` doubled
  to `""`, or an embedded newline for multi-line lore text). Placeholders like `{0}`, markup
  like `<b>...</b>`, and the `Key` value itself (including any trailing `[touch]`-style
  suffix) must be preserved exactly as in the source.
- Unlike the `export_localization/*.csv` reference data, which is organized purely by I2 Loc
  category (filename = category, per `Import_CSV`'s `Category` parameter — see
  `I2/Loc/LanguageSourceData.cs`), the `PhoenixPoint/`/`TFTV/` **top-level** split has real
  runtime meaning: `ChaosUkrainianLocalization.ImportShippedTranslations` reads
  `Data/Localization/localization-sources.json` (an array of `{Directory, RequiredModId,
  LoadAfterDirectory, UseFileNameAsCategory}` entries) rather than blindly recursing the whole
  tree. `RequiredModId` null/empty means "always eligible" (`PhoenixPoint/` has none);
  otherwise the directory is skipped entirely — no I2 Loc terms touched — unless `ModManager`
  reports a mod with that exact ID as `Enabled` (`TFTV/` uses `"phoenixrising.tftv"`, the
  actual TFTV mod's `meta.json` ID). This gate is re-checked on every call — Options-panel
  `Init`, `ChangeLanguageTo`, and a Harmony **Postfix** on `ModManager.TryEnableMod` all call
  it, per `ChaosUkrainianPatches` — so a mod enabled mid-session (either order relative to this
  mod, no restart needed) gets its directory imported as soon as it's enabled. A Harmony
  **Prefix** on the same `TryEnableMod` additionally imports (via
  `ImportTranslationsForRequiredMod`, which bypasses the `Enabled` check entirely) whatever
  directory is gated on the specific mod about to be enabled, before that mod's own
  `OnModEnabled` runs — needed because a third-party mod's own enable logic can itself
  populate I2 Loc terms and immediately render UI that queries them, all before the Postfix
  gets a turn (observed with TFTV: enabling it briefly showed/logged several of its own keys
  as missing at the exact frame it was enabled, since the Postfix's import hadn't run yet).
  Load order —
  which source wins a colliding key via `Import_CSV`'s `Merge` mode — defaults to array order,
  but an entry's `LoadAfterDirectory` (another entry's `Directory` value) overrides that
  explicitly regardless of position (`ResolveLoadOrder` topologically sorts on it, logging and
  ignoring a cycle or an unknown target rather than failing); `TFTV/` sets
  `LoadAfterDirectory: "PhoenixPoint"` so it always wins on collision even if the array were
  reordered. `UseFileNameAsCategory` (default `true`) controls whether each CSV's filename
  becomes the I2 Loc `Category` prefix on import (`Import_CSV` unconditionally prepends a
  non-empty Category to every key as `Category/Key`) — correct for `PhoenixPoint/`, whose CSVs
  came from `export_localization` with that same filename-as-category, so re-importing
  recreates the exact categorized term the base game already reads from (e.g.
  `Research/SomeKey`). `TFTV/` sets this `false`: the TFTV mod registers its own terms flat,
  with no category (its `SourceLocalization/TFTV/*.csv` source files are hand-authored, not
  produced by `export_localization`), so filename-as-category there silently created orphan
  terms like `TFTV_Options_Localization/TFTV_DIFFICULTY_ROOKIE_TITLE` that the game never
  queries — the translation imported successfully but was never visible in-game. Some TFTV
  rows already embed a genuine category directly in their `Key` column (e.g.
  `Geoscape/KEY_AIRCRAFT_STATS_DURABILITY`) — an empty Category leaves those keys untouched, so
  it's safe for the whole directory. A new subdirectory under `Data/Localization/` must be
  added to this manifest or it's silently never imported (a startup warning names any unlisted
  directory it finds).
- `ImportCsvDirectory` (`ChaosUkrainianLocalization.cs`) never calls `Import_CSV` on a real
  I2 Loc source directly — each CSV is parsed into a throwaway, unregistered
  `LanguageSourceData` ("scratch") purely to reuse I2 Loc's own CSV parsing, and each parsed
  `(term, Ukrainian text)` pair is then applied via `TryApplyTranslation`, which only ever
  *adds* Ukrainian onto a term some other source already contains — it never creates a new
  term itself. Terms not found anywhere yet go into `_pendingTranslations` and get rechecked
  on every subsequent `ImportEligibleSources` call (Options-panel Init, `ChangeLanguageTo`,
  `ModManager.TryEnableMod` pre/post). This replaced an earlier design that called
  `Sources[0].Import_CSV(...)` directly (which creates a term there if missing) after a
  concrete regression: TFTV registers some of its own terms (confirmed with its custom-game-
  options screen) lazily at runtime rather than at build time, via what's presumably an
  ordinary "does this term already exist?" guard before creating it — importing our Ukrainian
  CSV first made TFTV see the term as already present and skip populating its own English/
  Russian/etc. data, leaving it with only Ukrainian, forever, in every other language. Confirmed
  by removing the TFTV CSVs entirely and seeing every other language work correctly again.
- Translation is being done incrementally, file by file (smallest first), either directly
  or by dispatching one-file-at-a-time translation agents — not all 84 base-game files are
  done yet. Check `Data/Localization/PhoenixPoint/` against `export_localization/` to see
  what's left.

### Terminology glossary
Use these exact Ukrainian terms consistently across every translated file:

| English | Ukrainian |
|---|---|
| Phoenix Point (base name) | *(untranslated)* "Phoenix Point" — do not translate to "Точка Фенікс", it reads badly |
| Haven | Притулок |
| Pandoran(s) | Пандоран(и) |
| Synedrion | Синедріон |
| New Jericho | Новий Єрихон |
| Phoenix Project | Проєкт «Фенікс» |
| Disciples of Anu | Послідовники Ану |
| The Exalted | Возвеличена |
| Willpower | Сила волі |
| Perception | Сприйняття |
| Speed (stat) | Швидкість |
| Strength (stat) | Сила |
| Accuracy | Точність |
| Skill Points (SP) | ОУ (Очки Умінь) |
| Action Points (AP) | ОД (Очки Дій) |
| Will Points | Очки волі |
| Hit Points | Очки здоров'я |
| Panic | Паніка |
| Squad | Загін |
| Roster | Особовий склад |

Note: "Притулок" is grammatically masculine (притулок/притулку/притулків/притулкам/
притулком/притулку), like a masculine noun with a fleeting vowel — watch case endings
when this word is inflected in a sentence, not just the dictionary form.

## Harmony patch conventions (once implementation starts)
- Follow sibling mods' pattern: a dedicated `ChaosUkrainianPatches.cs`, namespace
  `ChaosUkrainian`, applied via `harmony.PatchAll()` in `OnModEnabled`.
- Expose config via a static accessor on `ChaosUkrainianMain` (e.g. `Main.Cfg`) so static
  patch methods can read it without reflection.
- For a new `.cs` file, add a `<Compile Include="NewFile.cs" />` entry in
  `ChaosUkrainian.csproj`; new classes in existing files need no csproj changes.