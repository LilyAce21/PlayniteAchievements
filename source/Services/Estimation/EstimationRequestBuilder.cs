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

        /// <summary>
        /// False for an achievement that already has every value being generated (native trophy and points). It still goes to
        /// the model as an anchor, but research is never spent on it and it is not counted in the evidence totals.
        /// </summary>
        public bool NeedsValues { get; set; } = true;

        /// <summary>What web research found about this achievement. Added by the research step; null when none.</summary>
        public string ResearchNote { get; set; }
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

        public static List<BuiltEstimationRequest> Build(
            EstimationGameContext context,
            int chunkSize = DefaultChunkSize,
            IDictionary<string, string> researchNotesById = null,
            string gameNotes = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var achievements = (context.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();

            // Ids are assigned once for the whole game, so chunks and the final response agree.
            var ids = AssignIds(achievements);

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
                    ["achievements"] = new JArray(slice.Select(a => AchievementJson(a, ids[a.ApiName],
                        researchNotesById != null && researchNotesById.TryGetValue(ids[a.ApiName], out var note) ? note : null)))
                };

                if (!string.IsNullOrWhiteSpace(gameNotes)) ((JObject)root["game"])["gameNotes"] = PrivacyFilter.Clean(gameNotes);

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

                var calibration = CalibrationJson(context.Calibration, achievements.Count);
                if (calibration != null) root["calibration"] = calibration;

                request.Json = root.ToString(Formatting.None);
                PrivacyFilter.AssertSafe(request.Json);
                requests.Add(request);
            }

            return requests;
        }

        /// <summary>
        /// The calibration block: summary statistics only. The closest real lists are anonymous (size and
        /// grade counts), so no title from the user's library is ever sent.
        /// </summary>
        private static JObject CalibrationJson(CalibrationProfile profile, int listSize)
        {
            if (profile == null || profile.IsEmpty) return null;

            var json = new JObject();
            if (profile.Rows.Count > 0)
            {
                json["bySize"] = new JArray(profile.Rows.Select(b => new JObject
                {
                    ["sizeBucket"] = b.SizeBucket,
                    ["lists"] = b.Lists,
                    ["bronzeShare"] = b.BronzeShare,
                    ["silverShare"] = b.SilverShare,
                    ["goldShare"] = b.GoldShare,
                    ["averagePoints"] = b.AveragePoints
                }));
            }

            var closest = profile.ClosestLists(listSize, 3);
            if (closest.Count > 0)
            {
                json["closestLists"] = new JArray(closest.Select(l => new JObject
                {
                    ["size"] = l.Size,
                    ["bronze"] = l.Bronze,
                    ["silver"] = l.Silver,
                    ["gold"] = l.Gold,
                    ["points"] = l.Points
                }));
            }

            if (profile.Extras != null)
            {
                json["extras"] = new JObject
                {
                    ["lists"] = profile.Extras.Lists,
                    ["pointShare"] = profile.Extras.AveragePointShare,
                    ["trophyShare"] = profile.Extras.AverageTrophyShare
                };
            }

            if (profile.GamerscoreRows.Count > 0)
            {
                json["gamerscoreBySize"] = new JArray(profile.GamerscoreRows.Select(b => new JObject
                {
                    ["sizeBucket"] = b.SizeBucket,
                    ["lists"] = b.Lists,
                    ["averageTotal"] = b.AverageTotal,
                    ["medianValue"] = b.MedianValue,
                    ["spikeShare"] = b.SpikeShare
                }));
            }

            return json;
        }

        /// <summary>The opaque id of each achievement (api name to "a0001"), the same in every request for a game.</summary>
        public static Dictionary<string, string> IdsByApiName(EstimationGameContext context)
        {
            var all = (context?.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();
            return AssignIds(all);
        }

        private static Dictionary<string, string> AssignIds(IList<PublicAchievement> achievements)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < achievements.Count; i++) ids[achievements[i].ApiName] = "a" + (i + 1).ToString("D4");
            return ids;
        }

        private static JObject AchievementJson(PublicAchievement a, string id, string researchNote = null, bool includeRarity = true)
        {
            var json = new JObject
            {
                ["id"] = id,
                ["name"] = PrivacyFilter.Clean(a.DisplayName),
                ["description"] = PrivacyFilter.Clean(a.Description),
                ["hidden"] = a.IsHidden
            };

            if (!string.IsNullOrWhiteSpace(a.Category)) json["category"] = PrivacyFilter.Clean(a.Category);
            if (includeRarity && a.RarityPercent.HasValue) json["rarityPercent"] = Math.Round(a.RarityPercent.Value, 1);
            if (!string.IsNullOrWhiteSpace(researchNote)) json["notes"] = PrivacyFilter.Clean(researchNote);
            return json;
        }

        /// <summary>
        /// The requests for the research step: small batches so the model can look at every achievement, with the
        /// same opaque ids as the scoring requests. Rarity is left out on purpose, so it cannot colour what the
        /// research finds. <paramref name="onlyApiNames"/> limits the batches to some achievements (a retry).
        /// </summary>
        public static List<BuiltEstimationRequest> BuildResearch(
            EstimationGameContext context,
            int batchSize,
            ISet<string> onlyApiNames = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var all = (context.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();

            // The same id scheme as Build, so a note found here belongs to the same achievement there.
            var ids = AssignIds(all);

            var wanted = all.Where(a => onlyApiNames == null || onlyApiNames.Contains(a.ApiName)).ToList();
            batchSize = Math.Max(1, batchSize);
            var batchCount = Math.Max(1, (int)Math.Ceiling(wanted.Count / (double)batchSize));
            var requests = new List<BuiltEstimationRequest>();

            for (var batch = 0; batch < batchCount && wanted.Count > 0; batch++)
            {
                var slice = wanted.Skip(batch * batchSize).Take(batchSize).ToList();
                var request = new BuiltEstimationRequest { ChunkIndex = batch, ChunkCount = batchCount };
                foreach (var a in slice) request.IdMap[ids[a.ApiName]] = a.ApiName;

                var root = new JObject
                {
                    ["task"] = "research_achievements",
                    ["game"] = new JObject
                    {
                        ["title"] = PrivacyFilter.Clean(context.Title),
                        ["releaseYear"] = context.ReleaseYear,
                        ["genre"] = PrivacyFilter.Clean(context.Genre)
                    },
                    ["chunk"] = batch,
                    ["chunkCount"] = batchCount,
                    ["achievements"] = new JArray(slice.Select(a => AchievementJson(a, ids[a.ApiName], null, includeRarity: false)))
                };

                request.Json = root.ToString(Formatting.None);
                PrivacyFilter.AssertSafe(request.Json);
                requests.Add(request);
            }

            return requests;
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
