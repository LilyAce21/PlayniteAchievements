using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    public sealed class TrophyEstimationInput
    {
        /// <summary>The model's scores for every achievement in the list, native or not.</summary>
        public IList<AchievementScore> Scores { get; set; } = new List<AchievementScore>();

        /// <summary>
        /// The achievements to generate for. Null means every achievement without a native grade.
        /// Anything with a native grade is never generated, whatever this contains.
        /// </summary>
        public ISet<string> Targets { get; set; }

        /// <summary>Native PSN grades already in this list, by api name.</summary>
        public IDictionary<string, TrophyGrade> NativeGrades { get; set; } =
            new Dictionary<string, TrophyGrade>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Native Xbox Gamerscore values for the same game, by api name. Compared by rank within
        /// the list, never by raw number.
        /// </summary>
        public IDictionary<string, int> OtherPlatformValues { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public GameScoring Game { get; set; } = new GameScoring();

        public EstimationSettings Settings { get; set; } = new EstimationSettings();
    }

    /// <summary>
    /// Estimates the PSN trophy grade (bronze, silver or gold) a developer would most likely have
    /// chosen. Platinum is never generated or budgeted. Trophy points exist only inside this class,
    /// as the unit the budget is measured in; they are never returned.
    /// </summary>
    public static class TrophyGradeEstimator
    {
        internal const int BronzePoints = 15;
        internal const int SilverPoints = 30;
        internal const int GoldPoints = 90;

        internal const double DefaultSilverCut = 0.45;
        internal const double DefaultGoldCut = 0.75;

        /// <summary>List size where a 15-point floor times the count reaches the default budget.</summary>
        private const int TighteningStart = 65;
        private const int TighteningSpan = 135;

        private const int MinNativesForFit = 4;

        /// <summary>
        /// From this many trophies on, gold is reserved for true capstones: the capstone itself (end
        /// of the story and similar beats), a true ending, or the last step of a real ladder. A hard
        /// challenge only counts at the extreme end of difficulty (5), because in huge
        /// undifferentiated lists (Geometry Dash, Cookie Clicker) the model rates far too many
        /// items as hard.
        /// </summary>
        internal const int LargeListThreshold = 100;

        public static List<TrophyEstimate> Estimate(TrophyEstimationInput input)
        {
            var result = new List<TrophyEstimate>();
            if (input?.Scores == null || input.Scores.Count == 0) return result;

            var settings = input.Settings ?? new EstimationSettings();
            var game = input.Game ?? new GameScoring();
            var natives = input.NativeGrades ?? new Dictionary<string, TrophyGrade>(StringComparer.OrdinalIgnoreCase);

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

            if (game.IsUniformSet)
            {
                return targets
                    .Select(s => new TrophyEstimate { ApiName = s.ApiName, Grade = TrophyGrade.Bronze, Reason = "uniform set" })
                    .ToList();
            }

            var effective = scores.ToDictionary(s => s.ApiName, s => ValueScoring.Effective(s, game), StringComparer.OrdinalIgnoreCase);
            var goldReasons = GoldReasons(scores, scores.Count >= LargeListThreshold);

            var otherPercentiles = ValueScoring.Percentiles(input.OtherPlatformValues);
            var hasNativeGrades = natives.Count > 0;

            return hasNativeGrades
                ? EstimateFillIns(targets, scores, natives, effective, goldReasons, otherPercentiles, game)
                : EstimateFullList(targets, effective, goldReasons, otherPercentiles, settings, game);
        }

        // ---------------------------------------------------------------------------------
        // Case A / C: no native grades on this platform. Thresholds first, budget second.
        // ---------------------------------------------------------------------------------
        private static List<TrophyEstimate> EstimateFullList(
            List<AchievementScore> targets,
            Dictionary<string, double> effective,
            Dictionary<string, string> goldReasons,
            Dictionary<string, double> otherPercentiles,
            EstimationSettings settings,
            GameScoring game)
        {
            var n = targets.Count;

            // The other platform's value rank is a prior when no overlap exists to learn from.
            double ScoreOf(AchievementScore s)
            {
                var value = effective[s.ApiName];
                return otherPercentiles.TryGetValue(s.ApiName, out var percentile)
                    ? 0.5 * value + 0.5 * percentile
                    : value;
            }

            var scoreByName = targets.ToDictionary(s => s.ApiName, ScoreOf, StringComparer.OrdinalIgnoreCase);

            var silverCut = DefaultSilverCut;
            var goldCut = DefaultGoldCut;
            Tighten(n, ref silverCut, ref goldCut);

            var grades = new Dictionary<string, TrophyGrade>(StringComparer.OrdinalIgnoreCase);
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in targets)
            {
                var score = scoreByName[s.ApiName];
                var grade = GradeFor(score, silverCut, goldCut, goldReasons.TryGetValue(s.ApiName, out var reason) ? reason : null, out var why);
                grades[s.ApiName] = grade;
                reasons[s.ApiName] = why;
            }

            ApplyBudget(targets, scoreByName, grades, reasons, settings, silverCut, goldCut);

            return targets
                .Select(s => new TrophyEstimate { ApiName = s.ApiName, Grade = grades[s.ApiName], Reason = reasons[s.ApiName] })
                .ToList();
        }

        /// <summary>
        /// Raises the silver and gold cuts for long lists, so the share of silver and gold
        /// tightens smoothly toward a bronze-dominant mix. Whole-list effect, no cliff.
        /// </summary>
        internal static void Tighten(int count, ref double silverCut, ref double goldCut)
        {
            if (count <= TighteningStart) return;
            var t = Math.Min(1.0, (count - TighteningStart) / (double)TighteningSpan);
            silverCut += 0.20 * t;
            goldCut += 0.15 * t;
        }

        private static TrophyGrade GradeFor(double score, double silverCut, double goldCut, string goldReason, out string reason)
        {
            if (score >= goldCut)
            {
                if (!string.IsNullOrEmpty(goldReason))
                {
                    reason = goldReason;
                    return TrophyGrade.Gold;
                }

                // Gold requires a stated reason; without one it drops to silver.
                reason = "no gold reason";
                return TrophyGrade.Silver;
            }

            if (score >= silverCut)
            {
                reason = "value";
                return TrophyGrade.Silver;
            }

            reason = "value";
            return TrophyGrade.Bronze;
        }

        /// <summary>
        /// Gold reasons the model's data can justify: a capstone, a true ending, a hard challenge,
        /// or a major completion (the last step of a real ladder).
        /// </summary>
        internal static Dictionary<string, string> GoldReasons(IList<AchievementScore> scores, bool largeList = false)
        {
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var seriesLast = scores
                .Where(s => !string.IsNullOrWhiteSpace(s.SeriesId))
                .GroupBy(s => s.SeriesId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 3)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.SeriesPosition).First().ApiName, StringComparer.OrdinalIgnoreCase);

            foreach (var s in scores)
            {
                string reason = null;
                if (s.IsCapstone) reason = "capstone";
                else if (s.Kind == AchievementKind.TrueEnding) reason = "true ending";
                else if (s.Difficulty >= 5 || (!largeList && s.Kind == AchievementKind.Challenge && s.Difficulty >= 4)) reason = "hard challenge";
                else if (!string.IsNullOrWhiteSpace(s.SeriesId) &&
                         seriesLast.TryGetValue(s.SeriesId, out var last) &&
                         string.Equals(last, s.ApiName, StringComparison.OrdinalIgnoreCase) &&
                         s.Difficulty >= 3) reason = "major completion";

                if (reason != null) reasons[s.ApiName] = reason;
            }

            return reasons;
        }

        // ---------------------------------------------------------------------------------
        // Budget nudging
        // ---------------------------------------------------------------------------------
        private static int PointsOf(TrophyGrade grade)
        {
            switch (grade)
            {
                case TrophyGrade.Gold: return GoldPoints;
                case TrophyGrade.Silver: return SilverPoints;
                default: return BronzePoints;
            }
        }

        /// <summary>
        /// The budget target for a list of this size, one continuous curve: the native budget for
        /// small lists, and for longer ones the all-bronze floor plus a headroom that fades smoothly
        /// to nothing. The target therefore rises with the list, no trophies are dropped, and the
        /// share of silver and gold tightens gradually toward a bronze-dominant mix, with no cliff
        /// at the point where the floor overtakes the budget. Never more than an all-gold list.
        /// </summary>
        internal static int BudgetTarget(int count, EstimationSettings settings)
        {
            if (count <= 0) return 0;

            var start = settings.TrophyHeadroomStart;
            var end = Math.Max(start + 1, settings.TrophyHeadroomEnd);
            var t = ValueScoring.Clamp01((count - start) / (double)(end - start));
            var fade = t * t * (3 - 2 * t); // smoothstep: flat at both ends

            var floorTotal = settings.TrophyFloorPerTrophy * count;
            var curve = floorTotal * (1 + settings.TrophyHeadroomAtStart * (1 - fade));

            var target = Math.Max(settings.TrophyBudget, (int)Math.Round(curve));
            return Math.Min(target, GoldPoints * count);
        }

        /// <summary>How close to the budget the grades are brought: tight for Native, still aiming at it for Value-based.</summary>
        private static double Tolerance(BudgetPreset preset) => preset == BudgetPreset.Native ? 0.03 : 0.08;

        /// <summary>
        /// Value-based preset: the most the exceptions may pull the total away from the budget (25%).
        /// Beyond that, the least clear exceptions are given back to the budget.
        /// </summary>
        private const double ExceptionLimit = 0.25;

        /// <summary>
        /// Value-based preset: how far past a grade's cut a score must be for the grade to count as clearly
        /// deserved (and so to be kept even when the budget would change it).
        /// </summary>
        private const double ClearMargin = 0.08;

        /// <summary>
        /// Brings the list's total toward the budget.
        /// <para>
        /// Native preset: strict. The budget only nudges: when the value-based grades already sit inside a tight
        /// band nothing changes; outside it, the mix the scores suggested is scaled to the band (a list keeps its
        /// shape, with a real middle of silvers) and grades are handed out in merit order.
        /// </para>
        /// <para>
        /// Value-based preset: the budget is a strong guide, not a rule, at every list size. Achievements that
        /// clearly deserve their grade by value (a clear gold with a stated reason, a clear silver, a clearly
        /// trivial bronze) keep it even when a strict budget would have changed it, and only the borderline ones
        /// are moved toward the budget. The exceptions are limited: if they would pull the total more than a
        /// quarter away from the budget, the least clear ones are given back to it.
        /// </para>
        /// </summary>
        private static void ApplyBudget(
            List<AchievementScore> targets,
            Dictionary<string, double> scoreByName,
            Dictionary<string, TrophyGrade> grades,
            Dictionary<string, string> reasons,
            EstimationSettings settings,
            double silverCut,
            double goldCut)
        {
            var n = targets.Count;
            var target = BudgetTarget(n, settings);
            var largeList = n >= LargeListThreshold;
            var valueBased = settings.Preset == BudgetPreset.ValueBased;

            var tolerance = Tolerance(settings.Preset);
            var low = target * (1 - tolerance);
            var high = target * (1 + tolerance);

            var total = grades.Values.Sum(PointsOf);
            if (total >= low && total <= high) return;

            var reasonsByName = GoldReasons(targets, largeList);

            // Which achievements keep their value-based grade whatever the budget says.
            var pinned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); // api name -> how clear
            if (valueBased)
            {
                foreach (var t in targets)
                {
                    var score = scoreByName[t.ApiName];
                    var grade = grades[t.ApiName];
                    if (grade == TrophyGrade.Gold && score >= goldCut + ClearMargin) pinned[t.ApiName] = score - goldCut;
                    else if (grade == TrophyGrade.Silver && score >= silverCut + ClearMargin) pinned[t.ApiName] = score - silverCut;
                    // An achievement with a stated gold reason (capstone, ending...) is never "clearly bronze".
                    else if (grade == TrophyGrade.Bronze && score < silverCut - ClearMargin && !reasonsByName.ContainsKey(t.ApiName)) pinned[t.ApiName] = silverCut - score;
                }

                GiveBackExceptionsBeyondTheLimit(targets, grades, reasonsByName, pinned, target);
            }

            var free = targets.Where(t => !pinned.ContainsKey(t.ApiName)).ToList();
            if (free.Count == 0) return;

            var pinnedPoints = targets.Where(t => pinned.ContainsKey(t.ApiName)).Sum(t => PointsOf(grades[t.ApiName]));
            var goal = total < low ? low : high;
            var nFree = free.Count;
            var floor = BronzePoints * nFree;

            // Gold needs a stated reason where one is required (large lists, value-based preset). The exception:
            // a value-based list that would still sit far below its budget may promote its strongest remaining
            // achievements to gold, only as many as it takes to get back within the exception limit.
            var goldRequiresReason = largeList || valueBased;
            var reasonedFree = free.Count(t => reasonsByName.ContainsKey(t.ApiName));
            var goldCap = nFree;
            if (goldRequiresReason)
            {
                goldCap = reasonedFree;
                if (valueBased)
                {
                    var reachableWithReasonsOnly = pinnedPoints + GoldPoints * reasonedFree + SilverPoints * (nFree - reasonedFree);
                    var lowLimit = target * (1 - ExceptionLimit);
                    if (reachableWithReasonsOnly < lowLimit)
                    {
                        var steps = (int)Math.Ceiling((lowLimit - reachableWithReasonsOnly) / (GoldPoints - SilverPoints));
                        goldCap = Math.Min(nFree, reasonedFree + steps);
                    }
                }
            }

            // The most the free achievements could add.
            var ceiling = GoldPoints * goldCap + SilverPoints * (nFree - goldCap);
            var extra = Math.Max(0.0, Math.Min(goal - pinnedPoints, ceiling) - floor);

            double Merit(AchievementScore s) => (reasonsByName.ContainsKey(s.ApiName) ? 10.0 : 0.0) + scoreByName[s.ApiName];
            var byMerit = free
                .OrderByDescending(Merit)
                .ThenBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The shape the scores suggested for these achievements: how many golds and silvers.
            var gold0 = free.Count(t => grades[t.ApiName] == TrophyGrade.Gold);
            var silver0 = free.Count(t => grades[t.ApiName] == TrophyGrade.Silver);
            if (gold0 + silver0 == 0) { gold0 = 1; silver0 = 3; } // nothing to scale: seed a typical shape
            var extra0 = (GoldPoints - BronzePoints) * gold0 + (SilverPoints - BronzePoints) * silver0;
            var factor = extra / extra0;

            var bestGold = 0;
            var bestSilver = 0;
            var bestError = double.MaxValue;
            // Golds are the rarest, best-justified grade. When the budget is squeezed they scale with
            // the square root of the factor, so a tight list gives up silvers before it gives up
            // golds; when the budget is generous (small lists) they scale fully with it.
            var goldIdeal = gold0 * (factor < 1 ? Math.Sqrt(factor) : factor);

            // Every gold count is tried, so a list that started with no golds can still reach its
            // budget. Points decide first; among near-equal fits the mix closest to the scaled shape wins.
            for (var g = 0; g <= Math.Min(nFree, goldCap); g++)
            {
                var silvers = (int)Math.Round((extra - (GoldPoints - BronzePoints) * g) / (SilverPoints - BronzePoints));
                silvers = Math.Max(0, Math.Min(nFree - g, silvers));

                var points = floor + (GoldPoints - BronzePoints) * g + (SilverPoints - BronzePoints) * silvers;
                var error = Math.Abs(points - (floor + extra)) + 0.001 * Math.Abs(g - goldIdeal);
                if (error < bestError) { bestError = error; bestGold = g; bestSilver = silvers; }
            }

            for (var i = 0; i < nFree; i++)
            {
                var s = byMerit[i];
                var grade = i < bestGold ? TrophyGrade.Gold : i < bestGold + bestSilver ? TrophyGrade.Silver : TrophyGrade.Bronze;
                if (grade == grades[s.ApiName]) continue;

                grades[s.ApiName] = grade;
                reasons[s.ApiName] = grade == TrophyGrade.Gold && reasonsByName.TryGetValue(s.ApiName, out var why)
                    ? "budget: " + why
                    : "budget";
            }

            // Say which grades were kept on value alone, so the reason in the data is honest.
            foreach (var name in pinned.Keys)
            {
                if (reasons.TryGetValue(name, out var existing) && existing != "budget" && !existing.StartsWith("budget:", StringComparison.Ordinal)) continue;
                reasons[name] = "clear value";
            }
        }

        /// <summary>
        /// Value-based preset: the exceptions are limited. If keeping every clearly deserved grade would leave the
        /// total more than the exception limit above the budget (even with everything else at bronze), the least clear
        /// silvers and golds are handed back to the budget; if it would be more than the limit below it, the least clear
        /// silvers are handed back so the strongest of them can be lifted. Clear bronzes always stay bronze.
        /// </summary>
        private static void GiveBackExceptionsBeyondTheLimit(
            List<AchievementScore> targets,
            Dictionary<string, TrophyGrade> grades,
            Dictionary<string, string> reasonsByName,
            Dictionary<string, double> pinned,
            int target)
        {
            var highLimit = target * (1 + ExceptionLimit);
            var lowLimit = target * (1 - ExceptionLimit);

            for (var guard = 0; guard <= targets.Count; guard++)
            {
                var free = targets.Where(t => !pinned.ContainsKey(t.ApiName)).ToList();
                var pinnedPoints = targets.Where(t => pinned.ContainsKey(t.ApiName)).Sum(t => PointsOf(grades[t.ApiName]));

                var lowest = pinnedPoints + BronzePoints * free.Count;
                var highest = pinnedPoints + GoldPoints * free.Count;

                string leastClear = null;
                if (lowest > highLimit)
                {
                    // Too generous: hand back the least clear silver or gold.
                    leastClear = pinned.Where(p => grades[p.Key] != TrophyGrade.Bronze).OrderBy(p => p.Value).Select(p => p.Key).FirstOrDefault();
                }
                else if (highest < lowLimit)
                {
                    // Too stingy. A value-based list that would sit far below its budget may promote its strongest
                    // remaining achievements to gold (no stated reason needed, only as many as it takes), so the free
                    // ones count as able to reach gold.

                    // Hand back the least clear silver. Clearly trivial bronzes are never handed back: lifting them would
                    // be exactly the abundance of undeserved grades this preset exists to avoid.
                    leastClear = pinned.Where(p => grades[p.Key] == TrophyGrade.Silver).OrderBy(p => p.Value).Select(p => p.Key).FirstOrDefault();
                }

                if (leastClear == null) return;
                pinned.Remove(leastClear);
            }
        }

        // ---------------------------------------------------------------------------------
        // Case B: fill-ins next to native grades. Budget ignored.
        // ---------------------------------------------------------------------------------
        private static List<TrophyEstimate> EstimateFillIns(
            List<AchievementScore> targets,
            List<AchievementScore> allScores,
            IDictionary<string, TrophyGrade> natives,
            Dictionary<string, double> effective,
            Dictionary<string, string> goldReasons,
            Dictionary<string, double> otherPercentiles,
            GameScoring game)
        {
            // 1. Cuts learned from the native grades, on the model's value score.
            var valuePoints = allScores
                .Where(s => natives.ContainsKey(s.ApiName))
                .Select(s => new KeyedGrade(effective[s.ApiName], natives[s.ApiName]))
                .ToList();
            var valueCuts = FitOrPrior(valuePoints, natives.Values.ToList());

            // 2. Cuts learned on the other platform's value rank, where enough achievements
            //    exist on both platforms to learn that game's mapping.
            var rankPoints = allScores
                .Where(s => natives.ContainsKey(s.ApiName) && otherPercentiles.ContainsKey(s.ApiName))
                .Select(s => new KeyedGrade(otherPercentiles[s.ApiName], natives[s.ApiName]))
                .ToList();
            var rankCuts = rankPoints.Count >= MinNativesForFit ? FitCuts(rankPoints) : null;

            var estimates = new List<TrophyEstimate>();
            foreach (var s in targets)
            {
                goldReasons.TryGetValue(s.ApiName, out var goldReason);

                double key;
                Cuts cuts;
                string source;
                if (rankCuts != null && otherPercentiles.TryGetValue(s.ApiName, out var percentile))
                {
                    key = percentile;
                    cuts = rankCuts;
                    source = "fill-in (other platform)";
                }
                else
                {
                    key = effective[s.ApiName];
                    cuts = valueCuts;
                    source = "fill-in";
                }

                var grade = GradeFor(key, cuts.Silver, cuts.Gold, goldReason, out var why);
                estimates.Add(new TrophyEstimate
                {
                    ApiName = s.ApiName,
                    Grade = grade,
                    Reason = grade == TrophyGrade.Gold ? source + ": " + why : source
                });
            }

            return estimates;
        }

        private static Cuts FitOrPrior(List<KeyedGrade> points, List<TrophyGrade> nativeGrades)
        {
            if (points.Count >= MinNativesForFit) return FitCuts(points);

            // Too few anchors to fit: use the default cuts. The native grade mix is a prior, not
            // a quota, so it only moves the cuts when it is clearly lopsided.
            var cuts = new Cuts { Silver = DefaultSilverCut, Gold = DefaultGoldCut };
            if (nativeGrades.Count >= 2)
            {
                var goldShare = nativeGrades.Count(g => g == TrophyGrade.Gold) / (double)nativeGrades.Count;
                var bronzeShare = nativeGrades.Count(g => g == TrophyGrade.Bronze) / (double)nativeGrades.Count;
                if (goldShare >= 0.6) { cuts.Gold -= 0.10; cuts.Silver -= 0.10; }
                else if (bronzeShare >= 0.9) { cuts.Gold += 0.05; cuts.Silver += 0.05; }
            }

            return cuts;
        }

        internal sealed class Cuts
        {
            public double Silver { get; set; }
            public double Gold { get; set; }
        }

        internal struct KeyedGrade
        {
            public KeyedGrade(double key, TrophyGrade grade)
            {
                Key = key;
                Grade = grade;
            }

            public double Key { get; }
            public TrophyGrade Grade { get; }
        }

        /// <summary>
        /// The silver and gold cuts that misclassify the fewest anchors. Ties go to the cuts closest
        /// to the defaults, so a thin anchor set does not swing the mapping.
        /// </summary>
        internal static Cuts FitCuts(IList<KeyedGrade> points)
        {
            var keys = points.Select(p => p.Key).Distinct().OrderBy(k => k).ToList();
            var candidates = new List<double> { 0.0 };
            for (var i = 0; i + 1 < keys.Count; i++) candidates.Add((keys[i] + keys[i + 1]) / 2.0);
            candidates.Add(1.01);

            if (candidates.Count > 48)
            {
                var step = candidates.Count / 48.0;
                candidates = Enumerable.Range(0, 48).Select(i => candidates[(int)(i * step)]).Distinct().ToList();
            }

            Cuts best = null;
            var bestErrors = int.MaxValue;
            var bestDistance = double.MaxValue;
            foreach (var silver in candidates)
            {
                foreach (var gold in candidates)
                {
                    if (gold < silver) continue;

                    var errors = 0;
                    foreach (var p in points)
                    {
                        var predicted = p.Key >= gold ? TrophyGrade.Gold : p.Key >= silver ? TrophyGrade.Silver : TrophyGrade.Bronze;
                        if (predicted != p.Grade) errors++;
                    }

                    var distance = Math.Abs(silver - DefaultSilverCut) + Math.Abs(gold - DefaultGoldCut);
                    if (errors < bestErrors || (errors == bestErrors && distance < bestDistance))
                    {
                        bestErrors = errors;
                        bestDistance = distance;
                        best = new Cuts { Silver = silver, Gold = gold };
                    }
                }
            }

            return best ?? new Cuts { Silver = DefaultSilverCut, Gold = DefaultGoldCut };
        }
    }
}
