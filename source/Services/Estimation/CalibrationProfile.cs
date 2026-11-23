using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>One native PSN list from the user's library, reduced to its grades.</summary>
    public sealed class NativeTrophyList
    {
        public string GameTitle { get; set; }

        /// <summary>The non-platinum grades of the list, one per trophy.</summary>
        public IList<TrophyGrade> Grades { get; set; } = new List<TrophyGrade>();
    }

    public sealed class CalibrationBucket
    {
        public string SizeBucket { get; set; }
        public int MinSize { get; set; }
        public int MaxSize { get; set; }
        public int Lists { get; set; }
        public double BronzeShare { get; set; }
        public double SilverShare { get; set; }
        public double GoldShare { get; set; }
        public double AveragePoints { get; set; }
    }

    /// <summary>
    /// A compact summary of the user's own native lists (grade mix and points by list size), computed
    /// locally. The request carries this summary instead of raw libraries: free-tier limits make raw
    /// dumps impractical, and the model is unreliable at deriving statistics itself.
    /// </summary>
    public sealed class CalibrationProfile
    {
        private static readonly int[][] Buckets =
        {
            new[] { 1, 12 }, new[] { 13, 30 }, new[] { 31, 65 }, new[] { 66, 100 }, new[] { 101, int.MaxValue }
        };

        public List<CalibrationBucket> Rows { get; } = new List<CalibrationBucket>();

        public static CalibrationProfile Build(IEnumerable<NativeTrophyList> lists)
        {
            var profile = new CalibrationProfile();
            var usable = (lists ?? Enumerable.Empty<NativeTrophyList>())
                .Where(l => l?.Grades != null && l.Grades.Count > 0)
                .ToList();

            foreach (var range in Buckets)
            {
                var inBucket = usable.Where(l => l.Grades.Count >= range[0] && l.Grades.Count <= range[1]).ToList();
                if (inBucket.Count == 0) continue;

                var bronze = inBucket.Average(l => Share(l, TrophyGrade.Bronze));
                var silver = inBucket.Average(l => Share(l, TrophyGrade.Silver));
                var gold = inBucket.Average(l => Share(l, TrophyGrade.Gold));
                var points = inBucket.Average(l => l.Grades.Sum(g => PointsOf(g)));

                profile.Rows.Add(new CalibrationBucket
                {
                    SizeBucket = range[1] == int.MaxValue ? range[0] + "+" : range[0] + "-" + range[1],
                    MinSize = range[0],
                    MaxSize = range[1],
                    Lists = inBucket.Count,
                    BronzeShare = Math.Round(bronze, 3),
                    SilverShare = Math.Round(silver, 3),
                    GoldShare = Math.Round(gold, 3),
                    AveragePoints = Math.Round(points, 0)
                });
            }

            return profile;
        }

        private static double Share(NativeTrophyList list, TrophyGrade grade) =>
            list.Grades.Count(g => g == grade) / (double)list.Grades.Count;

        private static int PointsOf(TrophyGrade grade) =>
            grade == TrophyGrade.Gold ? TrophyGradeEstimator.GoldPoints :
            grade == TrophyGrade.Silver ? TrophyGradeEstimator.SilverPoints :
            TrophyGradeEstimator.BronzePoints;
    }
}
