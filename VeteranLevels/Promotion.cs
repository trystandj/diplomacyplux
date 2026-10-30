using System;
using System.Collections.Generic;
using System.Linq;
using Components;
using Components.RawComponents;
using Components.RawComponents.FightComponents;
using Components.SystemStateComponents;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Utility.EnumsStorage;

namespace VeteranLevels
{
    /// <summary>
    /// Promotion: a unit in a promotion chain that reaches PromotionLevel is replaced by a fresh
    /// unit of the next type in its chain, at level 0, keeping its name and hero status.
    ///
    /// The swap follows what the game does when it turns a unit into something else (training,
    /// disbanding): the new unit takes over the old one's citizen data, home, extra population
    /// slots (ghost citizens), hunger/cold state, squad flags and mission-trigger references.
    /// </summary>
    public partial class VeterancySystem
    {
        private readonly Dictionary<UnitType, UnitType> _nextTier = new Dictionary<UnitType, UnitType>();
        private readonly Dictionary<UnitType, int> _tierOf = new Dictionary<UnitType, int>();
        private readonly Dictionary<UnitType, Entity> _unitPrefabs = new Dictionary<UnitType, Entity>();
        private readonly List<Entity> _pendingPromotions = new List<Entity>();
        private readonly List<Entity> _deferredPromotions = new List<Entity>();
        private EntityQuery _playerUnitPrefabs;
        private bool _prefabsLogged;

        private void CreatePromotion()
        {
            _playerUnitPrefabs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<Prefab>() },
                None = new[] { ComponentType.ReadOnly<Enemy>() },
                Options = EntityQueryOptions.IncludePrefab,
            });

            foreach (var chainText in Plugin.PromotionChains.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var chain = new List<UnitType>();
                foreach (var name in chainText.Split('>'))
                {
                    if (Enum.TryParse(name.Trim(), true, out UnitType type) && Enum.IsDefined(typeof(UnitType), type))
                        chain.Add(type);
                    else
                        Plugin.Log.LogWarning($"PromotionChains: unknown unit type '{name.Trim()}' in '{chainText.Trim()}'.");
                }
                for (int i = 0; i < chain.Count; i++)
                {
                    _tierOf[chain[i]] = i + 1;
                    if (i + 1 < chain.Count)
                        _nextTier[chain[i]] = chain[i + 1];
                }
            }
        }

        private UnitType UnitTypeOf(Entity unit) => EntityManager.GetComponentData<UnitBase>(unit).value.Value.type;

        /// <summary>Sets the unit's tier and level cap from its unit type.</summary>
        private void ConfigureTier(Entity unit, Veteran veteran)
        {
            if (veteran.IsEnemy || !EntityManager.HasComponent<UnitBase>(unit))
                return;
            var type = UnitTypeOf(unit);
            veteran.Tier = _tierOf.TryGetValue(type, out int tier) ? tier : 0;
            veteran.LevelCap = CanPromote(type) ? Plugin.PromotionLevel.Value : Plugin.MaxLevel.Value;
        }

        private bool CanPromote(UnitType type) => _nextTier.TryGetValue(type, out var next) && TryGetPrefab(next, out _);

        private bool TryGetPrefab(UnitType type, out Entity prefab)
        {
            if (_unitPrefabs.TryGetValue(type, out prefab) && EntityManager.Exists(prefab))
                return true;

            // Prefabs are made when a mission loads, so (re)build the map when it is stale.
            _unitPrefabs.Clear();
            using (var prefabs = _playerUnitPrefabs.ToEntityArray(Allocator.Temp))
                foreach (var candidate in prefabs)
                {
                    var candidateType = UnitTypeOf(candidate);
                    if (!_unitPrefabs.ContainsKey(candidateType))
                        _unitPrefabs[candidateType] = candidate;
                }

            if (!_prefabsLogged && _unitPrefabs.Count > 0)
            {
                _prefabsLogged = true;
                Plugin.Log.LogInfo($"Player unit types in this mission: {string.Join(", ", _unitPrefabs.Keys.Select(t => t.ToString()).OrderBy(t => t))}");
                foreach (var chainType in _tierOf.Keys.Where(t => !_unitPrefabs.ContainsKey(t)))
                    Plugin.Log.LogWarning($"PromotionChains: no player unit of type {chainType} in this mission; units can't promote into it.");
            }
            return _unitPrefabs.TryGetValue(type, out prefab);
        }

        /// <summary>Queues a promotion if the unit has reached its promotion level.</summary>
        private void CheckPromotion(Entity unit, Veteran veteran)
        {
            if (!veteran.IsEnemy && veteran.Level >= Plugin.PromotionLevel.Value && !_pendingPromotions.Contains(unit)
                && EntityManager.HasComponent<UnitBase>(unit) && CanPromote(UnitTypeOf(unit)))
                _pendingPromotions.Add(unit);
        }

        /// <summary>Runs queued promotions; done after damage processing so no loop holds the old entities.</summary>
        private void ProcessPromotions()
        {
            if (_pendingPromotions.Count == 0)
                return;
            _deferredPromotions.Clear();
            foreach (var unit in _pendingPromotions)
            {
                if (EntityManager.Exists(unit) && IsGarrisoned(unit))
                {
                    _deferredPromotions.Add(unit);
                    continue;
                }
                try
                {
                    Promote(unit);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Promotion failed: {e}");
                }
            }
            _pendingPromotions.Clear();
            _pendingPromotions.AddRange(_deferredPromotions);
        }

        /// <summary>
        /// Units on or heading to a tower, or inside a building, are tracked by that building.
        /// Swapping them would leave it pointing at a destroyed unit, so they promote once they're out.
        /// </summary>
        private bool IsGarrisoned(Entity unit)
        {
            return EntityManager.HasComponent<UnitOnTower>(unit) || EntityManager.HasComponent<UnitToTower>(unit)
                || EntityManager.HasComponent<TowerPlaceReference>(unit) || EntityManager.HasComponent<PlaceTowerReference>(unit)
                || EntityManager.HasComponent<IsInside>(unit) || EntityManager.HasComponent<ToInside>(unit);
        }

        private void Promote(Entity old)
        {
            if (!EntityManager.Exists(old) || EntityManager.HasComponent<Dead>(old) || !Veterans.TryGetValue(old, out var veteran))
                return;
            var oldType = UnitTypeOf(old);
            if (!_nextTier.TryGetValue(oldType, out var newType) || !TryGetPrefab(newType, out var prefab))
                return;

            var unit = EntityManager.Instantiate(prefab);
            var translation = EntityManager.GetComponentData<Translation>(old);
            SetOrAdd(unit, translation);
            CopyComponent<Rotation>(old, unit);
            CopyComponent<LocalToWorld>(old, unit);

            // State the trained unit carried over from its citizen.
            CopyComponent<Citizen>(old, unit);
            CopyComponent<Homeless>(old, unit);
            CopyComponent<Hungry>(old, unit);
            CopyComponent<Cold>(old, unit);
            CopyComponent<SquadMarker>(old, unit);
            bool selected = EntityManager.HasComponent<Selected>(old);
            if (selected)
                EntityManager.AddComponent<Selected>(unit);
            MoveHome(old, unit);
            MoveGhostCitizens(old, unit);

            // Hold position where the old unit stood, like a freshly trained unit.
            SetOrAdd(unit, new InDefend(translation.Value.xz));
            SetOrAdd(unit, new UnitFightState(UnitFightStates.InDefend));

            // Mission objectives that track the old unit follow the new one.
            var remap = EntityManager.CreateEntity();
            EntityManager.AddComponentData(remap, RemapEntityEventTask.Create(unit, old));

            Veterans.Remove(old);
            veteran.Level = 0;
            veteran.Xp = 0;
            veteran.Promotions++;
            veteran.BaseScale = null;
            Veterans[unit] = veteran;
            ConfigureTier(unit, veteran);
            ApplyHealthBonus(unit, veteran);
            ApplySize(unit, veteran);

            // Promotion comes with a full heal.
            var healthBase = EntityManager.GetComponentData<HealthBase>(unit);
            EntityManager.SetComponentData(unit, new Health { currentHealth = healthBase.GetData().maxHealth * healthBase.GetMaxHealthMultiplier() });

            // The home now lists the new unit; drop the old one's claim so the game doesn't evict it again.
            if (EntityManager.HasComponent<ParentBuildingState>(old))
                EntityManager.RemoveComponent<ParentBuildingState>(old);
            EntityManager.DestroyEntity(old);

            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Units);
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Population);
            if (EntityManager.HasComponent<SquadMarker>(unit))
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Squads);
            if (selected)
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Selection);

            VeterancyOverlay.Announce($"{veteran.Name} has been promoted from {oldType} to {newType}!");
        }

        private void MoveHome(Entity old, Entity unit)
        {
            if (!EntityManager.HasComponent<ParentBuildingState>(old))
                return;
            var home = EntityManager.GetComponentData<ParentBuildingState>(old);
            EntityManager.AddComponentData(unit, home);
            if (home.value == Entity.Null || !EntityManager.HasComponent<Housemate>(home.value))
                return;

            var housemates = EntityManager.GetBuffer<Housemate>(home.value);
            for (int i = 0; i < housemates.Length; i++)
            {
                if (housemates[i].citizen == old)
                {
                    housemates[i] = new Housemate { citizen = unit };
                    break;
                }
            }
        }

        /// <summary>Units that cost more than one person hold the extra people as ghost citizens.</summary>
        private void MoveGhostCitizens(Entity old, Entity unit)
        {
            if (!EntityManager.HasComponent<GhostCitizenReference>(old))
                return;
            var ghosts = EntityManager.GetBuffer<GhostCitizenReference>(old).ToNativeArray(Allocator.Temp);
            var buffer = EntityManager.HasComponent<GhostCitizenReference>(unit)
                ? EntityManager.GetBuffer<GhostCitizenReference>(unit)
                : EntityManager.AddBuffer<GhostCitizenReference>(unit);
            buffer.AddRange(ghosts);
            foreach (var ghost in ghosts)
                if (EntityManager.Exists(ghost.ghost))
                    SetOrAdd(ghost.ghost, new GhostCitizen(unit));
            ghosts.Dispose();
        }

        private void CopyComponent<T>(Entity from, Entity to) where T : struct, IComponentData
        {
            if (EntityManager.HasComponent<T>(from))
                SetOrAdd(to, EntityManager.GetComponentData<T>(from));
        }

        private void SetOrAdd<T>(Entity entity, T data) where T : struct, IComponentData
        {
            if (EntityManager.HasComponent<T>(entity))
                EntityManager.SetComponentData(entity, data);
            else
                EntityManager.AddComponentData(entity, data);
        }
    }
}
