using System;
using System.Collections.Generic;
using System.Linq;
using Components;
using Components.RawComponents;
using Components.RawComponents.ResearchComponents;
using Components.Structs;
using Components.SystemStateComponents;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Utility;
using Utility.EnumsStorage;

namespace VeteranLevels
{
    /// <summary>
    /// Trainable creatures: the Barracks can train wild creatures (wolves, Heffalumps) as player
    /// units once the Beast Taming research is bought.
    ///
    /// The game builds every unit prefab at startup, so creature prefabs exist in every mission.
    /// The Barracks buttons come from GameState.BarracksShopStates, so adding an entry there gives
    /// a native training button, price check and queue. The native research lock is a
    /// ResearchObjectLock tag on the prefab, which the mod adds until the research is bought.
    ///
    /// Creature prefabs are built as enemies (EnemyProxy bakes in an Enemy component), so a trained
    /// creature starts out hostile. Trained units carry a Citizen component and wild enemies never
    /// do, so "creature + Enemy + Citizen" means a trained creature that must be tamed. The same
    /// check re-tames creatures after loading a save, where the prefab's Enemy comes back.
    ///
    /// The game's unit cache (SpaceCacheSystem, used for selection and targeting) records whether a
    /// unit is an enemy only when the unit is first cached, so a creature cached before it was
    /// tamed would stay an enemy there. The cache drops a unit that has no Translation, so a tamed
    /// creature's Translation is removed until the cache lets go of it, then put back; the cache
    /// then adds it again as a player unit.
    /// </summary>
    public partial class VeterancySystem
    {
        private const int BeastTamingFlag = 1;

        /// <summary>Set by the hero panel's research button; handled on the next system update.</summary>
        internal static bool BuyBeastTamingRequested;

        public bool BeastTamingResearched => (ResearchFlags & BeastTamingFlag) != 0;

        private struct Creature
        {
            public UnitType Type;
            public PriceBaseData Price;
            public float TrainTime;
            public int ObjectId;
            public Entity Prefab;
        }

        private readonly List<Creature> _creatures = new List<Creature>();
        private readonly HashSet<UnitType> _creatureTypes = new HashSet<UnitType>();
        private readonly HashSet<UnitType> _creatureComponentsLogged = new HashSet<UnitType>();
        private EntityQuery _trainedHostileUnits;
        /// <summary>Tamed creatures waiting for the unit cache to drop them, with their position.</summary>
        private readonly Dictionary<Entity, Translation> _recaching = new Dictionary<Entity, Translation>();
        private bool _creaturePrefabsFound;
        private bool _barracksLogged;

        private void CreateCreatures()
        {
            _trainedHostileUnits = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Citizen>() },
                None = new[] { ComponentType.ReadOnly<Dead>() },
            });

            // Format: Type:food:people:seconds, comma separated.
            foreach (var entry in Plugin.TrainableCreatures.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':').Select(p => p.Trim()).ToArray();
                if (parts.Length == 4 && Enum.TryParse(parts[0], true, out UnitType type) && Enum.IsDefined(typeof(UnitType), type)
                    && int.TryParse(parts[1], out int food) && int.TryParse(parts[2], out int people) && float.TryParse(parts[3], out float seconds))
                {
                    _creatures.Add(new Creature
                    {
                        Type = type,
                        Price = new PriceBaseData
                        {
                            foodPrice = food,
                            peoplePrice = people,
                            totalWorkAmountMultiplier = 1f,
                            totalRepairWorkAmountMultiplier = 1f,
                        },
                        TrainTime = seconds,
                    });
                    _creatureTypes.Add(type);
                }
                else
                {
                    Plugin.Log.LogWarning($"Ignoring invalid TrainableCreatures entry '{entry.Trim()}' (expected Type:food:people:seconds).");
                }
            }
        }

        /// <summary>Periodic: keeps creature prefabs, prices, research locks and Barracks entries in sync.</summary>
        private void UpdateCreatures()
        {
            if (_creatures.Count == 0 || !FindCreaturePrefabs())
                return;

            bool locked = !BeastTamingResearched;
            for (int i = 0; i < _creatures.Count; i++)
            {
                var creature = _creatures[i];
                SetPrice(creature);
                bool hasLock = EntityManager.HasComponent<ResearchObjectLock>(creature.Prefab);
                if (locked && !hasLock)
                    EntityManager.AddComponent<ResearchObjectLock>(creature.Prefab);
                else if (!locked && hasLock)
                    EntityManager.RemoveComponent<ResearchObjectLock>(creature.Prefab);
            }
            AddCreaturesToBarracks();
        }

        private bool FindCreaturePrefabs()
        {
            if (_creaturePrefabsFound && _creatures.All(c => EntityManager.Exists(c.Prefab)))
                return true;
            var prefabs = GameState.PrefabsMap;
            if (!prefabs.IsCreated)
                return false;

            var found = new Dictionary<UnitType, (int id, Entity prefab)>();
            using (var ids = prefabs.GetKeyArray(Allocator.Temp))
            {
                foreach (int id in ids.OrderBy(id => id))
                {
                    var prefab = prefabs[id];
                    if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<UnitBase>(prefab))
                        continue;
                    var type = UnitTypeOf(prefab);
                    if (_creatureTypes.Contains(type) && !found.ContainsKey(type))
                        found[type] = (id, prefab);
                }
            }

            for (int i = _creatures.Count - 1; i >= 0; i--)
            {
                var creature = _creatures[i];
                if (!found.TryGetValue(creature.Type, out var match))
                {
                    Plugin.Log.LogWarning($"TrainableCreatures: no unit prefab of type {creature.Type}; it can't be trained.");
                    _creatures.RemoveAt(i);
                    continue;
                }
                creature.ObjectId = match.id;
                creature.Prefab = match.prefab;
                _creatures[i] = creature;
            }
            _creaturePrefabsFound = true;
            return _creatures.Count > 0;
        }

        /// <summary>Gives the creature prefab its training price (a new blob; prefab blobs can be shared).</summary>
        private void SetPrice(Creature creature)
        {
            var price = creature.Price;
            bool hasPrice = EntityManager.HasComponent<PriceBase>(creature.Prefab);
            if (!hasPrice || !SamePrice(EntityManager.GetComponentData<PriceBase>(creature.Prefab).Value(), price))
                SetOrAdd(creature.Prefab, new PriceBase(BlobConstructor.ConstructSharedImmutableData(price)));

            // The price warning shown when clicking a train button reads this map.
            var pricesById = GameState.ObjectsDataState.PricesById;
            if (pricesById.IsCreated && (!pricesById.TryGetValue(creature.ObjectId, out var listed) || !SamePrice(listed, price)))
            {
                pricesById.Remove(creature.ObjectId);
                pricesById.TryAdd(creature.ObjectId, price);
            }
        }

        private static bool SamePrice(PriceBaseData a, PriceBaseData b) =>
            a.foodPrice == b.foodPrice && a.peoplePrice == b.peoplePrice && a.woodPrice == b.woodPrice
            && a.stonePrice == b.stonePrice && a.ironPrice == b.ironPrice && a.moneyPrice == b.moneyPrice && a.soulsPrice == b.soulsPrice;

        /// <summary>Adds missing creature entries to the Barracks shop, in free button slots after its units.</summary>
        private void AddCreaturesToBarracks()
        {
            var shop = GameState.BarracksShopStates;
            if (!shop.IsCreated || shop.Count() == 0)
                return;

            int buildingId = -1;
            int lowestLevel = int.MaxValue;
            int nextSlot = 0;
            using (var ids = shop.GetKeyArray(Allocator.Temp))
            {
                foreach (int id in ids)
                {
                    var state = shop[id];
                    nextSlot = Math.Max(nextSlot, state.panelIndex + 1);
                    if (_creatures.Any(c => c.ObjectId == id))
                        continue;
                    // Train creatures at the lowest Barracks level any of its units need.
                    if (GameState.PrefabsMap.TryGetValue(state.buildingIdForTrain, out var building) && EntityManager.HasComponent<BuildingBase>(building))
                    {
                        int level = EntityManager.GetComponentData<BuildingBase>(building).value.Value.level;
                        if (level < lowestLevel)
                        {
                            lowestLevel = level;
                            buildingId = state.buildingIdForTrain;
                        }
                    }
                }
            }
            if (buildingId < 0)
                return;

            bool added = false;
            foreach (var creature in _creatures)
            {
                if (shop.ContainsKey(creature.ObjectId))
                    continue;
                shop.TryAdd(creature.ObjectId, new TrainItemState
                {
                    buildingIdForTrain = buildingId,
                    timeToTrain = creature.TrainTime,
                    panelIndex = nextSlot,
                    researchLocked = !BeastTamingResearched,
                });
                if (!_barracksLogged)
                    Plugin.Log.LogInfo($"Barracks can now train {creature.Type} (object {creature.ObjectId}) in button slot {nextSlot}.");
                nextSlot++;
                added = true;
            }
            _barracksLogged |= added;
            if (added)
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Selection);
        }

        /// <summary>Every frame: turns trained creatures (and creatures loaded from a save) to the player's side.</summary>
        private void TameTrainedCreatures()
        {
            FinishRecaching();
            if (_creatureTypes.Count == 0 || _trainedHostileUnits.IsEmpty)
                return;

            bool tamed = false;
            using (var units = _trainedHostileUnits.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    var type = UnitTypeOf(unit);
                    if (!_creatureTypes.Contains(type))
                        continue;

                    tamed = true;
                    EntityManager.RemoveComponent<Enemy>(unit);
                    RemoveIfPresent<EnemyLoiteringData>(unit);
                    RemoveIfPresent<EnemyPatrolData>(unit);
                    RemoveIfPresent<EnemyUnitMetadata>(unit);
                    RemoveIfPresent<HideUnitInFog>(unit);   // player units are never hidden by fog
                    if (EntityManager.HasComponent<UnitCacheState>(unit) && EntityManager.HasComponent<Translation>(unit))
                    {
                        _recaching[unit] = EntityManager.GetComponentData<Translation>(unit);
                        EntityManager.RemoveComponent<Translation>(unit);
                    }

                    // Logged once per type to compare against a normal player unit when testing.
                    if (_creatureComponentsLogged.Add(type))
                    {
                        using (var types = EntityManager.GetComponentTypes(unit))
                            Plugin.Log.LogInfo($"Tamed {type}; components: {string.Join(", ", types.Select(t => t.GetManagedType()?.Name ?? t.ToString()).OrderBy(n => n))}");
                    }
                }
            }
            if (tamed)
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Units);
        }

        /// <summary>Puts back the Translation of tamed creatures once the unit cache has dropped them.</summary>
        private void FinishRecaching()
        {
            if (_recaching.Count == 0)
                return;
            List<Entity> done = null;
            foreach (var pair in _recaching)
            {
                if (!EntityManager.Exists(pair.Key))
                    (done ??= new List<Entity>()).Add(pair.Key);
                else if (!EntityManager.HasComponent<UnitCacheState>(pair.Key))
                {
                    SetOrAdd(pair.Key, pair.Value);
                    (done ??= new List<Entity>()).Add(pair.Key);
                }
            }
            if (done != null)
                foreach (var unit in done)
                    _recaching.Remove(unit);
        }

        private void BuyBeastTaming()
        {
            RefreshHeroCap();
            if (BeastTamingResearched)
            {
                VeterancyOverlay.Announce("Beast Taming is already researched.");
                return;
            }
            if (!TrySpendSoulCrystals(Plugin.BeastTamingCost.Value, CapUpgrades, ResearchFlags | BeastTamingFlag))
                return;

            UpdateCreatures();
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Selection);
            string names = string.Join(" and ", _creatures.Select(c => c.Type.ToString()));
            VeterancyOverlay.Announce($"Beast Taming researched! The Barracks can now train {names}.");
        }

        private void RemoveIfPresent<T>(Entity entity) where T : struct, IComponentData
        {
            if (EntityManager.HasComponent<T>(entity))
                EntityManager.RemoveComponent<T>(entity);
        }
    }
}
