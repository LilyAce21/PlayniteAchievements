using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>PSN trophy grade. Platinum is deliberately absent: the plugin already creates one per game.</summary>
    public enum TrophyGrade
    {
        Bronze = 0,
        Silver = 1,
        Gold = 2
    }

    public enum AchievementKind
    {
        Unknown = 0,
        Tutorial,
        StoryMilestone,
        Boss,
        Collectible,
        TrueEnding,
        Grind,
        Optional,
        Challenge
    }

    public enum GamePhase
    {
        Unknown = 0,
        Early,
        Mid,
        Late
    }

    public enum GameProfile
    {
        Unknown = 0,
        ShortEasy,
        LongHard,
        CompletionistHeavy
    }

    public enum CategoryKind
    {
        Base = 0,
        Dlc,
        Update,
        Mixed
    }

    /// <summary>How strictly the PSN point budget shapes the grades, at every list size.</summary>
    public enum BudgetPreset
    {
        /// <summary>Strict: follows the native budget (about 1,000 non-platinum points, rising with the list) closely.</summary>
        Native = 0,

        /// <summary>
        /// The budget is a strong guide, not a rule. Achievements that clearly deserve a grade by value keep it even
        /// when the budget would change it, within limits; the rest are moved toward the budget.
        /// </summary>
        ValueBased
    }

    /// <summary>
    /// The model's judgement of one achievement. This is the only per-achievement data the
    /// estimators consume, and it carries no player state of any kind.
    /// </summary>
    public sealed class AchievementScore
    {
        public string ApiName { get; set; }

        /// <summary>1 (trivial) to 5 (extreme).</summary>
        public int Difficulty { get; set; } = 3;

        public AchievementKind Kind { get; set; }

        public GamePhase Phase { get; set; }

        public bool IsCapstone { get; set; }

        /// <summary>Groups kill counts, win counts, level completions and similar ladders. Null for none.</summary>
        public string SeriesId { get; set; }

        /// <summary>Position inside the series (1 = first). Zero when unknown.</summary>
        public int SeriesPosition { get; set; }

        /// <summary>The model's confidence in this one score, 0 to 1.</summary>
        public double Confidence { get; set; } = 1.0;

        /// <summary>Category the achievement belongs to (for example a DLC pack name), when known.</summary>
        public string Category { get; set; }

        /// <summary>True when web research about this achievement was available when it was scored.</summary>
        public bool Researched { get; set; }
    }

    /// <summary>Game-level judgement that accompanies the per-achievement scores.</summary>
    public sealed class GameScoring
    {
        public GameProfile Profile { get; set; }

        /// <summary>Overall confidence, 0 to 1. Low values make the estimator lean on structure and budget.</summary>
        public double OverallConfidence { get; set; } = 1.0;

        /// <summary>True when the achievements are essentially identical (hidden-object style sets).</summary>
        public bool IsUniformSet { get; set; }

        public Dictionary<string, CategoryKind> CategoryKinds { get; set; } = new Dictionary<string, CategoryKind>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class EstimationSettings
    {
        public BudgetPreset Preset { get; set; } = BudgetPreset.Native;

        /// <summary>
        /// Non-platinum trophy point budget. About 1,000 from community sources and two real
        /// examples, not an official spec, so it is a setting to be fitted from the native library.
        /// </summary>
        public int TrophyBudget { get; set; } = 1000;

        /// <summary>Per-trophy floor (all bronze). Past about 65 trophies the floor exceeds the budget.</summary>
        public int TrophyFloorPerTrophy { get; set; } = 15;

        /// <summary>List size where the headroom above the per-trophy floor starts to fade (about where the floor meets the budget).</summary>
        public int TrophyHeadroomStart { get; set; } = 65;

        /// <summary>List size where the headroom above the floor has faded to nothing (a bronze-dominant list).</summary>
        public int TrophyHeadroomEnd { get; set; } = 200;

        /// <summary>
        /// How far above the all-bronze floor a mid-size list's budget sits before the fade starts
        /// (0.35 = 35% above). A starting value to be fitted from the user's native library.
        /// </summary>
        public double TrophyHeadroomAtStart { get; set; } = 0.35;

        /// <summary>Gamerscore of one block (1,000 on Xbox).</summary>
        public int GamerscoreBlockTotal { get; set; } = 1000;

        /// <summary>Most achievements in one Gamerscore block (100 on Xbox).</summary>
        public int GamerscoreBlockSize { get; set; } = 100;

        /// <summary>
        /// Xbox requires at least this many achievements in a title, so a list smaller than this can never
        /// carry (or be given) Gamerscore. Also means a full 1,000 G block could not be reached.
        /// </summary>
        public int GamerscoreMinAchievements { get; set; } = 10;

        public int GamerscoreMinValue { get; set; } = 5;

        public int GamerscoreMaxValue { get; set; } = 200;

        public int GamerscoreStep { get; set; } = 5;
    }

    public sealed class TrophyEstimate
    {
        public string ApiName { get; set; }

        public TrophyGrade Grade { get; set; }

        /// <summary>Short explanation: the gold reason, "uniform set", "budget", "fill-in" and so on.</summary>
        public string Reason { get; set; }

        /// <summary>The trophy type string PA stores (lowercase "bronze", "silver", "gold").</summary>
        public string TrophyType
        {
            get
            {
                switch (Grade)
                {
                    case TrophyGrade.Gold: return "gold";
                    case TrophyGrade.Silver: return "silver";
                    default: return "bronze";
                }
            }
        }
    }

    public sealed class GamerscoreEstimate
    {
        public string ApiName { get; set; }

        public int Value { get; set; }

        /// <summary>Zero-based block the value belongs to.</summary>
        public int Block { get; set; }
    }
}
