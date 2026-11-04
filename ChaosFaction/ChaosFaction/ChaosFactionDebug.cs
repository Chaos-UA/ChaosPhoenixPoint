using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Assets;
using Base.Core;
using Base.Defs;
using Base.UI;
using Base.UI.MessageBox;
using Base.Utils.GameConsole;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.UI;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.Levels.Objectives;
using PhoenixPoint.Tactical.Entities;
using UnityEngine;

namespace ChaosFaction
{
	/// <summary>
	/// "chaos_factions_debug 1" console command: when set, adds a single
	/// "ChaosFactionsDebugOptions" context-menu entry (opens a picker popup listing every
	/// debug activity kind — see DebugActivityPickerAbility) to every mission type this mod
	/// already supports (see ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities),
	/// letting the player manually spawn one of these activities for free, on demand.
	/// **This flag no longer gates the activities themselves — only that manual button.**
	/// Originally the whole activity system (Pure/Forsaken hostile mutant incursions, etc.)
	/// was debug/testing-only; per explicit follow-up request it's now a normal,
	/// always-available part of the mod — activities auto-spawn on their own regardless of
	/// this flag (AutoDebugActivitySpawner, controlled by ActivitySpawnChancePercentPerDay),
	/// and a site's activity, once active, shows its map marker/objective/mission-brief row
	/// regardless of this flag too. This console command's only remaining job is the
	/// manual "spawn one for free right now" testing button.
	/// Explicit request. Not a [ConsoleVariable]: ConsoleCommandAttribute is the pattern
	/// already proven working in this repo (ChaosUkrainian's chaos_export_localization/
	/// chaos_export_missing_localization, ChaosUkrainianLocalization.
	/// RegisterConsoleCommands) — mirrored here almost verbatim rather than inventing a
	/// second, unverified reflection technique for the same underlying problem
	/// (ConsoleCommandAttribute.LoadCommands(), like its ConsoleVariableAttribute sibling,
	/// only ever scans Assembly.GetExecutingAssembly() — Assembly-CSharp.dll itself — so a
	/// mod assembly's own [ConsoleCommand] methods are never discovered the normal way).
	/// </summary>
	internal static class ChaosFactionDebugFlag
	{
		public static bool Enabled;

		private static bool _registered;

		/// <summary>
		/// Reflects our own [ConsoleCommand] methods into the same private CommandToInfo
		/// dictionary ConsoleCommandAttribute.LoadCommands() itself populates, using the
		/// exact same shape (Command/Description from the attribute, backing MethodInfo set
		/// via the same private field LoadCommands() sets) so the game's own console
		/// dispatch (ConsoleCommandAttribute.Invoke, reached via CommandLineParser after its
		/// own HasCommand check) treats "chaos_factions_debug 1" identically to a built-in
		/// command. Called once from ChaosFactionMain.OnModEnabled(); the _registered guard
		/// makes a second call (e.g. a mod re-enable within the same process) a no-op rather
		/// than throwing on a duplicate dictionary key.
		/// </summary>
		public static void RegisterConsoleCommands()
		{
			if (_registered)
				return;
			_registered = true;
			try
			{
				FieldInfo commandToInfoField = typeof(ConsoleCommandAttribute).GetField("CommandToInfo", BindingFlags.NonPublic | BindingFlags.Static);
				FieldInfo methodInfoField = typeof(ConsoleCommandAttribute).GetField("_methodInfo", BindingFlags.NonPublic | BindingFlags.Instance);
				var commandToInfo = (SortedList<string, ConsoleCommandAttribute>)commandToInfoField.GetValue(null);
				foreach (MethodInfo method in typeof(ChaosFactionDebugConsoleCommands).GetMethods(BindingFlags.Static | BindingFlags.Public))
				{
					if (!(Attribute.GetCustomAttribute(method, typeof(ConsoleCommandAttribute)) is ConsoleCommandAttribute attribute))
						continue;
					methodInfoField.SetValue(attribute, method);
					string commandName = attribute.Command ?? method.Name;
					commandToInfo[commandName] = attribute;
					UnityEngine.Debug.Log($"[ChaosFaction] Registered console command '{commandName}'.");
				}
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError($"[ChaosFaction] Failed to register console commands: {ex}");
			}
		}
	}

	internal static class ChaosFactionDebugConsoleCommands
	{
		[ConsoleCommand(Command = "chaos_factions_debug", Description = "ChaosFaction: 1 = show the 'ChaosFactionsDebugOptions' context-menu button to manually spawn a hostile/allied activity, 0 = hide it")]
		public static void SetDebugMode(IConsole console, int value)
		{
			ChaosFactionDebugFlag.Enabled = value == 1;
			Log.Debug($"chaos_factions_debug set to {value} (Enabled={ChaosFactionDebugFlag.Enabled}).");

			// Explicit request: "when chaos_factions_debug = 1 to show that counter on
			// map where it is higher than 0" — this is one of the two places display
			// state can change (the other being ShowUnrevealedAttacksCounter itself,
			// see ChaosFactionMain.OnConfigChanged), so toggling it re-evaluates every
			// currently-tracked site's marker. Always RefreshAllMarkers, never an
			// unconditional hide-everything on disable — RefreshMarker's own OR logic
			// (ChaosFactionDebugFlag.Enabled || ShowUnrevealedAttacksCounter) means a
			// marker can still legitimately stay visible after this flag turns off, if
			// ShowUnrevealedAttacksCounter is on.
			HavenSelfDefensePandoranSiteAttackTracker.RefreshAllMarkers(ChaosFactionGeoscape.CurrentController);
		}

		/// <summary>
		/// Explicit request: "add console command chaos_factions_state, it should print
		/// to console per faction info like current chance to roll on next haven defense
		/// mission." Reads ChaosFactionGeoscape.CurrentController (set/cleared alongside
		/// OnGeoscapeStart/OnGeoscapeEnd) rather than doing its own scene lookup, since
		/// nothing in this mod previously needed a "give me the live GeoLevelController
		/// from arbitrary code" accessor. Reuses the exact same base-chance + per-faction
		/// pity-bonus formula HavenDefenseRewardTracker.TryGrantFreeHelp itself computes
		/// (ChaosFactionState.cs) rather than a second copy of that math, so this can
		/// never drift out of sync with what the next roll will actually use.
		/// </summary>
		[ConsoleCommand(Command = "chaos_factions_state", Description = "ChaosFaction: prints each helper faction's current haven-defense free-help roll chance (base + pity streak) and relationship, plus every tracked Pandoran base's self-defense reveal chance (base + attack counter), to the console")]
		public static void PrintFactionsState(IConsole console)
		{
			GeoLevelController geoLevel = ChaosFactionGeoscape.CurrentController;
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (geoLevel == null || cfg == null)
			{
				console.WriteLine("[ChaosFaction] Not currently in a Geoscape session.");
				return;
			}

			GeoFaction playerFaction = geoLevel.PhoenixFaction;
			int baseChance = Mathf.Clamp(cfg.HelpForFreeAfterHavenDefenseChancePercent, 0, 100);
			int bonusPerFailure = Mathf.Max(0, cfg.HelpForFreeAfterHavenDefenseNextBonusPercent);

			console.WriteLine("[ChaosFaction] Haven-defense free-help state (base chance {0}%, +{1}% per consecutive failed roll, RelationshipThreshold {2}):", baseChance, bonusPerFailure, cfg.RelationshipThreshold);
			foreach (HelperFactionKind kind in Enum.GetValues(typeof(HelperFactionKind)))
			{
				string name = kind.DisplayName(geoLevel);
				GeoFaction faction = kind.ToFaction(geoLevel);
				if (faction == null || playerFaction == null)
				{
					console.WriteLine("  {0}: not available in this game.", name);
					continue;
				}

				int relationship = faction.Diplomacy.GetDiplomacy(playerFaction);
				bool meetsThreshold = relationship >= cfg.RelationshipThreshold;
				int failureStreak = HavenDefenseFreeHelpPity.GetFailureStreak(kind);
				int pityBonus = bonusPerFailure * failureStreak;
				int chance = Mathf.Clamp(baseChance + pityBonus, 0, 100);

				console.WriteLine("  {0}: next roll chance {1}% (base {2}% + pity {3}%, failure streak {4}) | relationship {5} ({6} threshold)",
					name, chance, baseChance, pityBonus, failureStreak, relationship, meetsThreshold ? "meets" : "below");
			}

			// Explicit follow-up request: "you should include that counter info per each
			// pandoran base for chaos_factions_state console command" — reuses
			// HavenDefenseRewardTracker.TryRevealPandoranBase's own reveal-chance
			// formula (HavenSelfDefensePandoranSiteRevealChance +
			// HavenSelfDefensePandoranSiteNextRevealChanceBonus x tracked attack count)
			// rather than a second copy of that math, now that the reveal decision is
			// this mod's own to compute again (not vanilla's).
			// Defensive filter: AllTrackedSites should never actually hand back a
			// zero/negative count (IncrementAndGet only ever stores >= 1, ResetCounter
			// removes the entry outright, RestoreCounts skips <= 0 on load) but filtering
			// here too costs nothing and guarantees this printout never shows a
			// no-longer-meaningful "0 tracked attacks" row regardless.
			int revealBaseChance = Mathf.Clamp(cfg.HavenSelfDefensePandoranSiteRevealChance, 0, 100);
			int revealBonusPerAttack = Mathf.Max(0, cfg.HavenSelfDefensePandoranSiteNextRevealChanceBonus);
			List<(GeoSite Site, int Count)> trackedSites = HavenSelfDefensePandoranSiteAttackTracker.AllTrackedSites(geoLevel).Where(e => e.Count > 0).ToList();
			if (trackedSites.Count == 0)
			{
				console.WriteLine("[ChaosFaction] No Pandoran base attack counters currently tracked (base reveal chance {0}%, +{1}% per tracked attack).", revealBaseChance, revealBonusPerAttack);
			}
			else
			{
				console.WriteLine("[ChaosFaction] Pandoran base attack counters (persists after reveal, only clears on destruction — map marker only shown while still unrevealed):");
				foreach ((GeoSite Site, int Count) entry in trackedSites.OrderByDescending(e => e.Count))
				{
					if (playerFaction != null && entry.Site.GetVisible(playerFaction))
					{
						console.WriteLine("  {0}: already revealed | {1} tracked attack(s) total",
							GeoSiteDisplayHelper.GetDisplayName(entry.Site), entry.Count);
						continue;
					}
					int bonus = revealBonusPerAttack * entry.Count;
					int chance = Mathf.Clamp(revealBaseChance + bonus, 0, 100);
					console.WriteLine("  {0}: next roll chance {1}% (base {2}% + {3}% from {4} tracked attack(s))",
						GeoSiteDisplayHelper.GetDisplayName(entry.Site), chance, revealBaseChance, bonus, entry.Count);
				}
			}
		}
	}

	/// <summary>
	/// Which hostile mutant sub-faction a debug activity represents. Pure and Forsaken are
	/// GeoSubFaction/GeoSubFactionDef instances (a sub-faction tier, distinct from the five
	/// main GeoFactions) rather than hardcoded PPFactionDef fields on SharedData — see
	/// DebugSubFactionResolver.
	/// </summary>
	internal enum DebugActivityKind
	{
		Pure,
		Forsaken,
		NewJericho,
		Synedrion,
		Anu,

		/// GeoSubFactionDef-backed (NEU_Bandits), same resolution path as Pure/Forsaken —
		/// see IsSubFaction/SubFactionShortName. Added per explicit request, purely for
		/// testing ("I just want to see what it is") — display name is the raw enum name
		/// ("Bandits"), not given a nicer DisplayName() override.
		Bandits,
	}

	internal static class DebugActivityKindExtensions
	{
		/// All possible debug-activity kinds — shared by the context-menu button list, so
		/// it stays in sync with the enum without needing to list them out twice.
		public static readonly DebugActivityKind[] All =
		{
			DebugActivityKind.Pure,
			DebugActivityKind.Forsaken,
			DebugActivityKind.NewJericho,
			DebugActivityKind.Synedrion,
			DebugActivityKind.Anu,
			DebugActivityKind.Bandits,
		};

		/// The three "always hostile" sub-faction kinds — see PickRandomWeightedByGroup.
		/// Bandits joined Pure/Forsaken here per explicit follow-up request (originally
		/// excluded, manual-click-only) — same "always hostile, no diplomacy" resolution
		/// as Pure/Forsaken (DebugActivityTracker.ResolveIsHostile, IsSubFaction()), so it
		/// fits this group exactly, not just nominally.
		private static readonly DebugActivityKind[] HostileGroup =
		{
			DebugActivityKind.Pure,
			DebugActivityKind.Forsaken,
			DebugActivityKind.Bandits,
		};

		/// The three "can resolve as an ally" main-faction kinds — see
		/// PickRandomWeightedByGroup. Named for what they're capable of, not a guarantee:
		/// whether one of these three actually spawns hostile or allied still depends on
		/// its live diplomacy tier at add time (DebugActivityTracker.ResolveIsHostile,
		/// PartyDiplomacyState.Conflict or worse) — this grouping only affects which kind
		/// gets picked at random, not what it resolves to once picked. Explicitly fine for
		/// one of these three to still resolve hostile if the player's relationship with
		/// it has genuinely broken down (that's the whole point of ResolveIsHostile's
		/// check) — this group only means "capable of resolving as an ally," never
		/// "guaranteed to."
		private static readonly DebugActivityKind[] AlliedCandidateGroup =
		{
			DebugActivityKind.NewJericho,
			DebugActivityKind.Synedrion,
			DebugActivityKind.Anu,
		};

		/// <summary>
		/// Explicit request: a flat random pick across all kinds under-weights the
		/// always-hostile sub-factions relative to the main factions — instead, pick one of
		/// the two groups first, weighted by hostileGroupChancePercent (config:
		/// ActivityHostileGroupChancePercent, default 50 — "50% chance to roll one
		/// of allied faction and 50% to roll hostile faction"), then pick uniformly within
		/// whichever group won. Both groups are now size 3 (Pure/Forsaken/Bandits vs. New
		/// Jericho/Synedrion/Anu, since Bandits joined HostileGroup per explicit follow-up
		/// request), so at the default 50 split every kind now lands at an even ~16.7% —
		/// used by AutoDebugActivitySpawner.OnHourTicked for its random kind pick.
		/// </summary>
		public static DebugActivityKind PickRandomWeightedByGroup(int hostileGroupChancePercent)
		{
			float hostileChance = Mathf.Clamp(hostileGroupChancePercent, 0, 100) / 100f;
			DebugActivityKind[] group = UnityEngine.Random.value < hostileChance ? HostileGroup : AlliedCandidateGroup;
			return group[UnityEngine.Random.Range(0, group.Length)];
		}

		/// <summary>
		/// True for the three GeoSubFactionDef-backed kinds (Pure/Forsaken/Bandits) — false
		/// for NewJericho/Synedrion/Anu (regular GeoFactions).
		/// DebugActivityTracker.TryResolveActivityFaction branches on this to know which
		/// resolution path (DebugSubFactionResolver vs. GeoLevelController.*Faction)
		/// applies.
		/// </summary>
		public static bool IsSubFaction(this DebugActivityKind kind)
		{
			return kind == DebugActivityKind.Pure || kind == DebugActivityKind.Forsaken || kind == DebugActivityKind.Bandits;
		}

		/// <summary>
		/// Confirmed via chaos_factions_list (Player.log): this game has exactly three
		/// GeoSubFactionDef-backed sub-factions in total — NJ_Purists, AN_FallenOnes, and
		/// NEU_Bandits. There is no faction literally named "Pure" or "Forsaken" anywhere
		/// in the game's data; those were placeholder names from this feature's original
		/// design doc. "Pure" (DebugActivityKind.Pure) maps to NJ_Purists (ShortNames:
		/// Purists, pu — a New Jericho splinter faction), "Forsaken"
		/// (DebugActivityKind.Forsaken) maps to AN_FallenOnes (ShortNames: FallenOnes, fo,
		/// fallen, Fallen Ones, fa — an Anu splinter faction), and "Bandits"
		/// (DebugActivityKind.Bandits) maps directly to NEU_Bandits (ShortNames: ban,
		/// bandits, Bandits) — added later purely for testing, so it keeps the raw
		/// "Bandits" name rather than a nicer display name. The originally-guessed
		/// "pure"/"forsaken" literals never matched any ShortNames entry and always failed
		/// to resolve (confirmed in-game: "No GeoSubFactionDef found matching short name
		/// 'pure'/'forsaken'."). DebugSubFactionResolver also falls back to a substring
		/// match against the Def's own asset name for resilience if a short name ever
		/// turns out to differ again. Only meaningful for the three sub-faction kinds —
		/// see IsSubFaction.
		/// </summary>
		public static string SubFactionShortName(this DebugActivityKind kind)
		{
			switch (kind)
			{
				case DebugActivityKind.Pure: return "purists";
				case DebugActivityKind.Forsaken: return "fallenones";
				case DebugActivityKind.Bandits: return "bandits";
				default: return null;
			}
		}

		/// <summary>Only meaningful for the three main-faction kinds — see IsSubFaction.</summary>
		public static GeoFaction ResolveMainFaction(this DebugActivityKind kind, GeoLevelController geoLevel)
		{
			if (geoLevel == null)
				return null;
			switch (kind)
			{
				case DebugActivityKind.NewJericho: return geoLevel.NewJerichoFaction;
				case DebugActivityKind.Synedrion: return geoLevel.SynedrionFaction;
				case DebugActivityKind.Anu: return geoLevel.AnuFaction;
				default: return null;
			}
		}

		/// Player-facing text everywhere uses this instead of a bare ToString() — "New
		/// Jericho" reads far better than the enum's own "NewJericho". Hardcoded-English
		/// fallback, kept for contexts with no GeoLevelController in hand — prefer the
		/// GeoLevelController overload below wherever one's available. Reuses the game's own
		/// real localization keys (found in ChaosUkrainian/SourceLocalization) instead of a
		/// mod-owned literal/key, per explicit follow-up request: "reuse as much as possible
		/// to not create additional translation keys" — KEY_FACTION_NAME_PURE ("THE PURE"),
		/// KEY_FACTION_NAME_FALLEN ("THE FORSAKEN"), and KEY_FACTION_NAME_BANDITS
		/// ("SCAVENGERS") are the vanilla flavor names for these three sub-factions.
		public static string DisplayName(this DebugActivityKind kind)
		{
			switch (kind)
			{
				case DebugActivityKind.NewJericho: return new LocalizedTextBind("KEY_FACTION_NAME_NEW_JERICHO").Localize(null);
				case DebugActivityKind.Pure: return new LocalizedTextBind("KEY_FACTION_NAME_PURE").Localize(null);
				case DebugActivityKind.Forsaken: return new LocalizedTextBind("KEY_FACTION_NAME_FALLEN").Localize(null);
				case DebugActivityKind.Bandits: return new LocalizedTextBind("KEY_FACTION_NAME_BANDITS").Localize(null);
				default: return kind.ToString();
			}
		}

		/// <summary>
		/// Explicit follow-up request ("try to reuse where possible localization from
		/// game"): reads the live faction's/sub-faction's own real localized name
		/// (GeoFaction.Name / GeoSubFaction.Name, both LocalizedTextBind resolving through
		/// GeoFactionViewDef.Name — confirmed via source to be safely readable
		/// from any context with the live instance in hand, unlike ModalCommonResources.
		/// GetFactionInfo, which is only ever a serialized field on already-open UI modals)
		/// instead of the vanilla-key fallback above — the live GeoSubFactionDef's own
		/// ViewDef.Name may differ from the vanilla flavor-name key the fallback reuses
		/// (KEY_FACTION_NAME_PURE/_FALLEN, "THE PURE"/"THE FORSAKEN"), so this overload is
		/// preferred wherever a GeoLevelController is available. Falls back to the
		/// parameterless DisplayName() if geoLevel is null or the live faction/sub-faction
		/// can't be resolved, same graceful-degradation pattern used throughout this mod.
		/// </summary>
		public static string DisplayName(this DebugActivityKind kind, GeoLevelController geoLevel)
		{
			if (geoLevel != null)
			{
				if (kind.IsSubFaction())
				{
					GeoSubFaction subFaction = DebugSubFactionResolver.Resolve(geoLevel, kind.SubFactionShortName());
					if (subFaction?.Name != null)
						return subFaction.Name.Localize(null);
				}
				else
				{
					GeoFaction faction = kind.ResolveMainFaction(geoLevel);
					if (faction?.Name != null)
						return faction.Name.Localize(null);
				}
			}
			return kind.DisplayName();
		}
	}

	/// <summary>
	/// Resolves "Pure"/"Forsaken" to a live GeoSubFaction by name, creating it on demand if
	/// the campaign hasn't instantiated it yet (mirrors GeoSubFaction.cs's own
	/// subfaction_create console command: GeoLevelController.CreateSubFaction is idempotent,
	/// returning the existing instance if one's already there for that Def). Falls back from
	/// GeoLevelController.SubFactions (already-instantiated sub-factions) to the full
	/// GeoSubFactionDef catalog (DefRepository.GetAllDefs) so this works even if the
	/// relevant Corrupted-Horizons-style content wasn't part of this campaign's starting
	/// sub-factions.
	/// </summary>
	internal static class DebugSubFactionResolver
	{
		public static GeoSubFaction Resolve(GeoLevelController geoLevel, string shortName)
		{
			if (geoLevel == null)
				return null;

			GeoSubFaction existing = geoLevel.SubFactions?.FirstOrDefault(sf => Matches(sf.PPFactionDef, shortName));
			if (existing != null)
				return existing;

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			GeoSubFactionDef def = repo?.GetAllDefs<GeoSubFactionDef>().FirstOrDefault(d => Matches(d.PPFactionDef, shortName));
			if (def == null)
			{
				Log.Debug($"No GeoSubFactionDef found matching short name '{shortName}'.");
				return null;
			}

			GeoSubFaction created = geoLevel.CreateSubFaction(def);
			Log.Debug($"Resolved sub-faction '{shortName}' -> {def.name}.");
			return created;
		}

		private static bool Matches(PPFactionDef def, string shortName)
		{
			if (def == null)
				return false;
			if (def.MatchesShortName(shortName))
				return true;
			string name = def.GetName();
			return name != null && name.IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}

	/// <summary>
	/// One pending (paid-for-free, not-yet-resolved) debug activity — everything
	/// DebugActivityTracker needs, mirroring PendingHelpRequest's shape. Persisted across
	/// save/load the same way PendingHelpRequest is (ChaosFactionSavedActivity/
	/// ChaosFactionGSInstanceData, ChaosFactionGeoscape.cs) — explicit follow-up request;
	/// originally left unpersisted on the theory that this was purely a debug/testing tool,
	/// no longer true now that activities auto-spawn as a normal, always-on part of the mod.
	/// </summary>
	internal class PendingDebugActivity
	{
		public DebugActivityKind Kind;

		/// <summary>
		/// The activity's own resolved faction/squad — moved onto the activity itself
		/// (rather than staying as separate parameters passed into WireMissionLifecycle
		/// at wire time) as part of fixing a real reported bug: see
		/// DebugActivityTracker.WireMissionLifecycle's own doc comment for the full
		/// root-cause story. Squad is null when it hasn't been pre-built yet (a restored,
		/// post-save/load activity — TacCharacterDef references aren't safe to
		/// serialize, so it's rebuilt fresh at actual launch time instead, from Kind).
		/// </summary>
		public PPFactionDef FactionDef;
		public List<TacCharacterDef> Squad;

		/// True (always, for Pure/Forsaken) if this activity spawns hostile to both the
		/// player and the mission's own enemy; false if it resolves as an ally instead
		/// (New Jericho/Synedrion/Anu only, when their diplomacy tier is above Conflict —
		/// see DebugActivityTracker.ResolveIsHostile).
		public bool IsHostile;

		/// <summary>
		/// Explicit request: the activity should first show as an unrevealed/unknown signal
		/// on the Geoscape (map marker + top-left objective), and only reveal its real
		/// faction/color once the player "comes to the mission and opens it" — interpreted
		/// as the first time the player opens this site's context menu or mission-brief
		/// popup while the activity is active (see DebugActivityTracker.RevealIfNeeded, the
		/// single choke point both of those UI patches call). Starts false and is a one-way
		/// latch (never reset back to false) for the lifetime of this pending activity.
		/// </summary>
		public bool IsRevealed;

		public double ExpiresAtHours;
		public DebugActivityGeoFactionObjective Objective;
	}

	/// <summary>
	/// Geoscape objective shown while a debug activity is active — mirrors
	/// AskForHelpGeoFactionObjective's shape exactly (same [SerializeType]/[SerializeMember]/
	/// private-ctor pattern, since GeoFactionObjective instances live inside
	/// GeoFaction._objectives, itself part of normal save data, whether we want it swept in
	/// or not). GivenByFaction is the Alien faction for the two GeoSubFaction-backed kinds
	/// (Pure/Forsaken have no GeoFactionViewDef of their own the base class's GetIcon()
	/// could read) but the activity's own real faction for NewJericho/Synedrion/Anu (a
	/// regular GeoFaction, so its own icon is both available and more accurate) — see
	/// DebugActivityTracker.AddActiveActivity for the actual icon-faction pick.
	/// </summary>
	[Base.Serialization.General.SerializeType]
	internal class DebugActivityGeoFactionObjective : GeoFactionObjective, IChaosTimedObjective
	{
		[Base.Serialization.General.SerializeMember]
		public readonly GeoSite Site;

		[Base.Serialization.General.SerializeMember]
		public readonly int Kind;

		public DebugActivityGeoFactionObjective(GeoSite site, GeoFaction iconFaction, DebugActivityKind kind)
			: base(iconFaction)
		{
			Site = site;
			Kind = (int)kind;
			IsCriticalPath = true;
		}

		/// For the (de)serializer — never called directly by this mod's own code.
		private DebugActivityGeoFactionObjective()
		{
		}

		private DebugActivityKind KindValue => (DebugActivityKind)Kind;

		public override string GetTitle()
		{
			if (!IsRevealedNow())
				return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_TITLE_UNKNOWN", GeoSiteDisplayHelper.GetDisplayName(Site));
			string key = IsHostileNow() ? "KEY_CHAOSFACTION_ACTIVITY_TITLE_HOSTILE_REVEALED" : "KEY_CHAOSFACTION_ACTIVITY_TITLE_ALLIED_REVEALED";
			return ChaosFactionLocalization.Get(key, KindValue.DisplayName(Site?.GeoLevel), GeoSiteDisplayHelper.GetDisplayName(Site));
		}

		public override string GetDescription()
		{
			if (!IsRevealedNow())
				return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_DESC_UNKNOWN", GeoSiteDisplayHelper.GetDisplayName(Site), RemainingTimeSuffix());
			if (IsHostileNow())
				return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_DESC_HOSTILE", KindValue.DisplayName(Site?.GeoLevel), GeoSiteDisplayHelper.GetDisplayName(Site), RemainingTimeSuffix());
			return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_DESC_ALLIED", KindValue.DisplayName(Site?.GeoLevel), GeoSiteDisplayHelper.GetDisplayName(Site), RemainingTimeSuffix());
		}

		/// Reads the live PendingDebugActivity rather than caching IsHostile on this
		/// objective directly — this instance is rebuilt fresh each AddActiveActivity call
		/// anyway (see DebugActivityTracker), so there's always exactly one source of truth.
		private bool IsHostileNow()
		{
			return !DebugActivityTracker.ActiveActivities.TryGetValue(Site, out PendingDebugActivity activity) || activity.IsHostile;
		}

		/// Same live-read reasoning as IsHostileNow() — an unresolvable Site/activity
		/// (already removed) defaults to "revealed" so GetTitle/GetDescription don't get
		/// stuck describing something that's no longer actually pending.
		private bool IsRevealedNow()
		{
			return !DebugActivityTracker.ActiveActivities.TryGetValue(Site, out PendingDebugActivity activity) || activity.IsRevealed;
		}

		public override IEnumerable<GeoActor> GetRelatedActors()
		{
			if (Site != null)
				yield return Site;
		}

		public override bool IsCompleted()
		{
			return Site == null || !DebugActivityTracker.ActiveActivities.ContainsKey(Site);
		}

		public override void OnAssignedToFaction(GeoFaction faction)
		{
		}

		public override void OnUnassignedFromFaction(GeoFaction faction)
		{
		}

		private string RemainingTimeSuffix()
		{
			if (!TryGetRemainingHours(out double remaining))
				return string.Empty;
			return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_REMAINING_TIME_SUFFIX", $"{Mathf.Max(0f, (float)remaining):0.0}");
		}

		public bool TryGetRemainingHours(out double hours)
		{
			hours = 0;
			if (Site?.GeoLevel == null || !DebugActivityTracker.ActiveActivities.TryGetValue(Site, out PendingDebugActivity activity))
				return false;
			hours = activity.ExpiresAtHours - AllyRequestTracker.CurrentHours(Site.GeoLevel);
			return true;
		}

		public GeoFaction ResolveOwningFaction() => Site?.GeoLevel?.PhoenixFaction;
	}

	/// <summary>
	/// Explicit follow-up request: this auto-spawner is **not** gated on
	/// `chaos_factions_debug 1` — it always runs, controlled purely by
	/// `ActivitySpawnChancePercentPerDay` (0 disables it). The console command's only
	/// remaining job is showing the "ChaosFactionsDebugOptions" context-menu button that
	/// lets the player manually spawn one of these activities for free — the activities
	/// themselves (auto-spawned or manually triggered) are a normal, always-available part
	/// of the mod, not a debug-only feature. Originally gated on `ChaosFactionDebugFlag.
	/// Enabled` when this was purely a testing tool; that gate was removed here (and only
	/// here — `DebugActivityPickerAbility`/`TryAddDebugActivityAbilities` still correctly
	/// gate the manual button itself) once the auto-spawner became the feature's normal,
	/// player-facing way of triggering activities.
	///
	/// Automatically spawns a Pure/Forsaken/etc. activity at eligible sites on its own,
	/// without the player clicking anything — explicit request, so hostile-activity
	/// testing doesn't require manually opening every candidate site's context menu.
	/// Driven by GeoLevelController.HourTicked (a real, public, sanctioned event —
	/// GeoLevelController.cs:337 — fired once per in-game hour from LevelHourlyUpdateCrt,
	/// the same hourly tick vanilla's own faction/haven/research updates run from), not a
	/// Harmony patch: no private method needed patching here since the event is already
	/// public.
	///
	/// Config expresses the chance as "per day" for readability (explicit request — "chance
	/// should be set for day to be easier to know chance for day") even though the actual
	/// roll happens once per hour, so it doesn't all-or-nothing at local midnight: converted
	/// to an equivalent independent hourly chance via `1 - (1 - dailyChance)^(1/24)`, the
	/// standard conversion where 24 independent hourly non-events compound to the same
	/// probability as one daily non-event — i.e. rolling this hourly chance 24 times and
	/// missing every time happens exactly as often as missing the single daily chance once.
	/// Each eligible site rolls independently (explicit request — "each 20% is independent
	/// for each active visible not destroyed mission type, so each nest for example has its
	/// own 20% daily roll chance"), reusing the exact same eligibility rules a manual
	/// "Add Pure/Forsaken Activity" click would need to pass (IsEligibleMission — mission
	/// type supported by this mod, not completed, site still Functioning) plus visibility
	/// to the player and DebugActivityTracker.CanAddActivity (not already busy with a help
	/// request or another debug activity).
	/// </summary>
	internal static class AutoDebugActivitySpawner
	{
		/// <summary>Subscribed once per Geoscape session — see ChaosFactionGeoscape.OnGeoscapeStart.</summary>
		public static void OnHourTicked(GeoLevelController geoLevel, int elapsedHours)
		{
			if (geoLevel == null)
				return;
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			GeoFaction playerFaction = geoLevel.PhoenixFaction;
			IList<GeoSite> sites = geoLevel.Map?.AllSites;
			if (cfg == null || playerFaction == null || sites == null)
				return;

			double dailyChance = Mathf.Clamp(cfg.ActivitySpawnChancePercentPerDay, 0, 100) / 100.0;
			if (dailyChance <= 0)
				return;
			double hourlyChance = 1.0 - Math.Pow(1.0 - dailyChance, 1.0 / 24.0);

			foreach (GeoSite site in sites)
			{
				if (!IsEligibleForAutoSpawn(site, geoLevel, playerFaction, cfg))
					continue;
				if (UnityEngine.Random.value >= hourlyChance)
					continue;

				DebugActivityKind kind = DebugActivityKindExtensions.PickRandomWeightedByGroup(cfg.ActivityHostileGroupChancePercent);
				Log.Debug($"Auto debug activity roll succeeded at {GeoSiteDisplayHelper.GetDisplayName(site)}: spawning {kind.DisplayName(geoLevel)} (hourly chance {hourlyChance:P2}, configured {dailyChance:P0}/day).");
				DebugActivityTracker.AddActivity(site.ActiveMission, kind, cfg);
			}
		}

		private static bool IsEligibleForAutoSpawn(GeoSite site, GeoLevelController geoLevel, GeoFaction playerFaction, ChaosFactionConfig cfg)
		{
			GeoMission mission = site?.ActiveMission;
			if (mission == null)
				return false;
			if (!ContextualAbilitiesPatchHelper.IsEligibleMissionForActivity(mission, geoLevel, cfg))
				return false;
			if (!site.GetVisible(playerFaction))
				return false;
			return DebugActivityTracker.CanAddActivity(site);
		}
	}

	/// <summary>
	/// Cross-cutting runtime state for debug Pure/Forsaken hostile activities — mirrors
	/// AllyRequestTracker's own structure (ActiveActivities keyed by GeoSite, pending
	/// templates bridging Geoscape to Tactical, marker/objective/pause wiring via the same
	/// HelpRequestedMarker/SitePauseCoordinator, mission-lifecycle handling with the same
	/// "popup close vs. real cancel" guard) since it's solving the same underlying problem
	/// (a site-scoped, paid-for-free, not-yet-launched mod action) for a different kind of
	/// activity. Kept as a separate tracker rather than folding into AllyRequestTracker
	/// itself: the semantics (hostile vs. friendly, no payment, different display text) are
	/// different enough that sharing one dictionary/class would need type-branching
	/// throughout, whereas two parallel trackers that each check the other's dictionary for
	/// mutual exclusivity (CanAddActivity / AllyRequestTracker.CanRequestHelp) stays simple.
	/// </summary>
	internal static class DebugActivityTracker
	{
		/// Sites with an active (added, not yet launched/cancelled/expired) debug activity —
		/// same GeoSite-keyed, save-load-safe rationale as AllyRequestTracker.ActiveRequests,
		/// though this tracker's own entries are deliberately not persisted across a save
		/// (see PendingDebugActivity's own doc comment) — the dictionary being keyed by
		/// GeoSite still matters for the "mutual exclusivity + already added" checks to work
		/// correctly within a single session.
		public static readonly Dictionary<GeoSite, PendingDebugActivity> ActiveActivities = new Dictionary<GeoSite, PendingDebugActivity>();

		/// Bridges Geoscape (where the activity is added and its squad picked) to Tactical
		/// (where it's actually spawned) — same single-static-list rationale as
		/// AllyRequestTracker.PendingAllyTemplates: only one tactical mission can ever be in
		/// progress at a time.
		public static List<TacCharacterDef> PendingSquadTemplates = new List<TacCharacterDef>();
		public static PPFactionDef PendingActivityFactionDef;

		/// Whether the squad above spawns hostile to the player (true, always for Pure/
		/// Forsaken) or as an ally (false — New Jericho/Synedrion/Anu only, see
		/// ResolveIsHostile) — read by ChaosFactionTactical.SpawnRequestedDebugActivity to
		/// pick which relation-setting branch to use.
		public static bool PendingActivityIsHostile = true;

		/// <summary>
		/// Mutual exclusivity, both directions: a site can have at most one of {an active
		/// Ask-for-Help request, an active debug activity} at a time — explicit request
		/// ("all of those should be mutually exclusive"). AllyRequestTracker.CanRequestHelp
		/// has the matching check against this tracker's ActiveActivities.
		/// </summary>
		public static bool CanAddActivity(GeoSite site)
		{
			return site != null && !ActiveActivities.ContainsKey(site) && !AllyRequestTracker.ActiveRequests.ContainsKey(site);
		}

		/// <summary>
		/// Called from DebugActivityAbility.ActivateInternal — no payment/popup (this is a
		/// debug/testing tool, not a player-facing paid action), so this does everything
		/// AllyRequestTracker.MarkRequested does minus the cost-deduction step: resolve the
		/// activity's faction (sub-faction or main faction — see TryResolveActivityFaction),
		/// build the squad, mark the site active, wire the launch/cancel/expiry outcomes.
		/// </summary>
		public static void AddActivity(GeoMission mission, DebugActivityKind kind, ChaosFactionConfig cfg)
		{
			GeoSite site = mission?.Site;
			GeoLevelController geoLevel = site?.GeoLevel;
			if (site == null || geoLevel == null || !CanAddActivity(site))
				return;

			if (!TryResolveActivityFaction(mission, geoLevel, kind, out PPFactionDef factionDef, out List<TacCharacterDef> templatePool))
			{
				Log.Debug($"Could not resolve '{kind}' as an activity faction; refusing to add debug activity.");
				return;
			}

			bool isHostile = ResolveIsHostile(kind, geoLevel, cfg);

			// New Jericho/Anu/Synedrion debug activities only (explicit follow-up request —
			// see BuildSquad's own doc comment): map DebugActivityKind.NewJericho/.Anu/
			// .Synedrion to the matching HelperFactionKind so AllyRequestTracker.
			// ResolveAugmentedPool (the same lookup the Ask-for-Help squad already uses)
			// can be reused as-is rather than duplicated.
			HelperFactionKind? helperKind = ToHelperFactionKind(kind);
			(List<TacCharacterDef> augmentedPool, int augmentedChancePercent) = AllyRequestTracker.ResolveAugmentedPool(mission, helperKind, cfg);

			DifficultyThreatLevel threatLevel = ContextualAbilitiesPatchHelper.ResolveThreatLevel(mission);
			List<TacCharacterDef> squad = BuildSquad(templatePool, helperKind, threatLevel, cfg, augmentedPool, augmentedChancePercent);
			Log.Debug($"Debug activity '{kind}' added at {GeoSiteDisplayHelper.GetDisplayName(site)}: {squad.Count} {kind.DisplayName(geoLevel)} template(s), hostile={isHostile}.");

			PendingDebugActivity activity = new PendingDebugActivity
			{
				Kind = kind,
				IsHostile = isHostile,
				ExpiresAtHours = AllyRequestTracker.CurrentHours(geoLevel) + Math.Max(1, AllyRequestTracker.RollExpiryHours(cfg.PendingActivityHoursMin, cfg.PendingActivityHoursMax)),
				FactionDef = factionDef,
				Squad = squad,
			};
			AddActiveActivity(site, activity, geoLevel);
			PreloadAssets(squad);
			WireMissionLifecycle(site, mission, geoLevel);
		}

		/// <summary>Shared by AddActivity and RestorePendingActivities so the mapping only
		/// lives in one place.</summary>
		private static HelperFactionKind? ToHelperFactionKind(DebugActivityKind kind)
		{
			return kind == DebugActivityKind.NewJericho ? HelperFactionKind.NewJericho
				: kind == DebugActivityKind.Anu ? HelperFactionKind.Anu
				: kind == DebugActivityKind.Synedrion ? HelperFactionKind.Synedrion
				: (HelperFactionKind?)null;
		}

		/// <summary>
		/// Pure/Forsaken are GeoSubFaction-backed (DebugSubFactionResolver, by name); New
		/// Jericho/Synedrion/Anu are the same three regular GeoFactions Feature 1 already
		/// uses as helpers (GeoLevelController.*Faction) — different underlying types
		/// (GeoSubFaction isn't a GeoFaction, see the class-level doc comments), but both
		/// expose "its own PPFactionDef" and "a pool of TacCharacterDef templates for this
		/// mission" the same way, which is all AddActivity/BuildSquad actually need.
		/// Vehicles and Mutogs are no longer filtered out of the pool here — originally
		/// excluded outright since this debug tool's squad was a flat random sample with no
		/// equivalent per-faction cap/unit-size concept for them; now that BuildSquad rolls
		/// them the same capped, budgeted way AllyRequestTracker.BuildSquad already does
		/// (explicit follow-up request), the full pool is returned and BuildSquad itself
		/// splits it into vehicle/Mutog/infantry buckets, same as the Ask-for-Help squad.
		/// </summary>
		private static bool TryResolveActivityFaction(GeoMission mission, GeoLevelController geoLevel, DebugActivityKind kind, out PPFactionDef factionDef, out List<TacCharacterDef> templatePool)
		{
			factionDef = null;
			templatePool = null;
			if (kind.IsSubFaction())
			{
				GeoSubFaction subFaction = DebugSubFactionResolver.Resolve(geoLevel, kind.SubFactionShortName());
				if (subFaction == null)
					return false;
				factionDef = subFaction.PPFactionDef;
				templatePool = subFaction.GetDeploymentForMission(mission, 0).OfType<TacCharacterDef>().ToList();
				return true;
			}

			GeoFaction faction = kind.ResolveMainFaction(geoLevel);
			if (faction == null)
				return false;
			factionDef = faction.PPFactionDef;
			templatePool = faction.GetEligibleTemplatesForMission(mission, 0).ToList();
			return true;
		}

		/// <summary>
		/// Pure/Forsaken always spawn hostile (no diplomacy relationship toward the player
		/// exists for a GeoSubFaction the way it does for a GeoFaction). New Jericho/
		/// Synedrion/Anu can be picked/auto-rolled as a debug activity regardless of current
		/// relationship (explicit request — "can be rolled as activity regardless of
		/// friendship score") but only actually spawn hostile if the game's own diplomacy
		/// tier for them is Conflict (or worse) — explicit follow-up request, replacing an
		/// earlier hand-picked numeric threshold (HostileActivityRelationshipThreshold, now
		/// removed) with the real vanilla signal instead. Confirmed via source
		/// that PartyDiplomacyState.Conflict is the exact live condition vanilla's own
		/// VehicleFactionController.PlanCurrentSiteAttack checks before a faction's vehicles
		/// will actually schedule an attack on a player Haven/Phoenix base (via
		/// GetDiplomacyState, not the separate FactionDiplomacyState.AtWar bool — that flag
		/// has no combat effect at all, it's a one-shot "reveal this faction's sites" latch
		/// set the first time diplomacy crosses into Conflict, never read by any mission/AI
		/// code) — so this reuses the same tier the game itself treats as "genuinely
		/// hostile enough to attack," rather than a value tuned by feel. `None` (the enum's
		/// -1 sentinel, "no relation data") is explicitly excluded so it isn't mistaken for
		/// "worse than Conflict" by a bare `&lt;=` comparison. Above Conflict, they spawn as
		/// an ally instead (see SpawnRequestedDebugActivity's own doc comment on the
		/// Tactical side for how that changes relations). Read at AddActivity time
		/// (Geoscape, the only place GeoFaction.Diplomacy is reachable at all) and
		/// snapshotted onto PendingDebugActivity.IsHostile — not re-evaluated at mission
		/// launch, same tradeoff BuildSquad's own squad-composition roll already makes for
		/// this feature (see PendingDebugActivity's own "not persisted" doc comment: this
		/// is a debug tool, a stale read within the ~24h expiry window isn't worth extra
		/// plumbing to avoid).
		/// </summary>
		private static bool ResolveIsHostile(DebugActivityKind kind, GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			if (kind.IsSubFaction())
				return true;
			GeoFaction faction = kind.ResolveMainFaction(geoLevel);
			GeoFaction playerFaction = geoLevel.PhoenixFaction;
			if (faction == null || playerFaction == null)
				return true;
			PartyDiplomacyState state = faction.Diplomacy.GetDiplomacyState(playerFaction);
			return state != PartyDiplomacyState.None && state <= PartyDiplomacyState.Conflict;
		}

		/// <summary>
		/// Starts every activity in the unrevealed state (explicit request: "first display
		/// it as unknown activity on strategy map... and only to reveal what faction is
		/// there when user come to mission and open it") — a neutral gray marker with a
		/// generic "Unknown Activity" label, and the Alien faction's icon on the top-left
		/// objective (its own GetTitle/GetDescription already read PendingDebugActivity.
		/// IsRevealed live, so no generic "unknown" wording is baked in here — see
		/// DebugActivityGeoFactionObjective). See RevealIfNeeded for the actual reveal.
		/// </summary>
		private static void AddActiveActivity(GeoSite site, PendingDebugActivity activity, GeoLevelController geoLevel)
		{
			ActiveActivities[site] = activity;
			try
			{
				// Always the neutral Alien-faction icon while unrevealed, regardless of kind
				// — even for New Jericho/Synedrion/Anu (which get their own real icon once
				// revealed, see RevealIfNeeded), showing their actual crest here from the
				// start would give away the identity a "first display it as unknown" request
				// is specifically trying to hide.
				activity.Objective = new DebugActivityGeoFactionObjective(site, geoLevel.AlienFaction, activity.Kind);
				geoLevel.PhoenixFaction?.AddObjective(activity.Objective);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to add debug-activity Geoscape objective: " + ex);
			}
			SitePauseCoordinator.Increment(geoLevel);
			HelpRequestedMarker.Show(site, ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_MARKER_UNKNOWN"), activity.ExpiresAtHours, () => ExpireActivity(site, geoLevel), ChaosFactionColors.InfoGray);
		}

		/// <summary>
		/// Explicit request: reveal a pending activity's real faction/color the first time
		/// the player investigates the site — called from
		/// Patch_CommonMissionDataController_SetData's debug-activity mission-brief row
		/// (ChaosFactionPatches.cs), the ONE call site, since the mission-brief popup is
		/// only reachable once the player has actually sent an aircraft/squad to launch the
		/// mission — a genuine "come to mission and open it." Originally also called from
		/// ContextualAbilitiesPatchHelper.TryAddDebugActivityAbilities (opening the site's
		/// context menu) on the theory that a left-click was an equally valid "opened it" —
		/// reported in-game as wrong: GetContextualAbilities fires on *any* left-click of a
		/// visible site, no aircraft/squad presence required, so activities were revealing
		/// the instant the player merely clicked the map, not when they'd actually come to
		/// investigate. That call was removed; only the mission-brief popup reveals now. A
		/// one-way latch — IsRevealed only ever flips false→true, and every call after the
		/// first is a cheap no-op.
		///
		/// Updates three surfaces: the map marker (HelpRequestedMarker.UpdateAppearance —
		/// color + label text, in place), and the top-left objective, which has to be
		/// *rebuilt* rather than updated in place — GeoFactionObjective.GivenByFaction
		/// (confirmed via source) is `readonly`, so the icon can't be swapped on
		/// the existing instance; RemoveObjective+AddObjective is the same pair this tracker
		/// already calls routinely elsewhere (mission cancel/launch), so recreating the
		/// objective here is a proven-safe operation, not a new risk. The context-menu
		/// status entry and mission-brief row text themselves need no separate handling for
		/// reveal state — they always show the true Kind/IsHostile regardless of whether
		/// this method has ever been called (see DebugActivityAbilityFactory.
		/// GetOrCreateStatus/Patch_CommonMissionDataController_SetData) — only the map
		/// marker and top-left objective have an actual "unrevealed" display state.
		/// </summary>
		public static void RevealIfNeeded(GeoSite site, GeoLevelController geoLevel)
		{
			if (site == null || geoLevel == null || !ActiveActivities.TryGetValue(site, out PendingDebugActivity activity) || activity.IsRevealed)
				return;
			activity.IsRevealed = true;

			try
			{
				if (activity.Objective != null)
					geoLevel.PhoenixFaction?.RemoveObjective(activity.Objective);
				GeoFaction revealedIconFaction = activity.Kind.IsSubFaction()
					? geoLevel.AlienFaction
					: activity.Kind.ResolveMainFaction(geoLevel) ?? geoLevel.AlienFaction;
				activity.Objective = new DebugActivityGeoFactionObjective(site, revealedIconFaction, activity.Kind);
				geoLevel.PhoenixFaction?.AddObjective(activity.Objective);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to refresh debug-activity Geoscape objective on reveal: " + ex);
			}

			// Allied case reuses HelpRequestedMarker.MarkerColor directly (the same shade
			// an actual Ask-for-Help marker uses), not ChaosFactionColors.LightBlue — the
			// latter is a different, only visually-similar shade shared by the
			// context-menu/mission-brief "informational" text, not the map marker itself.
			Color color = activity.IsHostile ? ChaosFactionColors.HostileRed : HelpRequestedMarker.MarkerColor;
			HelpRequestedMarker.UpdateAppearance(site, ChaosFactionLocalization.Get("KEY_CHAOSFACTION_ACTIVITY_MARKER_REVEALED", activity.Kind.DisplayName(geoLevel)), color);
			Log.Debug($"Revealed debug activity at {GeoSiteDisplayHelper.GetDisplayName(site)}: {activity.Kind.DisplayName(geoLevel)} (hostile={activity.IsHostile}).");
		}

		private static bool RemoveActiveActivity(GeoSite site, GeoLevelController geoLevel)
		{
			if (!ActiveActivities.TryGetValue(site, out PendingDebugActivity activity))
				return false;
			ActiveActivities.Remove(site);
			if (activity.Objective != null && geoLevel?.PhoenixFaction != null)
			{
				try
				{
					geoLevel.PhoenixFaction.RemoveObjective(activity.Objective);
				}
				catch (Exception ex)
				{
					Log.Debug("Failed to remove debug-activity Geoscape objective: " + ex);
				}
			}
			SitePauseCoordinator.Decrement(geoLevel);
			HelpRequestedMarker.Hide(site);
			return true;
		}

		private static void ExpireActivity(GeoSite site, GeoLevelController geoLevel)
		{
			if (!RemoveActiveActivity(site, geoLevel))
				return;
			Log.Debug($"Debug activity at {GeoSiteDisplayHelper.GetDisplayName(site)} expired without launching.");
		}

		/// <summary>Flattens a live activity into its save form; null if it can't be saved.
		/// Shared by ChaosFactionGeoscape.RecordGeoscapeInstanceData and the launch
		/// snapshot (LaunchedMissionSnapshots).</summary>
		internal static ChaosFactionSavedActivity ToSaved(GeoSite site, PendingDebugActivity activity)
		{
			if (site == null || site.SiteId < 0 || activity == null)
				return null;
			return new ChaosFactionSavedActivity
			{
				SiteId = site.SiteId,
				Kind = (int)activity.Kind,
				IsHostile = activity.IsHostile,
				IsRevealed = activity.IsRevealed,
				ExpiresAtHours = activity.ExpiresAtHours,
			};
		}

		/// <summary>
		/// Called once from ChaosFactionGeoscape.OnGeoscapeStart, every time the Geoscape
		/// (re)loads — not just a true save/load from the main menu, but also the ordinary
		/// scene reload that happens after launching (and returning from) any tactical
		/// mission, confirmed by ChaosFactionGeoscape's own doc comment ("Each time Geoscape
		/// level is loaded, new mod's ModGeoscape is created"). Debug activities are now
		/// persisted/restored the same way AllyRequestTracker.RestorePendingRequests already
		/// does for Ask-for-Help requests — explicit follow-up request ("activity state is
		/// not saved after save/load or after finishing another mission, make it to save and
		/// restore the same way as it is done for help request"), replacing what used to be
		/// an unconditional discard (an accepted limitation for a "testing tool" — no longer
		/// the case now that activities auto-spawn as a normal, always-on part of the mod).
		///
		/// Two things this mod itself owns need cleaning up regardless of whether a given
		/// entry gets restored, same reasoning the old discard-everything version already
		/// established:
		/// 1. ActiveActivities is a plain static Dictionary — untouched by the game's own
		///    serialize/reload cycle, so any entry still in it at this point is keyed by (or
		///    was wired to events on) a GeoSite/GeoMission instance from *before* the reload.
		///    Its OnMissionActivated/OnMissionCancel subscriptions are dead, so it has to be
		///    cleared and rebuilt fresh from ChaosFactionGSInstanceData below, not trusted.
		/// 2. DebugActivityGeoFactionObjective is [SerializeType] (it lives in
		///    GeoFaction._objectives, itself part of normal save data) — vanilla's own
		///    serializer can round-trip a stray, disconnected copy of it into the fresh
		///    GeoFaction.Objectives that this mod's own save data (the actual source of
		///    truth) knows nothing about; stripped defensively before restoring, same as
		///    AllyRequestTracker.RestorePendingRequests already does for its own objective
		///    type.
		///
		/// Deliberately conservative, same as AllyRequestTracker's own restore: any entry
		/// that can't be fully resolved (site/mission gone, faction unavailable, site no
		/// longer eligible) is just skipped and logged rather than guessed at.
		/// </summary>
		public static void RestorePendingActivities(GeoLevelController geoLevel, IEnumerable<ChaosFactionSavedActivity> saved)
		{
			if (geoLevel?.PhoenixFaction != null)
			{
				foreach (DebugActivityGeoFactionObjective stray in geoLevel.PhoenixFaction.Objectives.OfType<DebugActivityGeoFactionObjective>().ToList())
					geoLevel.PhoenixFaction.RemoveObjective(stray);
			}

			if (ActiveActivities.Count > 0)
			{
				Log.Debug($"Geoscape (re)started with {ActiveActivities.Count} debug activit{(ActiveActivities.Count == 1 ? "y" : "ies")} still tracked from before the reload; discarding in-memory state (rebuilt fresh from save data below, if any).");
				foreach (GeoSite site in ActiveActivities.Keys.ToList())
					RemoveActiveActivity(site, geoLevel);
			}

			if (geoLevel == null || saved == null)
				return;

			foreach (ChaosFactionSavedActivity entry in saved)
			{
				try
				{
					// Saved while the mission was launching, and that mission has since
					// completed (Geoscape reload after tactical) — see LaunchedMissionSnapshots.
					if (LaunchedMissionSnapshots.WasCompleted(entry.SiteId))
					{
						Log.Debug($"Not restoring debug activity for SiteId {entry.SiteId}: its mission was launched and has completed.");
						continue;
					}
					GeoSite site = geoLevel.Map?.AllSites?.FirstOrDefault(s => s.SiteId == entry.SiteId);
					GeoMission mission = site?.ActiveMission;
					if (site == null || mission == null)
					{
						Log.Debug($"Could not restore debug activity for SiteId {entry.SiteId}: site or its ActiveMission no longer exists.");
						continue;
					}
					if (!CanAddActivity(site))
					{
						Log.Debug($"Could not restore debug activity for SiteId {entry.SiteId}: site is no longer eligible (already has an active request/activity).");
						continue;
					}

					DebugActivityKind kind = (DebugActivityKind)entry.Kind;
					if (!TryResolveActivityFaction(mission, geoLevel, kind, out PPFactionDef factionDef, out List<TacCharacterDef> _))
					{
						Log.Debug($"Could not restore debug activity for SiteId {entry.SiteId}: could not resolve '{kind}' as an activity faction.");
						continue;
					}

					PendingDebugActivity activity = new PendingDebugActivity
					{
						Kind = kind,
						IsHostile = entry.IsHostile,
						ExpiresAtHours = entry.ExpiresAtHours,
						FactionDef = factionDef,
						// Squad: null — never persisted (TacCharacterDef references aren't
						// safe to serialize, same reasoning AllyRequestTracker's own restore
						// already established) — WireMissionLifecycle rebuilds it fresh at
						// actual launch time instead, from Kind.
					};
					AddActiveActivity(site, activity, geoLevel);
					// AddActiveActivity always creates it unrevealed (matching a brand-new
					// activity) — RevealIfNeeded (only called if it had actually been
					// revealed before the reload) is what flips it back and correctly
					// updates the marker/objective, reusing that method's own display logic
					// rather than duplicating it here.
					if (entry.IsRevealed)
						RevealIfNeeded(site, geoLevel);

					WireMissionLifecycle(site, mission, geoLevel);
					Log.Debug($"Restored debug activity '{kind}' for {GeoSiteDisplayHelper.GetDisplayName(site)}, {(entry.ExpiresAtHours - AllyRequestTracker.CurrentHours(geoLevel)):0.0}h remaining.");
				}
				catch (Exception ex)
				{
					Log.Debug($"Failed to restore debug activity for SiteId {entry.SiteId}: " + ex);
				}
			}
		}

		/// Guards WireMissionLifecycle against subscribing more than once per mission —
		/// see that method's own doc comment for the bug this fixes. Never cleared: a
		/// GeoMission only ever activates/cancels once in its life, so a stale entry
		/// for an already-concluded mission is harmless dead weight, same reasoning
		/// HavenDefenseRewardTracker.ProcessedMissions already relies on.
		private static readonly HashSet<GeoMission> WiredMissions = new HashSet<GeoMission>();

		/// <summary>
		/// Wires OnMissionActivated/OnMissionCancel exactly ONCE per mission — explicit
		/// bug report: "when first time Activity rolled with Pure, and then when
		/// player didn't come and it rolled second time with Bandits, it is displayed
		/// all correct that there are bandits on strategy map, but when I start
		/// mission, there are Pure instead of bandits."
		///
		/// Root cause: this method used to be called again, on the same live mission,
		/// every time a new activity was rolled for the same still-active site (an
		/// expired activity being replaced by a fresh auto-spawner roll never touches
		/// the mission's own events at all — only ActiveActivities[site] changes). Each
		/// call subscribed a FRESH pair of closures that captured that call's own
		/// kind/factionDef/squad, stacking on top of whatever was already subscribed
		/// from the previous roll — there was never a stored delegate reference to
		/// unsubscribe the old one with. Both handlers then fired when the mission
		/// actually launched, in registration order: the STALE (first-registered, e.g.
		/// Pure) one ran first, successfully removed the by-then-Bandits
		/// ActiveActivities entry via RemoveActiveActivity, and populated the Pending*
		/// bridging statics with ITS OWN stale data; the fresh (Bandits) closure then
		/// ran second, found the entry already removed, and bailed out via its own
		/// `if (!RemoveActiveActivity(...)) return;` guard — so the stale squad/faction
		/// data silently won, even though the Geoscape display (which reads
		/// ActiveActivities[site]/PendingDebugActivity.Kind directly, not through this
		/// event chain) had already shown the correct, current activity the whole time.
		///
		/// Fix: subscribe only once per mission (WiredMissions guards this), and have
		/// that one handler read whatever's CURRENTLY in ActiveActivities[site] at the
		/// moment the mission actually activates, instead of closing over data captured
		/// back whenever it was first wired — so it doesn't matter how many times the
		/// activity attached to this site gets re-rolled in between, the handler always
		/// reflects the latest one. PendingDebugActivity.FactionDef/Squad carry what
		/// used to be separate parameters passed in at wire-time.
		/// </summary>
		private static void WireMissionLifecycle(GeoSite site, GeoMission mission, GeoLevelController geoLevel)
		{
			if (mission == null || !WiredMissions.Add(mission))
				return;

			mission.OnMissionActivated += (m, result) =>
			{
				if (!ActiveActivities.TryGetValue(site, out PendingDebugActivity activity))
					return;
				// Keep a copy for the pre-mission autosave vanilla writes right after
				// this event — see LaunchedMissionSnapshots.
				ChaosFactionSavedActivity snapshot = ToSaved(site, activity);
				if (!RemoveActiveActivity(site, geoLevel))
					return;
				if (snapshot != null)
					LaunchedMissionSnapshots.Activities[snapshot.SiteId] = snapshot;
				List<TacCharacterDef> finalSquad = activity.Squad;
				if (finalSquad == null)
				{
					ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
					if (cfg != null && TryResolveActivityFaction(m, geoLevel, activity.Kind, out PPFactionDef _, out List<TacCharacterDef> pool))
					{
						HelperFactionKind? helperKind = ToHelperFactionKind(activity.Kind);
						(List<TacCharacterDef> augmentedPool, int augmentedChancePercent) = AllyRequestTracker.ResolveAugmentedPool(m, helperKind, cfg);
						DifficultyThreatLevel threatLevel = ContextualAbilitiesPatchHelper.ResolveThreatLevel(m);
						finalSquad = BuildSquad(pool, helperKind, threatLevel, cfg, augmentedPool, augmentedChancePercent);
					}
					else
					{
						finalSquad = new List<TacCharacterDef>();
					}
					PreloadAssets(finalSquad);
				}
				PendingSquadTemplates = finalSquad;
				PendingActivityFactionDef = activity.FactionDef;
				PendingActivityIsHostile = activity.IsHostile;
				Log.Debug($"Mission activated; debug activity squad ({finalSquad.Count}, kind={activity.Kind}, hostile={activity.IsHostile}) queued for spawn on tactical start.");
			};
			mission.OnMissionCancel += m =>
			{
				// Mirrors AllyRequestTracker.WireMissionLifecycle's own guard: GeoMission.
				// Cancel() fires this same event both for a true decline and for merely
				// closing the launch-confirmation popup without actually declining (the
				// mission stays the site's ActiveMission) — only treat the former as real.
				if (m.Site != null && m.Site.ActiveMission == m)
				{
					Log.Debug("Mission-brief closed without declining the mission itself; keeping the pending debug activity.");
					return;
				}
				if (RemoveActiveActivity(site, geoLevel))
					Log.Debug("Mission cancelled before launch; debug activity removed.");
			};
		}

		private static void PreloadAssets(List<TacCharacterDef> squad)
		{
			AssetsManager assetsManager = GameUtl.GameComponent<AssetsManager>();
			if (assetsManager == null)
				return;
			assetsManager.StartAcquireDependencies(squad.Cast<object>(), "ChaosFaction debug activity squad");
			Log.Debug($"Started preloading assets for {squad.Count} debug activity template(s).");
		}

		/// <summary>
		/// Per explicit request: "use same HelpMinSquadSize and HelpMaxSquadSize to spawn
		/// that number of their soldiers" — the rolled range is a slot budget, same as
		/// AllyRequestTracker.BuildSquad's, since vehicles/Mutogs weigh more than a regular
		/// trooper against it (see below). Regular trooper slots are still filled by
		/// sampling with replacement from whatever template pool TryResolveActivityFaction
		/// resolved (GeoSubFaction.GetDeploymentForMission for Pure/Forsaken — falls back to
		/// SubFactionDef.StartingUnits internally if the sub-faction's own eligible-
		/// templates list is empty, see GeoSubFaction.cs — or GeoFaction.
		/// GetEligibleTemplatesForMission for New Jericho/Synedrion/Anu) — this method's own
		/// "usual logic" was always plain sampling with replacement (no diversity-first
		/// concept, unlike AllyRequestTracker.BuildSquad's `PickPreferringNewClass`), and
		/// that stays unchanged; only the vehicle/Mutog handling was added.
		/// <paramref name="augmentedPool"/>/<paramref name="augmentedChancePercent"/>
		/// (New Jericho/Anu debug activities only — see AddActivity) apply the same
		/// per-soldier pool-swap this mod's Ask-for-Help squads use
		/// (AllyRequestTracker.ResolveAugmentedPool) — explicit follow-up request: "I want
		/// to any New Jericho/Anu squad, regardless if it is help squad or it is from
		/// activity." Only ever applies to regular trooper slots, same as the Ask-for-Help
		/// squad — vehicles/Mutogs are never pulled from the augmented pool either way.
		/// <paramref name="kind"/> (New Jericho/Synedrion/Anu only, `null` for the three
		/// GeoSubFaction-backed kinds — Pure/Forsaken/Bandits) drives vehicle/Mutog rolling
		/// — explicit follow-up request: "activity spawned units never roll vehicle/mutog...
		/// allow it to be rolled identically as it done in help requests." Reuses
		/// AllyRequestTracker.RollBigUnitSlots (the same streak-roll: keep succeeding, keep
		/// adding, first failure ends it) and the exact same per-faction max/unit-size
		/// resolution AllyRequestTracker.BuildSquad already does — a `null` kind (the
		/// sub-faction cases) naturally falls through the switch below to a 0 max, same as
		/// it does there for any faction that isn't New Jericho/Synedrion/Anu.
		/// </summary>
		private static List<TacCharacterDef> BuildSquad(List<TacCharacterDef> pool, HelperFactionKind? kind, DifficultyThreatLevel? threatLevel, ChaosFactionConfig cfg, List<TacCharacterDef> augmentedPool = null, int augmentedChancePercent = 0)
		{
			if (pool == null || pool.Count == 0)
				return new List<TacCharacterDef>();

			MissionHelpSettings settings = MissionHelpSettingsResolver.Get(threatLevel, cfg);
			List<TacCharacterDef> vehicleCandidates = pool.Where(t => t.IsVehicle).OrderBy(_ => UnityEngine.Random.value).ToList();
			List<TacCharacterDef> mutogCandidates = pool.Where(t => t.IsMutog).OrderBy(_ => UnityEngine.Random.value).ToList();
			List<TacCharacterDef> infantry = pool.Where(t => !t.IsVehicle && !t.IsMutog).ToList();

			int minSize = Mathf.Min(settings.MinSquadSize, settings.MaxSquadSize);
			int maxSize = Mathf.Max(settings.MinSquadSize, settings.MaxSquadSize);
			int squadSlotBudget = Mathf.Max(1, UnityEngine.Random.Range(minSize, maxSize + 1));
			Log.Debug($"Rolled debug activity squad slot budget: {squadSlotBudget} (range {minSize}-{maxSize}).");

			int vehicleChance = Mathf.Clamp(settings.VehicleSpawnChancePercent, 0, 100);
			int vehicleMax = 0;
			int vehicleUnitSize = 1;
			switch (kind)
			{
				case HelperFactionKind.NewJericho:
					vehicleMax = Mathf.Max(0, settings.MaxNewJerichoVehiclesInSquad);
					vehicleUnitSize = Mathf.Max(1, cfg.NewJerichoVehicleUnitSize);
					break;
				case HelperFactionKind.Synedrion:
					vehicleMax = Mathf.Max(0, settings.MaxSynedrionVehiclesInSquad);
					vehicleUnitSize = Mathf.Max(1, cfg.SynedrionVehicleUnitSize);
					break;
			}
			int mutogMax = kind == HelperFactionKind.Anu ? Mathf.Max(0, settings.MaxAnuMutogsInSquad) : 0;
			int mutogUnitSize = Mathf.Max(1, cfg.MutogUnitSize);

			List<TacCharacterDef> squad = new List<TacCharacterDef>();
			int slotsUsed = AllyRequestTracker.RollBigUnitSlots(vehicleCandidates, vehicleMax, vehicleUnitSize, vehicleChance, squadSlotBudget, squad);
			slotsUsed += AllyRequestTracker.RollBigUnitSlots(mutogCandidates, mutogMax, mutogUnitSize, vehicleChance, squadSlotBudget - slotsUsed, squad);

			while (slotsUsed < squadSlotBudget)
			{
				bool useAugmented = augmentedPool != null && augmentedPool.Count > 0
					&& UnityEngine.Random.Range(0, 100) < augmentedChancePercent;
				List<TacCharacterDef> sourcePool = useAugmented ? augmentedPool : infantry;
				if (sourcePool == null || sourcePool.Count == 0)
					sourcePool = useAugmented ? infantry : augmentedPool;
				if (sourcePool == null || sourcePool.Count == 0)
					break;
				squad.Add(sourcePool[UnityEngine.Random.Range(0, sourcePool.Count)]);
				slotsUsed++;
			}
			return squad;
		}

	}

	/// <summary>
	/// "ChaosFactionsDebugOptions" — the single debug/testing context-menu action, shown
	/// only when ChaosFactionDebugFlag.Enabled. Replaces the original one-entry-per-kind
	/// design (up to 8 "Add X Activity" buttons, one per DebugActivityKindExtensions.All)
	/// after that actually crashed the context menu in-game once the kind count grew from
	/// 5 to 8 — UIModuleSiteContextualMenu.SetMenuItems indexes into a fixed-size button
	/// pool with no bounds check, and 8 simultaneous entries (plus Ask-for-Help/
	/// mission-info) pushed a site's total past it. Clicking this single entry instead
	/// opens a MessageBox text-input popup (ActivateInternal) listing every kind by number
	/// and name; the player types a number or name and confirms, and only then does the
	/// matching action actually run — same "no payment, immediate effect once confirmed"
	/// behavior as the original per-kind buttons, just funneled through one shared picker.
	/// Also offers a **free** Ask-for-Help grant for each of the three real helper
	/// factions (New Jericho/Anu/Synedrion) — explicit follow-up request, so this debug
	/// tool doesn't only cover the hostile/ally "activity" system, but Feature 1's actual
	/// paid ally-request flow too, minus the payment. Reuses AllyRequestTracker.
	/// MarkRequested directly with a zero-amount FactionHelpOffer — the exact same
	/// mechanism HavenDefenseRewardTracker's free-help reward already uses (see
	/// ChaosFactionState.cs), just triggered on demand instead of after a Haven defense,
	/// and with no relationship-threshold gate (this is a debug tool; every other action
	/// in this picker already bypasses cost/relationship gates the same way).
	/// </summary>
	internal class DebugActivityPickerAbility : GeoAbility
	{
		/// The three real helper factions (Feature 1's own candidates), in the fixed order
		/// they're numbered after the DebugActivityKind entries in the picker list.
		private static readonly HelperFactionKind[] FreeHelpKinds =
		{
			HelperFactionKind.NewJericho,
			HelperFactionKind.Anu,
			HelperFactionKind.Synedrion,
		};

		public GeoMission Mission;

		protected override GeoAbilityDisabledState GetDisabledStateInternal()
		{
			if (!ChaosFactionDebugFlag.Enabled)
				return GeoAbilityDisabledState.RequirementsNotMet;
			if (Mission?.Site == null || !DebugActivityTracker.CanAddActivity(Mission.Site))
				return GeoAbilityDisabledState.RequirementsNotMet;
			return GeoAbilityDisabledState.NotDisabled;
		}

		protected override void ActivateInternal(GeoAbilityTarget target)
		{
			MessageBox messageBox = GameUtl.GetMessageBox();
			if (messageBox == null || Mission == null)
				return;

			messageBox.ShowInputPrompt(
				BuildPickerContent(Mission.Site?.GeoLevel),
				"",
				null,
				MessageBoxIcon.Question,
				MessageBoxButtons.OKCancel,
				OnPickerResult,
				this,
				null);
		}

		private void OnPickerResult(MessageBoxCallbackResult result)
		{
			// MessageBoxResult.Cancel (or anything else): player backed out, nothing to do.
			if (result.DialogResult != MessageBoxResult.OK)
				return;

			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null || Mission == null)
				return;

			if (!TryResolveChoiceFromInput(result.InputTextResult, Mission.Site?.GeoLevel, out DebugActivityKind? activityKind, out HelperFactionKind? helperKind))
			{
				int totalOptions = DebugActivityKindExtensions.All.Length + FreeHelpKinds.Length;
				GameUtl.GetMessageBox()?.ShowSimplePrompt(
					$"'{result.InputTextResult}' isn't one of the options. Enter a number 1-{totalOptions} or a name from the list.",
					MessageBoxIcon.Error, MessageBoxButtons.OK, null, null, null);
				return;
			}

			if (activityKind.HasValue)
				DebugActivityTracker.AddActivity(Mission, activityKind.Value, cfg);
			else
				GrantFreeAllyHelp(helperKind.Value, cfg);
		}

		/// <summary>
		/// Mutual exclusivity via DebugActivityTracker.CanAddActivity (checks both trackers'
		/// dictionaries — same gate the picker entry's own GetDisabledStateInternal already
		/// used to decide whether to show at all), not AllyRequestTracker.CanRequestHelp —
		/// deliberately skips the relationship-threshold check that method would otherwise
		/// apply, since this is a free, on-demand debug grant, not a real paid request.
		/// </summary>
		private void GrantFreeAllyHelp(HelperFactionKind helperKind, ChaosFactionConfig cfg)
		{
			GeoSite site = Mission.Site;
			GeoLevelController geoLevel = site?.GeoLevel;
			GeoFaction playerFaction = geoLevel?.PhoenixFaction;
			GeoFaction helperFaction = helperKind.ToFaction(geoLevel);
			if (site == null || geoLevel == null || playerFaction == null || helperFaction == null)
			{
				Log.Debug($"Could not resolve site/faction data to grant free {helperKind} help.");
				return;
			}
			if (!DebugActivityTracker.CanAddActivity(site))
			{
				Log.Debug($"Site already has an active help request or debug activity; refusing free {helperKind} help grant.");
				return;
			}

			// Amount 0 both ways — nothing is actually charged either way, so there's
			// nothing for a later cancel/expire to have needed to refund regardless.
			FactionHelpOffer freeOffer = new FactionHelpOffer { ResourceType = ResourceType.Supplies, ResourceAmount = 0, RelationshipAmount = 0 };
			AllyRequestTracker.MarkRequested(Mission, playerFaction, helperFaction, cfg, AskForHelpPaymentMethod.Resource, freeOffer);
			Log.Debug($"Debug: granted free {helperKind} help at {GeoSiteDisplayHelper.GetDisplayName(site)}.");
		}

		private static string BuildPickerContent(GeoLevelController geoLevel)
		{
			var content = new System.Text.StringBuilder("Choose a debug activity to add (type its number or name):\n");
			DebugActivityKind[] all = DebugActivityKindExtensions.All;
			for (int i = 0; i < all.Length; i++)
				content.Append($"{i + 1}. {all[i].DisplayName(geoLevel)}\n");

			for (int i = 0; i < FreeHelpKinds.Length; i++)
				content.Append($"{all.Length + i + 1}. Request {FreeHelpKinds[i].DisplayName(geoLevel)} Help\n");
			return content.ToString();
		}

		/// <summary>
		/// Numbers 1..DebugActivityKindExtensions.All.Length resolve to an activity kind;
		/// the next FreeHelpKinds.Length numbers resolve to a free-help faction instead.
		/// Name matching checks activity kinds first (so plain "New Jericho" still means
		/// the DebugActivityKind, unchanged from before this was added) — only the
		/// distinctly-labeled "Free New Jericho Help" (or its own number) reaches the free
		/// ally-help path, so there's no ambiguity between the two New Jericho options.
		/// geoLevel (optional — may be null) lets name matching also accept whatever the
		/// live localized name actually is (e.g. the real, non-English name in another
		/// language), not just the hardcoded-English DisplayName() fallback; numeric input
		/// always works regardless.
		/// </summary>
		private static bool TryResolveChoiceFromInput(string input, GeoLevelController geoLevel, out DebugActivityKind? activityKind, out HelperFactionKind? helperKind)
		{
			activityKind = null;
			helperKind = null;
			if (string.IsNullOrWhiteSpace(input))
				return false;
			input = input.Trim();

			DebugActivityKind[] all = DebugActivityKindExtensions.All;
			if (int.TryParse(input, out int index) && index >= 1)
			{
				if (index <= all.Length)
				{
					activityKind = all[index - 1];
					return true;
				}
				int helperIndex = index - all.Length - 1;
				if (helperIndex >= 0 && helperIndex < FreeHelpKinds.Length)
				{
					helperKind = FreeHelpKinds[helperIndex];
					return true;
				}
				return false;
			}

			foreach (DebugActivityKind candidate in all)
			{
				if (string.Equals(candidate.ToString(), input, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(candidate.DisplayName(), input, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(candidate.DisplayName(geoLevel), input, StringComparison.OrdinalIgnoreCase))
				{
					activityKind = candidate;
					return true;
				}
			}
			foreach (HelperFactionKind candidate in FreeHelpKinds)
			{
				if (string.Equals($"Free {candidate.DisplayName(geoLevel)} Help", input, StringComparison.OrdinalIgnoreCase))
				{
					helperKind = candidate;
					return true;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Builds/caches DebugActivityPickerAbility (one per mission — a single shared entry
	/// covering every DebugActivityKind, see that class's own doc comment for why) and
	/// reuses AlreadyRequestedHelpAbility (see ChaosFactionAbility.cs) for the "activity
	/// already active" status entry, same as AskForHelpAbilityFactory does for its own
	/// status entry — just with StatusColor set to ChaosFactionColors.HostileRed instead of
	/// the default light blue, so the two read as visually distinct (friendly vs. hostile)
	/// at a glance.
	/// </summary>
	internal static class DebugActivityAbilityFactory
	{
		private static readonly Dictionary<GeoMission, DebugActivityPickerAbility> PickerCache = new Dictionary<GeoMission, DebugActivityPickerAbility>();
		private static readonly Dictionary<GeoMission, AlreadyRequestedHelpAbility> StatusCache = new Dictionary<GeoMission, AlreadyRequestedHelpAbility>();
		private static readonly Dictionary<GeoMission, AlreadyRequestedHelpAbility> MissionInfoCache = new Dictionary<GeoMission, AlreadyRequestedHelpAbility>();

		/// <summary>
		/// Debug/testing-only, always-disabled entry showing this site's mission's own
		/// internal C# type name (e.g. "GeoPhoenixBaseDefenseMission") and MissionDef asset
		/// name — explicit request: "just to know name of site mission for internal use
		/// later by mod to more easily identify". Shown for *any* mission on the site
		/// (unlike the paid/debug-activity abilities, not gated on IsEligibleMission at
		/// all — the whole point is to identify mission types this mod doesn't necessarily
		/// support yet), reusing AlreadyRequestedHelpAbility the same way GetOrCreateStatus
		/// does, just with ChaosFactionColors.InfoGray instead of blue/red.
		/// </summary>
		public static AlreadyRequestedHelpAbility GetOrCreateMissionInfo(GeoMission mission)
		{
			string missionTypeName = mission.GetType().Name;
			string missionDefName = mission.MissionDef != null ? mission.MissionDef.name : "?";
			string text = $"Mission: {missionTypeName} (MissionDef: {missionDefName})";

			if (MissionInfoCache.TryGetValue(mission, out AlreadyRequestedHelpAbility existing))
			{
				if (existing.GeoscapeAbilityDef is AskForHelpAbilityDef existingDef && existingDef.ViewElementDef != null)
					existingDef.ViewElementDef.Description = new LocalizedTextBind(text, true);
				return existing;
			}

			GeoscapeAbilityViewElementDef viewElementDef = ScriptableObject.CreateInstance<GeoscapeAbilityViewElementDef>();
			viewElementDef.name = "ChaosFaction_DebugMissionInfoViewElementDef";
			viewElementDef.DisplayName1 = new LocalizedTextBind($"[Debug] {missionTypeName}", true);
			viewElementDef.Description = new LocalizedTextBind(text, true);
			viewElementDef.DisplayPriority = 0;
			viewElementDef.ShowContextMenuTooltip = true;
			viewElementDef.ContextMenuShowOnlyIfViewFaction = true;
			viewElementDef.ContextMenuVisibility = NewVisibilityCondition();
			viewElementDef.ActionBarVisibility = NewVisibilityCondition();
			viewElementDef.CallToActionButtonVisibility = NewVisibilityCondition();

			AskForHelpAbilityDef abilityDef = ScriptableObject.CreateInstance<AskForHelpAbilityDef>();
			abilityDef.name = "ChaosFaction_DebugMissionInfoAbilityDef";
			abilityDef.ViewElementDef = viewElementDef;
			abilityDef.Cost = new ResourcePack();

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			AlreadyRequestedHelpAbility ability = new AlreadyRequestedHelpAbility { StatusColor = ChaosFactionColors.InfoGray };
			ability.Init(abilityDef, repo);
			ability.SetActor(mission.Site);

			MissionInfoCache[mission] = ability;
			mission.OnMissionActivated += (m, result) => CleanupMissionInfo(m);
			mission.OnMissionCancel += CleanupMissionInfo;

			return ability;
		}

		private static void CleanupMissionInfo(GeoMission mission)
		{
			if (!MissionInfoCache.TryGetValue(mission, out AlreadyRequestedHelpAbility ability))
				return;
			MissionInfoCache.Remove(mission);
			DestroyDefs(ability.GeoscapeAbilityDef as AskForHelpAbilityDef);
		}

		/// <summary>
		/// One shared "ChaosFactionsDebugOptions" entry per mission — see
		/// DebugActivityPickerAbility's own doc comment for why this replaced one button
		/// per DebugActivityKind.
		/// </summary>
		public static DebugActivityPickerAbility GetOrCreatePicker(GeoMission mission)
		{
			if (PickerCache.TryGetValue(mission, out DebugActivityPickerAbility existing))
				return existing;

			GeoscapeAbilityViewElementDef viewElementDef = ScriptableObject.CreateInstance<GeoscapeAbilityViewElementDef>();
			viewElementDef.name = "ChaosFaction_DebugActivityPickerViewElementDef";
			viewElementDef.DisplayName1 = new LocalizedTextBind("ChaosFactionsDebugOptions", true);
			viewElementDef.Description = new LocalizedTextBind("Testing only: opens a list of debug activities (hostile mutant sub-factions, or New Jericho/Synedrion/Anu) to spawn near the middle of the map, plus free (no cost, no relationship requirement) Ask-for-Help grants from New Jericho/Anu/Synedrion.", true);
			viewElementDef.DisplayPriority = 0;
			viewElementDef.ShowContextMenuTooltip = true;
			viewElementDef.ContextMenuShowOnlyIfViewFaction = true;
			viewElementDef.ContextMenuVisibility = NewVisibilityCondition();
			viewElementDef.ActionBarVisibility = NewVisibilityCondition();
			viewElementDef.CallToActionButtonVisibility = NewVisibilityCondition();

			AskForHelpAbilityDef abilityDef = ScriptableObject.CreateInstance<AskForHelpAbilityDef>();
			abilityDef.name = "ChaosFaction_DebugActivityPickerAbilityDef";
			abilityDef.ViewElementDef = viewElementDef;
			abilityDef.Cost = new ResourcePack();

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			DebugActivityPickerAbility ability = new DebugActivityPickerAbility { Mission = mission };
			ability.Init(abilityDef, repo);
			ability.SetActor(mission.Site);

			PickerCache[mission] = ability;
			mission.OnMissionActivated += (m, result) => CleanupPicker(m);
			mission.OnMissionCancel += CleanupPicker;

			return ability;
		}

		public static AlreadyRequestedHelpAbility GetOrCreateStatus(GeoMission mission, DebugActivityKind kind, bool isHostile, double remainingHours)
		{
			GeoLevelController geoLevel = mission.Site?.GeoLevel;
			string timeText = $"{Mathf.Max(0f, (float)remainingHours):0.0}h remaining";
			string state = isHostile ? "activity already active" : "already helping as an ally";
			Color statusColor = isHostile ? ChaosFactionColors.HostileRed : ChaosFactionColors.LightBlue;

			if (StatusCache.TryGetValue(mission, out AlreadyRequestedHelpAbility existing))
			{
				if (existing.GeoscapeAbilityDef is AskForHelpAbilityDef existingDef && existingDef.ViewElementDef != null)
					existingDef.ViewElementDef.Description = new LocalizedTextBind($"{kind.DisplayName(geoLevel)} {state} ({timeText}).", true);
				existing.StatusColor = statusColor;
				return existing;
			}

			GeoscapeAbilityViewElementDef viewElementDef = ScriptableObject.CreateInstance<GeoscapeAbilityViewElementDef>();
			viewElementDef.name = "ChaosFaction_DebugActivityStatusViewElementDef";
			viewElementDef.DisplayName1 = new LocalizedTextBind($"{kind.DisplayName(geoLevel)} Activity Active", true);
			viewElementDef.Description = new LocalizedTextBind($"{kind.DisplayName(geoLevel)} {state} ({timeText}).", true);
			viewElementDef.DisplayPriority = 0;
			viewElementDef.ShowContextMenuTooltip = true;
			viewElementDef.ContextMenuShowOnlyIfViewFaction = true;
			viewElementDef.ContextMenuVisibility = NewVisibilityCondition();
			viewElementDef.ActionBarVisibility = NewVisibilityCondition();
			viewElementDef.CallToActionButtonVisibility = NewVisibilityCondition();

			AskForHelpAbilityDef abilityDef = ScriptableObject.CreateInstance<AskForHelpAbilityDef>();
			abilityDef.name = "ChaosFaction_DebugActivityStatusAbilityDef";
			abilityDef.ViewElementDef = viewElementDef;
			abilityDef.Cost = new ResourcePack();

			DefRepository repo = GameUtl.GameComponent<DefRepository>();
			AlreadyRequestedHelpAbility ability = new AlreadyRequestedHelpAbility { StatusColor = statusColor };
			ability.Init(abilityDef, repo);
			ability.SetActor(mission.Site);

			StatusCache[mission] = ability;
			mission.OnMissionActivated += (m, result) => CleanupStatus(m);
			mission.OnMissionCancel += CleanupStatus;

			return ability;
		}

		private static void CleanupPicker(GeoMission mission)
		{
			if (!PickerCache.TryGetValue(mission, out DebugActivityPickerAbility ability))
				return;
			PickerCache.Remove(mission);
			DestroyDefs(ability.GeoscapeAbilityDef as AskForHelpAbilityDef);
		}

		private static void CleanupStatus(GeoMission mission)
		{
			if (!StatusCache.TryGetValue(mission, out AlreadyRequestedHelpAbility ability))
				return;
			StatusCache.Remove(mission);
			DestroyDefs(ability.GeoscapeAbilityDef as AskForHelpAbilityDef);
		}

		private static void DestroyDefs(AskForHelpAbilityDef def)
		{
			if (def == null)
				return;
			try
			{
				if (def.ViewElementDef != null)
					UnityEngine.Object.Destroy(def.ViewElementDef);
				UnityEngine.Object.Destroy(def);
			}
			catch (Exception ex)
			{
				Log.Debug("Failed to clean up debug activity ability defs: " + ex);
			}
		}

		private static GeoscapeAbilityViewElementDef.VisibilityCondition NewVisibilityCondition()
		{
			return new GeoscapeAbilityViewElementDef.VisibilityCondition
			{
				Visible = true,
				DisabledStateFilters = new List<GeoAbilityDisabledState>(),
				TargetDisabledStateFilters = new List<GeoAbilityTargetDisabledState>(),
			};
		}
	}
}
