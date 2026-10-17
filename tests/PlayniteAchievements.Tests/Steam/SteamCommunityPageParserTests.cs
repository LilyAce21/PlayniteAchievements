using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Steam;
using System.Linq;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamCommunityPageParserTests
    {
        [TestMethod]
        public void ParseFriends_MapsSteamIdNameAndAvatar()
        {
            const string html =
                "<div id=\"friends_list\">" +
                "<a class=\"selectable friend_block_v2 persona online\" data-steamid=\"76561198000000001\" data-search=\"Fallback Name\" href=\"https://steamcommunity.com/profiles/76561198000000001/\">" +
                "<div class=\"player_avatar\"><img src=\"https://avatars.example/avatar.jpg\" /></div>" +
                "<div class=\"friend_block_content\">Display Name<br><span class=\"friend_small_text\">Online</span></div>" +
                "</a></div>";

            var friends = SteamCommunityPageParser.ParseFriends(html);

            Assert.AreEqual(1, friends.Count);
            Assert.AreEqual("76561198000000001", friends[0].SteamId);
            Assert.AreEqual("Display Name", friends[0].DisplayName);
            Assert.IsNull(friends[0].ProviderNickname);
            Assert.AreEqual("https://avatars.example/avatar.jpg", friends[0].AvatarUrl);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeFriendsPayload(html));
        }

        [TestMethod]
        public void ParseFriends_TreatsPrimaryTextAsNicknameWhenNicknameHintPresent()
        {
            // A nicknamed friend's block renders the assigned nickname as the primary text and
            // omits the persona name entirely; data-search and the avatar alt also carry the
            // nickname, so none of the fallbacks may be consulted.
            const string html =
                "<div id=\"friends_list\">" +
                "<a class=\"selectable friend_block_v2 persona online\" data-steamid=\"76561198000000002\" data-search=\"My Nick\" href=\"https://steamcommunity.com/profiles/76561198000000002/\">" +
                "<div class=\"player_avatar\"><img src=\"https://avatars.example/avatar2.jpg\" alt=\"My Nick\" /></div>" +
                "<div class=\"friend_block_content\">My Nick<span class=\"player_nickname_hint\"><br><span class=\"friend_small_text\">Last Online 3 days ago</span></span></div>" +
                "</a></div>";

            var friends = SteamCommunityPageParser.ParseFriends(html);

            Assert.AreEqual(1, friends.Count);
            Assert.AreEqual("My Nick", friends[0].ProviderNickname);
            Assert.IsNull(friends[0].DisplayName);
        }

        [TestMethod]
        public void TryExtractProfilePersonaName_ReadsCdataSteamIdElement()
        {
            const string xml =
                "<profile><steamID64>76561198000000002</steamID64>" +
                "<steamID><![CDATA[Persona Name]]></steamID></profile>";

            Assert.AreEqual("Persona Name", SteamCommunityPageParser.TryExtractProfilePersonaName(xml));
        }

        [TestMethod]
        public void TryExtractProfilePersonaName_ReadsEscapedXmlViewerPayload()
        {
            const string html =
                "<html><body><div id=\"webkit-xml-viewer-source-xml\">" +
                "&lt;profile&gt;&lt;steamID64&gt;76561198000000002&lt;/steamID64&gt;" +
                "&lt;steamID&gt;Persona Name&lt;/steamID&gt;&lt;/profile&gt;" +
                "</div></body></html>";

            Assert.AreEqual("Persona Name", SteamCommunityPageParser.TryExtractProfilePersonaName(html));
        }

        [TestMethod]
        public void TryExtractProfileAvatarUrl_ReadsCdataAvatarFullElement()
        {
            const string xml =
                "<profile><steamID64>76561198000000002</steamID64>" +
                "<avatarIcon><![CDATA[https://avatars.example/small.jpg]]></avatarIcon>" +
                "<avatarFull><![CDATA[https://avatars.example/full.jpg]]></avatarFull></profile>";

            Assert.AreEqual(
                "https://avatars.example/full.jpg",
                SteamCommunityPageParser.TryExtractProfileAvatarUrl(xml));
        }

        [TestMethod]
        public void TryExtractProfileAvatarUrl_ReadsEscapedXmlViewerPayload()
        {
            const string html =
                "<html><body><div id=\"webkit-xml-viewer-source-xml\">" +
                "&lt;profile&gt;&lt;avatarFull&gt;https://avatars.example/full.jpg&lt;/avatarFull&gt;&lt;/profile&gt;" +
                "</div></body></html>";

            Assert.AreEqual(
                "https://avatars.example/full.jpg",
                SteamCommunityPageParser.TryExtractProfileAvatarUrl(html));
        }

        [TestMethod]
        public void TryExtractProfileAvatarUrl_ReturnsNullWhenAbsent()
        {
            const string xml = "<profile><steamID64>76561198000000002</steamID64></profile>";

            Assert.IsNull(SteamCommunityPageParser.TryExtractProfileAvatarUrl(xml));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsXmlPayload()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>1.5</hoursOnRecord><hoursLast2Weeks>0.5</hoursLast2Weeks></game>" +
                "</games></gamesList>";

            var games = SteamCommunityPageParser.ParseOwnedGames(xml);

            Assert.AreEqual(1, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual("Team Fortress 2", games[0].Name);
            Assert.AreEqual(90, games[0].PlaytimeForever);
            Assert.AreEqual(30, games[0].Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(xml));
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesAchievementProgressFromGamesPageRow()
        {
            // Mirrors the community games page row: a store/app link plus an ACHIEVEMENTS link with a
            // sibling "11/17" progress count. The count feeds the friend refresh unlock hint.
            const string html =
                "<div class=\"gameListRow\">" +
                "<a href=\"https://store.steampowered.com/app/431960\">Slay the Spire</a>" +
                "<div>" +
                "<a href=\"https://steamcommunity.com/profiles/76561198000000001/achievements/431960\">ACHIEVEMENTS</a>" +
                "<span>11/17</span>" +
                "</div>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(431960, game.AppId);
            Assert.AreEqual(11, game.AchievementsEarned);
            Assert.AreEqual(17, game.AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsEscapedXmlViewerPayload()
        {
            const string html =
                "<html><body><div id=\"webkit-xml-viewer-source-xml\">" +
                "&lt;gamesList&gt;&lt;games&gt;" +
                "&lt;game&gt;&lt;appID&gt;440&lt;/appID&gt;&lt;hoursOnRecord&gt;2.0&lt;/hoursOnRecord&gt;&lt;/game&gt;" +
                "&lt;/games&gt;&lt;/gamesList&gt;" +
                "</div></body></html>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(120, game.PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsMostPlayedProfileXmlPayload()
        {
            const string xml =
                "<profile><mostPlayedGames><mostPlayedGame>" +
                "<gameLink><![CDATA[https://steamcommunity.com/app/730]]></gameLink>" +
                "<hoursPlayed>3.0</hoursPlayed><hoursOnRecord>3,222</hoursOnRecord>" +
                "</mostPlayedGame></mostPlayedGames></profile>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(730, game.AppId);
            Assert.AreEqual(193320, game.PlaytimeForever);
            Assert.AreEqual(180, game.Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(xml));
        }


        [TestMethod]
        public void ParseOwnedGames_MapsSteamSsrOwnedGamesQuery()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":[{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":156469}," +
                "{\"appid\":550,\"name\":\"Left 4 Dead 2\",\"playtime_forever\":281,\"playtime_2weeks\":12,\"rtime_last_played\":1710000000}]}}]}";
            var html = SsrPage(queryData);

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual("Left 4 Dead 2", games[0].Name);
            Assert.AreEqual(281, games[0].PlaytimeForever);
            Assert.AreEqual(12, games[0].Playtime2Weeks);
            Assert.AreEqual(1710000000L, games[0].LastPlayedUnixSeconds);
            Assert.AreEqual(730, games[1].AppId);
            Assert.AreEqual("Counter-Strike 2", games[1].Name);
            Assert.AreEqual(156469, games[1].PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MergesSteamSsrRenderContextAndLoaderData()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":[{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":156469}]}}]}";
            const string listData =
                "{\"listData\":{\"rgGames\":[{\"appid\":550,\"name\":\"Left 4 Dead 2\",\"playtime_forever\":281}]}}";
            var html = SsrPage(queryData, listData);

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual(730, games[1].AppId);
        }

        [TestMethod]
        public void ParseOwnedGames_MapsNestedSteamSsrOwnedGamesAndAchievementProgress()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[" +
                "{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":{\"response\":{\"game_count\":2,\"games\":[" +
                "{\"appid\":440,\"name\":\"Team Fortress 2\",\"playtime_forever\":90}," +
                "{\"appid\":570,\"name\":\"Dota 2\",\"playtime_forever\":180}]}}}}," +
                "{\"queryKey\":[\"AchievementsProgress\",\"76561198087595485\"]," +
                "\"state\":{\"data\":{\"achievement_progress\":[" +
                "{\"appid\":440,\"unlocked\":0,\"total\":520}," +
                "{\"appid\":570,\"unlocked\":14,\"total\":100}]}}}]}";
            var html = SsrPage(queryData);

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual(0, games[0].AchievementsEarned);
            Assert.AreEqual(520, games[0].AchievementsTotal);
            Assert.AreEqual(570, games[1].AppId);
            Assert.AreEqual(14, games[1].AchievementsEarned);
            Assert.AreEqual(100, games[1].AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsSteamSsrLoaderDataRecentlyPlayedFallback()
        {
            const string listData =
                "{\"listData\":{\"rgRecentlyPlayedGames\":[{\"appid\":2862420,\"name\":\"Tyr Playtest\"," +
                "\"playtime_forever\":111,\"playtime_2weeks\":111}]}}";
            var html = SsrPage(null, "{}", listData);

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(2862420, game.AppId);
            Assert.AreEqual("Tyr Playtest", game.Name);
            Assert.AreEqual(111, game.PlaytimeForever);
            Assert.AreEqual(111, game.Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MergesPartialSsrWithRenderedRows()
        {
            const string listData =
                "{\"listData\":{\"rgRecentlyPlayedGames\":[{\"appid\":2862420,\"name\":\"Tyr Playtest\"," +
                "\"playtime_forever\":111,\"playtime_2weeks\":111}]}}";
            var html = SsrPage(null, "{}", listData) +
                       "<div class=\"Panel\">" +
                       "<a href=\"https://store.steampowered.com/app/730\">Counter-Strike 2</a>" +
                       "<span>TOTAL PLAYED</span><span>2,607.8 hours</span>" +
                       "<a href=\"https://steamcommunity.com/id/test/achievements/730\">ACHIEVEMENTS</a>" +
                       "</div>" +
                       "<div class=\"Panel\">" +
                       "<a href=\"https://store.steampowered.com/app/550\">Left 4 Dead 2</a>" +
                       "<span>TOTAL PLAYED</span><span>5 hours</span>" +
                       "</div>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(3, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual(300, games[0].PlaytimeForever);
            Assert.AreEqual(730, games[1].AppId);
            Assert.AreEqual(156468, games[1].PlaytimeForever);
            Assert.AreEqual(2862420, games[2].AppId);
            Assert.AreEqual(111, games[2].PlaytimeForever);
            Assert.AreEqual(111, games[2].Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsModernHtmlRows()
        {
            const string html =
                "<div class=\"gameslistitems_GameListItem_abc\">" +
                "<a href=\"https://steamcommunity.com/app/72850\">The Elder Scrolls V: Skyrim</a>" +
                "<div>TOTAL PLAYED</div><div>1,549 hours</div>" +
                "<div>ACHIEVEMENTS</div><div>32/75</div>" +
                "</div>" +
                "<div class=\"gameslistitems_GameListItem_def\">" +
                "<a href=\"https://steamcommunity.com/app/292030/\">The Witcher 3: Wild Hunt</a>" +
                "<div>TOTAL PLAYED</div><div>476 hours</div>" +
                "</div>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(72850, games[0].AppId);
            Assert.AreEqual("The Elder Scrolls V: Skyrim", games[0].Name);
            Assert.AreEqual(92940, games[0].PlaytimeForever);
            Assert.AreEqual(32, games[0].AchievementsEarned);
            Assert.AreEqual(75, games[0].AchievementsTotal);
            Assert.AreEqual(292030, games[1].AppId);
            Assert.AreEqual("The Witcher 3: Wild Hunt", games[1].Name);
            Assert.AreEqual(28560, games[1].PlaytimeForever);
            Assert.IsFalse(games[1].AchievementsEarned.HasValue);
            Assert.IsFalse(games[1].AchievementsTotal.HasValue);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesFrenchCommaDecimalHoursOnRecord()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>12,5</hoursOnRecord></game>" +
                "</games></gamesList>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(750, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesSpaceThousandsWithCommaDecimalHoursOnRecord()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>1 234,5</hoursOnRecord></game>" +
                "</games></gamesList>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(74070, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesFrenchHtmlHoursUnit()
        {
            const string html =
                "<div class=\"gameslistitems_GameListItem_fr\">" +
                "<a href=\"https://steamcommunity.com/app/440\">Team Fortress 2</a>" +
                "<div>TOTAL PLAYED</div><div>12,5 heures</div>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(750, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_MapsModernHtmlRowsWithImageAppUrls()
        {
            const string html =
                "<div class=\"mtoll770TDI- Panel\" role=\"button\">" +
                "<img src=\"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/72850/header.jpg\" alt=\"Skyrim\" />" +
                "<span>The Elder Scrolls V: Skyrim</span>" +
                "<span>TOTAL PLAYED</span><span>1,549 hours</span>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(72850, game.AppId);
            Assert.AreEqual("Skyrim", game.Name);
            Assert.AreEqual(92940, game.PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_AppliesSsrPerGameAchievementProgressToLoaderDataGames()
        {
            // The modern games page delivers the owned list via loaderData and the per-game
            // achievement progress as individual React Query entries (one {appid,unlocked,total,...}
            // object each) in renderContext. The two sources must reconcile by appid so every game
            // with achievements gets its X/Y hint; games with total:0 get none.
            const string listData =
                "{\"listData\":{\"rgGames\":[" +
                "{\"appid\":440,\"name\":\"Team Fortress 2\",\"playtime_forever\":90}," +
                "{\"appid\":570,\"name\":\"Dota 2\",\"playtime_forever\":180}," +
                "{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":50}]}}";
            const string queryData =
                "{\"mutations\":[],\"queries\":[" +
                "{\"state\":{\"data\":{\"appid\":440,\"unlocked\":11,\"total\":17,\"percentage\":64,\"all_unlocked\":false}}," +
                "\"queryKey\":[\"AchievementProgress\",440]}," +
                "{\"state\":{\"data\":{\"appid\":570,\"unlocked\":0,\"total\":0,\"percentage\":0,\"all_unlocked\":false}}," +
                "\"queryKey\":[\"AchievementProgress\",570]}," +
                "{\"state\":{\"data\":{\"appid\":730,\"unlocked\":75,\"total\":75,\"percentage\":100,\"all_unlocked\":true}}," +
                "\"queryKey\":[\"AchievementProgress\",730]}]}";
            var html = SsrPage(queryData, listData);

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(3, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual(11, games[0].AchievementsEarned);
            Assert.AreEqual(17, games[0].AchievementsTotal);
            // total:0 game keeps a null hint so it is treated as achievement-less downstream.
            Assert.AreEqual(570, games[1].AppId);
            Assert.IsFalse(games[1].AchievementsTotal.HasValue && games[1].AchievementsTotal.Value > 0);
            Assert.AreEqual(730, games[2].AppId);
            Assert.AreEqual(75, games[2].AchievementsEarned);
            Assert.AreEqual(75, games[2].AchievementsTotal);
        }

        [TestMethod]
        public void ParseOwnedGames_ReadsWholeLibraryFromRedesignedPage_BeyondRenderedRows()
        {
            // Shape of the redesigned games page (October 2026): the OwnedGames query holds the
            // whole library and every game has its own AchievementProgress query, while the DOM
            // renders only the first rows. The parser must return every owned game with its hint.
            const string queryData =
                "{\"mutations\":[],\"queries\":[" +
                "{\"queryKey\":[\"OwnedGames\",\"76561198000000002\",\"english\"],\"state\":{\"status\":\"success\",\"data\":[" +
                "{\"appid\":22380,\"name\":\"Fallout: New Vegas\",\"playtime_forever\":34686,\"has_community_visible_stats\":true}," +
                "{\"appid\":50300,\"name\":\"Spec Ops: The Line\",\"playtime_forever\":120,\"has_community_visible_stats\":true}," +
                "{\"appid\":70,\"name\":\"Half-Life\",\"playtime_forever\":398,\"has_community_visible_stats\":false}]}}," +
                "{\"queryKey\":[\"AchievementProgress\",{\"steamid\":\"76561198000000002\",\"include_unvetted_apps\":true,\"language\":\"english\"},22380]," +
                "\"state\":{\"status\":\"success\",\"data\":{\"appid\":22380,\"unlocked\":75,\"total\":75,\"percentage\":100,\"all_unlocked\":true}}}," +
                "{\"queryKey\":[\"AchievementProgress\",{\"steamid\":\"76561198000000002\",\"include_unvetted_apps\":true,\"language\":\"english\"},50300]," +
                "\"state\":{\"status\":\"success\",\"data\":{\"appid\":50300,\"unlocked\":3,\"total\":50,\"percentage\":6,\"all_unlocked\":false}}}," +
                "{\"queryKey\":[\"AchievementProgress\",{\"steamid\":\"76561198000000002\",\"include_unvetted_apps\":true,\"language\":\"english\"},70]," +
                "\"state\":{\"status\":\"success\",\"data\":{\"appid\":70,\"unlocked\":0,\"total\":0,\"percentage\":0,\"all_unlocked\":false}}}]}";
            var html = SsrPage(queryData, "{\"steamid\":\"76561198000000001\"}") +
                       "<div class=\"Panel\">" +
                       "<img src=\"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/22380/header.jpg\" alt=\"Fallout: New Vegas\" />" +
                       "<span><span>TOTAL PLAYED</span>578.1 hours</span>" +
                       "<div><a href=\"https://steamcommunity.com/id/test/achievements/22380\">ACHIEVEMENTS</a><span>75/75</span></div>" +
                       "</div>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            CollectionAssert.AreEqual(new[] { 70, 22380, 50300 }, games.Select(game => game.AppId).ToArray());
            Assert.AreEqual(398, games[0].PlaytimeForever);
            Assert.IsFalse(games[0].AchievementsTotal > 0);
            Assert.AreEqual(34686, games[1].PlaytimeForever);
            Assert.AreEqual(75, games[1].AchievementsEarned);
            Assert.AreEqual(75, games[1].AchievementsTotal);
            Assert.AreEqual("Spec Ops: The Line", games[2].Name);
            Assert.AreEqual(3, games[2].AchievementsEarned);
            Assert.AreEqual(50, games[2].AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        // The redesigned pages' server-rendered payload: queryData is a string-encoded query cache
        // and each loaderData entry is a string-encoded JSON object.
        private static string SsrPage(string queryData, params string[] loaderItems)
        {
            var root = new Newtonsoft.Json.Linq.JObject
            {
                ["route"] = "/profiles/:steamid/games",
                ["loaderData"] = new Newtonsoft.Json.Linq.JArray(loaderItems ?? new string[0]),
                ["UserConfig"] = new Newtonsoft.Json.Linq.JObject { ["logged_in"] = true },
                ["renderContext"] = new Newtonsoft.Json.Linq.JObject { ["queryData"] = queryData ?? "{\"queries\":[]}" }
            };
            return "<script type=\"application/json\" id=\"valve-ssr-data\" nonce=\"x\">" +
                   root.ToString(Formatting.None) +
                   "</script>";
        }
    }
}
