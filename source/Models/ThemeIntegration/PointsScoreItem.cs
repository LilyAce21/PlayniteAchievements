namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// One points family's total for themes: the unlocked provider points summed for the family
    /// and the level, rank and mastery they reach on its ladder.
    /// </summary>
    public sealed class PointsScoreItem
    {
        /// <summary>The family's provider key, e.g. Xbox, PSN, Steam or Custom:&lt;id&gt;.</summary>
        public string FamilyKey { get; set; }

        /// <summary>The family's card name, e.g. Gamerscore or PlayStation Points.</summary>
        public string Name { get; set; }

        public int Score { get; set; }

        public int Level { get; set; }

        public double LevelProgress { get; set; }

        public string Rank { get; set; } = "Bronze5";

        public int Mastery { get; set; }
    }
}
