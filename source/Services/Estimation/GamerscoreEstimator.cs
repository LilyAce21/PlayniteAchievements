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

            // Xbox titles need at least this many achievements, so a smaller list can never have Gamerscore.
            if (scores.Count < settings.GamerscoreMinAchievements) return result;

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

                // A rare joke or failure-moment value comes off the block first; the rest share what is left.
                var special = SelectSpecialValues(chunk, game, total, settings, input.OtherPlatformGrades);
                BalanceSpecialValues(special, chunk, weights, milestone, total, settings);
                foreach (var pair in special)
                {
                    result.Add(new GamerscoreEstimate { ApiName = pair.Key, Value = pair.Value, Block = blockIndex });
                }

                var rest = chunk.Where(s => !special.ContainsKey(s.ApiName)).ToList();
                var values = AllocateBlock(
                    rest.Select(s => weights[s.ApiName]).ToList(),
                    rest.Select(s => milestone[s.ApiName]).ToList(),
                    total - special.Values.Sum(),
                    game.IsUniformSet,
                    settings);

                for (var i = 0; i < rest.Count; i++)
                {
                    result.Add(new GamerscoreEstimate { ApiName = rest[i].ApiName, Value = values[i], Block = blockIndex });
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
        private const double MinSpecialConfidence = 0.9;
        private const double MinSpecialCertainty = 0.95;
        private const double MinSpecialGameConfidence = 0.8;
        private const int SpecialShareDivisor = 10;

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

        /// <summary>
        /// Picks the rare achievements that get a joke, themed or failure-moment value. The model only proposes; this
        /// decides. Never a capstone, true ending or ladder step, never in a uniform or low-confidence game, only a
        /// confident proposal, and at most a tenth of the block, each needing near-certainty. A failure may be 0 G only in a DLC or
        /// update category (as on Xbox, where 0 G is for post-launch content); elsewhere it is 1 to 10 G, a joke 1 to
        /// 100 G. If the rest of the block could no longer hold the remaining total, none are used.
        /// </summary>
        internal static Dictionary<string, int> SelectSpecialValues(
            IList<AchievementScore> chunk, GameScoring game, int total, EstimationSettings settings, IDictionary<string, TrophyGrade> otherPlatformGrades = null)
        {
            var picked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!settings.GamerscoreAllowSpecialValues || game.IsUniformSet || game.OverallConfidence < MinSpecialGameConfidence) return picked;

            var limit = Math.Max(1, chunk.Count / SpecialShareDivisor);
            var candidates = chunk
                .Where(s => s.SpecialKind != SpecialValueKind.None && s.SpecialValue.HasValue)
                .Where(s => !s.IsCapstone && s.Kind != AchievementKind.TrueEnding && string.IsNullOrWhiteSpace(s.SeriesId))
                .Where(s => s.Confidence >= MinSpecialConfidence && s.SpecialConfidence >= MinSpecialCertainty)
                .OrderByDescending(s => s.Confidence)
                .ThenBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase);

            foreach (var s in candidates)
            {
                if (picked.Count >= limit) break;

                var postLaunch = !string.IsNullOrWhiteSpace(s.Category) &&
                                 game.CategoryKinds != null &&
                                 game.CategoryKinds.TryGetValue(s.Category, out var kind) &&
                                 (kind == CategoryKind.Dlc || kind == CategoryKind.Update);
                var value = s.SpecialValue.Value;

                // Fallback: when the native PlayStation trophy for this achievement exists, the special value must agree with
                // it. A joke or failure is a bronze, small by nature; anything else means this is not really a special case,
                // so it is valued normally, anchored to that native grade.
                if (otherPlatformGrades != null && otherPlatformGrades.TryGetValue(s.ApiName, out var native))
                {
                    if (native != TrophyGrade.Bronze || value > 10) continue;
                }

                if (s.SpecialKind == SpecialValueKind.Failure)
                {
                    if (value == 0 && !postLaunch) value = 1;
                    if (value < 0 || value > 10) continue;
                }
                else if (value < 1 || value > 100)
                {
                    continue;
                }

                picked[s.ApiName] = value;
            }

            var remainingCount = chunk.Count - picked.Count;
            var remainingTotal = total - picked.Values.Sum();
            if (picked.Count > 0 &&
                (remainingCount < 1 ||
                 remainingTotal < remainingCount * settings.GamerscoreMinValue ||
                 remainingTotal > remainingCount * settings.GamerscoreMaxValue))
            {
                picked.Clear();
            }

            return picked;
        }

        /// <summary>
        /// An odd special value (1 G, a themed number) would leave the block off its exact total, which is a multiple of
        /// the step. Developers fix that by pairing it with one more small odd value later in the list, so one ordinary,
        /// low-weight, non-milestone achievement takes the balancing value (the missing remainder plus one step, for
        /// example 9 G beside a 1 G). If no achievement can balance it or the rest cannot hold what is left, the special
        /// values are dropped and the block is allocated normally.
        /// </summary>
        internal static void BalanceSpecialValues(
            Dictionary<string, int> special, IList<AchievementScore> chunk, Dictionary<string, double> weights,
            Dictionary<string, bool> milestone, int total, EstimationSettings settings)
        {
            if (special.Count == 0) return;

            var step = Math.Max(1, settings.GamerscoreStep);
            var remainder = (((total - special.Values.Sum()) % step) + step) % step;
            if (remainder != 0)
            {
                var balancer = chunk
                    .Where(s => !special.ContainsKey(s.ApiName) && !s.IsCapstone && !milestone[s.ApiName])
                    .OrderBy(s => weights[s.ApiName])
                    .ThenBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (balancer == null) { special.Clear(); return; }

                special[balancer.ApiName] = remainder + step;
            }

            var restCount = chunk.Count - special.Count;
            var restTotal = total - special.Values.Sum();
            if (restCount < 1 || restTotal < restCount * settings.GamerscoreMinValue || restTotal > restCount * settings.GamerscoreMaxValue)
            {
                special.Clear();
            }
        }

        // ---------------------------------------------------------------------------------
        // Common values: developers overwhelmingly use a small set of round values
        // ---------------------------------------------------------------------------------
        /// <summary>
        /// Moves each ideal value to the nearest common value (in log distance), then repairs the block total by
        /// moving whole values up or down the common-value ladder, cheapest distortion first. Only when no ladder
        /// move fits the remaining difference does it fall back to single steps on distinct, non-milestone
        /// achievements, so an off-tier value is the exception and never lands on a capstone if avoidable.
        /// Returns null when it cannot satisfy the bounds (the caller then uses plain rounding).
        /// </summary>
        internal static int[] SnapToCommonValues(
            double[] ideal, double?[] fixedUnits, IList<bool> milestones, bool uniform,
            int units, int step, int minUnits, int maxUnits, EstimationSettings settings)
        {
            var n = ideal.Length;
            var ladder = (settings.GamerscoreCommonValues ?? new int[0])
                .Where(v => v > 0 && v % step == 0)
                .Select(v => v / step)
                .Where(u => u >= minUnits && u <= maxUnits)
                .Concat(new[] { minUnits, maxUnits })
                .Distinct()
                .OrderBy(u => u)
                .ToArray();
            if (ladder.Length < 2) return null;

            int NearestIndex(double x)
            {
                var target = Math.Max(1e-9, x);
                var best = 0;
                var bestCost = double.MaxValue;
                for (var k = 0; k < ladder.Length; k++)
                {
                    var cost = Math.Abs(Math.Log(ladder[k] / target));
                    if (cost < bestCost) { bestCost = cost; best = k; }
                }

                return best;
            }

            double Cost(int i, int value) => Math.Abs(Math.Log(Math.Max(1, value) / Math.Max(1e-9, ideal[i])));
            bool IsMilestone(int i) => milestones != null && i < milestones.Count && milestones[i];

            var current = new int[n];
            var rung = new int[n];
            for (var i = 0; i < n; i++)
            {
                if (fixedUnits[i] != null) { current[i] = (int)fixedUnits[i].Value; rung[i] = -1; continue; }
                rung[i] = NearestIndex(ideal[i]);
                current[i] = ladder[rung[i]];
            }

            var diff = units - current.Sum();
            var guard = 0;
            while (diff != 0 && guard++ < n * ladder.Length * 4)
            {
                var up = diff > 0;
                var bestI = -1; var bestCostDelta = double.MaxValue; var bestStep = 0;
                // In a uniform set, extra points go on milestones only, as long as they can take them.
                var milestonesOnly = uniform && up && Enumerable.Range(0, n).Any(IsMilestone);
                for (var i = 0; i < n; i++)
                {
                    if (rung[i] < 0) continue;
                    if (milestonesOnly && !IsMilestone(i)) continue;
                    var next = rung[i] + (up ? 1 : -1);
                    if (next < 0 || next >= ladder.Length) continue;

                    var delta = ladder[next] - current[i];
                    if (Math.Abs(delta) > Math.Abs(diff)) continue;

                    var costDelta = Cost(i, ladder[next]) - Cost(i, current[i]);
                    // Prefer the move that closes more of the gap for the same distortion.
                    costDelta -= 0.02 * Math.Abs(delta);
                    if (costDelta < bestCostDelta) { bestCostDelta = costDelta; bestI = i; bestStep = delta; }
                }

                if (bestI < 0) break;
                rung[bestI] += up ? 1 : -1;
                current[bestI] += bestStep;
                diff -= bestStep;
            }

            // No ladder move fits what is left: fix it with single steps, each on a different achievement,
            // preferring ones whose ideal sits furthest in that direction and that are not milestones.
            var used = new HashSet<int>();
            while (diff != 0)
            {
                var up = diff > 0;
                var candidates = Enumerable.Range(0, n)
                    .Where(i => current[i] + (up ? 1 : -1) >= minUnits && current[i] + (up ? 1 : -1) <= maxUnits)
                    .OrderBy(i => uniform && up ? (IsMilestone(i) ? 0 : 1) : 0)
                    .ThenBy(i => used.Contains(i) && !(uniform && up && IsMilestone(i)) ? 1 : 0)
                    .ThenBy(i => !(uniform && up) && IsMilestone(i) ? 1 : 0)
                    .ThenBy(i => Cost(i, current[i] + (up ? 1 : -1)) - Cost(i, current[i]))
                    .ThenBy(i => i)
                    .ToList();
                if (candidates.Count == 0) return null;

                var pick = candidates[0];
                current[pick] += up ? 1 : -1;
                used.Add(pick);
                diff += up ? -1 : 1;
            }

            return current;
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

            if (settings.GamerscorePreferCommonValues)
            {
                var snapped = SnapToCommonValues(ideal, fixedUnits, milestones, uniform, units, step, minUnits, maxUnits, settings);
                if (snapped != null) return snapped.Select(u => u * step).ToArray();
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
