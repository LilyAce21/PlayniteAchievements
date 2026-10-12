using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Psn;

namespace PlayniteAchievements.Services.Tests.Psn
{
    [TestClass]
    public class PsnNpwrTests
    {
        [TestMethod]
        public void FindsTheIdInAPastedPageTitle()
        {
            var ids = PsnNpwr.Extract("Clair Obscur: Expedition 33 (NPWR39144_00) — Trophies");
            CollectionAssert.AreEqual(new[] { "NPWR39144_00" }, ids.ToArray());
        }

        [TestMethod]
        public void FindsSeveralIdsInOrderWithoutDuplicates_AndUpperCasesThem()
        {
            var ids = PsnNpwr.Extract("npwr16485_00 ... NPWR58037_00 ... NPWR16485_00");
            CollectionAssert.AreEqual(new[] { "NPWR16485_00", "NPWR58037_00" }, ids.ToArray());
        }

        [TestMethod]
        public void FindsIdsInsideALargePastedSource()
        {
            var source = "<meta property=\"og:title\" content=\"X (NPWR39253_00) — Trophies\"><meta name=\"twitter:title\" content=\"X (NPWR39253_00) — Trophies\">";
            Assert.AreEqual(1, PsnNpwr.Extract(source).Count);
        }

        [TestMethod]
        public void NothingFoundInUnrelatedText()
        {
            Assert.AreEqual(0, PsnNpwr.Extract("CUSA12345 PPSA01325_00").Count);
            Assert.AreEqual(0, PsnNpwr.Extract(null).Count);
            Assert.AreEqual(0, PsnNpwr.Extract("   ").Count);
            Assert.AreEqual(0, PsnNpwr.Extract("NPWR123_00").Count);
        }

        [TestMethod]
        public void ValidatesAWholeId()
        {
            Assert.IsTrue(PsnNpwr.IsValid("NPWR39144_00"));
            Assert.IsTrue(PsnNpwr.IsValid(" npwr39144_00 "));
            Assert.IsFalse(PsnNpwr.IsValid("NPWR39144"));
            Assert.IsFalse(PsnNpwr.IsValid("x NPWR39144_00"));
            Assert.IsFalse(PsnNpwr.IsValid(null));
        }
    }

    [TestClass]
    public class PsnIconUrlTests
    {
        [TestMethod]
        public void OnlyHttpsPlayStationHostsAreAllowed()
        {
            Assert.IsTrue(PsnIconUrl.IsAllowed("https://psnobj.prod.dl.playstation.net/psnobj/NPWR1/a.png"));
            Assert.IsTrue(PsnIconUrl.IsAllowed("https://image.api.playstation.com/x.png"));
            Assert.IsFalse(PsnIconUrl.IsAllowed("http://psnobj.prod.dl.playstation.net/a.png"));
            Assert.IsFalse(PsnIconUrl.IsAllowed("https://evil.example.com/a.png"));
            Assert.IsFalse(PsnIconUrl.IsAllowed("https://playstation.net.evil.example.com/a.png"));
            Assert.IsFalse(PsnIconUrl.IsAllowed("file:///c:/secret.png"));
            Assert.IsFalse(PsnIconUrl.IsAllowed(""));
            Assert.IsFalse(PsnIconUrl.IsAllowed(null));
        }
    }

    [TestClass]
    public class PsnPlatinumImportTests
    {
        private const string ScriptJson =
            "{\n  \"game_name\": \"Some Game\",\n  \"title_id\": \"NPWR39144_00\",\n  \"name\": \"Test Platinum\",\n" +
            "  \"description\": \"Earn every other trophy.\",\n  \"icon_url\": \"https://psnobj.prod.dl.playstation.net/psnobj/NPWR39144_00/x.png\"\n}";

        [TestMethod]
        public void ReadsTheScriptsPlatinumDataFile()
        {
            Assert.IsTrue(PsnPlatinumImport.TryParse(ScriptJson, out var platinum));
            Assert.AreEqual("NPWR39144_00", platinum.Npwr);
            Assert.AreEqual("Test Platinum", platinum.TrophyName);
            Assert.AreEqual("Earn every other trophy.", platinum.TrophyDetail);
            Assert.AreEqual("https://psnobj.prod.dl.playstation.net/psnobj/NPWR39144_00/x.png", platinum.IconUrl);
            Assert.IsTrue(platinum.HasText);
        }

        [TestMethod]
        public void AForeignIconAddressIsDroppedButTheTextIsKept()
        {
            var json = ScriptJson.Replace("psnobj.prod.dl.playstation.net", "evil.example.com");
            Assert.IsTrue(PsnPlatinumImport.TryParse(json, out var platinum));
            Assert.AreEqual(string.Empty, platinum.IconUrl);
            Assert.AreEqual("Test Platinum", platinum.TrophyName);
        }

        [TestMethod]
        public void AnInvalidIdIsLeftBlankRatherThanTrusted()
        {
            Assert.IsTrue(PsnPlatinumImport.TryParse(ScriptJson.Replace("NPWR39144_00", "not-an-id"), out var platinum));
            Assert.AreEqual(string.Empty, platinum.Npwr);
        }

        [TestMethod]
        public void RejectsMissingNameGarbageAndOversizedInput()
        {
            Assert.IsFalse(PsnPlatinumImport.TryParse(ScriptJson.Replace("\"name\": \"Test Platinum\",", string.Empty), out _));
            Assert.IsFalse(PsnPlatinumImport.TryParse("not json", out _));
            Assert.IsFalse(PsnPlatinumImport.TryParse("[1]", out _));
            Assert.IsFalse(PsnPlatinumImport.TryParse(null, out _));
            Assert.IsFalse(PsnPlatinumImport.TryParse(new string(' ', PsnPlatinumImport.MaxJsonLength + 1), out _));
        }
    }

    [TestClass]
    public class PsnTrophyResponseTests
    {
        public const string Ps5Set =
            "{\"trophySetVersion\":\"01.00\",\"hasTrophyGroups\":false,\"trophies\":[" +
            "{\"trophyId\":0,\"trophyHidden\":false,\"trophyType\":\"platinum\",\"trophyName\":\"Test Platinum\"," +
            "\"trophyDetail\":\"Earn every other trophy.\",\"trophyIconUrl\":\"https://psnobj.prod.dl.playstation.net/psnobj/NPWR39144_00/p.png\",\"trophyGroupId\":\"default\"}," +
            "{\"trophyId\":1,\"trophyHidden\":false,\"trophyType\":\"bronze\",\"trophyName\":\"First\",\"trophyDetail\":\"Start.\",\"trophyIconUrl\":\"https://psnobj.prod.dl.playstation.net/b.png\",\"trophyGroupId\":\"default\"}]," +
            "\"totalItemCount\":2}";

        [TestMethod]
        public void PicksThePlatinumOutOfTheTrophyList()
        {
            var page = PsnTrophyResponse.Parse(Ps5Set, "NPWR39144_00", "PS5");
            Assert.IsNull(page.Error);
            Assert.AreEqual(2, page.TrophyCount);
            Assert.AreEqual("Test Platinum", page.Platinum.TrophyName);
            Assert.AreEqual("Earn every other trophy.", page.Platinum.TrophyDetail);
            Assert.AreEqual("NPWR39144_00", page.Platinum.Npwr);
            Assert.AreEqual("PS5", page.Platinum.Platform);
            Assert.IsNull(page.NextOffset);
        }

        [TestMethod]
        public void ASetWithoutAPlatinumHasNone()
        {
            var json = "{\"trophies\":[{\"trophyId\":0,\"trophyType\":\"gold\",\"trophyName\":\"G\"}],\"totalItemCount\":1}";
            var page = PsnTrophyResponse.Parse(json, "NPWR1_00", "PS4");
            Assert.IsNull(page.Error);
            Assert.IsNull(page.Platinum);
        }

        [TestMethod]
        public void ReportsTheNextPageOffsetWhenThereIsOne()
        {
            var json = "{\"trophies\":[{\"trophyType\":\"bronze\"}],\"nextOffset\":100,\"totalItemCount\":150}";
            Assert.AreEqual(100, PsnTrophyResponse.Parse(json, "NPWR1_00", "PS4").NextOffset);
        }

        [TestMethod]
        public void SurfacesAnApiErrorAndBadJson()
        {
            var error = PsnTrophyResponse.Parse("{\"error\":{\"code\":2240526,\"message\":\"Resource not found\"}}", "NPWR1_00", "PS5");
            Assert.AreEqual("Resource not found", error.Error);
            Assert.IsNotNull(PsnTrophyResponse.Parse("<html>", "NPWR1_00", "PS5").Error);
            Assert.IsNotNull(PsnTrophyResponse.Parse("{}", "NPWR1_00", "PS5").Error);
            Assert.IsNotNull(PsnTrophyResponse.Parse(null, "NPWR1_00", "PS5").Error);
        }
    }

    [TestClass]
    public class PsnNpssoProtectorTests
    {
        [TestMethod]
        public void ProtectedValueRoundTripsAndIsNotThePlainText()
        {
            const string npsso = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ01";
            var stored = NpssoProtector.Protect(npsso);

            Assert.IsFalse(string.IsNullOrEmpty(stored));
            Assert.IsFalse(stored.Contains(npsso));
            Assert.AreEqual(npsso, NpssoProtector.Unprotect(stored));
        }

        [TestMethod]
        public void NothingStoredOrGarbageGivesNull()
        {
            Assert.IsNull(NpssoProtector.Unprotect(null));
            Assert.IsNull(NpssoProtector.Unprotect(""));
            Assert.IsNull(NpssoProtector.Unprotect("not base64!!"));
            Assert.IsNull(NpssoProtector.Unprotect("aGVsbG8="));
            Assert.AreEqual(string.Empty, NpssoProtector.Protect("  "));
        }

        [TestMethod]
        public void PlausibilityCheckRejectsObviousMistakes()
        {
            Assert.IsTrue(NpssoProtector.LooksPlausible(new string('a', 64)));
            Assert.IsFalse(NpssoProtector.LooksPlausible("short"));
            Assert.IsFalse(NpssoProtector.LooksPlausible(new string('a', 30) + " " + new string('b', 30)));
            Assert.IsFalse(NpssoProtector.LooksPlausible(null));
        }
    }
}
