using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Scores achievements from list structure alone, for when the model's answer is missing,
    /// invalid or unusable. Position in the list stands in for story phase, a number inside a name
    /// groups ladders ("Win 10 games", "Win 50 games"), and every score carries low confidence so the
    /// estimators lean on structure and budget rather than on any claim of game knowledge.
    /// </summary>
    public static class StructureFallback
    {
        public const double FallbackConfidence = 0.2;

        private static readonly Regex Number = new Regex(@"\d[\d,\.]*", RegexOptions.Compiled);

        public static List<AchievementScore> Score(IList<PublicAchievement> achievements)
        {
            var ordered = (achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();
            var scores = new List<AchievementScore>();
            if (ordered.Count == 0) return scores;

            var ladders = FindLadders(ordered);

            for (var i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i];
                var t = ordered.Count == 1 ? 0.5 : i / (double)(ordered.Count - 1);

                var score = new AchievementScore
                {
                    ApiName = a.ApiName,
                    Difficulty = 2 + (t > 0.66 ? 1 : 0),
                    Kind = AchievementKind.Unknown,
                    Phase = t < 0.34 ? GamePhase.Early : t < 0.67 ? GamePhase.Mid : GamePhase.Late,
                    Category = a.Category,
                    Confidence = FallbackConfidence
                };

                if (ladders.TryGetValue(a.ApiName, out var step))
                {
                    score.SeriesId = step.SeriesId;
                    score.SeriesPosition = step.Position;
                }

                scores.Add(score);
            }

            // The last achievement of a list of any real size is the likeliest capstone.
            if (scores.Count >= 5 && scores[scores.Count - 1].SeriesId == null)
            {
                scores[scores.Count - 1].IsCapstone = true;
            }

            return scores;
        }

        private sealed class Step
        {
            public string SeriesId { get; set; }
            public int Position { get; set; }
        }

        /// <summary>Names that differ only by a number form a ladder; its steps are ordered by that number.</summary>
        private static Dictionary<string, Step> FindLadders(IList<PublicAchievement> achievements)
        {
            var result = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);

            var groups = achievements
                .Select(a =>
                {
                    var text = (a.DisplayName ?? string.Empty) + "|" + (a.Description ?? string.Empty);
                    var match = Number.Match(text);
                    return new
                    {
                        Achievement = a,
                        Stem = match.Success ? Number.Replace(text, "#") : null,
                        Value = match.Success ? ParseNumber(match.Value) : 0.0
                    };
                })
                .Where(x => x.Stem != null)
                .GroupBy(x => x.Stem, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 3);

            var index = 0;
            foreach (var group in groups)
            {
                index++;
                var position = 0;
                foreach (var item in group.OrderBy(x => x.Value))
                {
                    position++;
                    result[item.Achievement.ApiName] = new Step { SeriesId = "ladder" + index, Position = position };
                }
            }

            return result;
        }

        private static double ParseNumber(string text)
        {
            return double.TryParse(text.Replace(",", string.Empty).TrimEnd('.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : 0.0;
        }
    }
}
