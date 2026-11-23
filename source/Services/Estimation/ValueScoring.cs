using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Turns the model's per-achievement judgement into one absolute value score (0 to 1) that
    /// both estimators use. Absolute, not rank-based: grades come from these values, and budgets
    /// only nudge borderline cases.
    /// </summary>
    public static class ValueScoring
    {
        public static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

        public static double KindWeight(AchievementKind kind)
        {
            switch (kind)
            {
                case AchievementKind.Tutorial: return 0.0;
                case AchievementKind.Collectible: return 0.25;
                case AchievementKind.Grind: return 0.35;
                case AchievementKind.Optional: return 0.40;
                case AchievementKind.StoryMilestone: return 0.50;
                case AchievementKind.Boss: return 0.70;
                case AchievementKind.Challenge: return 0.85;
                case AchievementKind.TrueEnding: return 1.0;
                default: return 0.40;
            }
        }

        public static double PhaseWeight(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Early: return 0.2;
                case GamePhase.Late: return 0.8;
                default: return 0.5;
            }
        }

        /// <summary>The raw value score from difficulty, kind, phase and the capstone flag.</summary>
        public static double Raw(AchievementScore score)
        {
            if (score == null) return 0;
            var difficulty = (Math.Max(1, Math.Min(5, score.Difficulty)) - 1) / 4.0;
            var value = 0.55 * difficulty + 0.30 * KindWeight(score.Kind) + 0.15 * PhaseWeight(score.Phase);
            if (score.IsCapstone) value += 0.25;
            return Clamp01(value);
        }

        /// <summary>Structure-only score: where the achievement sits in the game, ignoring game knowledge.</summary>
        public static double Structural(AchievementScore score)
        {
            if (score == null) return 0;
            var value = 0.7 * PhaseWeight(score.Phase) + 0.3 * KindWeight(score.Kind);
            if (score.IsCapstone) value += 0.3;
            return Clamp01(value);
        }

        /// <summary>
        /// The score the estimators grade on. A low-confidence score (or game) leans toward
        /// structure instead of the model's game knowledge.
        /// </summary>
        public static double Effective(AchievementScore score, GameScoring game)
        {
            var confidence = Clamp01(Math.Min(
                score?.Confidence ?? 1.0,
                game?.OverallConfidence ?? 1.0));

            const double trust = 0.5;
            var weight = confidence >= trust ? 1.0 : confidence / trust;
            return Clamp01(weight * Raw(score) + (1 - weight) * Structural(score));
        }

        /// <summary>
        /// Position within a list as a 0..1 percentile (ties share the mid rank). Used to compare
        /// values across platforms by rank, never by raw number.
        /// </summary>
        public static Dictionary<string, double> Percentiles(IDictionary<string, int> valuesByApiName)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (valuesByApiName == null || valuesByApiName.Count == 0) return result;

            var all = valuesByApiName.Values.ToList();
            var n = all.Count;
            foreach (var pair in valuesByApiName)
            {
                var below = all.Count(v => v < pair.Value);
                var equal = all.Count(v => v == pair.Value);
                result[pair.Key] = (below + 0.5 * equal) / n;
            }

            return result;
        }
    }
}
