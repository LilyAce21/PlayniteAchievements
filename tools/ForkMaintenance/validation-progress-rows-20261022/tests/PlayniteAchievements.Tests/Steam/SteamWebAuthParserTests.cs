using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamWebAuthParserTests
    {
        [TestMethod]
        public void Parse_ExtractsPlayniteSteamLibraryTokenShape()
        {
            const string source =
                "var g_steamID = \"76561198000000000\";" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000000", session.SteamId64);
            Assert.AreEqual("store-token", session.WebApiToken);
        }

        [TestMethod]
        public void Parse_SupportsCookieSteamId_WhenPageOnlyHasToken()
        {
            const string source = "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source, "76561198000000000", hasSteamSessionCookies: true);

            Assert.IsTrue(session.IsComplete);
            Assert.IsTrue(session.HasSteamSessionCookies);
            Assert.AreEqual("76561198000000000", session.SteamId64);
        }

        [TestMethod]
        public void Parse_PrefersLoyaltyWebApiToken_WhenPresent()
        {
            const string source =
                "var g_steamID = \"76561198000000000\";" +
                "data-loyalty_webapi_token=\"oauth-token\"" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000000", session.SteamId64);
            Assert.AreEqual("oauth-token", session.WebApiToken);
        }

        [TestMethod]
        public void Parse_PrefersPageSteamId_OverCookieSteamId()
        {
            const string source =
                "var g_steamID = \"76561198000000002\";" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source, "76561198000000001", hasSteamSessionCookies: true);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000002", session.SteamId64);
        }

        [TestMethod]
        public void Parse_ReadsSsrPageToken_AndViewerSteamId()
        {
            // Shape of a redesigned community page: the loader entry is a string-escaped JSON
            // document, and a friend's steamid appears only inside escaped query data.
            const string source =
                "<script type=\"application/json\" id=\"valve-ssr-data\">{\"route\":\"/profiles/:steamid/achievements/:appid\"," +
                "\"loaderData\":[\"{\\\"strWebAPIToken\\\":\\\"eyJ0.eyJz-_x.sig\\\",\\\"steamid\\\":\\\"76561198000000001\\\"}\"," +
                "\"{\\\"appid\\\":620,\\\"targetSteamId\\\":\\\"76561198000000002\\\"}\"]," +
                "\"UserConfig\":{\"logged_in\":true,\"steamid\":\"76561198000000001\"}}</script>";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("eyJ0.eyJz-_x.sig", session.WebApiToken);
            Assert.AreEqual("76561198000000001", session.SteamId64);
        }

        [TestMethod]
        public void Parse_LoggedOutSsrPage_HasNoSession()
        {
            const string source =
                "<script type=\"application/json\" id=\"valve-ssr-data\">{\"loaderData\":[\"{\\\"steamid\\\":\\\"0\\\"}\"]," +
                "\"UserConfig\":{\"logged_in\":false,\"steamid\":\"0\"}}</script>";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsFalse(session.HasSteamId);
            Assert.IsFalse(session.HasWebApiToken);
        }

        [TestMethod]
        public void Parse_DoesNotCompleteSession_WhenTokenMissing()
        {
            const string source = "var g_steamID = \"76561198000000000\";";

            var session = SteamWebAuthParser.Parse(source, hasSteamSessionCookies: true);

            Assert.IsTrue(session.HasSteamId);
            Assert.IsFalse(session.HasWebApiToken);
            Assert.IsFalse(session.IsComplete);
        }
    }
}
