using Components;
using HarmonyLib;
using Systems.GameStateSystems;
using UI.New;
using Unity.Entities;

namespace VeteranLevels
{
    /// <summary>
    /// The info panel shows damage from the unit type's base stats, which don't include the
    /// veteran bonus. While the panel builds its stat list, swap in the selected units' boosted
    /// damage, then restore the game's value so its own change tracking is unaffected.
    /// </summary>
    [HarmonyPatch(typeof(InfoPanel), "UpdateParametersAndStatuses")]
    internal static class InfoPanelPatch
    {
        private static void Prefix(out float? __state)
        {
            __state = null;
            float multiplier = SelectedDamageMultiplier();
            if (multiplier == 1f)
                return;

            __state = GameState.SelectedUnitsState.sameSelectionBaseData.damage;
            GameState.SelectedUnitsState.sameSelectionBaseData.damage *= multiplier;
        }

        private static void Postfix(float? __state)
        {
            if (__state.HasValue)
                GameState.SelectedUnitsState.sameSelectionBaseData.damage = __state.Value;
        }

        /// <summary>The damage multiplier shared by every selected unit, or 1 if they differ.</summary>
        private static float SelectedDamageMultiplier()
        {
            var system = VeterancySystem.Instance;
            var selected = GameState.SelectedEntities;
            if (system == null || !selected.IsCreated || selected.Length == 0)
                return 1f;

            var entityManager = system.EntityManager;
            float? shared = null;
            for (int i = 0; i < selected.Length; i++)
            {
                var entity = selected[i];
                if (!entityManager.Exists(entity) || !entityManager.HasComponent<UnitBase>(entity))
                    continue;
                float multiplier = system.Veterans.TryGetValue(entity, out var veteran) ? veteran.DamageMultiplier : 1f;
                if (shared.HasValue && shared.Value != multiplier)
                    return 1f;
                shared = multiplier;
            }
            return shared ?? 1f;
        }
    }
}
