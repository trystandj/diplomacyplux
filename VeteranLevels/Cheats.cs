using System;
using System.Collections.Generic;
using System.Linq;
using Components;
using Systems.GameStateSystems;
using Systems.InputSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Utility.EnumsStorage;

namespace VeteranLevels
{
    /// <summary>
    /// Resource cheat: drops a pile of each configured resource at the mouse cursor, using the
    /// game's own pickable resource drops (what destroyed buildings leave behind). A player unit
    /// walking over a pile collects it.
    /// </summary>
    public partial class VeterancySystem
    {
        private const float PileSpacing = 1.5f;

        /// <summary>Set by the resource hotkey; handled on the next system update.</summary>
        internal static bool SpawnResourcesRequested;

        /// <summary>Pickable prefab per resource type; generic piles are used for types without their own.</summary>
        private readonly Dictionary<ResourceType, Entity> _pilePrefabs = new Dictionary<ResourceType, Entity>();
        private Entity _genericPilePrefab = Entity.Null;

        private void SpawnResourcesAtCursor()
        {
            var input = PlayerInputSystem.PlayerInputState;
            if (!input.isInsidePlayableWorld || input.isOverGui)
            {
                VeterancyOverlay.Announce("Point the mouse at the map to spawn resources.");
                return;
            }

            var types = ParseResourceTypes();
            if (types.Count == 0 || !FindPilePrefabs())
            {
                VeterancyOverlay.Announce("No resource piles to spawn (check SpawnResourceTypes, or the game has no pickable resources).");
                return;
            }

            int amount = Plugin.SpawnResourceAmount.Value;
            var center = input.groundPosition;
            for (int i = 0; i < types.Count; i++)
            {
                var prefab = _pilePrefabs.TryGetValue(types[i], out var own) ? own : _genericPilePrefab;
                // Lay the piles out in a small ring so they don't overlap.
                float angle = 2f * math.PI * i / types.Count;
                float2 offset = types.Count == 1 ? float2.zero : new float2(math.cos(angle), math.sin(angle)) * PileSpacing;

                var pile = EntityManager.Instantiate(prefab);
                SetOrAdd(pile, new Translation { Value = new float3(center.x + offset.x, 0f, center.y + offset.y) });
                SetOrAdd(pile, new PickableResourceCustomValue { type = types[i], count = amount });
            }
            VeterancyOverlay.Announce($"Spawned {amount} {string.Join(", ", types)} at the cursor. Walk a unit over the piles to collect them.");
        }

        private List<ResourceType> ParseResourceTypes()
        {
            var types = new List<ResourceType>();
            foreach (var name in Plugin.SpawnResourceTypes.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse(name.Trim(), true, out ResourceType type) && Enum.IsDefined(typeof(ResourceType), type))
                    types.Add(type);
                else
                    Plugin.Log.LogWarning($"SpawnResourceTypes: unknown resource '{name.Trim()}'.");
            }
            return types;
        }

        /// <summary>Finds the game's resource pile prefabs, preferring one that already drops each resource.</summary>
        private bool FindPilePrefabs()
        {
            if (_genericPilePrefab != Entity.Null && EntityManager.Exists(_genericPilePrefab))
                return true;
            _pilePrefabs.Clear();
            _genericPilePrefab = Entity.Null;

            var prefabs = GameState.PrefabsMap;
            if (!prefabs.IsCreated)
                return false;
            using (var ids = prefabs.GetKeyArray(Allocator.Temp))
            {
                foreach (int id in ids.OrderBy(id => id))
                {
                    var prefab = prefabs[id];
                    if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<PickableResource>(prefab)
                        || EntityManager.HasComponent<MissionPickable>(prefab) || EntityManager.HasComponent<HealPickable>(prefab))
                        continue;

                    if (_genericPilePrefab == Entity.Null)
                        _genericPilePrefab = prefab;
                    if (EntityManager.HasComponent<PickableResourceCustomValue>(prefab))
                    {
                        var type = EntityManager.GetComponentData<PickableResourceCustomValue>(prefab).type;
                        if (!_pilePrefabs.ContainsKey(type))
                            _pilePrefabs[type] = prefab;
                    }
                    else if (EntityManager.HasComponent<PriceBase>(prefab))
                    {
                        var price = EntityManager.GetComponentData<PriceBase>(prefab).Value();
                        foreach (var type in ResourcesIn(price))
                            if (!_pilePrefabs.ContainsKey(type))
                                _pilePrefabs[type] = prefab;
                    }
                }
            }
            Plugin.Log.LogInfo($"Resource piles found for: {string.Join(", ", _pilePrefabs.Keys)}" + (_genericPilePrefab == Entity.Null ? " (none)" : ""));
            return _genericPilePrefab != Entity.Null;
        }

        private static IEnumerable<ResourceType> ResourcesIn(Components.Structs.PriceBaseData price)
        {
            if (price.foodPrice > 0) yield return ResourceType.Food;
            if (price.woodPrice > 0) yield return ResourceType.Wood;
            if (price.stonePrice > 0) yield return ResourceType.Stone;
            if (price.ironPrice > 0) yield return ResourceType.Iron;
            if (price.moneyPrice > 0) yield return ResourceType.Money;
            if (price.soulsPrice > 0) yield return ResourceType.Souls;
        }
    }
}
