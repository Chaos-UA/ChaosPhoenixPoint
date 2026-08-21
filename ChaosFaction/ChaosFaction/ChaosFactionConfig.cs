using PhoenixPoint.Modding;

namespace ChaosFaction
{
	/// <summary>
	/// ModConfig is mod settings that players can change from within the game.
	///
	/// Fields are grouped in two tiers — explicit follow-up request ("Group related to
	/// site settings together like in one group... note those are global settings" —
	/// listing HelpCostVariancePercent/NewJerichoAugmentedSoldierChance/
	/// AnuForsekenSoldierChance/PendingHelpHoursMin/Max/PendingActivityHoursMin/Max as the
	/// ones that stay global). The Mod Settings screen itself has no real
	/// section/category/grouping mechanism (`ConfigFieldAttribute` only carries
	/// `Text`/`Description`, and `ModConfig.GetConfigFields()` just reflects this class's
	/// own public fields in declaration order into one flat vertical list — confirmed via
	/// source, no nested-type/grouping support exists anywhere in
	/// `UIModuleModManager`), so "grouping" here means what it practically can: declaring
	/// fields in adjacent blocks so they render as contiguous runs in that flat list.
	///
	/// 1. **Global settings** (below) — apply the same way regardless of mission type or
	///    threat level.
	/// 2. **Mission-type eligibility toggles** (`EnableFor*`, further down) — whether
	///    Ask-for-Help/activities exist for that mission type at all. Phoenix Base
	///    Defense also keeps its own reinforcement-delay settings here, since those are
	///    genuinely specific to that one mission type, not something threat level should
	///    drive.
	/// 3. **Per-threat-level settings** (one block per `DifficultyThreatLevel` — Low/
	///    Medium/High/Extreme, further down) — what used to be one block of
	///    `HelpResourcesCost`/`HelpRelationshipCost`/`HelpMinSquadSize`/
	///    `HelpMaxSquadSize`/`HelpMaxNewJerichoVehiclesInSquad`/
	///    `HelpMaxSynedrionVehiclesInSquad`/`HelpMaxAnuMutogsInSquad`/
	///    `VehicleSpawnChancePercent` fields *per mission type* (56 fields across 7
	///    mission types) is now one independent copy of each *per threat level* (32
	///    fields across 4 threat levels) — explicit follow-up request: "perhaps we can
	///    spawn squad size by threat level instead of site type" / "Yes lets replace
	///    them". `DifficultyThreatLevel` (`PhoenixPoint.Common.Core`) is the game's own
	///    real threat-level signal — the same one shown in the mission-brief popup's own
	///    Threat Level row — resolved fresh per mission via `GeoMission.
	///    GetMissionThreatLevel(MissionDef)`, not something this mod invented. See
	///    `MissionHelpSettingsResolver` (`ChaosFactionState.cs`) for how a live mission
	///    resolves to one of these 4 blocks, and the "squad size/cost now keyed by threat
	///    level" implementation note in CLAUDE.md for the full reasoning, including why
	///    `FactionHelpOfferTracker`'s own offer cache still keys on (faction, mission
	///    type, threat level) — a 3-way key, not just (faction, threat level) — even
	///    though the *base values* it rolls around now only depend on threat level.
	/// </summary>
	public class ChaosFactionConfig : ModConfig
	{
		// ===== Global settings — apply regardless of mission type =====

        [ConfigField(description: "Write mod decisions to the Unity log.")]
        public bool EnableDebugLog;

		[ConfigField(description: "How much each faction's rolled price can randomly swing above/below that mission type's base HelpResourcesCost/HelpRelationshipCost, as a percent (25 = ±25%). Rolled independently per faction, per cost type, per mission type.")]
		public int HelpCostVariancePercent = 25;

		[ConfigField(description: "How many slots one of New Jericho's vehicles (e.g. an Armadillo) takes out of the rolled squad size, regardless of mission type. A regular trooper takes 1 slot.")]
		public int NewJerichoVehicleUnitSize = 3;

		[ConfigField(description: "How many slots one of Synedrion's vehicles takes out of the rolled squad size, regardless of mission type. A regular trooper takes 1 slot.")]
		public int SynedrionVehicleUnitSize = 3;

		[ConfigField(description: "How many slots one of Anu's Mutogs takes out of the rolled squad size, regardless of mission type. A regular trooper takes 1 slot")]
		public int MutogUnitSize = 2;

		[ConfigField(description: "Applies to any New Jericho squad this mod spawns - unit has a chance to be Augmented soldier. Rolled for each unit independently")]
		public int NewJerichoAugmentedSoldierChance = 25;

		[ConfigField(description: "Applies to any Anu squad this mod spawns - unit has a chance to be fully mutated soldier. Rolled for each unit independently")]
		public int AnuForsekenSoldierChance = 25;

		[ConfigField(description: "Minimum relationship value required to be able to ask for help, regardless of mission type")]
		public int RelationshipThreshold = 15;

		[ConfigField(description: "Minimum hours a help request stays valid before it expires, regardless of mission type. A random value between this and PendingHelpHoursMax is rolled fresh for each request")]
		public int PendingHelpHoursMin = 18;

		[ConfigField(description: "Maximum hours a help request stays valid before it expires. Rolled randomly between PendingHelpHoursMin and this")]
		public int PendingHelpHoursMax = 28;

		[ConfigField(description: "Minimum hours a hostile/allied activity (Pure, Forsaken, New Jericho, Synedrion, Anu, or Bandits) stays valid before it expires without launching. A random value between this and PendingActivityHoursMax is rolled fresh for each activity")]
		public int PendingActivityHoursMin = 12;

		[ConfigField(description: "Maximum hours an activity stays valid before it expires without launching. Rolled randomly between PendingActivityHoursMin and this.")]
		public int PendingActivityHoursMax = 24;

		[ConfigField(description: "Chance (0-100%) that after defending a Haven (win or lose, even if auto-resolved without playing it out), faction wants revenge and will grant a free help request at a nearby eligible site, from the faction whose Haven was attacked, provided your relationship with them meets RelationshipThreshold")]
		public int HelpForFreeAfterHavenDefenseChancePercent = 20;

		[ConfigField(description: "Pity bonus (0-100%) added to HelpForFreeAfterHavenDefenseChancePercent, per consecutive failed roll, tracked independently per faction (New Jericho/Anu/Synedrion). E.g. with the defaults (25% base, 10% bonus): 1st failure raises the next roll for that faction to 35%, a 2nd consecutive failure to 45%, and so on; a successful roll resets that faction's streak back to the 25% base. Survives saves and mission transitions.")]
		public int HelpForFreeAfterHavenDefenseNextBonusPercent = 10;

		[ConfigField(description: "Search radius around the defended Haven to look for an eligible nearby site for the free help grant above (as a sample 3500 is Synedrion flight machine max move distance). The closest eligible site within this radius is picked.")]
		public int HelpForFreeAfterHavenDefenseRadius = 3500;

		[ConfigField(description: "Shows a map marker + label ('Unrevealed Attacks: N') at any not-yet-discovered Pandoran base (Nest/Lair/Citadel/Palace) that has attacked a Haven or your Phoenix base at least once, using this mod's own per-base attack counter (the same one HavenSelfDefensePandoranSiteRevealChance/NextRevealChanceBonus below roll against). Shown even without chaos_factions_debug enabled when this is true; the marker disappears once that base is actually revealed.")]
		public bool ShowUnrevealedAttacksCounter = true;

		[ConfigField(description: "Chance (0-100%) that a Pandoran base attacking a Haven (without the player playing the defense out) or your Phoenix base is revealed as a result. Rolled by this mod directly (not vanilla's own internal reveal chance) so it can be tuned here; still yields to vanilla's own base-scanner-range auto-reveal first, when applicable.")]
		public int HavenSelfDefensePandoranSiteRevealChance = 20;

		[ConfigField(description: "Bonus (0-100%) added to HavenSelfDefensePandoranSiteRevealChance for a specific Pandoran base, per tracked attack from it (e.g. with the defaults, 25% base + 2 tracked attacks x 10% = 45% on the 3rd attack's roll). Tracked independently per Pandoran base and persists across saves.")]
		public int HavenSelfDefensePandoranSiteNextRevealChanceBonus = 10;

		[ConfigField(description: "Ongoing chance (0-100%) PER DAY for a hostile/allied activity (Pure, Forsaken, New Jericho, Synedrion, Anu, or Bandits) to automatically spawn at an eligible site on its own. Expressed as a daily percentage for readability — actually evaluated once per in-game hour internally (converted to an equivalent hourly chance), so it doesn't all-or-nothing at the start of each day. Rolled independently per eligible site (visible, not destroyed, mission type supported by this mod, not already busy) — e.g. with the default 20%, every visible Nest under assault gets its own 20%-per-day chance, not one shared roll across all of them. 0 disables auto-spawning entirely")]
		public int ActivitySpawnChancePercentPerDay = 25;

		[ConfigField(description: "When the hourly auto-spawner (ActivitySpawnChancePercentPerDay above) picks a random activity kind, this is the chance (0-100%) it picks from the Pure/Forsaken/Bandits group rather than the New Jericho/Synedrion/Anu group — each group is then picked from evenly within itself. 50 (default) means a 50/50 split between the two groups.")]
		public int ActivityHostileGroupChancePercent = 50;

		[ConfigField(description: "Minimum distance activity's squad must spawn from any currently-alive player unit — they should appear near the middle of the map (or further), not right next to your soldiers where you'd immediately spot them. If every candidate position near the map's center is closer than this to a player unit, the closest-available position is used anyway rather than failing to spawn at all.")]
		public int ActivityMinDistanceFromPlayerUnits = 30;

		[ConfigField(description: "Activity squads only (hostile/allied, not Ask-for-Help squads) - what percent (0-100) of max Action Points they have on their first turn after spawning. Every turn after that is unaffected. 100 = full AP on arrival, same as Ask-for-Help; 0 = no AP that first turn at all.")]
		public int ActivityFirstTurnActionPointsPercent = 25;

		[ConfigField(description: "Whether soldiers this mod spawns (Ask-for-Help squads and hostile/allied activity squads alike) drop loot when they die. Default true.")]
		public bool DisableLootDropForModSpawnedUnits = true;

		[ConfigField(description: "Minimum distance, in tiles, kept between individually-spawned squadmates for activity squads (hostile/allied) and Phoenix base defense delayed reinforcements, so they don't all clump onto the same or adjacent tile. Does NOT apply to the normal, immediate Ask-for-Help squad. 0 disables the spacing check. Tile size is approximated internally, since the game's own tile size is per-map nav-mesh data, not a fixed value this mod can read directly.")]
		public int MinSquadmateSpawnDistanceTiles = 2;

        // ===== Low threat level settings =====

        [ConfigField(description: "Low threat level. Base resource cost to ask a friendly faction for combat help on a mission at this threat level. Each faction rolls its own price independently, ± HelpCostVariancePercent of this value, and wants a different resource (Supplies, Materials, or Tech at 1/5 value).")]
        public int LowThreatHelpResourcesCost = 400;

        [ConfigField(description: "Low threat level. Base relationship cost, if paying with friendship instead of resources, on a mission at this threat level.")]
        public int LowThreatHelpRelationshipCost = 10;

        [ConfigField(description: "Low threat level. Minimum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between this and HelpMaxSquadSize on each request.")]
        public int LowThreatHelpMinSquadSize = 3;

        [ConfigField(description: "Low threat level. Maximum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between HelpMinSquadSize and this on each request.")]
        public int LowThreatHelpMaxSquadSize = 5;

        [ConfigField(description: "Low threat level. Max New Jericho vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int LowThreatHelpMaxNewJerichoVehiclesInSquad = 1;

        [ConfigField(description: "Low threat level. Max Synedrion vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int LowThreatHelpMaxSynedrionVehiclesInSquad = 1;

        [ConfigField(description: "Low threat level. Max Anu Mutogs allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int LowThreatHelpMaxAnuMutogsInSquad = 1;

        [ConfigField(description: "Low threat level. Chance (0-100%) for each individual vehicle/Mutog slot to actually be included in the squad on a mission at this threat level, rolled independently per slot.")]
        public int LowThreatVehicleSpawnChancePercent = 10;

        // ===== Medium threat level settings =====

        [ConfigField(description: "Medium threat level. Base resource cost to ask a friendly faction for combat help on a mission at this threat level. Each faction rolls its own price independently, ± HelpCostVariancePercent of this value, and wants a different resource (Supplies, Materials, or Tech at 1/5 value).")]
        public int MediumThreatHelpResourcesCost = 500;

        [ConfigField(description: "Medium threat level. Base relationship cost, if paying with friendship instead of resources, on a mission at this threat level.")]
        public int MediumThreatHelpRelationshipCost = 13;

        [ConfigField(description: "Medium threat level. Minimum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between this and HelpMaxSquadSize on each request.")]
        public int MediumThreatHelpMinSquadSize = 4;

        [ConfigField(description: "Medium threat level. Maximum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between HelpMinSquadSize and this on each request.")]
        public int MediumThreatHelpMaxSquadSize = 6;

        [ConfigField(description: "Medium threat level. Max New Jericho vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int MediumThreatHelpMaxNewJerichoVehiclesInSquad = 1;

        [ConfigField(description: "Medium threat level. Max Synedrion vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int MediumThreatHelpMaxSynedrionVehiclesInSquad = 1;

        [ConfigField(description: "Medium threat level. Max Anu Mutogs allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int MediumThreatHelpMaxAnuMutogsInSquad = 1;

        [ConfigField(description: "Medium threat level. Chance (0-100%) for each individual vehicle/Mutog slot to actually be included in the squad on a mission at this threat level, rolled independently per slot.")]
        public int MediumThreatVehicleSpawnChancePercent = 35;

        // ===== High threat level settings =====

        [ConfigField(description: "High threat level. Base resource cost to ask a friendly faction for combat help on a mission at this threat level. Each faction rolls its own price independently, ± HelpCostVariancePercent of this value, and wants a different resource (Supplies, Materials, or Tech at 1/5 value).")]
        public int HighThreatHelpResourcesCost = 600;

        [ConfigField(description: "High threat level. Base relationship cost, if paying with friendship instead of resources, on a mission at this threat level.")]
        public int HighThreatHelpRelationshipCost = 15;

        [ConfigField(description: "High threat level. Minimum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between this and HelpMaxSquadSize on each request.")]
        public int HighThreatHelpMinSquadSize = 5;

        [ConfigField(description: "High threat level. Maximum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between HelpMinSquadSize and this on each request.")]
        public int HighThreatHelpMaxSquadSize = 7;

        [ConfigField(description: "High threat level. Max New Jericho vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int HighThreatHelpMaxNewJerichoVehiclesInSquad = 1;

        [ConfigField(description: "High threat level. Max Synedrion vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int HighThreatHelpMaxSynedrionVehiclesInSquad = 1;

        [ConfigField(description: "High threat level. Max Anu Mutogs allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int HighThreatHelpMaxAnuMutogsInSquad = 1;

        [ConfigField(description: "High threat level. Chance (0-100%) for each individual vehicle/Mutog slot to actually be included in the squad on a mission at this threat level, rolled independently per slot.")]
        public int HighThreatVehicleSpawnChancePercent = 50;

        // ===== Extreme threat level settings =====

        [ConfigField(description: "Extreme threat level. Base resource cost to ask a friendly faction for combat help on a mission at this threat level. Each faction rolls its own price independently, ± HelpCostVariancePercent of this value, and wants a different resource (Supplies, Materials, or Tech at 1/5 value).")]
        public int ExtremeThreatHelpResourcesCost = 700;

        [ConfigField(description: "Extreme threat level. Base relationship cost, if paying with friendship instead of resources, on a mission at this threat level.")]
        public int ExtremeThreatHelpRelationshipCost = 18;

        [ConfigField(description: "Extreme threat level. Minimum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between this and HelpMaxSquadSize on each request.")]
        public int ExtremeThreatHelpMinSquadSize = 6;

        [ConfigField(description: "Extreme threat level. Maximum squad size (in slots) the helping faction can send on a mission at this threat level. Rolled randomly between HelpMinSquadSize and this on each request.")]
        public int ExtremeThreatHelpMaxSquadSize = 9;

        [ConfigField(description: "Extreme threat level. Max New Jericho vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int ExtremeThreatHelpMaxNewJerichoVehiclesInSquad = 1;

        [ConfigField(description: "Extreme threat level. Max Synedrion vehicles allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int ExtremeThreatHelpMaxSynedrionVehiclesInSquad = 1;

        [ConfigField(description: "Extreme threat level. Max Anu Mutogs allowed in the squad on a mission at this threat level. Each of the up to this many slots is rolled independently against VehicleSpawnChancePercent.")]
        public int ExtremeThreatHelpMaxAnuMutogsInSquad = 1;

        [ConfigField(description: "Extreme threat level. Chance (0-100%) for each individual vehicle/Mutog slot to actually be included in the squad on a mission at this threat level, rolled independently per slot. 100 = always include them (up to the slot budget and per-faction max), 0 = never send any.")]
        public int ExtremeThreatVehicleSpawnChancePercent = 75;


		// ===== Mission-type eligibility toggles =====
		// Cost/squad-size/vehicle settings used to live here too (one block per mission
		// type) — replaced with the four DifficultyThreatLevel blocks below per explicit
		// follow-up request: "when you generate cost for mission it should be per faction
		// per siteType per DifficultyThreatLevel" / "perhaps we can spawn squad size by
		// threat level instead of site type". These EnableFor* toggles are unrelated to
		// that axis (they gate whether Ask-for-Help/activities exist for a mission type at
		// all, regardless of its threat level) and stay exactly as they were.

		[ConfigField(description: "Pandoran Nest Assault. Allow help for assaulting a Pandoran Nest (the smallest/earliest base tier) - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForPandoranNestAssault = true;

		[ConfigField(description: "Pandoran Lair Assault. Allow help for assaulting a Pandoran Lair - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForPandoranLairAssault = true;

		[ConfigField(description: "Pandoran Citadel Assault. Allow help for assaulting a Pandoran Citadel - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForPandoranCitadelAssault = true;

		[ConfigField(description: "Pandoran Palace Assault. Allow help for assaulting a Pandoran Palace (the largest/final base tier) - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForPandoranPalaceAssault = true;

		[ConfigField(description: "Haven Infestation. Allow help for cleaning up an infested Haven - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForHavenInfestation = true;

		[ConfigField(description: "Phoenix Base Infestation. Allow help for cleaning up your own infested Phoenix base - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
		public bool EnableForPhoenixBaseInfestation = true;

		[ConfigField(description: "Allow asking for help on ANY site mission, not just the mission types above (e.g. a mission type this mod doesn't otherwise recognize). If the mission's enemy is New Jericho/Anu/Synedrion, a given faction can only be asked for help if it is itself at war with that enemy; against any other enemy (Pandoran, Pure, Forsaken, Bandits, etc.) all three can be asked normally. Default true.")]
		public bool EnableRequestForHelpForAnyMissionType = true;

        // ===== Phoenix Base Defense settings =====

        [ConfigField(description: "Phoenix Base Defense. Allow help for a Phoenix base defense - if true, both asking for help and hostile/allied activities are allowed for this mission type; if false, neither is.")]
        public bool EnableForPhoenixBaseDefense = true;

        [ConfigField(description: "Phoenix Base Defense only: minimum turns to delay the ally squad's arrival as reinforcements at the map's entrance, instead of spawning immediately alongside your own squad like every other mission type. Rolled randomly between this and ReinforcementMaxTurns, then always +1 on top of the roll. Set both to 0 to spawn immediately instead (the +1 is skipped in that case).")]
        public int PhoenixBaseDefenseReinforcementMinTurns = 2;

        [ConfigField(description: "Phoenix Base Defense only: maximum turns to delay the ally squad's reinforcement arrival. Rolled randomly between ReinforcementMinTurns and this, then always +1 on top of the roll.")]
        public int PhoenixBaseDefenseReinforcementMaxTurns = 3;

        [ConfigField(description: "Phoenix Base Defense only: what percent (0-100) of max Action Points the delayed ally reinforcement squad has on the turn they arrive, instead of leaving them with whatever partial amount the game's own spawn pipeline gives them. 100 = full AP that turn (also grants full Will Points); anything less only affects Action Points. Every turn after the first is unaffected.")]
        public int PhoenixBaseDefenseAllyReinforcementFirstTurnAP = 100;
	}
}
