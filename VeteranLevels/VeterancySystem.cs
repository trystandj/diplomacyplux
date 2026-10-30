using System.Collections.Generic;
using System.Reflection;
using Components;
using Components.RawComponents;
using Systems;
using Systems.ComponentSystemGroups;
using Systems.GameStateSystems;
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
    ///  - Some enemy wave units spawn as heroes: enemy veterans that level up by killing player units.
    ///  - Player units reaching certain levels award soul crystals.
    ///  - Only HeroCap player units can be heroes (HeroCap.cs), and units in a promotion chain
    ///    become the next unit type at PromotionLevel (Promotion.cs).
    ///  - The Barracks can train tamed creatures after the Beast Taming research (Creatures.cs).
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(FightGroup))]
    [UpdateBefore(typeof(HealthTrackSystem))]
    public partial class VeterancySystem : SystemBase
    {
        private const float MaintenanceInterval = 2f;
        private const int NoSourcePosition = -999;

        private static readonly FieldInfo MaxHealthMultiplierField =
            typeof(HealthBase).GetField("_maxHealthMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

        public readonly Dictionary<Entity, Veteran> Veterans = new Dictionary<Entity, Veteran>();

        /// <summary>The running system, for UI patches that live outside ECS.</summary>
        internal static VeterancySystem Instance;

        /// <summary>Set by the level-up hotkey; handled on the next system update.</summary>
        internal static bool LevelUpSelectedRequested;

        private EntityQuery _fighters;
        private EntityQuery _selectedPlayerUnits;
        private EntityQuery _playerUnitsWithHealth;
        private EntityQuery _damageRequests;
        private EntityQuery _projectiles;
        private EntityQuery _arrivingEnemies;
        private EntityQuery _souls;

        /// <summary>Wave enemies already rolled for hero status (dropped once they enter the map).</summary>
        private readonly HashSet<Entity> _rolledEnemies = new HashSet<Entity>();

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
            Instance = this;
            if (MaxHealthMultiplierField == null)
                Plugin.Log.LogError("HealthBase._maxHealthMultiplier not found; health bonuses are disabled (game update?).");

            _fighters = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<FightTarget>(), ComponentType.ReadOnly<Translation>() },
                None = new[] { ComponentType.ReadOnly<Dead>() },
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
            _projectiles = GetEntityQuery(ComponentType.ReadWrite<ProjectileFlyData>());
            // Wave units walk in from outside the map with WaveUnitDisabled, so each one is seen
            // here once, when it spawns. Units already on the map when a save loads are not rolled.
            _arrivingEnemies = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<HealthBase>(), ComponentType.ReadOnly<WaveUnitDisabled>() },
                None = new[] { ComponentType.ReadOnly<Dead>() },
            });
            _souls = GetEntityQuery(ComponentType.ReadWrite<CurrentSouls>());
            CreatePromotion();
            CreateCreatures();
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        protected override void OnUpdate()
        {
            float now = (float)Time.ElapsedTime;
            if (now >= _nextMaintenance || now < _nextMaintenance - MaintenanceInterval)
            {
                _nextMaintenance = now + MaintenanceInterval;
                RemoveGoneVeterans();
                RestoreLevelsFromSave();
                RefreshHeroCap();
                UpdateCreatures();
            }

            if (BuyCapUpgradeRequested)
            {
                BuyCapUpgradeRequested = false;
                BuyCapUpgrade();
            }
            if (BuyBeastTamingRequested)
            {
                BuyBeastTamingRequested = false;
                BuyBeastTaming();
            }
            if (SpawnResourcesRequested)
            {
                SpawnResourcesRequested = false;
                SpawnResourcesAtCursor();
            }
            if (LevelUpSelectedRequested)
            {
                LevelUpSelectedRequested = false;
                LevelUpSelected();
            }

            TameTrainedCreatures();
            RollEnemyHeroes();
            ScaleNewProjectiles();
            ProcessDamageRequests();
            ProcessPromotions();
            RefreshVeteranPositions();
        }

        private Veteran GetOrCreatePlayerVeteran(Entity unit)
        {
            if (!Veterans.TryGetValue(unit, out var veteran))
            {
                Veterans[unit] = veteran = new Veteran();
                ConfigureTier(unit, veteran);
            }
            return veteran;
        }

        /// <summary>Hotkey cheat: give every selected player unit one level.</summary>
        private void LevelUpSelected()
        {
            int leveled = 0;
            int blockedByCap = 0;
            using (var units = _selectedPlayerUnits.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    var veteran = GetOrCreatePlayerVeteran(unit);
                    if (veteran.IsMaxLevel)
                        continue;
                    if (!veteran.IsHero && CountHeroes() >= HeroCap)
                    {
                        blockedByCap++;
                        continue;
                    }

                    veteran.Level++;
                    veteran.Xp = 0;
                    OnLevelGained(unit, veteran, veteran.Level - 1, announce: units.Length <= 3);
                    leveled++;
                }
                if (units.Length == 0)
                    VeterancyOverlay.Announce($"Select some of your units, then press {Plugin.LevelUpSelectedKey.Value} to level them up.");
                if (blockedByCap > 0)
                    VeterancyOverlay.Announce($"Hero cap reached ({HeroCap}): {blockedByCap} selected unit(s) can't become heroes. Press {Plugin.HeroPanelKey.Value} to raise the cap.");
                else if (units.Length > 0 && leveled == 0)
                    VeterancyOverlay.Announce("The selected units are already at max level.");
                else if (units.Length > 3)
                    VeterancyOverlay.Announce($"{leveled} selected units gained a level.");
            }
        }

        /// <summary>Each newly spawned wave enemy has a 1 in EnemyHeroOneIn chance to become a hero.</summary>
        private void RollEnemyHeroes()
        {
            int oneIn = Plugin.EnemyHeroOneIn.Value;
            if (oneIn <= 0 || _arrivingEnemies.IsEmpty)
                return;

            Entity lastHero = Entity.Null;
            int heroes = 0;
            using (var units = _arrivingEnemies.ToEntityArray(Allocator.Temp))
            {
                foreach (var unit in units)
                {
                    if (!_rolledEnemies.Add(unit) || Veterans.ContainsKey(unit) || Veteran.Rng.Next(oneIn) != 0)
                        continue;

                    var veteran = new Veteran
                    {
                        IsEnemy = true,
                        Level = math.clamp(Plugin.EnemyHeroStartLevel.Value, 1, Plugin.MaxLevel.Value),
                        Name = Veteran.RandomName(),
                        BaseHealthMultiplier = EntityManager.GetComponentData<HealthBase>(unit).GetMaxHealthMultiplier(),
                    };
                    Veterans[unit] = veteran;
                    ApplyHealthBonus(unit, veteran);
                    ApplySize(unit, veteran);
                    lastHero = unit;
                    heroes++;
                }
            }

            if (heroes == 1)
                VeterancyOverlay.Announce($"An enemy hero, {Veterans[lastHero].DisplayName}, joins the attack!");
            else if (heroes > 1)
                VeterancyOverlay.Announce($"{heroes} enemy heroes join the attack!");
        }

        private void ScaleNewProjectiles()
        {
            _seenProjectilesNext.Clear();
            using (var projectiles = _projectiles.ToEntityArray(Allocator.Temp))
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
                    if (request.Value >= 0f || !IsLivingTarget(target))
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
                        GrantKill(attacker, target);
                }
            }
            (_seenRequests, _seenRequestsNext) = (_seenRequestsNext, _seenRequests);
        }

        private bool IsLivingTarget(Entity e)
        {
            return EntityManager.Exists(e)
                && EntityManager.HasComponent<Health>(e)
                && !EntityManager.HasComponent<Dead>(e);
        }

        /// <summary>
        /// Requests don't say who attacked, so pick the unit fighting this target that is closest
        /// to where the hit came from (the attacker/shooter position when the game records it,
        /// otherwise the target).
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

            using (var units = _fighters.ToEntityArray(Allocator.Temp))
            using (var targets = _fighters.ToComponentDataArray<FightTarget>(Allocator.Temp))
            using (var positions = _fighters.ToComponentDataArray<Translation>(Allocator.Temp))
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

        /// <summary>
        /// Player units get XP for killing any enemy; enemies only level up if they are heroes,
        /// and only from killing player units.
        /// </summary>
        private void GrantKill(Entity unit, Entity target)
        {
            bool attackerIsEnemy = EntityManager.HasComponent<Enemy>(unit);
            if (attackerIsEnemy == EntityManager.HasComponent<Enemy>(target))
                return;

            if (Veterans.TryGetValue(target, out var fallen) && fallen.IsEnemy)
                VeterancyOverlay.Announce($"Enemy hero {fallen.DisplayName} has fallen!");

            Veteran veteran;
            if (attackerIsEnemy)
            {
                if (!Veterans.TryGetValue(unit, out veteran) || !EntityManager.HasComponent<UnitBase>(target))
                    return;
            }
            else
            {
                veteran = GetOrCreatePlayerVeteran(unit);
                // At the hero cap, units that aren't heroes yet don't gain XP.
                if (!veteran.IsHero && !HasFreeHeroSlot())
                    return;
            }

            veteran.Kills++;
            veteran.Xp += Plugin.XpPerKill.Value;

            int oldLevel = veteran.Level;
            while (veteran.Level < veteran.LevelCap && veteran.Xp >= Veteran.XpToNextLevel(veteran.Level))
            {
                veteran.Xp -= Veteran.XpToNextLevel(veteran.Level);
                veteran.Level++;
            }
            if (veteran.Level != oldLevel)
                OnLevelGained(unit, veteran, oldLevel, announce: true);
        }

        private void OnLevelGained(Entity unit, Veteran veteran, int oldLevel, bool announce)
        {
            if (veteran.Name == null)
                veteran.Name = Veteran.RandomName();
            ApplyHealthBonus(unit, veteran);
            ApplySize(unit, veteran);
            // Refresh the info panel so a selected unit shows its new damage and health.
            if (EntityManager.HasComponent<Selected>(unit))
                GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.UI);
            if (announce)
            {
                string who = veteran.IsEnemy ? $"Enemy hero {veteran.DisplayName}" : veteran.DisplayName;
                VeterancyOverlay.Announce($"{who} reached level {veteran.Level}! ({veteran.Kills} kills)");
            }
            if (!veteran.IsEnemy)
            {
                AwardSoulCrystals(veteran, oldLevel);
                CheckPromotion(unit, veteran);
            }
        }

        /// <summary>Adds the configured soul crystals for each reward level the unit just passed.</summary>
        private void AwardSoulCrystals(Veteran veteran, int oldLevel)
        {
            int crystals = 0;
            for (int level = oldLevel + 1; level <= veteran.Level; level++)
                if (Plugin.SoulCrystalsByLevel.TryGetValue(level, out int reward))
                    crystals += reward;
            if (crystals <= 0 || _souls.CalculateEntityCount() != 1)
                return;

            var souls = _souls.GetSingleton<CurrentSouls>();
            souls.IncreaseAmount(crystals);
            _souls.SetSingleton(souls);
            // The resource bar only re-reads resources when a Resources update task exists.
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Resources);
            SoulCrystals = souls.CurrentAmount();
            VeterancyOverlay.Announce($"{veteran.DisplayName} earned you {crystals} soul crystal{(crystals == 1 ? "" : "s")}!");
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

            _rolledEnemies.RemoveWhere(e => !EntityManager.Exists(e) || !EntityManager.HasComponent<WaveUnitDisabled>(e));
        }

        /// <summary>
        /// After loading a save, rebuild levels and promotions from the saved max-health multiplier:
        /// 1 + HealthPerLevel * level + PromotionMarker * promotions.
        /// </summary>
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
                    float bonus = healthBases[i].GetMaxHealthMultiplier() - 1f;
                    int level = (int)math.round(bonus / healthPerLevel);
                    int promotions = math.max(0, (int)math.round((bonus - level * healthPerLevel) / Veteran.PromotionMarker));
                    if (level < 1 && promotions < 1)
                        continue;
                    var veteran = new Veteran
                    {
                        Level = math.clamp(level, 0, Plugin.MaxLevel.Value),
                        Promotions = promotions,
                        Name = Veteran.RandomName(),
                    };
                    Veterans[units[i]] = veteran;
                    ConfigureTier(units[i], veteran);
                    ApplySize(units[i], veteran);   // Scale isn't part of the game's save data.
                    CheckPromotion(units[i], veteran);   // e.g. saved while waiting in a tower
                }
            }
        }

        private void RefreshVeteranPositions()
        {
            foreach (var pair in Veterans)
            {
                var veteran = pair.Value;
                if (!veteran.IsShown || !EntityManager.Exists(pair.Key))
                    continue;
                if (EntityManager.HasComponent<Translation>(pair.Key))
                    veteran.Position = EntityManager.GetComponentData<Translation>(pair.Key).Value;
                // Don't reveal enemy heroes in the fog of war (HideUnitInFog > 0 means hidden).
                veteran.Hidden = veteran.IsEnemy && EntityManager.HasComponent<HideUnitInFog>(pair.Key)
                    && EntityManager.GetComponentData<HideUnitInFog>(pair.Key).Value > 0f;
            }
        }
    }
}
