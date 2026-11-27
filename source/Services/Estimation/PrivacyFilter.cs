using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// The last line of defence before anything leaves the machine. Requests are built from an
    /// explicit allowlist of public game-level fields, so player state cannot be added by accident;
    /// this filter then removes anything in the text that looks like a credential or personal
    /// detail, and refuses to send a request whose JSON has a key outside the allowlist.
    /// </summary>
    public static class PrivacyFilter
    {
        public const string Redacted = "[removed]";

        private static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        private static readonly Regex LongToken = new Regex(@"(?<![A-Za-z0-9])[A-Za-z0-9_\-+/=]{32,}(?![A-Za-z0-9])", RegexOptions.Compiled);
        private static readonly Regex Bearer = new Regex(@"\bBearer\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WindowsUserPath = new Regex(@"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SecretAssignment = new Regex(@"\b(password|passwd|token|secret|api[_\- ]?key|npsso|xuid)\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The only JSON keys a request may contain.</summary>
        public static readonly ISet<string> AllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "task", "targetPlatform", "mode", "game", "title", "releaseYear", "genre",
            "achievements", "id", "name", "description", "hidden", "category", "rarityPercent",
            "categories", "anchors", "platform", "grade", "gamerscore",
            "calibration", "sizeBucket", "lists", "bronzeShare", "silverShare", "goldShare", "averagePoints",
            "bySize", "closestLists", "size", "bronze", "silver", "gold", "points", "extras", "pointShare", "trophyShare",
            "gamerscoreBySize", "averageTotal", "medianValue", "spikeShare", "notes", "gameNotes",
            "responseSchema", "difficulty", "type", "phase", "capstone", "seriesId", "seriesPosition", "confidence",
            "profile", "overallConfidence", "uniformSet", "kind", "categoryKinds", "chunk", "chunkCount"
        };

        /// <summary>Cleans one piece of text. Returns the text unchanged when nothing sensitive is found.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var cleaned = text;
            cleaned = Email.Replace(cleaned, Redacted);
            cleaned = Bearer.Replace(cleaned, Redacted);
            cleaned = SecretAssignment.Replace(cleaned, Redacted);
            cleaned = WindowsUserPath.Replace(cleaned, Redacted);
            cleaned = LongToken.Replace(cleaned, Redacted);
            return cleaned;
        }

        /// <summary>True when <see cref="Clean"/> would change the text.</summary>
        public static bool LooksSensitive(string text) => !string.Equals(Clean(text), text, StringComparison.Ordinal);

        /// <summary>Property names in the JSON that are not allowlisted.</summary>
        public static List<string> UnexpectedKeys(string json)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return found;

            Walk(JToken.Parse(json), found);
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Throws when the request holds a key outside the allowlist. Called right before sending.</summary>
        public static void AssertSafe(string json)
        {
            var unexpected = UnexpectedKeys(json);
            if (unexpected.Count > 0)
            {
                throw new InvalidOperationException(
                    "Estimation request blocked: it contains fields outside the allowlist (" + string.Join(", ", unexpected) + ").");
            }
        }

        private static void Walk(JToken token, List<string> found)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (!AllowedKeys.Contains(property.Name)) found.Add(property.Name);
                    Walk(property.Value, found);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) Walk(item, found);
            }
        }
    }
}
