using System;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>One page of a trophy-set listing, reduced to what a PSN Capstone needs.</summary>
    internal sealed class PsnTrophyPage
    {
        /// <summary>The API's error message when the request failed (for example "not found").</summary>
        public string Error { get; set; }

        public PsnPlatinum Platinum { get; set; }

        /// <summary>Offset of the next page, or null when this was the last one.</summary>
        public int? NextOffset { get; set; }

        public int TrophyCount { get; set; }
    }

    internal static class PsnTrophyResponse
    {
        public static PsnTrophyPage Parse(string json, string npwr, string platform)
        {
            var page = new PsnTrophyPage();
            JObject root;
            try
            {
                root = JObject.Parse(json ?? string.Empty);
            }
            catch (Exception)
            {
                page.Error = "The response was not valid JSON.";
                return page;
            }

            var error = root["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                page.Error = (string)error["message"] ?? "The request failed.";
                return page;
            }

            var trophies = root["trophies"] as JArray;
            if (trophies == null)
            {
                page.Error = "The response had no trophy list.";
                return page;
            }

            page.TrophyCount = trophies.Count;
            foreach (var trophy in trophies)
            {
                if (!string.Equals((string)trophy["trophyType"], "platinum", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                page.Platinum = new PsnPlatinum
                {
                    Npwr = npwr,
                    Platform = platform,
                    TrophyName = ((string)trophy["trophyName"] ?? string.Empty).Trim(),
                    TrophyDetail = ((string)trophy["trophyDetail"] ?? string.Empty).Trim(),
                    IconUrl = ((string)trophy["trophyIconUrl"] ?? string.Empty).Trim()
                };
                break;
            }

            var next = root["nextOffset"];
            if (next != null && next.Type == JTokenType.Integer)
            {
                page.NextOffset = (int)next;
            }

            return page;
        }
    }
}
