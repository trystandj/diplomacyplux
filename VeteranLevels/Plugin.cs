using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Unity.Entities;
using UnityEngine;

namespace VeteranLevels
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "trystan.dino.veteranlevels";
        public const string Name = "Veteran Levels";
        public const string Version = "0.3.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> XpPerKill;
        internal static ConfigEntry<int> XpBase;
        internal static ConfigEntry<int> MaxLevel;
        internal static ConfigEntry<float> DamagePerLevel;
        internal static ConfigEntry<float> HealthPerLevel;
        internal static ConfigEntry<float> SizePerLevel;
        internal static ConfigEntry<bool> ShowLabels;
        internal static ConfigEntry<KeyCode> ToggleLabelsKey;
        internal static ConfigEntry<KeyCode> LevelUpSelectedKey;

        private void Awake()
        {
            Log = Logger;

            XpPerKill = Config.Bind("Leveling", "XpPerKill", 10, "Experience gained for each enemy a unit kills.");
            XpBase = Config.Bind("Leveling", "XpBase", 20,
                "XP needed to go from level L to L+1 is XpBase * (L + 1). Default: 2 kills for level 1, 4 more for level 2, ...");
            MaxLevel = Config.Bind("Leveling", "MaxLevel", 10, "Highest level a unit can reach.");
            DamagePerLevel = Config.Bind("Bonuses", "DamagePerLevel", 0.25f, "Extra damage per level (0.25 = +25% per level).");
            HealthPerLevel = Config.Bind("Bonuses", "HealthPerLevel", 0.25f,
                "Extra max health per level (0.25 = +25% per level). Levels are restored from saves using this value, so changing it mid-campaign changes the levels of saved units.");
            SizePerLevel = Config.Bind("Bonuses", "SizePerLevel", 0.03f,
                "Extra model size per level (0.03 = 3% bigger per level). Visual only; set to 0 to disable.");
            ShowLabels = Config.Bind("Display", "ShowLabels", true, "Show name and level above veteran units.");
            ToggleLabelsKey = Config.Bind("Display", "ToggleLabelsKey", KeyCode.F9, "Key that shows/hides the veteran labels.");
            LevelUpSelectedKey = Config.Bind("Cheats", "LevelUpSelectedKey", KeyCode.F10,
                "Key that gives every selected unit one level. Set to None to disable.");

            // Run from our own persistent object: some games destroy BepInEx's manager object
            // (and with it this plugin's Update/OnGUI) during startup or scene loads.
            var runner = new GameObject("VeteranLevelsRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<VeteranRunner>();

            Log.LogInfo($"{Name} {Version} loaded.");
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
