using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace VeteranLevels
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "trystan.dino.veteranlevels";
        public const string Name = "Veteran Levels";
        public const string Version = "0.6.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> XpPerKill;
        internal static ConfigEntry<int> XpBase;
        internal static ConfigEntry<int> MaxLevel;
        internal static ConfigEntry<float> DamagePerLevel;
        internal static ConfigEntry<float> HealthPerLevel;
        internal static ConfigEntry<float> SizePerLevel;
        internal static ConfigEntry<int> EnemyHeroOneIn;
        internal static ConfigEntry<int> EnemyHeroStartLevel;
        internal static ConfigEntry<string> SoulCrystalRewards;
        /// <summary>Parsed <see cref="SoulCrystalRewards"/>: level reached -> soul crystals awarded.</summary>
        internal static Dictionary<int, int> SoulCrystalsByLevel;
        internal static ConfigEntry<int> BaseHeroCap;
        internal static ConfigEntry<int> HeroCapPerUpgrade;
        internal static ConfigEntry<string> HeroCapUpgradeCosts;
        /// <summary>Parsed <see cref="HeroCapUpgradeCosts"/>: soul crystal cost of each cap upgrade, in order.</summary>
        internal static List<int> CapUpgradeCosts;
        internal static ConfigEntry<KeyCode> HeroPanelKey;
        internal static ConfigEntry<int> PromotionLevel;
        internal static ConfigEntry<string> PromotionChains;
        internal static ConfigEntry<string> TrainableCreatures;
        internal static ConfigEntry<int> BeastTamingCost;
        internal static ConfigEntry<bool> ShowLabels;
        internal static ConfigEntry<KeyCode> ToggleLabelsKey;
        internal static ConfigEntry<KeyCode> LevelUpSelectedKey;
        internal static ConfigEntry<KeyCode> SpawnResourcesKey;
        internal static ConfigEntry<int> SpawnResourceAmount;
        internal static ConfigEntry<string> SpawnResourceTypes;

        private void Awake()
        {
            Log = Logger;

            XpPerKill = Config.Bind("Leveling", "XpPerKill", 10, "Experience gained for each enemy a unit kills.");
            XpBase = Config.Bind("Leveling", "XpBase", 20,
                "XP needed to go from level L to L+1 is XpBase * (L + 1). Default: 2 kills for level 1, 4 more for level 2, ...");
            MaxLevel = Config.Bind("Leveling", "MaxLevel", 10, "Highest level a unit can reach.");
            DamagePerLevel = Config.Bind("Bonuses", "DamagePerLevel", 0.05f, "Extra damage per level (0.05 = +5% per level).");
            HealthPerLevel = Config.Bind("Bonuses", "HealthPerLevel", 0.05f,
                "Extra max health per level (0.05 = +5% per level). Levels are restored from saves using this value, so changing it mid-campaign changes the levels of saved units.");
            SizePerLevel = Config.Bind("Bonuses", "SizePerLevel", 0.01f,
                "Extra model size per level (0.01 = 1% bigger per level). Visual only; set to 0 to disable.");
            EnemyHeroOneIn = Config.Bind("Enemy Heroes", "EnemyHeroOneIn", 30,
                "About 1 in this many enemy wave units spawns as a hero. Heroes level up by killing your units. 0 disables enemy heroes.");
            EnemyHeroStartLevel = Config.Bind("Enemy Heroes", "EnemyHeroStartLevel", 2, "Level an enemy hero starts at.");
            SoulCrystalRewards = Config.Bind("Rewards", "SoulCrystalRewards", "5:2, 10:5",
                "Soul crystals you get when one of your units reaches a level, as level:crystals pairs. Empty disables rewards.");
            SoulCrystalsByLevel = ParseRewards(SoulCrystalRewards.Value);
            BaseHeroCap = Config.Bind("Hero Cap", "BaseHeroCap", 5,
                "How many heroes (named units, level 1 or promoted) you can have before buying upgrades. At the cap, other units don't gain XP.");
            HeroCapPerUpgrade = Config.Bind("Hero Cap", "HeroCapPerUpgrade", 5, "Extra hero slots per cap upgrade.");
            HeroCapUpgradeCosts = Config.Bind("Hero Cap", "HeroCapUpgradeCosts", "10, 20",
                "Soul crystal cost of each cap upgrade, in order. Default: 5 -> 10 heroes for 10 crystals, 10 -> 15 for 20.");
            CapUpgradeCosts = ParseCosts(HeroCapUpgradeCosts.Value);
            HeroPanelKey = Config.Bind("Hero Cap", "HeroPanelKey", KeyCode.F8, "Key that opens the hero panel (hero count and cap upgrades).");
            PromotionLevel = Config.Bind("Promotion", "PromotionLevel", 5,
                "Level at which a unit in a promotion chain becomes the next unit type, starting again at level 0. The last unit type in a chain levels up to MaxLevel.");
            PromotionChains = Config.Bind("Promotion", "PromotionChains",
                "Spearman > Swordsman > Knight, Archer > Crossbowman, Horseman > Cavalier > DarkKnight",
                "Comma-separated promotion chains of unit types (names from the game's UnitType list). Units not in a chain don't promote and level up to MaxLevel. Empty disables promotion.");
            TrainableCreatures = Config.Bind("Creatures", "TrainableCreatures", "Wolf:1:1:25, Giant:1:4:75",
                "Creatures the Barracks can train after the Beast Taming research, as Type:food:population:seconds. Giant is the Heffalump. Empty disables creatures.");
            BeastTamingCost = Config.Bind("Creatures", "BeastTamingCost", 25, "Soul crystal cost of the Beast Taming research.");
            ShowLabels = Config.Bind("Display", "ShowLabels", true, "Show name and level above veteran units.");
            ToggleLabelsKey = Config.Bind("Display", "ToggleLabelsKey", KeyCode.F9, "Key that shows/hides the veteran labels.");
            LevelUpSelectedKey = Config.Bind("Cheats", "LevelUpSelectedKey", KeyCode.F10,
                "Key that gives every selected unit one level. Set to None to disable.");
            SpawnResourcesKey = Config.Bind("Cheats", "SpawnResourcesKey", KeyCode.F11,
                "Key that drops a pile of each resource at the mouse cursor; walk a unit over them to collect. Set to None to disable.");
            SpawnResourceAmount = Config.Bind("Cheats", "SpawnResourceAmount", 50, "Amount in each resource pile.");
            SpawnResourceTypes = Config.Bind("Cheats", "SpawnResourceTypes", "Wood, Stone",
                "Resources the hotkey spawns (Money is gold, Souls are soul crystals).");

            // Run from our own persistent object: some games destroy BepInEx's manager object
            // (and with it this plugin's Update/OnGUI) during startup or scene loads.
            var runner = new GameObject("VeteranLevelsRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<VeteranRunner>();

            try
            {
                new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony patches failed; the unit info panel won't show veteran damage. {e}");
            }

            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private static List<int> ParseCosts(string text)
        {
            var costs = new List<int>();
            foreach (var part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int cost) && cost >= 0)
                    costs.Add(cost);
                else
                    Log.LogWarning($"Ignoring invalid HeroCapUpgradeCosts entry '{part.Trim()}'.");
            }
            return costs;
        }

        private static Dictionary<int, int> ParseRewards(string text)
        {
            var rewards = new Dictionary<int, int>();
            foreach (var pair in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int level) && int.TryParse(parts[1].Trim(), out int crystals))
                    rewards[level] = crystals;
                else
                    Log.LogWarning($"Ignoring invalid SoulCrystalRewards entry '{pair.Trim()}' (expected level:crystals).");
            }
            return rewards;
        }
    }

    /// <summary>Per-frame driver: hooks our ECS system into the game world and draws the overlay.</summary>
    internal class VeteranRunner : MonoBehaviour
    {
        private const float WorldReportInterval = 10f;
        private const int MaxWorldReports = 6;

        private World _hookedWorld;
        private float _nextWorldReport;
        private int _worldReports;
        private bool _hookErrorLogged;

        private void Start()
        {
            Plugin.Log.LogInfo("Runner started; waiting for the game's ECS world.");
        }

        private void OnDestroy()
        {
            Plugin.Log.LogWarning("Runner object was destroyed; leveling stops until the game restarts.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.ToggleLabelsKey.Value))
                Plugin.ShowLabels.Value = !Plugin.ShowLabels.Value;
            if (Plugin.SpawnResourcesKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.SpawnResourcesKey.Value))
            {
                if (_hookedWorld == null)
                    VeterancyOverlay.Announce("Veteran Levels isn't active yet; start a mission first.");
                else
                    VeterancySystem.SpawnResourcesRequested = true;
            }
            if (Input.GetKeyDown(Plugin.HeroPanelKey.Value))
                VeterancyOverlay.HeroPanelOpen = !VeterancyOverlay.HeroPanelOpen;
            if (Plugin.LevelUpSelectedKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.LevelUpSelectedKey.Value))
            {
                if (_hookedWorld == null)
                    VeterancyOverlay.Announce("Veteran Levels isn't active yet; start a mission first.");
                else
                    VeterancySystem.LevelUpSelectedRequested = true;
            }

            try
            {
                HookGameWorld();
            }
            catch (Exception e)
            {
                if (!_hookErrorLogged)
                    Plugin.Log.LogError($"Failed to hook the game world: {e}");
                _hookErrorLogged = true;
            }
        }

        // The game may (re)create its ECS world, so keep checking and add our system to
        // whichever world is running the game's combat systems.
        private void HookGameWorld()
        {
            if (_hookedWorld != null && _hookedWorld.IsCreated && _hookedWorld.GetExistingSystem<VeterancySystem>() != null)
                return;

            _hookedWorld = null;
            foreach (var world in World.All)
            {
                if (!world.IsCreated || world.GetExistingSystem<Systems.HealthTrackSystem>() == null)
                    continue;

                var simulation = world.GetExistingSystem<SimulationSystemGroup>();
                if (simulation == null)
                    continue;

                var system = world.GetOrCreateSystem<VeterancySystem>();
                simulation.AddSystemToUpdateList(system);
                simulation.SortSystems();
                _hookedWorld = world;
                Plugin.Log.LogInfo($"Veterancy system added to world '{world.Name}'.");
                return;
            }

            if (Time.unscaledTime >= _nextWorldReport && _worldReports < MaxWorldReports)
            {
                _nextWorldReport = Time.unscaledTime + WorldReportInterval;
                _worldReports++;
                string worlds = string.Join(", ", World.All.Select(w => $"'{w.Name}'"));
                Plugin.Log.LogInfo($"No world with HealthTrackSystem yet. Worlds: [{worlds}]");
            }
        }

        private void OnGUI()
        {
            VeterancyOverlay.Draw(_hookedWorld);
        }
    }
}
