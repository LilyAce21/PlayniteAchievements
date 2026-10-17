using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamCommunityFriend
    {
        public string SteamId { get; set; }
        public string DisplayName { get; set; }
        public string ProviderNickname { get; set; }
        public string AvatarUrl { get; set; }
    }

    internal static class SteamCommunityPageParser
    {
        private static readonly Regex ProfileSteamIdPattern =
            new Regex(@"/profiles/(?<id>\d{17})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AppPathPattern =
            new Regex("(?:^|[/:\"'])apps?/(?<id>\\d+)(?=$|[/?#\"'&<\\s])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool LooksLikeFriendsPayload(string html)
        {
            return !string.IsNullOrWhiteSpace(html) &&
                   html.IndexOf("friends_list", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static List<SteamCommunityFriend> ParseFriends(string html)
        {
            var result = new List<SteamCommunityFriend>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return result;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var nodes = doc.DocumentNode.SelectNodes(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' friend_block_v2 ')]");
            if (nodes == null || nodes.Count == 0)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                var steamId = ExtractFriendSteamId(node);
                if (string.IsNullOrWhiteSpace(steamId) || !seen.Add(steamId))
                {
                    continue;
                }

                ExtractFriendNames(node, out var personaName, out var providerNickname);
                result.Add(new SteamCommunityFriend
                {
                    SteamId = steamId,
                    // When a nickname exists the persona name is absent from the block; leave
                    // DisplayName null so the provider knows to recover it from the profile XML.
                    DisplayName = personaName ?? (providerNickname == null ? steamId : null),
                    ProviderNickname = providerNickname,
                    AvatarUrl = NormalizeText(node.SelectSingleNode(".//img")?.GetAttributeValue("src", null))
                });
            }

            return result;
        }

        public static bool LooksLikeOwnedGamesPayload(string html)
        {
            return !string.IsNullOrWhiteSpace(html) &&
                   (TryExtractXmlPayload(html, "gamesList") != null ||
                    TryExtractXmlPayload(html, "mostPlayedGames") != null ||
                    html.IndexOf("rgGames", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("games_list_rows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("data-app", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("OwnedGames", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("playtime_forever", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    AppPathPattern.IsMatch(html));
        }

        public static List<SteamOwnedGame> ParseOwnedGames(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();

            var fromXml = TryParseOwnedGamesXml(html);
            if (fromXml != null)
            {
                foreach (var game in fromXml)
                {
                    UpsertLargestPlaytime(result, game);
                }
            }

            var ssrRoot = SteamSsrPayload.TryParse(html);
            foreach (var game in ParseOwnedGamesSteamSsr(ssrRoot))
            {
                UpsertLargestPlaytime(result, game);
            }

            // The page renders only its first rows; the SSR owned-games query covers the whole
            // library, so rendered rows only fill gaps when the payload is missing.
            foreach (var game in ParseOwnedGamesHtml(html))
            {
                UpsertLargestPlaytime(result, game);
            }

            // Per-game achievement progress arrives as one AchievementProgress query per game
            // ({appid,unlocked,total,...}) covering the whole library. It is the authoritative,
            // locale-independent source for the "X/Y" hint, so it overrides rendered-row text.
            OverlayAchievementProgressFromSsr(result, ssrRoot);

            return result.Values.ToList();
        }

        private static void OverlayAchievementProgressFromSsr(Dictionary<int, SteamOwnedGame> result, JObject ssrRoot)
        {
            var queries = SteamSsrPayload.ReadQueries(ssrRoot);
            if (result == null || result.Count == 0 || queries == null)
            {
                return;
            }

            foreach (var query in queries)
            {
                AddAchievementProgressFromToken(result, SteamSsrPayload.Data(query));
            }
        }

        private static List<SteamOwnedGame> ParseOwnedGamesSteamSsr(JObject ssrRoot)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            if (ssrRoot == null)
            {
                return new List<SteamOwnedGame>();
            }

            foreach (var game in ParseOwnedGamesFromQueries(ssrRoot))
            {
                UpsertLargestPlaytime(result, game);
            }

            foreach (var game in ParseOwnedGamesFromLoaderData(ssrRoot))
            {
                UpsertLargestPlaytime(result, game);
            }

            return result.Values.ToList();
        }

        private static string ExtractFriendSteamId(HtmlNode node)
        {
            var steamId = NormalizeText(node?.GetAttributeValue("data-steamid", null));
            if (SteamWebAuthSession.NormalizeSteamId64(steamId) != null)
            {
                return steamId;
            }

            var href = NormalizeText(node?.GetAttributeValue("href", null));
            if (string.IsNullOrWhiteSpace(href))
            {
                href = NormalizeText(node?.SelectSingleNode(".//a")?.GetAttributeValue("href", null));
            }

            var match = string.IsNullOrWhiteSpace(href) ? null : ProfileSteamIdPattern.Match(href);
            return match?.Success == true
                ? SteamWebAuthSession.NormalizeSteamId64(match.Groups["id"].Value)
                : null;
        }

        // When the logged-in user has assigned a friend a nickname, the friend block's primary
        // text is the nickname (followed by a player_nickname_hint element) and the persona name
        // is absent from the block entirely; data-search and the avatar alt also carry the
        // nickname, so the fallback chain must not run in that case.
        private static void ExtractFriendNames(HtmlNode node, out string personaName, out string providerNickname)
        {
            personaName = null;
            providerNickname = null;

            var contentNode = node.SelectSingleNode(
                ".//*[contains(concat(' ', normalize-space(@class), ' '), ' friend_block_content ')]");
            var directText = contentNode?.ChildNodes?
                .FirstOrDefault(child => child.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(child.InnerText));

            var nicknameHint = contentNode?.SelectSingleNode(
                ".//*[contains(concat(' ', normalize-space(@class), ' '), ' player_nickname_hint ')]");
            if (nicknameHint != null)
            {
                providerNickname = FirstTextLine(PreviousNonEmptyTextSibling(nicknameHint)?.InnerText) ??
                                   FirstTextLine(directText?.InnerText);
                return;
            }

            personaName = FirstTextLine(directText?.InnerText);
            if (!string.IsNullOrWhiteSpace(personaName))
            {
                return;
            }

            personaName = FirstTextLine(contentNode?.InnerText);
            if (!string.IsNullOrWhiteSpace(personaName))
            {
                return;
            }

            personaName = FirstTextLine(node.GetAttributeValue("data-search", null));
            if (!string.IsNullOrWhiteSpace(personaName))
            {
                return;
            }

            personaName = FirstTextLine(node.SelectSingleNode(".//img")?.GetAttributeValue("alt", null));
        }

        private static HtmlNode PreviousNonEmptyTextSibling(HtmlNode node)
        {
            for (var sibling = node?.PreviousSibling; sibling != null; sibling = sibling.PreviousSibling)
            {
                if (sibling.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(sibling.InnerText))
                {
                    return sibling;
                }

                if (sibling.NodeType == HtmlNodeType.Element)
                {
                    return null;
                }
            }

            return null;
        }

        // Reads the persona name from a profile ?xml=1 payload (<steamID> element; the regex in
        // TryExtractXmlPayloadFromText does not match <steamID64>). Returns null on any failure.
        public static string TryExtractProfilePersonaName(string html)
        {
            var payload = TryExtractXmlPayload(html, "steamID");
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return NormalizeText(XDocument.Parse(payload).Root?.Value);
            }
            catch
            {
                return null;
            }
        }

        // Reads the full-size avatar URL from a profile ?xml=1 payload (<avatarFull> element).
        // Returns null on any failure.
        public static string TryExtractProfileAvatarUrl(string html)
        {
            var payload = TryExtractXmlPayload(html, "avatarFull");
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return NormalizeText(XDocument.Parse(payload).Root?.Value);
            }
            catch
            {
                return null;
            }
        }

        private static List<SteamOwnedGame> TryParseOwnedGamesXml(string html)
        {
            var gamesListXml = TryExtractXmlPayload(html, "gamesList");
            if (!string.IsNullOrWhiteSpace(gamesListXml))
            {
                try
                {
                    var doc = XDocument.Parse(gamesListXml);
                    return doc.Descendants("game")
                        .Select(ToOwnedGame)
                        .Where(game => game != null)
                        .ToList();
                }
                catch
                {
                    return null;
                }
            }

            var mostPlayedXml = TryExtractXmlPayload(html, "mostPlayedGames");
            if (!string.IsNullOrWhiteSpace(mostPlayedXml))
            {
                try
                {
                    var doc = XDocument.Parse(mostPlayedXml);
                    return doc.Descendants("mostPlayedGame")
                        .Select(ToMostPlayedOwnedGame)
                        .Where(game => game != null)
                        .ToList();
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string TryExtractXmlPayload(string html, string rootElementName)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(rootElementName))
            {
                return null;
            }

            var direct = TryExtractXmlPayloadFromText(html, rootElementName);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var decoded = WebUtility.HtmlDecode(html);
            var decodedDirect = TryExtractXmlPayloadFromText(decoded, rootElementName);
            if (!string.IsNullOrWhiteSpace(decodedDirect))
            {
                return decodedDirect;
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var xmlViewer = doc.GetElementbyId("webkit-xml-viewer-source-xml") ??
                                doc.DocumentNode.SelectSingleNode("//*[contains(@class,'webkit-xml-viewer-source-xml')]");
                var text = WebUtility.HtmlDecode(xmlViewer?.InnerText ?? string.Empty);
                return TryExtractXmlPayloadFromText(text, rootElementName);
            }
            catch
            {
                return null;
            }
        }

        private static string TryExtractXmlPayloadFromText(string text, string rootElementName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var startMatch = Regex.Match(
                text,
                $@"<{Regex.Escape(rootElementName)}(?:\s[^>]*)?>",
                RegexOptions.IgnoreCase);
            if (!startMatch.Success)
            {
                return null;
            }

            var endMatch = Regex.Match(
                text.Substring(startMatch.Index),
                $@"</{Regex.Escape(rootElementName)}>",
                RegexOptions.IgnoreCase);
            if (!endMatch.Success)
            {
                return null;
            }

            var endExclusive = startMatch.Index + endMatch.Index + endMatch.Length;
            return text.Substring(startMatch.Index, endExclusive - startMatch.Index);
        }

        private static SteamOwnedGame ToMostPlayedOwnedGame(XElement game)
        {
            var link = game?.Element("gameLink")?.Value;
            var appId = ExtractAppIdFromGameLink(link);
            if (appId <= 0)
            {
                return null;
            }

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(game.Element("gameName")?.Value),
                PlaytimeForever = HoursToMinutes(game.Element("hoursOnRecord")?.Value),
                Playtime2Weeks = NullableHoursToMinutes(game.Element("hoursPlayed")?.Value)
            };
        }

        private static int ExtractAppIdFromGameLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                return 0;
            }

            var match = AppPathPattern.Match(link);
            return match.Success
                ? ParseInt(match.Groups["id"].Value)
                : 0;
        }

        private static SteamOwnedGame ToOwnedGame(XElement game)
        {
            var appId = ParseInt(game?.Element("appID")?.Value);
            if (appId <= 0)
            {
                return null;
            }

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(game.Element("name")?.Value),
                PlaytimeForever = HoursToMinutes(game.Element("hoursOnRecord")?.Value),
                Playtime2Weeks = NullableHoursToMinutes(game.Element("hoursLast2Weeks")?.Value)
            };
        }

        private static List<SteamOwnedGame> ParseOwnedGamesFromQueries(JObject ssrRoot)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            var queries = SteamSsrPayload.ReadQueries(ssrRoot);
            if (queries == null)
            {
                return new List<SteamOwnedGame>();
            }

            try
            {
                foreach (var query in queries)
                {
                    var data = query["state"]?["data"];
                    if (IsOwnedGamesQuery(query["queryKey"]))
                    {
                        AddOwnedGamesFromToken(result, data);
                    }
                }

                foreach (var query in queries)
                {
                    AddAchievementProgressFromToken(result, query["state"]?["data"]);
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static List<SteamOwnedGame> ParseOwnedGamesFromLoaderData(JObject ssrRoot)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            try
            {
                foreach (var itemObject in SteamSsrPayload.ReadLoaderItems(ssrRoot))
                {
                    var listData = itemObject["listData"] as JObject;
                    if (listData == null)
                    {
                        continue;
                    }

                    AddOwnedGamesFromToken(result, listData);
                    AddAchievementProgressFromToken(result, listData);
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static void AddOwnedGamesFromToken(Dictionary<int, SteamOwnedGame> result, JToken token)
        {
            if (result == null || token == null)
            {
                return;
            }

            if (token is JArray array && LooksLikeOwnedGamesArray(array))
            {
                AddGamesFromArray(result, array);
                return;
            }

            foreach (var child in token.Children())
            {
                AddOwnedGamesFromToken(result, child);
            }
        }

        private static void AddAchievementProgressFromToken(Dictionary<int, SteamOwnedGame> result, JToken token)
        {
            if (result == null || token == null)
            {
                return;
            }

            if (token is JArray array && LooksLikeAchievementProgressArray(array))
            {
                foreach (var item in array.OfType<JObject>())
                {
                    UpsertAchievementProgress(result, item);
                }

                return;
            }

            // A standalone progress record (one React Query entry per game on the modern page).
            if (token is JObject obj && IsAchievementProgressObject(obj))
            {
                UpsertAchievementProgress(result, obj);
                return;
            }

            foreach (var child in token.Children())
            {
                AddAchievementProgressFromToken(result, child);
            }
        }

        private static bool IsAchievementProgressObject(JObject item)
        {
            return item != null &&
                   item["appid"] != null &&
                   item["total"] != null &&
                   item["unlocked"] != null;
        }

        private static void AddGamesFromArray(Dictionary<int, SteamOwnedGame> result, JArray games)
        {
            if (result == null || games == null)
            {
                return;
            }

            foreach (var gameToken in games)
            {
                var game = ToOwnedGame(gameToken as JObject);
                if (game != null)
                {
                    UpsertLargestPlaytime(result, game);
                }
            }
        }

        private static bool LooksLikeOwnedGamesArray(JArray array)
        {
            return array != null &&
                   array.OfType<JObject>().Any(item =>
                       item["appid"] != null &&
                       (item["playtime_forever"] != null ||
                        item["playtime_2weeks"] != null ||
                        item["has_community_visible_stats"] != null));
        }

        private static bool LooksLikeAchievementProgressArray(JArray array)
        {
            return array != null &&
                   array.OfType<JObject>().Any(item =>
                       item["appid"] != null &&
                       item["total"] != null &&
                       item["unlocked"] != null);
        }

        private static bool IsOwnedGamesQuery(JToken queryKey)
        {
            var array = queryKey as JArray;
            if (array != null && array.Count > 0)
            {
                return string.Equals(array[0]?.Value<string>(), "OwnedGames", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(queryKey?.Value<string>(), "OwnedGames", StringComparison.OrdinalIgnoreCase);
        }

        private static SteamOwnedGame ToOwnedGame(JObject game)
        {
            var appId = ReadInt(game?["appid"]) ?? ReadInt(game?["appID"]) ?? 0;
            if (appId <= 0)
            {
                return null;
            }

            var playtimeForever = ReadInt(game["playtime_forever"]) ??
                                  ReadInt(game["playtimeForever"]) ??
                                  0;
            var playtime2Weeks = ReadInt(game["playtime_2weeks"]) ??
                                 ReadInt(game["playtime2Weeks"]);

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(FirstNonEmpty(
                    game["name"]?.Value<string>(),
                    game["display_name"]?.Value<string>(),
                    game["title"]?.Value<string>())),
                PlaytimeForever = Math.Max(0, playtimeForever),
                Playtime2Weeks = playtime2Weeks.HasValue ? Math.Max(0, playtime2Weeks.Value) : (int?)null,
                LastPlayedUnixSeconds = ReadLong(game["rtime_last_played"]) ?? ReadLong(game["last_played"]),
                AchievementsEarned = ReadInt(game["achievements_unlocked"]) ?? ReadInt(game["unlocked"]),
                AchievementsTotal = ReadInt(game["achievements_total"]) ?? ReadInt(game["total_achievements"]) ?? ReadInt(game["total"])
            };
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (token.Type == JTokenType.Float)
            {
                return (int)Math.Round(token.Value<decimal>(), MidpointRounding.AwayFromZero);
            }

            return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private static long? ReadLong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }

            return long.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null;
        }

        private static List<SteamOwnedGame> ParseOwnedGamesHtml(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            if (string.IsNullOrWhiteSpace(html) ||
                (html.IndexOf("data-app", StringComparison.OrdinalIgnoreCase) < 0 &&
                 !AppPathPattern.IsMatch(html)))
            {
                return new List<SteamOwnedGame>();
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var appNodes = doc.DocumentNode.SelectNodes(
                    "//*[@href or @src or @data-ds-appid or @data-appid or @data-app-id]");
                if (appNodes == null)
                {
                    return new List<SteamOwnedGame>();
                }

                foreach (var node in appNodes)
                {
                    var appId = ExtractAppIdFromNode(node);
                    if (appId <= 0)
                    {
                        continue;
                    }

                    var row = FindLikelyGameRow(node);
                    var rowText = NormalizeWhitespace(WebUtility.HtmlDecode(row?.InnerText ?? node.ParentNode?.InnerText ?? string.Empty));
                    var playtime = ExtractPlaytimeMinutes(rowText);
                    var (achievementsEarned, achievementsTotal) = ExtractAchievementCounts(row);
                    UpsertLargestPlaytime(result, new SteamOwnedGame
                    {
                        AppId = appId,
                        Name = ExtractGameNameFromNode(node, row),
                        PlaytimeForever = playtime ?? 0,
                        AchievementsEarned = achievementsEarned,
                        AchievementsTotal = achievementsTotal
                    });
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static int ExtractAppIdFromNode(HtmlNode node)
        {
            if (node == null)
            {
                return 0;
            }

            foreach (var attributeName in new[] { "data-ds-appid", "data-appid", "data-app-id" })
            {
                var appId = ParseInt(node.GetAttributeValue(attributeName, null));
                if (appId > 0)
                {
                    return appId;
                }
            }

            foreach (var attributeName in new[] { "href", "src" })
            {
                var appId = ExtractAppIdFromGameLink(node.GetAttributeValue(attributeName, null));
                if (appId > 0)
                {
                    return appId;
                }
            }

            return 0;
        }

        private static HtmlNode FindLikelyGameRow(HtmlNode link)
        {
            var node = link;
            for (var depth = 0; node != null && depth < 8; depth++, node = node.ParentNode)
            {
                var className = node.GetAttributeValue("class", string.Empty);
                var id = node.GetAttributeValue("id", string.Empty);
                var text = node.InnerText ?? string.Empty;
                if (className.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    className.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.StartsWith("game_", StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return node;
                }
            }

            node = link;
            for (var depth = 0; node != null && depth < 8; depth++, node = node.ParentNode)
            {
                var className = node.GetAttributeValue("class", string.Empty);
                var id = node.GetAttributeValue("id", string.Empty);
                if (className.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    className.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.StartsWith("game_", StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }

            return link.ParentNode;
        }

        // Time-unit words for the localized "hours played" cell on scraped community pages. Covers
        // the common Steam client languages; a bare "h" catches abbreviated forms like "12h".
        private const string HourUnitPattern = @"hours?|hrs?|heures?|stunden|std\.?|horas?|ore|uur|timer|hodin\w*|h\b";

        private static int? ExtractPlaytimeMinutes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var totalMatch = Regex.Match(
                text,
                @"TOTAL\s+PLAYED\s*(?<hours>[\d.,]+)\s*(?:" + HourUnitPattern + @")",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
            {
                return NullableHoursToMinutes(totalMatch.Groups["hours"].Value);
            }

            var genericMatch = Regex.Match(
                text,
                @"(?<hours>[\d.,]+)\s*(?:" + HourUnitPattern + @")",
                RegexOptions.IgnoreCase);
            return genericMatch.Success
                ? NullableHoursToMinutes(genericMatch.Groups["hours"].Value)
                : null;
        }

        // Reads the "earned/total" achievement count from a games-page row. The React page renders
        // it as an ACHIEVEMENTS link followed by a sibling "11/17" span; scope the search to the
        // achievements anchor's container to avoid picking up unrelated numbers, falling back to the
        // whole row. Returns (null, null) when the row exposes no achievement progress.
        private static (int? Earned, int? Total) ExtractAchievementCounts(HtmlNode row)
        {
            if (row == null)
            {
                return (null, null);
            }

            var anchor = row.SelectSingleNode(".//a[contains(@href, '/achievements/')]");
            var scope = anchor?.ParentNode ?? row;
            var text = WebUtility.HtmlDecode(scope.InnerText ?? string.Empty);

            var match = Regex.Match(text, @"(\d+)\s*/\s*(\d+)");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var earned) &&
                int.TryParse(match.Groups[2].Value, out var total))
            {
                return (Math.Max(0, earned), Math.Max(0, total));
            }

            return (null, null);
        }

        private static string NormalizeWhitespace(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Regex.Replace(value, @"\s+", " ").Trim();
        }

        private static void UpsertLargestPlaytime(Dictionary<int, SteamOwnedGame> target, SteamOwnedGame game)
        {
            if (target == null || game == null || game.AppId <= 0)
            {
                return;
            }

            if (!target.TryGetValue(game.AppId, out var existing))
            {
                target[game.AppId] = game;
                return;
            }

            var primary = game.PlaytimeForever > existing.PlaytimeForever ? game : existing;
            var secondary = ReferenceEquals(primary, game) ? existing : game;
            target[game.AppId] = new SteamOwnedGame
            {
                AppId = primary.AppId,
                Name = FirstNonEmpty(primary.Name, secondary.Name),
                PlaytimeForever = Math.Max(0, primary.PlaytimeForever),
                Playtime2Weeks = primary.Playtime2Weeks ?? secondary.Playtime2Weeks,
                LastPlayedUnixSeconds = primary.LastPlayedUnixSeconds ?? secondary.LastPlayedUnixSeconds,
                // The achievement hint is sourced from the rendered HTML row, which is often a
                // different source than the playtime-bearing list entry; coalesce so it is not
                // dropped when rows for the same app are merged.
                AchievementsEarned = primary.AchievementsEarned ?? secondary.AchievementsEarned,
                AchievementsTotal = primary.AchievementsTotal ?? secondary.AchievementsTotal
            };
        }

        private static void UpsertAchievementProgress(Dictionary<int, SteamOwnedGame> target, JObject progress)
        {
            var appId = ReadInt(progress?["appid"]) ?? 0;
            if (target == null || appId <= 0)
            {
                return;
            }

            var earned = ReadInt(progress["unlocked"]);
            var total = ReadInt(progress["total"]);
            if (!earned.HasValue || !total.HasValue)
            {
                return;
            }

            if (!target.TryGetValue(appId, out var existing))
            {
                return;
            }

            target[appId] = new SteamOwnedGame
            {
                AppId = existing.AppId,
                Name = existing.Name,
                PlaytimeForever = Math.Max(0, existing.PlaytimeForever),
                Playtime2Weeks = existing.Playtime2Weeks,
                LastPlayedUnixSeconds = existing.LastPlayedUnixSeconds,
                AchievementsEarned = existing.AchievementsEarned ?? Math.Max(0, earned.Value),
                AchievementsTotal = existing.AchievementsTotal ?? Math.Max(0, total.Value)
            };
        }

        private static string ExtractGameNameFromNode(HtmlNode node, HtmlNode row)
        {
            var direct = FirstNonEmpty(
                node?.GetAttributeValue("title", null),
                node?.GetAttributeValue("alt", null),
                node?.SelectSingleNode(".//img")?.GetAttributeValue("alt", null));
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return NormalizeWhitespace(WebUtility.HtmlDecode(direct));
            }

            var linkText = FirstTextLine(node?.InnerText);
            if (!string.IsNullOrWhiteSpace(linkText) &&
                linkText.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase) < 0 &&
                linkText.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return linkText;
            }

            var text = NormalizeWhitespace(WebUtility.HtmlDecode(row?.InnerText ?? string.Empty));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var totalIndex = text.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase);
            if (totalIndex > 0)
            {
                return text.Substring(0, totalIndex).Trim();
            }

            var achievementsIndex = text.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase);
            return achievementsIndex > 0
                ? text.Substring(0, achievementsIndex).Trim()
                : null;
        }

        private static string FirstTextLine(string value)
        {
            var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return null;
            }

            return decoded
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeText)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : WebUtility.HtmlDecode(value).Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static int HoursToMinutes(string value)
        {
            return NullableHoursToMinutes(value) ?? 0;
        }

        private static int? NullableHoursToMinutes(string value)
        {
            var normalized = NormalizeNumericString(value);
            if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
            {
                return null;
            }

            return Math.Max(0, (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Normalizes a locale-formatted number to an invariant "1234.5" form. Handles comma
        /// decimals (French "12,5"), NBSP/space thousands separators ("1 234,5"), and mixed
        /// dot/comma grouping ("1.234,5" / "1,234.5"). The right-most separator is treated as the
        /// decimal point when both a dot and comma are present; a lone comma is a decimal separator
        /// only when it looks like a fractional part (1-2 trailing digits), otherwise it is a
        /// thousands grouping.
        /// </summary>
        private static string NormalizeNumericString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            // Space, non-breaking space, and narrow no-break space are thousands separators.
            var cleaned = value.Trim()
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty);

            var hasDot = cleaned.IndexOf('.') >= 0;
            var hasComma = cleaned.IndexOf(',') >= 0;

            if (hasDot && hasComma)
            {
                if (cleaned.LastIndexOf('.') > cleaned.LastIndexOf(','))
                {
                    cleaned = cleaned.Replace(",", string.Empty);
                }
                else
                {
                    cleaned = cleaned.Replace(".", string.Empty).Replace(',', '.');
                }
            }
            else if (hasComma)
            {
                var firstComma = cleaned.IndexOf(',');
                var lastComma = cleaned.LastIndexOf(',');
                var trailing = cleaned.Length - lastComma - 1;
                if (firstComma == lastComma && trailing >= 1 && trailing <= 2)
                {
                    cleaned = cleaned.Replace(',', '.');
                }
                else
                {
                    cleaned = cleaned.Replace(",", string.Empty);
                }
            }

            return cleaned;
        }

    }
}
