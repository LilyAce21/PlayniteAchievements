using System;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>
    /// Reads the <c>platinum_data.json</c> that the user's own psn-platinum-gen script writes
    /// (game_name, title_id, name, description, icon_url). Importing it needs no PSN login.
    /// </summary>
    internal static class PsnPlatinumImport
    {
        public const int MaxJsonLength = 64 * 1024;

        public static bool TryParse(string json, out PsnPlatinum platinum)
        {
            platinum = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(json);
                var name = Clean((string)root["name"]);
                if (string.IsNullOrEmpty(name))
                {
                    return false;
                }

                var npwr = Clean((string)root["title_id"]).ToUpperInvariant();
                var iconUrl = Clean((string)root["icon_url"]);
                platinum = new PsnPlatinum
                {
                    Npwr = PsnNpwr.IsValid(npwr) ? npwr : string.Empty,
                    Platform = string.Empty,
                    TrophyName = name,
                    TrophyDetail = Clean((string)root["description"]),
                    IconUrl = PsnIconUrl.IsAllowed(iconUrl) ? iconUrl : string.Empty
                };
                return true;
            }
            catch (Exception)
            {
                platinum = null;
                return false;
            }
        }

        private static string Clean(string value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
