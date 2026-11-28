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

        /// <summary>The grades of the trophies that sit in DLC or update categories (a subset of <see cref="Grades"/>).</summary>
        public IList<TrophyGrade> ExtraGrades { get; set; } = new List<TrophyGrade>();
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

    /// <summary>One real native list, anonymised: size and grade counts only, never a title.</summary>
    public sealed class NativeTrophyListStat
    {
        public int Size { get; set; }
        public int Bronze { get; set; }
        public int Silver { get; set; }
        public int Gold { get; set; }
        public int Points { get; set; }
    }

    /// <summary>How DLC and update packs share a game's trophy points, from native lists that have them.</summary>
    public sealed class ExtrasSplit
    {
        public int Lists { get; set; }

        /// <summary>Average share of a list's trophy points that sit in DLC or update categories.</summary>
        public double AveragePointShare { get; set; }

        /// <summary>Average share of a list's trophies that sit in DLC or update categories.</summary>
        public double AverageTrophyShare { get; set; }
    }

    public sealed class GamerscoreBucket
    {
        public string SizeBucket { get; set; }
        public int Lists { get; set; }
        public double AverageTotal { get; set; }
        public double MedianValue { get; set; }

        /// <summary>Average share of a list's achievements worth more than three times its average (the capstone spikes).</summary>
        public double SpikeShare { get; set; }
    }

    /// <summary>
    /// A compact summary of the user's own native lists, computed locally: grade mix and points by list
    /// size, how DLC and update packs split their points, Gamerscore by list size, and the closest real
    /// lists as anonymous examples. The request carries this summary instead of raw libraries: free-tier
    /// limits make raw dumps impractical, the model is unreliable at deriving statistics itself, and no
    /// game title from the user's library ever leaves the machine.
    /// </summary>
    public sealed class CalibrationProfile
    {
        private static readonly int[][] Buckets =
        {
            new[] { 1, 12 }, new[] { 13, 30 }, new[] { 31, 65 }, new[] { 66, 100 }, new[] { 101, int.MaxValue }
        };

        private static readonly int[][] GamerscoreBuckets =
        {
            new[] { 1, 25 }, new[] { 26, 50 }, new[] { 51, 100 }, new[] { 101, int.MaxValue }
        };

        /// <summary>Provider keys whose trophy type is a real PSN grade. Other providers reuse the field for their own tiers.</summary>
        private static readonly HashSet<string> PsnFamilyProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PSN", "RPCS3", "ShadPS4", "Vita3K" };

        private const int MinListSize = 3;

        public List<CalibrationBucket> Rows { get; } = new List<CalibrationBucket>();
        public List<NativeTrophyListStat> Lists { get; } = new List<NativeTrophyListStat>();
        public List<GamerscoreBucket> GamerscoreRows { get; } = new List<GamerscoreBucket>();
        public ExtrasSplit Extras { get; set; }

        public bool IsEmpty => Rows.Count == 0 && GamerscoreRows.Count == 0 && Extras == null;

        /// <summary>
        /// The median total trophy points of real lists with 5 to 45 trophies, where lists land near the
        /// native budget. A suggestion for fitting the budget setting; null when there are too few lists.
        /// </summary>
        public int? SuggestedTrophyBudget
        {
            get
            {
                var points = Lists.Where(l => l.Size >= 5 && l.Size <= 45).Select(l => l.Points).OrderBy(p => p).ToList();
                if (points.Count < 5) return null;
                return points[points.Count / 2];
            }
        }

        /// <summary>The real lists closest in size to a list of <paramref name="size"/> trophies.</summary>
        public List<NativeTrophyListStat> ClosestLists(int size, int take = 3)
        {
            return Lists
                .OrderBy(l => Math.Abs(l.Size - size))
                .ThenBy(l => l.Size)
                .ThenBy(l => l.Points)
                .Take(Math.Max(0, take))
                .ToList();
        }

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

                profile.Rows.Add(new CalibrationBucket
                {
                    SizeBucket = range[1] == int.MaxValue ? range[0] + "+" : range[0] + "-" + range[1],
                    MinSize = range[0],
                    MaxSize = range[1],
                    Lists = inBucket.Count,
                    BronzeShare = Math.Round(inBucket.Average(l => Share(l.Grades, TrophyGrade.Bronze)), 3),
                    SilverShare = Math.Round(inBucket.Average(l => Share(l.Grades, TrophyGrade.Silver)), 3),
                    GoldShare = Math.Round(inBucket.Average(l => Share(l.Grades, TrophyGrade.Gold)), 3),
                    AveragePoints = Math.Round(inBucket.Average(l => l.Grades.Sum(g => PointsOf(g))), 0)
                });
            }

            foreach (var list in usable)
            {
                profile.Lists.Add(new NativeTrophyListStat
                {
                    Size = list.Grades.Count,
                    Bronze = list.Grades.Count(g => g == TrophyGrade.Bronze),
                    Silver = list.Grades.Count(g => g == TrophyGrade.Silver),
                    Gold = list.Grades.Count(g => g == TrophyGrade.Gold),
                    Points = list.Grades.Sum(g => PointsOf(g))
                });
            }

            var withExtras = usable.Where(l => l.ExtraGrades != null && l.ExtraGrades.Count > 0 && l.ExtraGrades.Count < l.Grades.Count).ToList();
            if (withExtras.Count > 0)
            {
                profile.Extras = new ExtrasSplit
                {
                    Lists = withExtras.Count,
                    AveragePointShare = Math.Round(withExtras.Average(l => l.ExtraGrades.Sum(g => PointsOf(g)) / (double)l.Grades.Sum(g => PointsOf(g))), 3),
                    AverageTrophyShare = Math.Round(withExtras.Average(l => l.ExtraGrades.Count / (double)l.Grades.Count), 3)
                };
            }

            return profile;
        }

        /// <summary>
        /// Builds the profile from the user's games. A list counts only when it is a genuine PlayStation-style
        /// list (a PSN-family provider, or grades taken from the PlayStation metadata source) and every non-platinum achievement has a native bronze, silver or gold,
        /// so partial lists never skew the mix. Gamerscore comes from complete Xbox-family lists, including values taken from the Xbox metadata source.
        /// </summary>
        public static CalibrationProfile FromSources(IEnumerable<EstimationSourceGame> games)
        {
            var trophyLists = new List<NativeTrophyList>();
            var gamerscoreLists = new List<List<int>>();

            foreach (var game in games ?? Enumerable.Empty<EstimationSourceGame>())
            {
                if (game?.Achievements == null) continue;

                var rows = game.Achievements
                    .Where(a => a != null && !string.Equals((a.NativeTrophyType ?? string.Empty).Trim(), "platinum", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (rows.Count < MinListSize) continue;

                var psnList = (!string.IsNullOrWhiteSpace(game.ProviderKey) && PsnFamilyProviders.Contains(game.ProviderKey)) || game.TrophiesFromPlayStationMetadata;
                if (psnList)
                {
                    var grades = new List<TrophyGrade>();
                    var extras = new List<TrophyGrade>();
                    var complete = true;
                    foreach (var row in rows)
                    {
                        if (!EstimationPlanner.TryParseGrade(row.NativeTrophyType, out var grade)) { complete = false; break; }
                        grades.Add(grade);
                        if (IsExtraCategory(row.CategoryKind)) extras.Add(grade);
                    }

                    if (complete) trophyLists.Add(new NativeTrophyList { GameTitle = game.Title, Grades = grades, ExtraGrades = extras });
                }

                if ((game.PointsAreGamerscore || game.PointsFromXboxMetadata) && rows.All(a => a.NativePoints.HasValue && a.NativePoints.Value > 0))
                {
                    gamerscoreLists.Add(rows.Select(a => a.NativePoints.Value).ToList());
                }
            }

            var profile = Build(trophyLists);
            AddGamerscore(profile, gamerscoreLists);
            return profile;
        }

        private static void AddGamerscore(CalibrationProfile profile, List<List<int>> lists)
        {
            foreach (var range in GamerscoreBuckets)
            {
                var inBucket = lists.Where(l => l.Count >= range[0] && l.Count <= range[1]).ToList();
                if (inBucket.Count == 0) continue;

                profile.GamerscoreRows.Add(new GamerscoreBucket
                {
                    SizeBucket = range[1] == int.MaxValue ? range[0] + "+" : range[0] + "-" + range[1],
                    Lists = inBucket.Count,
                    AverageTotal = Math.Round(inBucket.Average(l => (double)l.Sum()), 0),
                    MedianValue = Math.Round(inBucket.Average(l => Median(l)), 1),
                    SpikeShare = Math.Round(inBucket.Average(l =>
                    {
                        var mean = l.Average();
                        return l.Count(v => v > 3 * mean) / (double)l.Count;
                    }), 3)
                });
            }
        }

        private static double Median(List<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        private static bool IsExtraCategory(string kind)
        {
            var value = (kind ?? string.Empty).ToLowerInvariant();
            return value.Contains("dlc") || value.Contains("update");
        }

        private static double Share(IList<TrophyGrade> grades, TrophyGrade grade) =>
            grades.Count(g => g == grade) / (double)grades.Count;

        private static int PointsOf(TrophyGrade grade) =>
            grade == TrophyGrade.Gold ? TrophyGradeEstimator.GoldPoints :
            grade == TrophyGrade.Silver ? TrophyGradeEstimator.SilverPoints :
            TrophyGradeEstimator.BronzePoints;
    }
}
