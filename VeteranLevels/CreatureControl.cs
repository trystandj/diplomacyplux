using System.Collections.Generic;
using System.Linq;
using Components;
using Components.RawComponents;
using Components.SharedContainerSingletons;
using Components.Structs;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Utility;
using Utility.EnumsStorage;

namespace VeteranLevels
{
    /// <summary>
    /// What a tamed creature needs, beyond losing its Enemy tag, to act like one of the player's units.
    ///
    /// Orders: move and attack orders are laid out as a formation, and the formation only places
    /// unit types listed in the game's formation groups; any other type is silently left out, so
    /// creatures ignored every order. Each creature type is added to the game's sub-group map as a
    /// variant of a similar soldier (the game does the same for its own unit variants), so it takes
    /// that soldier's place in formations.
    ///
    /// Fog of war: player units reveal fog through AffectFogBase, which enemy prefabs don't have.
    /// A tamed creature gets the stand-in soldier's.
    ///
    /// Health: tamed creatures get their own copy of the health data with max health scaled by
    /// CreatureHealth (wild creatures keep theirs). The copy isn't saved, so it is reapplied after
    /// loading, and periodically in case the game swaps the data back.
    ///
    /// Looks: the selection circle is a child entity whose material is baked into the prefab, red for
    /// enemy prefabs. A tamed creature gets the circle material of that same soldier. Child entities
    /// of an enemy prefab (its collision obstacle) carry their own Enemy tag, which is removed too.
    /// </summary>
    public partial class VeterancySystem
    {
        /// <summary>Soldier types a creature stands in for, in order of preference.</summary>
        private static readonly Dictionary<UnitType, UnitType[]> StandInTypes = new Dictionary<UnitType, UnitType[]>
        {
            [UnitType.Wolf] = new[] { UnitType.Horseman, UnitType.Spearman, UnitType.Swordsman },
            [UnitType.Giant] = new[] { UnitType.Knight, UnitType.Swordsman, UnitType.Spearman },
        };
        private static readonly UnitType[] DefaultStandIns = { UnitType.Swordsman, UnitType.Spearman };

        private EntityQuery _formationSingleton;
        private EntityQuery _playerUnits;
        /// <summary>Creature type -> the player soldier prefab (and its object id) it borrows from.</summary>
        private readonly Dictionary<UnitType, (int id, Entity prefab)> _standIns = new Dictionary<UnitType, (int, Entity)>();
        private readonly HashSet<UnitType> _formationsLogged = new HashSet<UnitType>();
        private bool _playerUnitLogged;
        /// <summary>Creatures already given player visuals (covers ones tamed by an older version too).</summary>
        private readonly HashSet<Entity> _friendlyCreatures = new HashSet<Entity>();
        /// <summary>Per creature type: the health data blob of the wild creature and our scaled copy of it.</summary>
        private readonly Dictionary<UnitType, (BlobAssetReference<HealthBaseData> wild, BlobAssetReference<HealthBaseData> tamed)> _tamedHealth =
            new Dictionary<UnitType, (BlobAssetReference<HealthBaseData>, BlobAssetReference<HealthBaseData>)>();

        private void CreateCreatureControl()
        {
            _formationSingleton = GetEntityQuery(ComponentType.ReadOnly<FormationEnvironmentSingleton>());
            _playerUnits = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Selectable>() },
                None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Dead>() },
            });
        }

        /// <summary>Periodic: makes sure every creature type has a place in the game's formations.</summary>
        private void RegisterCreatureFormations()
        {
            if (_formationSingleton.CalculateEntityCount() != 1)
                return;
            var singleton = this.GetSingleton<FormationEnvironmentSingleton>();
            var container = singleton.Container;
            if (!container.SubGroupMap.IsCreated || !container.DefaultUnitMap.IsCreated)
                return;

            bool completed = false;
            foreach (var creature in _creatures)
            {
                // The game reloads its formation maps when a mission starts or loads, so check each time.
                if (container.DefaultUnitMap.ContainsKey(creature.ObjectId) || container.SubGroupMap.ContainsKey(creature.ObjectId))
                    continue;
                var standIn = StandInFor(creature.Type);
                if (standIn.id < 0 || !container.DefaultUnitMap.ContainsKey(standIn.id))
                {
                    if (_formationsLogged.Add(creature.Type))
                        Plugin.Log.LogWarning($"No soldier in the formation groups for {creature.Type} to stand in for; tamed {creature.Type}s won't take orders.");
                    continue;
                }
                if (!completed)
                {
                    // Formation jobs read this map; let them finish before writing to it.
                    this.CompleteDependencies<FormationEnvironmentSingleton>();
                    completed = true;
                }
                container.SubGroupMap.Add(creature.ObjectId, standIn.id);
                if (_formationsLogged.Add(creature.Type))
                    Plugin.Log.LogInfo($"{creature.Type} takes orders in formation as {UnitTypeOf(standIn.prefab)} (object {standIn.id}).");
            }
        }

        /// <summary>The player soldier a creature type borrows formation slot and selection circle from.</summary>
        private (int id, Entity prefab) StandInFor(UnitType creatureType)
        {
            if (_standIns.TryGetValue(creatureType, out var cached) && EntityManager.Exists(cached.prefab))
                return cached;

            var prefabs = GameState.PrefabsMap;
            if (!prefabs.IsCreated)
                return (-1, Entity.Null);
            var preferences = StandInTypes.TryGetValue(creatureType, out var types) ? types : DefaultStandIns;
            var best = (id: -1, prefab: Entity.Null, rank: int.MaxValue);
            using (var ids = prefabs.GetKeyArray(Allocator.Temp))
            {
                foreach (int id in ids.OrderBy(id => id))
                {
                    var prefab = prefabs[id];
                    if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<UnitBase>(prefab)
                        || !EntityManager.HasComponent<Selectable>(prefab) || EntityManager.HasComponent<Enemy>(prefab))
                        continue;
                    int rank = System.Array.IndexOf(preferences, UnitTypeOf(prefab));
                    if (rank >= 0 && rank < best.rank)
                        best = (id, prefab, rank);
                }
            }
            if (best.id >= 0)
                _standIns[creatureType] = (best.id, best.prefab);
            return (best.id, best.prefab);
        }

        /// <summary>When taming: drops the Enemy tag from child entities and swaps in a player selection circle.</summary>
        private void MakeCreatureFriendly(Entity unit, UnitType type)
        {
            if (EntityManager.HasComponent<LinkedEntityGroup>(unit))
            {
                // Copy first: removing components is a structural change that invalidates the buffer.
                using (var children = EntityManager.GetBuffer<LinkedEntityGroup>(unit).Reinterpret<Entity>().ToNativeArray(Allocator.Temp))
                {
                    foreach (var child in children)
                        if (child != unit && EntityManager.Exists(child) && EntityManager.HasComponent<Enemy>(child))
                            EntityManager.RemoveComponent<Enemy>(child);
                }
            }

            var standIn = StandInFor(type);
            if (standIn.id < 0 || !EntityManager.HasComponent<Selectable>(unit))
                return;
            _friendlyCreatures.Add(unit);
            if (EntityManager.HasComponent<AffectFogBase>(standIn.prefab) && !EntityManager.HasComponent<AffectFogBase>(unit))
                EntityManager.AddComponentData(unit, EntityManager.GetComponentData<AffectFogBase>(standIn.prefab));
            var mine = EntityManager.GetComponentData<Selectable>(unit);
            var theirs = EntityManager.GetComponentData<Selectable>(standIn.prefab);
            CopyCircleMaterial(theirs.selectionEntity, mine.selectionEntity);
            CopyCircleMaterial(theirs.weakSelectable, mine.weakSelectable);
        }

        private void CopyCircleMaterial(Entity from, Entity to)
        {
            if (from == Entity.Null || to == Entity.Null || !EntityManager.Exists(from) || !EntityManager.Exists(to)
                || !EntityManager.HasComponent<RenderMesh>(from) || !EntityManager.HasComponent<RenderMesh>(to))
                return;
            var mesh = EntityManager.GetSharedComponentData<RenderMesh>(to);
            mesh.material = EntityManager.GetSharedComponentData<RenderMesh>(from).material;
            EntityManager.SetSharedComponentData(to, mesh);
        }

        /// <summary>Periodic: fixes up tamed creatures that haven't been made friendly yet.</summary>
        private void UpdateFriendlyCreatures()
        {
            if (_playerUnits.IsEmpty)
                return;
            _friendlyCreatures.RemoveWhere(e => !EntityManager.Exists(e));
            using (var units = _playerUnits.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    if (_friendlyCreatures.Contains(unit))
                        continue;
                    var type = UnitTypeOf(unit);
                    if (_creatureTypes.Contains(type))
                        MakeCreatureFriendly(unit, type);
                }
                foreach (var unit in units)
                {
                    var type = UnitTypeOf(unit);
                    if (_creatureTypes.Contains(type))
                        ApplyCreatureHealth(unit, type);
                }
            }
        }

        /// <summary>Scales a tamed creature's max health by CreatureHealth (no-op when already done).</summary>
        private void ApplyCreatureHealth(Entity unit, UnitType type)
        {
            float scale = Plugin.CreatureHealth.Value;
            if (scale <= 0f || System.Math.Abs(scale - 1f) < 0.0001f || !EntityManager.HasComponent<HealthBase>(unit))
                return;
            var healthBase = EntityManager.GetComponentData<HealthBase>(unit);
            if (_tamedHealth.TryGetValue(type, out var blobs) && healthBase.value == blobs.tamed)
                return;
            if (!_tamedHealth.TryGetValue(type, out blobs) || healthBase.value != blobs.wild)
            {
                var data = healthBase.GetData();
                data.maxHealth *= scale;
                blobs = (healthBase.value, BlobConstructor.ConstructSharedImmutableData(data));
                _tamedHealth[type] = blobs;
            }

            // Only the blob changes; the multiplier (veteran levels) is kept.
            float oldMax = healthBase.GetData().maxHealth * healthBase.GetMaxHealthMultiplier();
            healthBase.value = blobs.tamed;
            EntityManager.SetComponentData(unit, healthBase);
            if (EntityManager.HasComponent<Health>(unit) && oldMax > 0f)
            {
                var health = EntityManager.GetComponentData<Health>(unit);
                float newMax = healthBase.GetData().maxHealth * healthBase.GetMaxHealthMultiplier();
                health.currentHealth = System.Math.Min(newMax, health.currentHealth * newMax / oldMax);
                EntityManager.SetComponentData(unit, health);
            }
        }

        /// <summary>Once: logs a normal player unit's components, to compare with the tamed creature logs.</summary>
        private void LogPlayerUnitComponents()
        {
            if (_playerUnitLogged || _playerUnits.IsEmpty)
                return;
            using (var units = _playerUnits.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    var type = UnitTypeOf(unit);
                    if (_creatureTypes.Contains(type))
                        continue;
                    _playerUnitLogged = true;
                    using (var types = EntityManager.GetComponentTypes(unit))
                        Plugin.Log.LogInfo($"Player {type} for comparison; components: {string.Join(", ", types.Select(t => t.GetManagedType()?.Name ?? t.ToString()).OrderBy(n => n))}");
                    return;
                }
            }
        }
    }
}
