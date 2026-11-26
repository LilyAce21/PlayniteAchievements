using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>One achievement as the planner sees it: public facts plus what PA already holds natively.</summary>
    public sealed class EstimationSourceAchievement
    {
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool IsHidden { get; set; }
        public string Category { get; set; }

        /// <summary>The raw category kind PA holds (for example a DLC or update kind). Used only for calibration.</summary>
        public string CategoryKind { get; set; }

        public double? RarityPercent { get; set; }

        /// <summary>
        /// The trophy type PA holds natively (platform or metadata source), or null when there is none
        /// or the value is itself an estimate. May be a value that is not a PSN grade (some providers
        /// reuse the field), which the planner treats as an occupied slot.
        /// </summary>
        public string NativeTrophyType { get; set; }

        /// <summary>Points PA holds natively, or null when there are none or the value is an estimate.</summary>
        public int? NativePoints { get; set; }

        /// <summary>True when the stored trophy type is a previously generated estimate.</summary>
        public bool HasEstimatedTrophy { get; set; }

        /// <summary>True when the stored points are a previously generated estimate.</summary>
        public bool HasEstimatedPoints { get; set; }
    }

    public sealed class EstimationSourceGame
    {
        public Guid PlayniteGameId { get; set; }
        public string Title { get; set; }
        public int? ReleaseYear { get; set; }
        public string Genre { get; set; }

        /// <summary>The game's provider (for example "PSN" or "Steam"). Used only for calibration.</summary>
        public string ProviderKey { get; set; }

        /// <summary>
        /// True when the game's native Points are Xbox Gamerscore (an Xbox-family provider), so a partial
        /// native list can be filled in. When false, any native Points belong to another system (for
        /// example RetroAchievements points) and no Gamerscore is generated next to them.
        /// </summary>
        public bool PointsAreGamerscore { get; set; }

        public IList<EstimationSourceAchievement> Achievements { get; set; } = new List<EstimationSourceAchievement>();
    }

    public sealed class EstimationRunOptions
    {
        public EstimationSettings Settings { get; set; } = new EstimationSettings();
        public CalibrationProfile Calibration { get; set; }
        public bool GenerateTrophies { get; set; } = true;
        public bool GenerateGamerscore { get; set; } = true;
        public string Model { get; set; }

        /// <summary>
        /// False (the default) keeps earlier results: a kind whose targets all already carry an estimate is
        /// not generated again. True regenerates everything that has no native data.
        /// </summary>
        public bool Regenerate { get; set; }

        /// <summary>
        /// Summarise the user's own native lists into the request (grade mix by size, DLC split, closest
        /// anonymous lists). The coordinator computes it once per run when this is true and none is supplied.
        /// </summary>
        public bool UseCalibration { get; set; } = true;

        /// <summary>
        /// Research every achievement with web search before scoring it (when the client supports it), so the model
        /// understands what each one really is. Costs extra requests and uses Google Search grounding.
        /// </summary>
        public bool UseResearch { get; set; } = true;

        /// <summary>Achievements per research request. Smaller batches let the model look at each one properly.</summary>
        public int ResearchBatchSize { get; set; } = 25;
    }

    public sealed class EstimationPlan
    {
        public List<EstimationSourceAchievement> Achievements { get; } = new List<EstimationSourceAchievement>();

        public Dictionary<string, TrophyGrade> NativeGrades { get; } = new Dictionary<string, TrophyGrade>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> NativeGamerscore { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> TrophyTargets { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PointsTargets { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool NeedsAnything => TrophyTargets.Count > 0 || PointsTargets.Count > 0;

        /// <summary>Why nothing (or only one kind) is generated, in plain words.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Decides what may be generated for a game. A slot is generated into only when nothing occupies it:
    /// platinum rows are left out of everything, a trophy type that is not bronze, silver or gold belongs
    /// to another provider, and native Points that are not Gamerscore are never mixed with an estimate.
    /// </summary>
    public static class EstimationPlanner
    {
        internal static bool TryParseGrade(string value, out TrophyGrade grade)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "bronze": grade = TrophyGrade.Bronze; return true;
                case "silver": grade = TrophyGrade.Silver; return true;
                case "gold": grade = TrophyGrade.Gold; return true;
                default: grade = TrophyGrade.Bronze; return false;
            }
        }

        private static bool IsPlatinum(string value) =>
            string.Equals((value ?? string.Empty).Trim(), "platinum", StringComparison.OrdinalIgnoreCase);

        public static EstimationPlan Plan(EstimationSourceGame game, EstimationRunOptions options)
        {
            var plan = new EstimationPlan();
            if (game?.Achievements == null) return plan;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in game.Achievements)
            {
                if (a == null || string.IsNullOrWhiteSpace(a.ApiName) || !seen.Add(a.ApiName)) continue;

                // Platinum is excluded from everything: never graded, budgeted, anchored or sent.
                if (IsPlatinum(a.NativeTrophyType)) continue;
                plan.Achievements.Add(a);
            }

            if (plan.Achievements.Count == 0) return plan;

            // ---- trophies ---------------------------------------------------------------------
            var trophyBlocked = false;
            foreach (var a in plan.Achievements)
            {
                if (string.IsNullOrWhiteSpace(a.NativeTrophyType)) continue;
                if (TryParseGrade(a.NativeTrophyType, out var grade)) plan.NativeGrades[a.ApiName] = grade;
                else trophyBlocked = true; // the field means something else here
            }

            if (options.GenerateTrophies)
            {
                if (trophyBlocked)
                {
                    plan.Notes.Add("Trophy grades were skipped: this game's trophy field holds another provider's values.");
                }
                else
                {
                    foreach (var a in plan.Achievements.Where(x => string.IsNullOrWhiteSpace(x.NativeTrophyType)))
                    {
                        plan.TrophyTargets.Add(a.ApiName);
                    }
                }
            }

            // ---- gamerscore -------------------------------------------------------------------
            var anyNativePoints = plan.Achievements.Any(a => a.NativePoints.HasValue);
            var pointsBlocked = anyNativePoints && !game.PointsAreGamerscore;
            if (game.PointsAreGamerscore)
            {
                foreach (var a in plan.Achievements.Where(x => x.NativePoints.HasValue && x.NativePoints.Value > 0))
                {
                    plan.NativeGamerscore[a.ApiName] = a.NativePoints.Value;
                }
            }

            var settings = options.Settings ?? new EstimationSettings();
            var tooFewForXbox = plan.Achievements.Count < settings.GamerscoreMinAchievements;

            if (options.GenerateGamerscore)
            {
                if (tooFewForXbox)
                {
                    // Xbox needs at least 10 achievements, and fewer could not reach a 1,000 G block either.
                    plan.Notes.Add("Gamerscore was skipped: Xbox titles need at least " + settings.GamerscoreMinAchievements +
                                   " achievements and this list has " + plan.Achievements.Count + ".");
                }
                else if (pointsBlocked)
                {
                    plan.Notes.Add("Gamerscore was skipped: this game's native points are not Xbox Gamerscore.");
                }
                else
                {
                    foreach (var a in plan.Achievements.Where(x => !x.NativePoints.HasValue))
                    {
                        plan.PointsTargets.Add(a.ApiName);
                    }
                }
            }

            // Results are kept permanently. A kind is skipped only when every target already has an
            // estimate; if any target is new, all targets of that kind are regenerated together so the
            // list stays graded as one.
            if (!options.Regenerate)
            {
                var byName = plan.Achievements.ToDictionary(a => a.ApiName, StringComparer.OrdinalIgnoreCase);
                if (plan.TrophyTargets.Count > 0 && plan.TrophyTargets.All(n => byName[n].HasEstimatedTrophy))
                {
                    plan.TrophyTargets.Clear();
                    plan.Notes.Add("Trophy grades were kept from an earlier run.");
                }

                if (plan.PointsTargets.Count > 0 && plan.PointsTargets.All(n => byName[n].HasEstimatedPoints))
                {
                    plan.PointsTargets.Clear();
                    plan.Notes.Add("Gamerscore was kept from an earlier run.");
                }
            }

            if (!plan.NeedsAnything && plan.Notes.Count == 0) plan.Notes.Add("Nothing to generate: every slot already has native data.");
            return plan;
        }
    }

    public enum EstimationRunStatus
    {
        /// <summary>Nothing needed generating.</summary>
        Skipped = 0,

        /// <summary>Generated from the model's scores.</summary>
        Generated,

        /// <summary>Generated, but some or all scores came from structure because the model's answer was unusable.</summary>
        GeneratedWithFallback,

        /// <summary>The model could not be reached or refused. Nothing was generated; try again later.</summary>
        Failed
    }

    public sealed class EstimationRunResult
    {
        public EstimationRunStatus Status { get; set; }
        public string Message { get; set; }
        public LlmFailureKind FailureKind { get; set; }

        /// <summary>Why the game was scored without web research, when research was wanted but unavailable.</summary>
        public string ResearchSkippedReason { get; set; }
        public List<EstimatedValueRow> Rows { get; } = new List<EstimatedValueRow>();
        public EstimationPlan Plan { get; set; }
        public ScoringOutcome Scoring { get; set; }
    }

    public static class EstimationRunner
    {
        public static async Task<EstimationRunResult> RunAsync(
            EstimationSourceGame game,
            IEstimationLlmClient client,
            EstimationRunOptions options,
            CancellationToken cancellationToken)
        {
            options = options ?? new EstimationRunOptions();
            var result = new EstimationRunResult();
            var plan = EstimationPlanner.Plan(game, options);
            result.Plan = plan;

            if (!plan.NeedsAnything)
            {
                result.Status = EstimationRunStatus.Skipped;
                result.Message = string.Join(" ", plan.Notes);
                return result;
            }

            // ---- ask the model, using only public game facts and native anchors -----------------
            var context = new EstimationGameContext
            {
                Title = game.Title,
                ReleaseYear = game.ReleaseYear,
                Genre = game.Genre,
                TargetPlatform = plan.TrophyTargets.Count > 0 && plan.PointsTargets.Count > 0 ? "psn+xbox" : plan.TrophyTargets.Count > 0 ? "psn" : "xbox",
                Calibration = options.Calibration,
                Achievements = plan.Achievements.Select(a => new PublicAchievement
                {
                    ApiName = a.ApiName,
                    DisplayName = a.DisplayName,
                    Description = a.Description,
                    IsHidden = a.IsHidden,
                    Category = a.Category,
                    RarityPercent = a.RarityPercent
                }).ToList()
            };

            foreach (var pair in plan.NativeGrades)
            {
                context.Anchors.Add(new NativeAnchor { ApiName = pair.Key, Platform = "psn", Grade = pair.Value });
            }

            foreach (var pair in plan.NativeGamerscore)
            {
                context.Anchors.Add(new NativeAnchor { ApiName = pair.Key, Platform = "xbox", Gamerscore = pair.Value });
            }

            var researchClient = options.UseResearch ? client as IEstimationResearchClient : null;
            var scoring = await LlmScoringService.ScoreAsync(
                client, context, cancellationToken,
                EstimationRequestBuilder.DefaultChunkSize, researchClient, Math.Max(1, options.ResearchBatchSize),
                researchClient != null ? ResearchNotesCache.For(game.PlayniteGameId) : null).ConfigureAwait(false);
            result.Scoring = scoring;
            result.ResearchSkippedReason = scoring.ResearchSkippedReason;
            if (!scoring.Succeeded)
            {
                result.Status = EstimationRunStatus.Failed;
                result.Message = scoring.Error;
                result.FailureKind = scoring.FailureKind;
                return result;
            }

            // Category and order from the list itself; base-game achievements come before DLC and updates.
            var categoryByApi = plan.Achievements.ToDictionary(a => a.ApiName, a => a.Category, StringComparer.OrdinalIgnoreCase);
            var position = plan.Achievements.Select((a, i) => new { a.ApiName, Index = i }).ToDictionary(x => x.ApiName, x => x.Index, StringComparer.OrdinalIgnoreCase);
            foreach (var score in scoring.Scores) score.Category = categoryByApi.TryGetValue(score.ApiName, out var category) ? category : null;

            var ordered = scoring.Scores
                .OrderBy(s => CategoryRank(s.Category, scoring.Game))
                .ThenBy(s => position.TryGetValue(s.ApiName, out var index) ? index : int.MaxValue)
                .ToList();

            var rows = new Dictionary<string, EstimatedValueRow>(StringComparer.OrdinalIgnoreCase);
            EstimatedValueRow RowFor(string apiName)
            {
                if (!rows.TryGetValue(apiName, out var row))
                {
                    row = new EstimatedValueRow { ApiName = apiName };
                    rows[apiName] = row;
                }

                return row;
            }

            var confidence = scoring.Scores.ToDictionary(s => s.ApiName, s => Math.Min(s.Confidence, scoring.Game.OverallConfidence), StringComparer.OrdinalIgnoreCase);

            if (plan.TrophyTargets.Count > 0)
            {
                var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput
                {
                    Scores = ordered,
                    Targets = plan.TrophyTargets,
                    NativeGrades = plan.NativeGrades,
                    OtherPlatformValues = plan.NativeGamerscore,
                    Game = scoring.Game,
                    Settings = options.Settings
                });

                foreach (var estimate in estimates)
                {
                    var row = RowFor(estimate.ApiName);
                    row.TrophyType = estimate.TrophyType;
                    row.TrophyReason = estimate.Reason;
                    row.Confidence = confidence.TryGetValue(estimate.ApiName, out var c) ? c : (double?)null;
                }
            }

            if (plan.PointsTargets.Count > 0)
            {
                var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput
                {
                    Scores = ordered,
                    Targets = plan.PointsTargets,
                    NativeValues = plan.NativeGamerscore,
                    OtherPlatformGrades = plan.NativeGrades,
                    Game = scoring.Game,
                    Settings = options.Settings
                });

                foreach (var estimate in estimates)
                {
                    var row = RowFor(estimate.ApiName);
                    row.Points = estimate.Value;
                    row.PointsReason = "block " + (estimate.Block + 1);
                    row.Confidence = confidence.TryGetValue(estimate.ApiName, out var c) ? c : (double?)null;
                }
            }

            // Hard rule 1 again, before anything is handed to the store: never a value for a slot with native data.
            foreach (var row in rows.Values)
            {
                if (!plan.TrophyTargets.Contains(row.ApiName)) { row.TrophyType = null; row.TrophyReason = null; }
                if (!plan.PointsTargets.Contains(row.ApiName)) { row.Points = null; row.PointsReason = null; }
            }

            result.Rows.AddRange(rows.Values.Where(r => r.TrophyType != null || r.Points.HasValue));
            result.Status = scoring.FallbackApiNames.Count > 0 ? EstimationRunStatus.GeneratedWithFallback : EstimationRunStatus.Generated;
            result.Message = scoring.Notes.Count > 0 ? string.Join(" ", scoring.Notes.Distinct()) : null;
            return result;
        }

        /// <summary>Base-game achievements first, then updates, then DLC, so Gamerscore blocks follow release order.</summary>
        private static int CategoryRank(string category, GameScoring game)
        {
            if (!string.IsNullOrWhiteSpace(category) && game.CategoryKinds.TryGetValue(category, out var kind))
            {
                switch (kind)
                {
                    case CategoryKind.Base: return 0;
                    case CategoryKind.Mixed: return 1;
                    case CategoryKind.Update: return 2;
                    case CategoryKind.Dlc: return 3;
                }
            }

            return 0;
        }
    }
}
