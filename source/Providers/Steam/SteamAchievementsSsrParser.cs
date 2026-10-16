using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    internal enum SteamSsrQueryState
    {
        Missing,
        Error,
        Success
    }

    /// <summary>
    /// The data embedded in Steam's achievements page (steamcommunity.com/achievements/{appid},
    /// /profiles/{id}/achievements/{appid} and /id/{vanity}/achievements/{appid}). The page is
    /// server-rendered React whose CSS class names are build hashes, so its markup is not
    /// parsed; the same data travels in the valve-ssr-data script as a dehydrated query cache.
    /// </summary>
    internal sealed class SteamSsrAchievementsPage
    {
        public string Route { get; set; }

        public int AppId { get; set; }

        public bool ViewerLoggedIn { get; set; }

        public string ViewerSteamId { get; set; }

        public string TargetSteamId { get; set; }

        /// <summary>Definitions, sets and global percentages; null when the page carries no schema.</summary>
        public SchemaAndPercentages Schema { get; set; }

        public SteamSsrQueryState UserAchievementsState { get; set; }

        /// <summary>The target player's unlocks; set only when <see cref="UserAchievementsState"/> is Success.</summary>
        public SteamUserAchievements UserAchievements { get; set; }
    }

    internal static class SteamAchievementsSsrParser
    {
        private static readonly Regex SsrScriptPattern = new Regex(
            @"<script\b[^>]*\bid\s*=\s*[""']valve-ssr-data[""'][^>]*>(?<json>.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public static bool HasSsrData(string html)
        {
            return !string.IsNullOrEmpty(html) &&
                   html.IndexOf("valve-ssr-data", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Parses the page, or returns null when it has no readable valve-ssr-data payload or no
        /// achievements data for <paramref name="appId"/>.
        /// </summary>
        public static SteamSsrAchievementsPage Parse(string html, int appId)
        {
            if (!HasSsrData(html) || appId <= 0)
            {
                return null;
            }

            JObject root;
            try
            {
                var match = SsrScriptPattern.Match(html);
                if (!match.Success)
                {
                    return null;
                }

                root = JObject.Parse(match.Groups["json"].Value);
            }
            catch
            {
                return null;
            }

            var page = new SteamSsrAchievementsPage
            {
                Route = root["route"]?.Value<string>(),
                AppId = appId,
                ViewerLoggedIn = root["UserConfig"]?["logged_in"]?.Value<bool?>() == true,
                ViewerSteamId = NormalizeSteamId(root["UserConfig"]?["steamid"]?.ToString())
            };
            page.TargetSteamId = ReadTargetSteamId(root["loaderData"]);

            var queries = ReadQueries(root["renderContext"]?["queryData"]);
            if (queries == null)
            {
                return null;
            }

            JObject schemaQuery = null;
            JObject percentagesQuery = null;
            JObject userQuery = null;
            foreach (var query in queries)
            {
                var key = query["queryKey"] as JArray;
                if (key == null || key.Count < 3 ||
                    !string.Equals(key[0]?.ToString(), "achievements", StringComparison.Ordinal) ||
                    !int.TryParse(key[1]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyAppId) ||
                    keyAppId != appId)
                {
                    continue;
                }

                switch (key[2]?.ToString())
                {
                    case "schema":
                        schemaQuery = query;
                        break;
                    case "globalpercentages":
                        percentagesQuery = query;
                        break;
                    case "user_achievements":
                        // Keyed by target steamid; the page's own target wins when several exist.
                        var keySteamId = key.Count > 3 ? NormalizeSteamId(key[3]?.ToString()) : null;
                        if (userQuery == null ||
                            (page.TargetSteamId != null && string.Equals(keySteamId, page.TargetSteamId, StringComparison.Ordinal)))
                        {
                            userQuery = query;
                        }

                        break;
                }
            }

            if (schemaQuery == null && userQuery == null)
            {
                return null;
            }

            page.Schema = ParseSchema(appId, Data(schemaQuery), Data(percentagesQuery));

            if (userQuery == null)
            {
                page.UserAchievementsState = SteamSsrQueryState.Missing;
            }
            else if (!IsSuccess(userQuery) || !(Data(userQuery) is JObject userData))
            {
                page.UserAchievementsState = SteamSsrQueryState.Error;
            }
            else
            {
                page.UserAchievementsState = SteamSsrQueryState.Success;
                page.UserAchievements = ParseUserAchievements(userData);
                if (page.TargetSteamId == null)
                {
                    page.TargetSteamId = NormalizeSteamId(userData["steamid"]?.ToString());
                }
            }

            return page;
        }

        private static List<JObject> ReadQueries(JToken queryData)
        {
            try
            {
                JObject parsed;
                if (queryData == null)
                {
                    return null;
                }

                if (queryData.Type == JTokenType.String)
                {
                    var text = queryData.Value<string>();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return null;
                    }

                    parsed = JObject.Parse(text);
                }
                else
                {
                    parsed = queryData as JObject;
                }

                return parsed?["queries"]?.OfType<JObject>().ToList();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadTargetSteamId(JToken loaderData)
        {
            if (!(loaderData is JArray items))
            {
                return null;
            }

            foreach (var item in items)
            {
                try
                {
                    var obj = item.Type == JTokenType.String
                        ? JObject.Parse(item.Value<string>())
                        : item as JObject;
                    var target = NormalizeSteamId(obj?["targetSteamId"]?.ToString());
                    if (target != null)
                    {
                        return target;
                    }
                }
                catch
                {
                    // Not every loader entry is JSON; skip it.
                }
            }

            return null;
        }

        private static JToken Data(JObject query) => query?["state"]?["data"];

        private static bool IsSuccess(JObject query) =>
            string.Equals(query?["state"]?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);

        private static SchemaAndPercentages ParseSchema(int appId, JToken schemaData, JToken percentagesData)
        {
            if (!(schemaData?["groups"] is JArray groups))
            {
                return null;
            }

            var percentsByKey = new Dictionary<int, double>();
            if (percentagesData?["percentages"] is JObject percentages)
            {
                foreach (var property in percentages.Properties())
                {
                    if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) &&
                        TryReadDouble(property.Value, out var percent))
                    {
                        percentsByKey[key] = percent;
                    }
                }
            }

            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>(),
                GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                SchemaHash = schemaData["schema_hash"]?.Value<long?>()
            };

            foreach (var group in groups.OfType<JObject>())
            {
                var groupId = group["id"]?.Value<int?>() ?? 0;
                schema.Groups[groupId] = new SchemaAchievementGroup
                {
                    GroupId = groupId,
                    Name = Text(group["name"]),
                    DlcAppId = group["dlcappid"]?.Value<int?>() ?? 0,
                    Archived = group["archived"]?.Value<bool?>() == true,
                    DeveloperOnly = group["developeronly"]?.Value<bool?>() == true,
                    IsPublic = group["ispublic"]?.Value<bool?>() ?? true,
                    Order = group["order"]?.Value<int?>() ?? 0
                };

                foreach (var achievement in (group["achievements"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var apiName = Text(achievement["api_name"]);
                    var internalKey = achievement["internal_key"]?.Value<int?>();
                    schema.Achievements.Add(new SchemaAchievement
                    {
                        Name = apiName,
                        DisplayName = Text(achievement["name"]),
                        Description = Text(achievement["description"]),
                        Icon = SteamApiClient.BuildAchievementIconUrl(appId, Text(achievement["icon_achieved"])),
                        IconGray = SteamApiClient.BuildAchievementIconUrl(appId, Text(achievement["icon_unachieved"])),
                        Hidden = achievement["hidden"]?.Value<bool?>() == true ? 1 : 0,
                        InternalKey = internalKey,
                        GroupId = achievement["groupid"]?.Value<int?>() ?? groupId,
                        Archived = achievement["archived"]?.Value<bool?>() == true
                    });

                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        internalKey.HasValue &&
                        percentsByKey.TryGetValue(internalKey.Value, out var percent))
                    {
                        schema.GlobalPercentages[apiName] = percent;
                    }
                }
            }

            return schema.Achievements.Count > 0 ? schema : null;
        }

        private static SteamUserAchievements ParseUserAchievements(JObject data)
        {
            var result = new SteamUserAchievements
            {
                SchemaHash = data["schema_hash"]?.Value<long?>(),
                Source = "SsrPage"
            };

            // The page stores achievements and groups as objects keyed by id; accept arrays too.
            foreach (var item in EnumerateEntries(data["achievements"]))
            {
                var key = item["internal_key"]?.Value<int?>();
                if (!key.HasValue)
                {
                    continue;
                }

                var progress = item["progress"];
                result.ByInternalKey[key.Value] = new UserAchievement
                {
                    InternalKey = key.Value,
                    Unlocked = item["unlocked"]?.Value<bool?>() == true,
                    UnlockTime = item["unlock_time"]?.Value<long?>(),
                    ProgressInt = item["progress_int"]?.Value<long?>() ??
                                  (progress?.Type == JTokenType.Integer ? progress.Value<long?>() : null),
                    ProgressFloat = item["progress_float"]?.Value<double?>() ??
                                    (progress?.Type == JTokenType.Float ? progress.Value<double?>() : null)
                };
            }

            foreach (var item in EnumerateEntries(data["groups"]))
            {
                var groupId = item["groupid"]?.Value<int?>();
                if (!groupId.HasValue)
                {
                    continue;
                }

                result.Groups[groupId.Value] = new UserAchievementGroup
                {
                    GroupId = groupId.Value,
                    IsAchievable = item["is_achievable"]?.Value<bool?>(),
                    CompletedAchievements = item["completed_achievements"]?.Value<int?>(),
                    IsCompleted = item["is_completed"]?.Value<bool?>(),
                    TimeCompleted = item["time_completed"]?.Value<long?>()
                };
            }

            return result;
        }

        private static IEnumerable<JObject> EnumerateEntries(JToken token)
        {
            if (token is JObject obj)
            {
                return obj.Properties().Select(property => property.Value).OfType<JObject>();
            }

            if (token is JArray array)
            {
                return array.OfType<JObject>();
            }

            return Enumerable.Empty<JObject>();
        }

        private static bool TryReadDouble(JToken token, out double value)
        {
            value = 0;
            if (token == null)
            {
                return false;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
                return true;
            }

            return double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static string Text(JToken token)
        {
            var value = token?.Type == JTokenType.String ? token.Value<string>() : token?.ToString();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeSteamId(string value)
        {
            var trimmed = value?.Trim();
            return !string.IsNullOrEmpty(trimmed) &&
                   trimmed != "0" &&
                   ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? trimmed
                : null;
        }
    }
}
