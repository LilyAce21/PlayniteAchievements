using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation
{
    public sealed class ScoringOutcome
    {
        /// <summary>False when the model could not be reached or refused (see <see cref="Error"/>): nothing should be generated or cached.</summary>
        public bool Succeeded { get; set; }

        public string Error { get; set; }

        /// <summary>Why the call failed, when it did.</summary>
        public LlmFailureKind FailureKind { get; set; }

        public List<AchievementScore> Scores { get; } = new List<AchievementScore>();

        public GameScoring Game { get; set; } = new GameScoring();

        /// <summary>Achievements scored from list structure because the model's answer was missing or unusable.</summary>
        public List<string> FallbackApiNames { get; } = new List<string>();

        public List<string> Notes { get; } = new List<string>();

        /// <summary>How many achievements had web research available when they were scored.</summary>
        public int ResearchedCount { get; set; }

        /// <summary>Web searches Gemini reports running during research, and the sources it used.</summary>
        public int SearchQueryCount { get; set; }

        public int SourceCount { get; set; }

        /// <summary>Search credits spent on this game's research (plugin-driven web search only).</summary>
        public int SearchCredits { get; set; }

        /// <summary>
        /// Set when web research was wanted but could not be used (for example Google Search is not available for the key
        /// or model), so the game was scored without it. The scores are still valid; this says why they lack research.
        /// </summary>
        public string ResearchSkippedReason { get; set; }

        /// <summary>True when every score came from the structure fallback (the model answered, but unusably).</summary>
        public bool UsedFallbackForAll => Succeeded && Scores.Count > 0 && FallbackApiNames.Count == Scores.Count;
    }

    /// <summary>
    /// One request per game (chunked for large lists), validated, with structure-based fallback.
    /// A network, quota, key or model problem is a failure and generates nothing, so it can simply be
    /// retried later; only a model that answered unusably falls back to structure.
    /// </summary>
    public static class LlmScoringService
    {
        /// <summary>Confidence is capped at this for an achievement that could not be researched while research was on.</summary>
        internal const double UnresearchedConfidenceCap = 0.5;

        /// <summary>
        /// With plugin-driven web search many achievements simply are not discussed online. The model still has the
        /// achievement text and its own knowledge, so these are trusted a little less, not as little as an unresearched gap.
        /// </summary>
        internal const double NoEvidenceConfidenceCap = 0.7;

        public static async Task<ScoringOutcome> ScoreAsync(
            IEstimationLlmClient client,
            EstimationGameContext context,
            CancellationToken cancellationToken,
            int chunkSize = EstimationRequestBuilder.DefaultChunkSize,
            IEstimationResearchClient research = null,
            int researchBatchSize = DefaultResearchBatchSize,
            IDictionary<string, string> researchCache = null,
            IEvidenceSource evidenceSource = null,
            ISet<string> evidenceMisses = null)
        {
            var outcome = new ScoringOutcome();
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var achievements = (context.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();
            if (achievements.Count == 0)
            {
                outcome.Succeeded = true;
                return outcome;
            }

            // ---- step 1 (optional): research every achievement with web search -------------------------------
            var notesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string gameNotes = null;
            var evidenceMode = evidenceSource != null;
            var researching = evidenceMode || research != null;

            if (evidenceMode)
            {
                // Plugin-driven web search takes the place of Gemini's own search: it needs no Gemini requests.
                var evidence = await evidenceSource.GatherAsync(context, researchCache, evidenceMisses, cancellationToken).ConfigureAwait(false);
                outcome.SearchQueryCount = evidence.Searches;
                outcome.SourceCount = evidence.Sources;
                outcome.SearchCredits = evidence.Credits;
                gameNotes = evidence.GameNotes;
                outcome.Notes.AddRange(evidence.Notes);

                var ids = EstimationRequestBuilder.IdsByApiName(context);
                foreach (var pair in evidence.NotesByApiName)
                {
                    if (ids.TryGetValue(pair.Key, out var evidenceId)) notesById[evidenceId] = pair.Value;
                }

                if (evidence.Unavailable)
                {
                    outcome.ResearchSkippedReason = evidence.Reason;
                    outcome.Notes.Add("Scored without web research: " + evidence.Reason);
                    researching = false;
                }
            }
            else if (researching)
            {
                var researchFailure = await ResearchAsync(research, context, researchBatchSize, notesById, outcome, researchCache, cancellationToken).ConfigureAwait(false);
                if (researchFailure != null && researchFailure.ResearchUnavailable)
                {
                    // Google Search cannot be used here. That is not a reason to lose the game: carry on without it.
                    outcome.ResearchSkippedReason = researchFailure.Error;
                    outcome.Notes.Add("Scored without web research: " + researchFailure.Error);

                    // Nothing was researched, so nothing was missed: no confidence penalty. If some batches did succeed
                    // first, the rest count as unresearched like any other gap.
                    if (notesById.Count == 0) researching = false;
                }
                else if (researchFailure != null)
                {
                    outcome.Succeeded = false;
                    outcome.Error = researchFailure.Error;
                    outcome.FailureKind = researchFailure.Failure;
                    outcome.Scores.Clear();
                    outcome.FallbackApiNames.Clear();
                    return outcome;
                }
            }

            // ---- step 2: score, with each achievement's research attached ------------------------------------
            var requests = EstimationRequestBuilder.Build(context, chunkSize, notesById, gameNotes);
            var usableGames = new List<GameScoring>();

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var call = await client.GenerateAsync(request.Json, cancellationToken).ConfigureAwait(false);
                if (!call.Success)
                {
                    outcome.Succeeded = false;
                    outcome.Error = call.Error;
                    outcome.FailureKind = call.Failure;
                    outcome.Scores.Clear();
                    outcome.FallbackApiNames.Clear();
                    return outcome;
                }

                var parsed = call.Truncated
                    ? EstimationResponseParser.Parse(null, request.IdMap)
                    : EstimationResponseParser.Parse(call.Text, request.IdMap);
                if (call.Truncated) outcome.Notes.Add("Part " + (request.ChunkIndex + 1) + " was cut off; structure was used for it.");
                outcome.Notes.AddRange(parsed.Problems);

                if (parsed.IsUsable) usableGames.Add(parsed.Game);

                if (researching)
                {
                    // Anything that could not be researched is trusted less, so the estimators lean on structure for it.
                    var idByApiName = request.IdMap.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
                    foreach (var score in parsed.Scores)
                    {
                        score.Researched = idByApiName.TryGetValue(score.ApiName, out var scoreId) && notesById.ContainsKey(scoreId);
                        if (!score.Researched) score.Confidence = Math.Min(score.Confidence, evidenceMode ? NoEvidenceConfidenceCap : UnresearchedConfidenceCap);
                        else outcome.ResearchedCount++;
                    }
                }

                outcome.Scores.AddRange(parsed.Scores);

                if (parsed.Missing.Count > 0)
                {
                    var missing = new HashSet<string>(parsed.Missing, StringComparer.OrdinalIgnoreCase);
                    var fallbackScores = StructureFallback.Score(achievements.Where(a => missing.Contains(a.ApiName)).ToList());
                    outcome.Scores.AddRange(fallbackScores);
                    outcome.FallbackApiNames.AddRange(fallbackScores.Select(s => s.ApiName));
                }
            }

            outcome.Game = CombineGames(usableGames);
            outcome.Succeeded = true;

            if (researching)
            {
                outcome.Notes.Insert(0, evidenceMode
                    ? "Found written evidence for " + outcome.ResearchedCount + " of " + achievements.Count + " achievements (" +
                      outcome.SearchQueryCount + " searches, " + outcome.SearchCredits + " credits, " + outcome.SourceCount + " sources)."
                    : "Researched " + outcome.ResearchedCount + " of " + achievements.Count + " achievements (" +
                      outcome.SearchQueryCount + " web searches, " + outcome.SourceCount + " sources).");
            }

            return outcome;
        }

        internal const int DefaultResearchBatchSize = 25;

        /// <summary>
        /// Researches the achievements in small batches, then retries once for any the model skipped. Returns the
        /// failing call when Gemini itself cannot be used (key, quota, network); a batch whose answer simply could not
        /// be read is not a failure, its achievements just go unresearched.
        /// </summary>
        private static async Task<LlmCallResult> ResearchAsync(
            IEstimationResearchClient research,
            EstimationGameContext context,
            int batchSize,
            Dictionary<string, string> notesById,
            ScoringOutcome outcome,
            IDictionary<string, string> cache,
            CancellationToken cancellationToken)
        {
            // What an earlier attempt already found is reused, so a rate limit part way through research does not
            // make the next attempt pay for the finished batches again.
            var ids = EstimationRequestBuilder.IdsByApiName(context);
            var alreadyResearched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cache != null)
            {
                foreach (var pair in ids)
                {
                    if (cache.TryGetValue(pair.Key, out var cachedNote))
                    {
                        notesById[pair.Value] = cachedNote;
                        alreadyResearched.Add(pair.Key);
                    }
                }
            }

            void Remember(KeyValuePair<string, string> found, IDictionary<string, string> idMap)
            {
                notesById[found.Key] = found.Value;
                if (cache != null && idMap.TryGetValue(found.Key, out var apiName)) cache[apiName] = found.Value;
            }

            var remaining = alreadyResearched.Count == 0
                ? null
                : new HashSet<string>(ids.Keys.Where(name => !alreadyResearched.Contains(name)), StringComparer.OrdinalIgnoreCase);
            if (remaining != null && remaining.Count == 0) return null; // everything was researched earlier

            foreach (var request in EstimationRequestBuilder.BuildResearch(context, batchSize, remaining))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var call = await research.ResearchAsync(request.Json, cancellationToken).ConfigureAwait(false);
                if (!call.Success) return call;

                outcome.SearchQueryCount += call.SearchQueries;
                outcome.SourceCount += call.Sources;
                foreach (var pair in ResearchResponseParser.Parse(call.Text, request.IdMap)) Remember(pair, request.IdMap);

                var missing = request.IdMap.Where(p => !notesById.ContainsKey(p.Key)).Select(p => p.Value).ToList();
                if (missing.Count == 0 || call.Truncated && missing.Count == request.IdMap.Count) continue;

                // One more try for the achievements the model skipped.
                var retries = EstimationRequestBuilder.BuildResearch(context, batchSize, new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase));
                foreach (var retry in retries)
                {
                    var again = await research.ResearchAsync(retry.Json, cancellationToken).ConfigureAwait(false);
                    if (!again.Success) return again;

                    outcome.SearchQueryCount += again.SearchQueries;
                    outcome.SourceCount += again.Sources;
                    foreach (var pair in ResearchResponseParser.Parse(again.Text, retry.IdMap)) Remember(pair, retry.IdMap);
                }
            }

            if (notesById.Count == 0) outcome.Notes.Add("Web research returned nothing usable; scores rely on the achievement text alone.");
            if (outcome.SearchQueryCount == 0) outcome.Notes.Add("Gemini reported running no web searches, so the research may rest on its own knowledge.");
            return null;
        }

        private static GameScoring CombineGames(IList<GameScoring> games)
        {
            if (games.Count == 0)
            {
                // The model never gave a usable answer: lean on structure and budget.
                return new GameScoring { OverallConfidence = StructureFallback.FallbackConfidence };
            }

            var combined = new GameScoring
            {
                OverallConfidence = games.Average(g => g.OverallConfidence),
                IsUniformSet = games.All(g => g.IsUniformSet),
                Profile = games.Select(g => g.Profile).FirstOrDefault(p => p != GameProfile.Unknown)
            };

            foreach (var game in games)
            {
                foreach (var pair in game.CategoryKinds) combined.CategoryKinds[pair.Key] = pair.Value;
            }

            return combined;
        }
    }
}
