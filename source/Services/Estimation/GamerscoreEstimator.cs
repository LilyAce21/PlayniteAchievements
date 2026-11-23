using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    public sealed class GamerscoreEstimationInput
    {
        /// <summary>The model's scores for every achievement in the list, in list order (base game first).</summary>
        public IList<AchievementScore> Scores { get; set; } = new List<AchievementScore>();

        /// <summary>The achievements to generate for. Null means every achievement without a native value.</summary>
        public ISet<string> Targets { get; set; }

        /// <summary>Native Gamerscore already in this list, by api name.</summary>
        public IDictionary<string, int> NativeValues { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Native PSN grades for the same game, by api name. A signal, compared within the list.</summary>
        public IDictionary<string, TrophyGrade> OtherPlatformGrades { get; set; } =
            new Dictionary<string, TrophyGrade>(StringComparer.OrdinalIgnoreCase);

        public GameScoring Game { get; set; } = new GameScoring();

        public EstimationSettings Settings { get; set; } = new EstimationSettings();
    }

    /// <summary>
    /// Estimates Xbox Gamerscore the way native lists are built: a block of at most 100
    /// achievements is worth 1,000 G, values are multiples of 5 between 5 and 200, values cluster
    /// near the average with a few capstone spikes, ladders are flat with a bump at the top, and
    /// code (not the model) fixes rounding so each block sums exactly.
    /// </summary>
    public static class GamerscoreEstimator
    {
        private const double CapstoneBoostBase = 3.0;
        private const double CapstoneBoostScore = 2.0;
        private const double SeriesCapstoneBoost = 2.5;
        private const int MinSeriesSize = 3;

        public static List<GamerscoreEstimate> Estimate(GamerscoreEstimationInput input)
        {
            var result = new List<GamerscoreEstimate>();
            if (input?.Scores == null || input.Scores.Count == 0) return result;

            var settings = input.Settings ?? new EstimationSettings();
            var game = input.Game ?? new GameScoring();
            var natives = input.NativeValues ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var scores = input.Scores
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.ApiName))
                .GroupBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Hard rules 1 and 3: native data is never a target.
            var targets = scores
                .Where(s => !natives.ContainsKey(s.ApiName))
                .Where(s => input.Targets == null || input.Targets.Contains(s.ApiName))
                .ToList();
            if (targets.Count == 0) return result;

            var weights = BuildWeights(targets, game, input.OtherPlatformGrades);
            var milestone = targets.ToDictionary(s => s.ApiName, s => IsMilestone(s, targets), StringComparer.OrdinalIgnoreCase);

            // Partial native lists: the fill-ins are an extra block valued relative to native values.
            double? nativeAverage = natives.Count > 0 ? natives.Values.Average() : (double?)null;

            var blockSize = Math.Max(1, settings.GamerscoreBlockSize);
            var blockIndex = 0;
            for (var offset = 0; offset < targets.Count; offset += blockSize, blockIndex++)
            {
                var chunk = targets.Skip(offset).Take(blockSize).ToList();
                var total = BlockTotal(chunk.Count, nativeAverage, settings);

                var values = AllocateBlock(
                    chunk.Select(s => weights[s.ApiName]).ToList(),
                    chunk.Select(s => milestone[s.ApiName]).ToList(),
                    total,
                    game.IsUniformSet,
                    settings);

                for (var i = 0; i < chunk.Count; i++)
                {
                    result.Add(new GamerscoreEstimate { ApiName = chunk[i].ApiName, Value = values[i], Block = blockIndex });
                }
            }

            return result;
        }

        /// <summary>
        /// A block's total. Full natives-free blocks get the block total (1,000 G) even when the
        /// block is small, capped by what its achievements can hold. A fill-in block next to native
        /// values is priced from the native average instead.
        /// </summary>
        internal static int BlockTotal(int count, double? nativeAverage, EstimationSettings settings)
        {
            var step = Math.Max(1, settings.GamerscoreStep);
            var min = Math.Max(step, settings.GamerscoreMinValue) * count;
            var max = Math.Min(settings.GamerscoreBlockTotal, settings.GamerscoreMaxValue * count);

            var total = nativeAverage.HasValue
                ? (int)Math.Round(nativeAverage.Value * count / step) * step
                : settings.GamerscoreBlockTotal;

            total = Math.Max(min, Math.Min(max, total));
            return total / step * step;
        }

        // ---------------------------------------------------------------------------------
        // Weights
        // ---------------------------------------------------------------------------------
        internal static Dictionary<string, double> BuildWeights(
            IList<AchievementScore> targets,
            GameScoring game,
            IDictionary<string, TrophyGrade> otherPlatformGrades)
        {
            var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Values cluster near the average, with spikes only for capstones.
            foreach (var s in targets)
            {
                var score = ValueScoring.Effective(s, game);
                var weight = 0.7 + 0.6 * score;

                if (otherPlatformGrades != null && otherPlatformGrades.TryGetValue(s.ApiName, out var grade))
                {
                    switch (grade)
                    {
                        case TrophyGrade.Gold: weight *= 1.8; break;
                        case TrophyGrade.Silver: weight *= 1.15; break;
                        default: weight *= 0.85; break;
                    }
                }

                if (s.IsCapstone) weight *= CapstoneBoostBase + CapstoneBoostScore * score;
                weights[s.ApiName] = weight;
            }

            ApplySeriesShape(targets, weights);
            return weights;
        }

        /// <summary>
        /// Ladders (win 10 / 50 / 100 ...) are flat with a bump at the capstone, not a smooth
        /// escalation. The series keeps its total weight, so its budget does not change; only the
        /// shape inside it does.
        /// </summary>
        internal static void ApplySeriesShape(IList<AchievementScore> targets, Dictionary<string, double> weights)
        {
            var groups = targets
                .Where(s => !string.IsNullOrWhiteSpace(s.SeriesId))
                .GroupBy(s => s.SeriesId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= MinSeriesSize);

            foreach (var group in groups)
            {
                var members = group.OrderBy(s => s.SeriesPosition).ToList();
                var capstone = members.Last();
                var others = members.Take(members.Count - 1).ToList();

                var seriesTotal = members.Sum(s => weights[s.ApiName]);
                var unit = seriesTotal / (others.Count + SeriesCapstoneBoost);

                foreach (var s in others) weights[s.ApiName] = unit;
                weights[capstone.ApiName] = unit * SeriesCapstoneBoost;
            }
        }

        internal static bool IsMilestone(AchievementScore s, IList<AchievementScore> targets)
        {
            if (s.IsCapstone || s.Kind == AchievementKind.TrueEnding) return true;
            if (string.IsNullOrWhiteSpace(s.SeriesId)) return false;

            var members = targets.Where(t => string.Equals(t.SeriesId, s.SeriesId, StringComparison.OrdinalIgnoreCase)).ToList();
            return members.Count >= MinSeriesSize &&
                   members.OrderByDescending(t => t.SeriesPosition).First().ApiName.Equals(s.ApiName, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------
        // Allocation: exact sums, multiples of the step, min/max respected
        // ---------------------------------------------------------------------------------
        /// <summary>
        /// Splits <paramref name="total"/> across the weights. Every result is a multiple of the
        /// step between the minimum and maximum, and the results sum to exactly <paramref name="total"/>.
        /// In a uniform set the split is even, and the points that do not divide evenly go to
        /// structural milestones instead of random items.
        /// </summary>
        public static int[] AllocateBlock(
            IList<double> weights,
            IList<bool> milestones,
            int total,
            bool uniform,
            EstimationSettings settings)
        {
            var n = weights.Count;
            var step = Math.Max(1, settings.GamerscoreStep);
            var minUnits = Math.Max(1, (int)Math.Ceiling(settings.GamerscoreMinValue / (double)step));
            var maxUnits = Math.Max(minUnits, settings.GamerscoreMaxValue / step);

            var units = total / step;
            units = Math.Max(n * minUnits, Math.Min(n * maxUnits, units));

            var w = uniform ? Enumerable.Repeat(1.0, n).ToList() : weights.Select(x => Math.Max(1e-9, x)).ToList();

            // Water-filling: pin anything that would leave [min, max], share out the rest.
            var fixedUnits = new double?[n];
            for (var pass = 0; pass < n + 2; pass++)
            {
                var freeIndexes = Enumerable.Range(0, n).Where(i => fixedUnits[i] == null).ToList();
                if (freeIndexes.Count == 0) break;

                var remaining = units - Enumerable.Range(0, n).Where(i => fixedUnits[i] != null).Sum(i => fixedUnits[i].Value);
                var weightSum = freeIndexes.Sum(i => w[i]);

                var changed = false;
                foreach (var i in freeIndexes)
                {
                    var share = remaining * w[i] / weightSum;
                    if (share < minUnits) { fixedUnits[i] = minUnits; changed = true; }
                    else if (share > maxUnits) { fixedUnits[i] = maxUnits; changed = true; }
                }

                if (!changed) break;
            }

            var ideal = new double[n];
            {
                var freeIndexes = Enumerable.Range(0, n).Where(i => fixedUnits[i] == null).ToList();
                var remaining = units - Enumerable.Range(0, n).Where(i => fixedUnits[i] != null).Sum(i => fixedUnits[i].Value);
                var weightSum = freeIndexes.Sum(i => w[i]);
                for (var i = 0; i < n; i++)
                {
                    ideal[i] = fixedUnits[i] ?? remaining * w[i] / weightSum;
                }
            }

            var allocated = ideal.Select(x => Math.Max(minUnits, Math.Min(maxUnits, (int)Math.Floor(x)))).ToArray();
            var leftover = units - allocated.Sum();

            // Hand out leftover units: milestones first in a uniform set, otherwise largest remainder.
            while (leftover != 0)
            {
                IEnumerable<int> order;
                if (leftover > 0)
                {
                    order = Enumerable.Range(0, n)
                        .Where(i => allocated[i] < maxUnits)
                        .OrderByDescending(i => uniform && milestones != null && milestones[i] ? 1 : 0)
                        .ThenByDescending(i => ideal[i] - allocated[i])
                        .ThenBy(i => i);
                }
                else
                {
                    order = Enumerable.Range(0, n)
                        .Where(i => allocated[i] > minUnits)
                        .OrderBy(i => ideal[i] - allocated[i])
                        .ThenByDescending(i => i);
                }

                var pick = order.Cast<int?>().FirstOrDefault();
                if (pick == null) break;

                allocated[pick.Value] += leftover > 0 ? 1 : -1;
                leftover += leftover > 0 ? -1 : 1;
            }

            return allocated.Select(u => u * step).ToArray();
        }
    }
}
