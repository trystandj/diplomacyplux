using System.Collections.Generic;
using System.Reflection;
using Components;
using Components.RawComponents;
using Systems;
using Systems.ComponentSystemGroups;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace VeteranLevels
{
    /// <summary>
    /// Gives player units experience for kills, levels them up, and applies the level bonuses.
    ///
    /// Runs on the main thread before the game's fight systems and HealthTrackSystem, so it sees
    /// each frame's new damage requests and projectiles before the game applies them:
    ///  - Melee hits are HealthChangeRequest entities: a veteran's request Value is scaled up.
    ///  - Arrows carry ProjectileFlyData.damage: a veteran's new projectiles are scaled up.
    ///  - A request that takes an enemy to 0 HP credits the player unit attacking that enemy.
    ///  - Max health uses the unit's own HealthBase multiplier, which the game saves, so
    ///    levels are restored from it after loading a save.
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(FightGroup))]
    [UpdateBefore(typeof(HealthTrackSystem))]
    public class VeterancySystem : SystemBase
    {
        private const float MaintenanceInterval = 2f;
        private const int NoSourcePosition = -999;

        private static readonly FieldInfo MaxHealthMultiplierField =
            typeof(HealthBase).GetField("_maxHealthMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

        public readonly Dictionary<Entity, Veteran> Veterans = new Dictionary<Entity, Veteran>();

        /// <summary>Set by the level-up hotkey; handled on the next system update.</summary>
        internal static bool LevelUpSelectedRequested;

        private EntityQuery _playerFighters;
        private EntityQuery _selectedPlayerUnits;
        private EntityQuery _playerUnitsWithHealth;
        private EntityQuery _damageRequests;
        private EntityQuery _playerProjectiles;

        private HashSet<Entity> _seenRequests = new HashSet<Entity>();
        private HashSet<Entity> _seenRequestsNext = new HashSet<Entity>();
        private HashSet<Entity> _seenProjectiles = new HashSet<Entity>();
        private HashSet<Entity> _seenProjectilesNext = new HashSet<Entity>();

        private readonly Dictionary<Entity, List<(Entity unit, float2 pos)>> _attackersByTarget =
            new Dictionary<Entity, List<(Entity, float2)>>();
        private readonly Dictionary<Entity, float> _predictedHealth = new Dictionary<Entity, float>();
        private bool _attackerMapBuilt;
        private bool _scaleConflictLogged;
        private float _nextMaintenance;

        protected override void OnCreate()
        {
            if (MaxHealthMultiplierField == null)
                Plugin.Log.LogError("HealthBase._maxHealthMultiplier not found; health bonuses are disabled (game update?).");

            _playerFighters = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<FightTarget>(), ComponentType.ReadOnly<Translation>() },
                None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Dead>() },
            });
            _playerUnitsWithHealth = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<HealthBase>() },
                None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Dead>() },
            });
            _selectedPlayerUnits = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<Selected>() },
                None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Dead>() },
            });
            _damageRequests = GetEntityQuery(ComponentType.ReadWrite<HealthChangeRequest>());
            _playerProjectiles = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<ProjectileFlyData>() },
                None = new[] { ComponentType.ReadOnly<Enemy>() },
            });
        }

        protected override void OnUpdate()
        {
            float now = (float)Time.ElapsedTime;
            if (now >= _nextMaintenance || now < _nextMaintenance - MaintenanceInterval)
            {
                _nextMaintenance = now + MaintenanceInterval;
                RemoveGoneVeterans();
                RestoreLevelsFromSave();
            }

            if (LevelUpSelectedRequested)
            {
                LevelUpSelectedRequested = false;
                LevelUpSelected();
            }

            ScaleNewProjectiles();
            ProcessDamageRequests();
            RefreshVeteranPositions();
        }

        /// <summary>Hotkey cheat: give every selected player unit one level.</summary>
        private void LevelUpSelected()
        {
            int leveled = 0;
            using (var units = _selectedPlayerUnits.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    if (!Veterans.TryGetValue(unit, out var veteran))
                        Veterans[unit] = veteran = new Veteran();
                    if (veteran.Level >= Plugin.MaxLevel.Value)
                        continue;

                    veteran.Level++;
                    veteran.Xp = 0;
                    OnLevelGained(unit, veteran, announce: units.Length <= 3);
                    leveled++;
                }
                if (units.Length == 0)
                    VeterancyOverlay.Announce($"Select some of your units, then press {Plugin.LevelUpSelectedKey.Value} to level them up.");
                else if (leveled == 0)
                    VeterancyOverlay.Announce("The selected units are already at max level.");
                else if (units.Length > 3)
                    VeterancyOverlay.Announce($"{leveled} selected units gained a level.");
            }
        }

        private void ScaleNewProjectiles()
        {
            _seenProjectilesNext.Clear();
            using (var projectiles = _playerProjectiles.ToEntityArray(Allocator.Temp))
            {
                foreach (var projectile in projectiles)
                {
                    _seenProjectilesNext.Add(projectile);
                    if (_seenProjectiles.Contains(projectile))
                        continue;

                    var fly = EntityManager.GetComponentData<ProjectileFlyData>(projectile);
                    if (Veterans.TryGetValue(fly.sourceEntity, out var veteran) && veteran.Level > 0)
                    {
                        fly.damage *= veteran.DamageMultiplier;
                        EntityManager.SetComponentData(projectile, fly);
                    }
                }
            }
            (_seenProjectiles, _seenProjectilesNext) = (_seenProjectilesNext, _seenProjectiles);
        }

        private void ProcessDamageRequests()
        {
            _seenRequestsNext.Clear();
            _predictedHealth.Clear();
            _attackerMapBuilt = false;

            using (var requestEntities = _damageRequests.ToEntityArray(Allocator.Temp))
            using (var requests = _damageRequests.ToComponentDataArray<HealthChangeRequest>(Allocator.Temp))
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    var requestEntity = requestEntities[i];
                    _seenRequestsNext.Add(requestEntity);
                    if (_seenRequests.Contains(requestEntity))
                        continue;

                    var request = requests[i];
                    var target = request.Target;
                    if (request.Value >= 0f || !IsLivingEnemy(target))
                        continue;

                    var attacker = FindAttacker(request, target);
                    bool isMelee = (request.AdditionalData & HealthRequestFlags.IsMeleeDamage) != 0;
                    if (isMelee && attacker != Entity.Null && Veterans.TryGetValue(attacker, out var veteran) && veteran.Level > 0)
                    {
                        request.Value *= veteran.DamageMultiplier;
                        EntityManager.SetComponentData(requestEntity, request);
                    }

                    // Several hits can land in one frame; the one that crosses 0 HP gets the kill.
                    if (!_predictedHealth.TryGetValue(target, out float health))
                        health = EntityManager.GetComponentData<Health>(target).currentHealth;
                    float healthAfter = health + request.Value;
                    _predictedHealth[target] = healthAfter;

                    if (health > 0f && healthAfter <= 0f && attacker != Entity.Null)
                        GrantKill(attacker);
                }
            }
            (_seenRequests, _seenRequestsNext) = (_seenRequestsNext, _seenRequests);
        }

        private bool IsLivingEnemy(Entity e)
        {
            return EntityManager.Exists(e)
                && EntityManager.HasComponent<Enemy>(e)
                && EntityManager.HasComponent<Health>(e)
                && !EntityManager.HasComponent<Dead>(e);
        }

        /// <summary>
        /// Requests don't say who attacked, so pick the player unit fighting this target that is
        /// closest to where the hit came from (the attacker/shooter position when the game records
        /// it, otherwise the target).
        /// </summary>
        private Entity FindAttacker(HealthChangeRequest request, Entity target)
        {
            BuildAttackerMap();
            if (!_attackersByTarget.TryGetValue(target, out var candidates))
                return Entity.Null;
            if (candidates.Count == 1)
                return candidates[0].unit;

            float2 from;
            if (request.DamageSourcePosition.x != NoSourcePosition)
                from = new float2(request.DamageSourcePosition);
            else if (EntityManager.HasComponent<Translation>(target))
                from = EntityManager.GetComponentData<Translation>(target).Value.xz;
            else
                return candidates[0].unit;

            var best = candidates[0];
            float bestDistance = math.distancesq(best.pos, from);
            for (int i = 1; i < candidates.Count; i++)
            {
                float d = math.distancesq(candidates[i].pos, from);
                if (d < bestDistance)
                {
                    best = candidates[i];
                    bestDistance = d;
                }
            }
            return best.unit;
        }

        private void BuildAttackerMap()
        {
            if (_attackerMapBuilt)
                return;
            _attackerMapBuilt = true;

            foreach (var list in _attackersByTarget.Values)
                list.Clear();

            using (var units = _playerFighters.ToEntityArray(Allocator.Temp))
            using (var targets = _playerFighters.ToComponentDataArray<FightTarget>(Allocator.Temp))
            using (var positions = _playerFighters.ToComponentDataArray<Translation>(Allocator.Temp))
            {
                for (int i = 0; i < units.Length; i++)
                {
                    var target = targets[i].Target;
                    if (target == Entity.Null)
                        continue;
                    if (!_attackersByTarget.TryGetValue(target, out var list))
                        _attackersByTarget[target] = list = new List<(Entity, float2)>();
                    list.Add((units[i], positions[i].Value.xz));
                }
            }

            // Drop empty lists so the map doesn't grow forever.
            List<Entity> empty = null;
            foreach (var pair in _attackersByTarget)
                if (pair.Value.Count == 0)
                    (empty ??= new List<Entity>()).Add(pair.Key);
            if (empty != null)
                foreach (var key in empty)
                    _attackersByTarget.Remove(key);
        }

        private void GrantKill(Entity unit)
        {
            if (!Veterans.TryGetValue(unit, out var veteran))
                Veterans[unit] = veteran = new Veteran();

            veteran.Kills++;
            veteran.Xp += Plugin.XpPerKill.Value;

            int oldLevel = veteran.Level;
            while (veteran.Level < Plugin.MaxLevel.Value && veteran.Xp >= Veteran.XpToNextLevel(veteran.Level))
            {
                veteran.Xp -= Veteran.XpToNextLevel(veteran.Level);
                veteran.Level++;
            }
            if (veteran.Level != oldLevel)
                OnLevelGained(unit, veteran, announce: true);
        }

        private void OnLevelGained(Entity unit, Veteran veteran, bool announce)
        {
            if (veteran.Name == null)
                veteran.Name = Veteran.RandomName();
            ApplyHealthBonus(unit, veteran);
            ApplySize(unit, veteran);
            if (announce)
                VeterancyOverlay.Announce($"{veteran.DisplayName} reached level {veteran.Level}! ({veteran.Kills} kills)");
        }

        private void ApplyHealthBonus(Entity unit, Veteran veteran)
        {
            if (MaxHealthMultiplierField == null || !EntityManager.HasComponent<HealthBase>(unit))
                return;

            var healthBase = EntityManager.GetComponentData<HealthBase>(unit);
            float oldMultiplier = healthBase.GetMaxHealthMultiplier();
            float newMultiplier = veteran.HealthMultiplier;

            object boxed = healthBase;
            MaxHealthMultiplierField.SetValue(boxed, newMultiplier);
            EntityManager.SetComponentData(unit, (HealthBase)boxed);

            // Keep the same health percentage, so the unit also gains the new hit points.
            if (EntityManager.HasComponent<Health>(unit))
            {
                var health = EntityManager.GetComponentData<Health>(unit);
                health.currentHealth *= newMultiplier / oldMultiplier;
                EntityManager.SetComponentData(unit, health);
            }
        }

        /// <summary>
        /// Grows the unit model with its level. The transform system builds LocalToWorld from
        /// Translation/Rotation/Scale, and child parts (weapons, shields) follow the parent.
        /// </summary>
        private void ApplySize(Entity unit, Veteran veteran)
        {
            if (Plugin.SizePerLevel.Value <= 0f && veteran.BaseScale == null)
                return;
            if (EntityManager.HasComponent<NonUniformScale>(unit) || EntityManager.HasComponent<CompositeScale>(unit))
            {
                if (!_scaleConflictLogged)
                    Plugin.Log.LogWarning("Some units use a non-uniform scale; they won't grow with levels.");
                _scaleConflictLogged = true;
                return;
            }

            bool hasScale = EntityManager.HasComponent<Scale>(unit);
            if (veteran.BaseScale == null)
                veteran.BaseScale = hasScale ? EntityManager.GetComponentData<Scale>(unit).Value : 1f;

            var scale = new Scale { Value = veteran.BaseScale.Value * veteran.SizeMultiplier };
            if (hasScale)
                EntityManager.SetComponentData(unit, scale);
            else
                EntityManager.AddComponentData(unit, scale);
        }

        private void RemoveGoneVeterans()
        {
            List<Entity> gone = null;
            foreach (var unit in Veterans.Keys)
                if (!EntityManager.Exists(unit) || EntityManager.HasComponent<Dead>(unit))
                    (gone ??= new List<Entity>()).Add(unit);
            if (gone != null)
                foreach (var unit in gone)
                    Veterans.Remove(unit);
        }

        /// <summary>After loading a save, rebuild levels from the saved max-health multiplier.</summary>
        private void RestoreLevelsFromSave()
        {
            float healthPerLevel = Plugin.HealthPerLevel.Value;
            if (healthPerLevel <= 0f)
                return;

            using (var units = _playerUnitsWithHealth.ToEntityArray(Allocator.Temp))
            using (var healthBases = _playerUnitsWithHealth.ToComponentDataArray<HealthBase>(Allocator.Temp))
            {
                for (int i = 0; i < units.Length; i++)
                {
                    if (!healthBases[i].HasHealthMultiplier || Veterans.ContainsKey(units[i]))
                        continue;
                    int level = (int)math.round((healthBases[i].GetMaxHealthMultiplier() - 1f) / healthPerLevel);
                    if (level < 1)
                        continue;
                    var veteran = new Veteran
                    {
                        Level = math.min(level, Plugin.MaxLevel.Value),
                        Name = Veteran.RandomName(),
                    };
                    Veterans[units[i]] = veteran;
                    ApplySize(units[i], veteran);   // Scale isn't part of the game's save data.
                }
            }
        }

        private void RefreshVeteranPositions()
        {
            foreach (var pair in Veterans)
                if (pair.Value.IsShown && EntityManager.Exists(pair.Key) && EntityManager.HasComponent<Translation>(pair.Key))
                    pair.Value.Position = EntityManager.GetComponentData<Translation>(pair.Key).Value;
        }
    }
}
