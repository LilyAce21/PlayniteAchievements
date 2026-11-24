using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    public sealed class ParsedEstimation
    {
        public List<AchievementScore> Scores { get; } = new List<AchievementScore>();

        public GameScoring Game { get; set; } = new GameScoring();

        /// <summary>Api names the request asked about that the response did not usefully cover.</summary>
        public List<string> Missing { get; } = new List<string>();

        /// <summary>Human-readable notes on anything dropped or repaired. Never contains keys or personal data.</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>False when the response could not be used at all (not JSON, wrong shape).</summary>
        public bool IsUsable { get; set; }
    }

    /// <summary>
    /// Turns the model's JSON into validated scores. Nothing from the response is trusted: ids must
    /// belong to the request, numbers are clamped, unknown words become "unknown", and anything
    /// missing is reported so the caller can fall back to structure-based scoring.
    /// </summary>
    public static class EstimationResponseParser
    {
        public static ParsedEstimation Parse(string responseText, IDictionary<string, string> idToApiName)
        {
            var result = new ParsedEstimation();
            var expected = idToApiName ?? new Dictionary<string, string>();

            JObject root;
            try
            {
                var cleaned = StripCodeFence(responseText);
                root = JToken.Parse(cleaned) as JObject;
            }
            catch (Exception)
            {
                result.Problems.Add("The response was not valid JSON.");
                result.Missing.AddRange(expected.Values);
                return result;
            }

            var items = root?["achievements"] as JArray;
            if (items == null)
            {
                result.Problems.Add("The response had no achievements list.");
                result.Missing.AddRange(expected.Values);
                return result;
            }

            result.IsUsable = true;
            result.Game = ParseGame(root["game"] as JObject);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.OfType<JObject>())
            {
                var id = (string)item["id"];
                if (string.IsNullOrWhiteSpace(id) || !expected.TryGetValue(id, out var apiName))
                {
                    result.Problems.Add("Ignored an entry with an unknown id.");
                    continue;
                }

                if (!seen.Add(id))
                {
                    result.Problems.Add("Ignored a duplicate entry for " + id + ".");
                    continue;
                }

                var difficulty = ReadInt(item["difficulty"]);
                if (difficulty == null)
                {
                    // Without a difficulty there is nothing to score from: treat as missing.
                    result.Problems.Add("Entry " + id + " had no usable difficulty.");
                    continue;
                }

                var seriesId = (string)item["seriesId"];
                result.Scores.Add(new AchievementScore
                {
                    ApiName = apiName,
                    Difficulty = Math.Max(1, Math.Min(5, difficulty.Value)),
                    Kind = ParseKind((string)item["type"]),
                    Phase = ParsePhase((string)item["phase"]),
                    IsCapstone = ReadBool(item["capstone"]),
                    SeriesId = string.IsNullOrWhiteSpace(seriesId) || seriesId.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : seriesId.Trim(),
                    SeriesPosition = Math.Max(0, ReadInt(item["seriesPosition"]) ?? 0),
                    Confidence = ValueScoring.Clamp01(ReadDouble(item["confidence"]) ?? 0.5)
                });
            }

            var covered = new HashSet<string>(result.Scores.Select(s => s.ApiName), StringComparer.OrdinalIgnoreCase);
            result.Missing.AddRange(expected.Values.Where(name => !covered.Contains(name)));
            if (result.Missing.Count > 0)
            {
                result.Problems.Add(result.Missing.Count + " achievement(s) were missing from the response.");
            }

            return result;
        }

        private static GameScoring ParseGame(JObject game)
        {
            var scoring = new GameScoring { OverallConfidence = 0.5 };
            if (game == null) return scoring;

            scoring.OverallConfidence = ValueScoring.Clamp01(ReadDouble(game["overallConfidence"]) ?? 0.5);
            scoring.IsUniformSet = ReadBool(game["uniformSet"]);
            switch (((string)game["profile"] ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "short_easy": scoring.Profile = GameProfile.ShortEasy; break;
                case "long_hard": scoring.Profile = GameProfile.LongHard; break;
                case "completionist_heavy": scoring.Profile = GameProfile.CompletionistHeavy; break;
            }

            foreach (var entry in (game["categoryKinds"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var category = (string)entry["category"];
                if (string.IsNullOrWhiteSpace(category)) continue;

                switch (((string)entry["kind"] ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "base": scoring.CategoryKinds[category] = CategoryKind.Base; break;
                    case "dlc": scoring.CategoryKinds[category] = CategoryKind.Dlc; break;
                    case "update": scoring.CategoryKinds[category] = CategoryKind.Update; break;
                    case "mixed": scoring.CategoryKinds[category] = CategoryKind.Mixed; break;
                }
            }

            return scoring;
        }

        internal static AchievementKind ParseKind(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'))
            {
                case "tutorial": return AchievementKind.Tutorial;
                case "story_milestone": return AchievementKind.StoryMilestone;
                case "boss": return AchievementKind.Boss;
                case "collectible": return AchievementKind.Collectible;
                case "true_ending":
                case "secret_true_ending":
                case "secret": return AchievementKind.TrueEnding;
                case "grind": return AchievementKind.Grind;
                case "optional": return AchievementKind.Optional;
                case "challenge": return AchievementKind.Challenge;
                default: return AchievementKind.Unknown;
            }
        }

        internal static GamePhase ParsePhase(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "early": return GamePhase.Early;
                case "mid": return GamePhase.Mid;
                case "late": return GamePhase.Late;
                default: return GamePhase.Unknown;
            }
        }

        private static string StripCodeFence(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline < 0 || lastFence <= firstNewline) return trimmed;
            return trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)token));
            if (token.Type == JTokenType.Float) return (int)Math.Round((double)token);
            return int.TryParse((string)token, out var parsed) ? parsed : (int?)null;
        }

        private static double? ReadDouble(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return (double)token;
            return double.TryParse((string)token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null;
        }

        private static bool ReadBool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            return bool.TryParse((string)token, out var parsed) && parsed;
        }
    }
}
