using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>One public achievement as the model sees it. No unlock state, dates, or profile data exist on this type.</summary>
    public sealed class PublicAchievement
    {
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool IsHidden { get; set; }
        public string Category { get; set; }
        public double? RarityPercent { get; set; }
    }

    /// <summary>A native value from the same game, offered as an anchor (either platform).</summary>
    public sealed class NativeAnchor
    {
        public string ApiName { get; set; }

        /// <summary>"psn" or "xbox".</summary>
        public string Platform { get; set; }

        public TrophyGrade? Grade { get; set; }

        public int? Gamerscore { get; set; }
    }

    public sealed class EstimationGameContext
    {
        public string Title { get; set; }
        public int? ReleaseYear { get; set; }
        public string Genre { get; set; }

        /// <summary>"psn" or "xbox": the platform the estimates are for.</summary>
        public string TargetPlatform { get; set; }

        public IList<PublicAchievement> Achievements { get; set; } = new List<PublicAchievement>();
        public IList<NativeAnchor> Anchors { get; set; } = new List<NativeAnchor>();
        public CalibrationProfile Calibration { get; set; }
    }

    public sealed class BuiltEstimationRequest
    {
        public string Json { get; set; }

        /// <summary>Opaque request id back to the real api name. Stays on this machine.</summary>
        public Dictionary<string, string> IdMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; } = 1;
    }

    /// <summary>
    /// Builds the one-request-per-game payload (chunked for large lists). Only public game-level
    /// data goes in, achievements are referred to by opaque ids, and every string passes through
    /// <see cref="PrivacyFilter"/>; the finished JSON is checked against the key allowlist.
    /// </summary>
    public static class EstimationRequestBuilder
    {
        public const int DefaultChunkSize = 100;

        public static List<BuiltEstimationRequest> Build(EstimationGameContext context, int chunkSize = DefaultChunkSize)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var achievements = (context.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();

            // Ids are assigned once for the whole game, so chunks and the final response agree.
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < achievements.Count; i++) ids[achievements[i].ApiName] = "a" + (i + 1).ToString("D4");

            chunkSize = Math.Max(1, chunkSize);
            var chunkCount = Math.Max(1, (int)Math.Ceiling(achievements.Count / (double)chunkSize));
            var requests = new List<BuiltEstimationRequest>();

            for (var chunk = 0; chunk < chunkCount; chunk++)
            {
                var slice = achievements.Skip(chunk * chunkSize).Take(chunkSize).ToList();
                var request = new BuiltEstimationRequest { ChunkIndex = chunk, ChunkCount = chunkCount };
                foreach (var a in slice) request.IdMap[ids[a.ApiName]] = a.ApiName;

                var root = new JObject
                {
                    ["task"] = "estimate_achievement_values",
                    ["targetPlatform"] = context.TargetPlatform,
                    ["game"] = new JObject
                    {
                        ["title"] = PrivacyFilter.Clean(context.Title),
                        ["releaseYear"] = context.ReleaseYear,
                        ["genre"] = PrivacyFilter.Clean(context.Genre)
                    },
                    ["chunk"] = chunk,
                    ["chunkCount"] = chunkCount,
                    ["achievements"] = new JArray(slice.Select(a => AchievementJson(a, ids[a.ApiName])))
                };

                var categories = slice.Select(a => PrivacyFilter.Clean(a.Category))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (categories.Count > 0) root["categories"] = new JArray(categories);

                var anchors = (context.Anchors ?? new List<NativeAnchor>())
                    .Where(a => a != null && a.ApiName != null && ids.ContainsKey(a.ApiName))
                    .Select(a => AnchorJson(a, ids[a.ApiName]))
                    .ToList();
                if (anchors.Count > 0) root["anchors"] = new JArray(anchors);

                if (context.Calibration != null && context.Calibration.Rows.Count > 0)
                {
                    root["calibration"] = new JArray(context.Calibration.Rows.Select(b => new JObject
                    {
                        ["sizeBucket"] = b.SizeBucket,
                        ["lists"] = b.Lists,
                        ["bronzeShare"] = b.BronzeShare,
                        ["silverShare"] = b.SilverShare,
                        ["goldShare"] = b.GoldShare,
                        ["averagePoints"] = b.AveragePoints
                    }));
                }

                request.Json = root.ToString(Formatting.None);
                PrivacyFilter.AssertSafe(request.Json);
                requests.Add(request);
            }

            return requests;
        }

        private static JObject AchievementJson(PublicAchievement a, string id)
        {
            var json = new JObject
            {
                ["id"] = id,
                ["name"] = PrivacyFilter.Clean(a.DisplayName),
                ["description"] = PrivacyFilter.Clean(a.Description),
                ["hidden"] = a.IsHidden
            };

            if (!string.IsNullOrWhiteSpace(a.Category)) json["category"] = PrivacyFilter.Clean(a.Category);
            if (a.RarityPercent.HasValue) json["rarityPercent"] = Math.Round(a.RarityPercent.Value, 1);
            return json;
        }

        private static JObject AnchorJson(NativeAnchor a, string id)
        {
            var json = new JObject { ["id"] = id, ["platform"] = a.Platform };
            if (a.Grade.HasValue) json["grade"] = a.Grade.Value.ToString().ToLowerInvariant();
            if (a.Gamerscore.HasValue) json["gamerscore"] = a.Gamerscore.Value;
            return json;
        }
    }
}
