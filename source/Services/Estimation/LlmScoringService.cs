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
        public static async Task<ScoringOutcome> ScoreAsync(
            IEstimationLlmClient client,
            EstimationGameContext context,
            CancellationToken cancellationToken,
            int chunkSize = EstimationRequestBuilder.DefaultChunkSize)
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

            var requests = EstimationRequestBuilder.Build(context, chunkSize);
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
            return outcome;
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
