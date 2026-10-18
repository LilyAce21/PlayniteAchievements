using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Reads the server-rendered payload of redesigned Steam community pages: the
    /// <c>&lt;script type="application/json" id="valve-ssr-data"&gt;</c> block, whose
    /// renderContext.queryData is a string-encoded TanStack Query cache (one entry per query, each
    /// with queryKey and state.data) and whose loaderData entries are string-encoded JSON objects.
    /// </summary>
    internal static class SteamSsrPayload
    {
        private static readonly Regex ScriptPattern = new Regex(
            @"<script\b[^>]*\bid\s*=\s*[""']valve-ssr-data[""'][^>]*>(?<json>.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public static bool HasData(string html)
        {
            return !string.IsNullOrWhiteSpace(html) &&
                   html.IndexOf("valve-ssr-data", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The payload's root object, or null when the page has none or it is malformed.</summary>
        public static JObject TryParse(string html)
        {
            if (!HasData(html))
            {
                return null;
            }

            try
            {
                var match = ScriptPattern.Match(html);
                return match.Success ? JObject.Parse(match.Groups["json"].Value) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The query cache entries, or null when the payload carries no readable cache.</summary>
        public static List<JObject> ReadQueries(JObject root)
        {
            var queryData = root?["renderContext"]?["queryData"];
            if (queryData == null)
            {
                return null;
            }

            try
            {
                JObject parsed;
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

        /// <summary>The loaderData entries that parse as JSON objects; other entries are skipped.</summary>
        public static IEnumerable<JObject> ReadLoaderItems(JObject root)
        {
            if (!(root?["loaderData"] is JArray items))
            {
                yield break;
            }

            foreach (var item in items)
            {
                JObject obj = null;
                try
                {
                    obj = item.Type == JTokenType.String
                        ? JObject.Parse(item.Value<string>())
                        : item as JObject;
                }
                catch
                {
                    // Not every loader entry is JSON.
                }

                if (obj != null)
                {
                    yield return obj;
                }
            }
        }

        public static string QueryName(JObject query)
        {
            var key = query?["queryKey"];
            return key is JArray array && array.Count > 0
                ? array[0]?.ToString()
                : key?.ToString();
        }

        public static JToken Data(JObject query) => query?["state"]?["data"];
    }
}
