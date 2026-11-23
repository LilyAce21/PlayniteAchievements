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

            ApplyBudget(targets, scoreByName, grades, reasons, settings);

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

        private static double Tolerance(BudgetPreset preset) => preset == BudgetPreset.Native ? 0.03 : 0.50;

        /// <summary>
        /// Brings the list's total into the budget band. The budget only nudges: when the value-based
        /// grades already sit inside the band nothing changes. Outside it, the mix the scores
        /// suggested is scaled to the band (a list keeps its shape, with a real middle of silvers,
        /// instead of collapsing into gold and bronze), and grades are then handed out in merit
        /// order: trophies with a stated gold reason first, then by value.
        /// </summary>
        private static void ApplyBudget(
            List<AchievementScore> targets,
            Dictionary<string, double> scoreByName,
            Dictionary<string, TrophyGrade> grades,
            Dictionary<string, string> reasons,
            EstimationSettings settings)
        {
            var n = targets.Count;
            var target = BudgetTarget(n, settings);
            var largeList = n >= LargeListThreshold;

            var tolerance = Tolerance(settings.Preset);
            var low = target * (1 - tolerance);
            var high = target * (1 + tolerance);

            var total = grades.Values.Sum(PointsOf);
            if (total >= low && total <= high) return;

            var goal = total < low ? low : high;
            var floor = BronzePoints * n;
            var extra = Math.Max(0.0, goal - floor);

            var reasonsByName = GoldReasons(targets, largeList);
            double Merit(AchievementScore s) => (reasonsByName.ContainsKey(s.ApiName) ? 10.0 : 0.0) + scoreByName[s.ApiName];
            var byMerit = targets
                .OrderByDescending(Merit)
                .ThenBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The shape the scores suggested: how many golds and silvers, and how much each adds above bronze.
            var gold0 = grades.Values.Count(g => g == TrophyGrade.Gold);
            var silver0 = grades.Values.Count(g => g == TrophyGrade.Silver);
            if (gold0 + silver0 == 0) { gold0 = 1; silver0 = 3; } // nothing to scale: seed a typical shape
            var extra0 = (GoldPoints - BronzePoints) * gold0 + (SilverPoints - BronzePoints) * silver0;
            var factor = extra / extra0;

            // Golds are capped by stated reasons where a reason is required (large lists, value-based preset).
            var goldCap = largeList || settings.Preset == BudgetPreset.ValueBased
                ? reasonsByName.Count
                : n;

            var bestGold = 0;
            var bestSilver = 0;
            var bestError = double.MaxValue;
            // Golds are the rarest, best-justified grade. When the budget is squeezed they scale with
            // the square root of the factor, so a tight list gives up silvers before it gives up
            // golds; when the budget is generous (small lists) they scale fully with it.
            var goldIdeal = gold0 * (factor < 1 ? Math.Sqrt(factor) : factor);
            var scaledGold = (int)Math.Round(goldIdeal);
            for (var g = Math.Max(0, scaledGold - 3); g <= Math.Min(Math.Min(n, goldCap), scaledGold + 3); g++)
            {
                var silvers = (int)Math.Round((extra - (GoldPoints - BronzePoints) * g) / (SilverPoints - BronzePoints));
                silvers = Math.Max(0, Math.Min(n - g, silvers));

                var points = floor + (GoldPoints - BronzePoints) * g + (SilverPoints - BronzePoints) * silvers;
                var error = Math.Abs(points - goal) + 0.001 * Math.Abs(g - goldIdeal);
                if (error < bestError) { bestError = error; bestGold = g; bestSilver = silvers; }
            }

            for (var i = 0; i < n; i++)
            {
                var s = byMerit[i];
                var grade = i < bestGold ? TrophyGrade.Gold : i < bestGold + bestSilver ? TrophyGrade.Silver : TrophyGrade.Bronze;
                if (grade == grades[s.ApiName]) continue;

                grades[s.ApiName] = grade;
                reasons[s.ApiName] = grade == TrophyGrade.Gold && reasonsByName.TryGetValue(s.ApiName, out var why)
                    ? "budget: " + why
                    : "budget";
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
