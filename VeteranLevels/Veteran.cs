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
        /// <summary>The unit's scale before any level growth (null until first resized).</summary>
        public float? BaseScale;

        public float DamageMultiplier => 1f + Plugin.DamagePerLevel.Value * Level;
        public float HealthMultiplier => 1f + Plugin.HealthPerLevel.Value * Level;
        public float SizeMultiplier => 1f + Plugin.SizePerLevel.Value * Level;

        public string Title => Titles[math.clamp(Level, 0, Titles.Length - 1)];
        public string DisplayName => Name == null ? "Recruit" : $"{Name} the {Title}";

        /// <summary>Has a label/XP bar: levelled up, or has at least one kill toward level 1.</summary>
        public bool IsShown => Level > 0 || Xp > 0;
        public bool IsMaxLevel => Level >= Plugin.MaxLevel.Value;

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

        private static readonly System.Random Rng = new System.Random();

        public static string RandomName() => GivenNames[Rng.Next(GivenNames.Length)];
    }
}
