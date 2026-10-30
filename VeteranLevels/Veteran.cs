using Unity.Mathematics;

namespace VeteranLevels
{
    /// <summary>Per-unit leveling state, kept by <see cref="VeterancySystem"/>.</summary>
    public class Veteran
    {
        public int Level;
        public int Xp;
        public int Kills;
        public string Name;
        public float3 Position;
        /// <summary>An enemy hero rather than one of the player's units.</summary>
        public bool IsEnemy;
        /// <summary>Hidden by the fog of war (enemy heroes only).</summary>
        public bool Hidden;
        /// <summary>Health multiplier the unit had before leveling (waves give enemies their own).</summary>
        public float BaseHealthMultiplier = 1f;
        /// <summary>The unit's scale before any level growth (null until first resized).</summary>
        public float? BaseScale;
        /// <summary>Times this unit was promoted to a higher-tier unit type.</summary>
        public int Promotions;
        /// <summary>Position in its promotion chain (1 = first unit type), or 0 if not in a chain.</summary>
        public int Tier;
        /// <summary>Highest level this unit can reach as its current unit type.</summary>
        public int LevelCap = Plugin.MaxLevel.Value;

        /// <summary>
        /// Added to the health multiplier per promotion. Too small to notice in game, but it is
        /// saved with the multiplier, so promoted units stay heroes after loading at level 0.
        /// </summary>
        public const float PromotionMarker = 0.001f;

        public float DamageMultiplier => 1f + Plugin.DamagePerLevel.Value * Level;
        public float HealthMultiplier => BaseHealthMultiplier * (1f + Plugin.HealthPerLevel.Value * Level) + PromotionMarker * Promotions;
        public float SizeMultiplier => 1f + Plugin.SizePerLevel.Value * Level;

        public string Title => Level == 0 && Promotions > 0 ? "Promoted" : Titles[math.clamp(Level, 0, Titles.Length - 1)];
        public string DisplayName => Name == null ? "Recruit" : $"{Name} the {Title}";

        /// <summary>A named player unit that counts toward the hero cap (or an enemy hero).</summary>
        public bool IsHero => Level > 0 || Promotions > 0;
        /// <summary>Has a label/XP bar: a hero, or has at least one kill toward level 1.</summary>
        public bool IsShown => IsHero || Xp > 0;
        public bool IsMaxLevel => Level >= LevelCap;

        /// <summary>Progress toward the next level, 0..1 (1 at max level).</summary>
        public float XpProgress => IsMaxLevel ? 1f : math.saturate((float)Xp / XpToNextLevel(Level));

        public static int XpToNextLevel(int level) => Plugin.XpBase.Value * (level + 1);

        // Index = level. Levels above the list reuse the last title.
        private static readonly string[] Titles =
        {
            "Recruit", "Blooded", "Veteran", "Hardened", "Elite", "Champion",
            "Hero", "Warlord", "Legend", "Mythic", "Immortal",
        };

        private static readonly string[] GivenNames =
        {
            "Aldric", "Bertram", "Cedric", "Duncan", "Edmund", "Fulk", "Godfrey", "Hugh",
            "Ivo", "Jocelyn", "Kenric", "Leofric", "Merek", "Norbert", "Osric", "Percival",
            "Ranulf", "Sigurd", "Tybalt", "Ulric", "Walter", "Wymond", "Ada", "Beatrix",
            "Cecily", "Edith", "Gisela", "Helewise", "Isolde", "Joan", "Matilda", "Rohesia",
            "Sibyl", "Agnes", "Brom", "Corwin", "Dain", "Eadric", "Gunnar", "Harald",
            "Ingram", "Jarvis", "Lambert", "Odo", "Roderick", "Swein", "Theobald", "Wulfric",
        };

        internal static readonly System.Random Rng = new System.Random();

        public static string RandomName() => GivenNames[Rng.Next(GivenNames.Length)];
    }
}
