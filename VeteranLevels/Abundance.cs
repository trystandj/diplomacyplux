using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;

namespace VeteranLevels
{
    /// <summary>
    /// The Abundance research: levels bought with soul crystals that make food last longer.
    ///  - Fish spots (ResourceSource + FoodSource) hold more fish. The game saves each spot's
    ///    current and max amount, so spots are scaled once, at the moment a level is bought, by
    ///    the ratio between the new and old level's multiplier. Loading a save needs nothing.
    ///    A Fisherman's hut built later multiplies the spot again, as it would without the mod.
    ///  - Berry bushes (BerryBush) hold more berries. Saves keep only the current amount and
    ///    take the max from the prefab, so every bush's max is kept at prefab max * multiplier.
    ///    Bushes regrow to that max on their own (BushRegrowJob).
    ///  - Farms have no finite amount; they produce for ever. Instead they work faster, through
    ///    the game's own ProduceSpeedModifier buffer (the same one nearby-building bonuses use, so
    ///    the bonus also shows in the farm's info panel). The game rebuilds that buffer when
    ///    nearby buildings change, so the mod's entry (affectorEntity = Entity.Null) is re-added.
    ///
    /// The level is stored in ResearchFlags (see HeroCap.cs): bit 0 is Beast Taming, bits 1-3
    /// the Abundance level.
    /// </summary>
    public partial class VeterancySystem
    {
        private const int AbundanceShift = 1;
        private const int AbundanceMask = 7;

        /// <summary>Set by the research tree's Abundance buttons; handled on the next system update.</summary>
        internal static bool BuyAbundanceRequested;

        public int AbundanceLevel => (ResearchFlags >> AbundanceShift) & AbundanceMask;

        /// <summary>Cost of the next Abundance level, or null when all are bought.</summary>
        public int? NextAbundanceCost =>
            AbundanceLevel < Plugin.AbundanceCosts.Count ? Plugin.AbundanceCosts[AbundanceLevel] : (int?)null;

        public static float FishMultiplier(int level) => 1f + Plugin.FishPerLevel.Value * level;
        public static float BerryMultiplier(int level) => 1f + Plugin.BerriesPerLevel.Value * level;
        public static float FarmBonus(int level) => Plugin.FarmSpeedPerLevel.Value * level;

        private EntityQuery _fishSources;
        private EntityQuery _berryBushes;
        private EntityQuery _farms;
        private readonly Dictionary<int, int> _berryBaseByObject = new Dictionary<int, int>();

        private void CreateAbundance()
        {
            _fishSources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<ResourceSource>(), ComponentType.ReadWrite<ResourceSourceBase>(), ComponentType.ReadOnly<FoodSource>() },
                None = new[] { ComponentType.ReadOnly<IsEmptySource>() },
            });
            _berryBushes = GetEntityQuery(ComponentType.ReadWrite<BerryBush>(), ComponentType.ReadOnly<ObjectId>());
            _farms = GetEntityQuery(ComponentType.ReadOnly<Farm>(), ComponentType.ReadOnly<ProducerBase>());
        }

        /// <summary>Periodic: keeps berry bush maxima and the farm speed bonus in line with the level.</summary>
        private void UpdateAbundance()
        {
            UpdateBerryBushes();
            UpdateFarms();
        }

        private void BuyAbundance()
        {
            RefreshHeroCap();
            var cost = NextAbundanceCost;
            if (!cost.HasValue)
            {
                VeterancyOverlay.Announce("Abundance is already fully researched.");
                return;
            }
            int oldLevel = AbundanceLevel;
            int newLevel = oldLevel + 1;
            int flags = (ResearchFlags & ~(AbundanceMask << AbundanceShift)) | (newLevel << AbundanceShift);
            if (!TrySpendSoulCrystals(cost.Value, CapUpgrades, flags))
                return;

            int fish = ScaleFishSources(FishMultiplier(newLevel) / FishMultiplier(oldLevel));
            UpdateAbundance();
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Other);
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Buildings);
            Plugin.Log.LogInfo($"Abundance {newLevel} researched; scaled {fish} fish spots.");
            VeterancyOverlay.Announce($"Abundance {newLevel} researched! Fish x{FishMultiplier(newLevel):0.##}, berries x{BerryMultiplier(newLevel):0.##}, farms +{FarmBonus(newLevel) * 100f:0}% speed.");
        }

        /// <summary>Multiplies the fish left and the max of every fish spot on the map.</summary>
        private int ScaleFishSources(float ratio)
        {
            if (ratio <= 1f || _fishSources.IsEmpty)
                return 0;
            int scaled = 0;
            using (var sources = _fishSources.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in sources)
                {
                    var baseData = EntityManager.GetComponentData<ResourceSourceBase>(entity);
                    if (baseData.value.IsCreated && baseData.value.Value.infinite)
                        continue;
                    var source = EntityManager.GetComponentData<ResourceSource>(entity);
                    if (source.currentAmount <= 0)
                        continue;
                    source.currentAmount = ScaleInt(source.currentAmount, ratio);
                    baseData.maxTotalAmount = ScaleInt(baseData.maxTotalAmount, ratio);
                    baseData.increased = true;
                    EntityManager.SetComponentData(entity, source);
                    EntityManager.SetComponentData(entity, baseData);
                    scaled++;
                }
            }
            return scaled;
        }

        private void UpdateBerryBushes()
        {
            if (_berryBushes.IsEmpty)
                return;
            float multiplier = BerryMultiplier(AbundanceLevel);
            bool changed = false;
            using (var bushes = _berryBushes.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in bushes)
                {
                    int objectId = EntityManager.GetComponentData<ObjectId>(entity).value;
                    if (!TryGetBerryBase(objectId, out int baseMax))
                        continue;
                    int desired = Math.Max(1, ScaleInt(baseMax, multiplier));
                    var bush = EntityManager.GetComponentData<BerryBush>(entity);
                    if (bush.maxFoodAmount == desired)
                        continue;

                    // A full bush fills up to the new max; a picked or regrowing one keeps its
                    // berries and regrows to the new max when emptied.
                    bool full = bush.currentFoodAmount >= bush.maxFoodAmount
                        && !EntityManager.HasComponent<SourceResourceEmpty>(entity);
                    bush.maxFoodAmount = desired;
                    bush.currentFoodAmount = full ? desired : Math.Min(bush.currentFoodAmount, desired);
                    EntityManager.SetComponentData(entity, bush);
                    changed = true;
                }
            }
            if (changed)
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Other);
        }

        /// <summary>The berry bush prefab's own max (unchanged by the mod), cached per object id.</summary>
        private bool TryGetBerryBase(int objectId, out int baseMax)
        {
            if (_berryBaseByObject.TryGetValue(objectId, out baseMax))
                return baseMax > 0;
            baseMax = 0;
            var prefabs = GameState.PrefabsMap;
            if (!prefabs.IsCreated || !prefabs.TryGetValue(objectId, out var prefab) || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<BerryBush>(prefab))
                return false;
            baseMax = EntityManager.GetComponentData<BerryBush>(prefab).maxFoodAmount;
            _berryBaseByObject[objectId] = baseMax;
            return baseMax > 0;
        }

        /// <summary>Adds (or updates) the mod's entry in every farm's produce speed modifiers.</summary>
        private void UpdateFarms()
        {
            if (_farms.IsEmpty)
                return;
            float bonus = FarmBonus(AbundanceLevel);
            bool changed = false;
            using (var farms = _farms.ToEntityArray(Allocator.Temp))
            {
                foreach (var farm in farms)
                {
                    bool hasBuffer = EntityManager.HasComponent<ProduceSpeedModifier>(farm);
                    if (!hasBuffer && bonus <= 0f)
                        continue;
                    var buffer = hasBuffer ? EntityManager.GetBuffer<ProduceSpeedModifier>(farm) : EntityManager.AddBuffer<ProduceSpeedModifier>(farm);
                    int index = -1;
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (buffer[i].affectorEntity == Entity.Null)
                        {
                            index = i;
                            break;
                        }
                    }
                    if (bonus <= 0f)
                    {
                        if (index >= 0)
                        {
                            buffer.RemoveAt(index);
                            changed = true;
                        }
                    }
                    else if (index < 0)
                    {
                        buffer.Add(new ProduceSpeedModifier { Value = bonus, affectorEntity = Entity.Null });
                        changed = true;
                    }
                    else if (Math.Abs(buffer[index].Value - bonus) > 0.0001f)
                    {
                        buffer[index] = new ProduceSpeedModifier { Value = bonus, affectorEntity = Entity.Null };
                        changed = true;
                    }
                }
            }
            if (changed)
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Buildings);
        }

        private static int ScaleInt(int value, float multiplier) =>
            (int)Math.Min(int.MaxValue, Math.Floor(value * (double)multiplier + 0.0001));
    }
}
