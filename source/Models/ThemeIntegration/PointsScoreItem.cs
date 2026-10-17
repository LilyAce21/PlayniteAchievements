namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// One points family's totals for themes: the unlocked provider points summed for the family,
    /// and the points of every achievement in its games.
    /// </summary>
    public sealed class PointsScoreItem
    {
        /// <summary>The family's provider key, e.g. Xbox, PSN, Steam or Custom:&lt;id&gt;.</summary>
        public string FamilyKey { get; set; }

        /// <summary>The family's card name, e.g. Gamerscore or PlayStation Points.</summary>
        public string Name { get; set; }

        /// <summary>Unlocked points earned toward the family.</summary>
        public int Score { get; set; }

        /// <summary>Points of every achievement in the family's games, unlocked or not.</summary>
        public int Total { get; set; }
    }
}
