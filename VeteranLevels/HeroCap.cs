using Components.RawComponents;
using Systems.GameStateSystems;

namespace VeteranLevels
{
    /// <summary>
    /// The hero cap: how many named player units there can be, raised by buying upgrades with
    /// soul crystals.
    ///
    /// The mod's purchases (cap upgrades and research) are kept in CurrentSouls.value. The game
    /// still saves that legacy field but only reads it from very old saves (the real amount lives
    /// in valueContainer), so it carries them through save/load. It is stored as
    /// SavedStateMarker - (upgrades + ResearchFlagUnit * researchFlags), a negative number, so a
    /// leftover legacy value is never mistaken for purchases.
    /// </summary>
    public partial class VeterancySystem
    {
        private const int SavedStateMarker = -1000;
        private const int ResearchFlagUnit = 16;
        private const float CapWarningInterval = 60f;

        /// <summary>Set by the hero panel's buy button; handled on the next system update.</summary>
        internal static bool BuyCapUpgradeRequested;

        public int HeroCap { get; private set; }
        public int CapUpgrades { get; private set; }
        /// <summary>Bit flags of the mod researches bought (see Creatures.cs).</summary>
        public int ResearchFlags { get; private set; }
        public int SoulCrystals { get; private set; }

        /// <summary>Cost of the next cap upgrade, or null when all are bought.</summary>
        public int? NextUpgradeCost =>
            CapUpgrades < Plugin.CapUpgradeCosts.Count ? Plugin.CapUpgradeCosts[CapUpgrades] : (int?)null;

        private float _nextCapWarning;

        public int CountHeroes()
        {
            int heroes = 0;
            foreach (var veteran in Veterans.Values)
                if (!veteran.IsEnemy && veteran.IsHero)
                    heroes++;
            return heroes;
        }

        private void RefreshHeroCap()
        {
            int saved = 0;
            if (_souls.CalculateEntityCount() == 1)
            {
                var souls = _souls.GetSingleton<CurrentSouls>();
                SoulCrystals = souls.CurrentAmount();
                if (souls.value <= SavedStateMarker)
                    saved = SavedStateMarker - souls.value;
            }
            CapUpgrades = saved % ResearchFlagUnit;
            ResearchFlags = saved / ResearchFlagUnit;
            HeroCap = Plugin.BaseHeroCap.Value + Plugin.HeroCapPerUpgrade.Value * CapUpgrades;
        }

        /// <summary>Spends soul crystals and saves the new purchase state; false if not affordable.</summary>
        private bool TrySpendSoulCrystals(int cost, int upgrades, int researchFlags)
        {
            if (_souls.CalculateEntityCount() != 1)
                return false;
            var souls = _souls.GetSingleton<CurrentSouls>();
            if (souls.CurrentAmount() < cost)
            {
                VeterancyOverlay.Announce($"Not enough soul crystals: this costs {cost}, you have {souls.CurrentAmount()}.");
                return false;
            }

            souls.DecreaseAmount(cost);
            souls.value = SavedStateMarker - (upgrades + ResearchFlagUnit * researchFlags);
            _souls.SetSingleton(souls);
            GameStateUpdateTasksUtility.UpdateGameState(EntityManager, GameStateUpdateTaskType.Resources);
            RefreshHeroCap();
            return true;
        }

        /// <summary>True if a unit that isn't a hero yet may become one.</summary>
        private bool HasFreeHeroSlot()
        {
            if (CountHeroes() < HeroCap)
                return true;

            float now = (float)Time.ElapsedTime;
            if (now >= _nextCapWarning || now < _nextCapWarning - CapWarningInterval)
            {
                _nextCapWarning = now + CapWarningInterval;
                string upgrade = NextUpgradeCost.HasValue ? $" Press {Plugin.HeroPanelKey.Value} to raise it." : "";
                VeterancyOverlay.Announce($"Hero cap reached ({HeroCap}). New heroes must wait for a free slot.{upgrade}");
            }
            return false;
        }

        private void BuyCapUpgrade()
        {
            RefreshHeroCap();
            var cost = NextUpgradeCost;
            if (!cost.HasValue)
            {
                VeterancyOverlay.Announce("The hero cap is already fully upgraded.");
                return;
            }
            if (TrySpendSoulCrystals(cost.Value, CapUpgrades + 1, ResearchFlags))
                VeterancyOverlay.Announce($"Hero cap raised to {HeroCap}!");
        }
    }
}
