using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    [DoNotParallelize]
    public class ScoreCardTypeTests
    {
        [DataTestMethod]
        [DataRow("Collection", "Collection")]
        [DataRow("Prestige", "Prestige")]
        [DataRow("Gamerscore", "Points:Xbox")]
        [DataRow("EpicXp", "Points:Epic")]
        [DataRow("RetroPoints", "Points:RetroAchievements")]
        [DataRow("2", "Points:Xbox")]
        [DataRow("Points:PSN", "Points:PSN")]
        [DataRow("Points:RPCS3", "Points:PSN")]
        [DataRow("points:xenia", "Points:Xbox")]
        [DataRow("Points:Custom:abc", "Points:Custom:abc")]
        public void TryParse_ReadsKeysAndLegacyEnumValues(string raw, string expectedKey)
        {
            Assert.IsTrue(ScoreCardType.TryParse(raw, out var type));
            Assert.AreEqual(expectedKey, type.Key);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("Points:")]
        [DataRow("Points:Custom")]
        [DataRow("Bogus")]
        [DataRow("9")]
        public void TryParse_RejectsUnknownValues(string raw)
        {
            Assert.IsFalse(ScoreCardType.TryParse(raw, out var type));
            Assert.AreEqual(ScoreCardType.Collection, type);
        }

        [DataTestMethod]
        [DataRow("None", null)]
        [DataRow("0", null)]
        [DataRow("1", "Collection")]
        [DataRow("3", "Points:Xbox")]
        [DataRow("5", "Points:RetroAchievements")]
        [DataRow("Gamerscore", "Points:Xbox")]
        [DataRow("Points:Steam", "Points:Steam")]
        public void Slots_ReadLegacySlotNumbersAndNames(string slot, string expectedKey)
        {
            var hasCard = ScoreCardSlots.TryGetCardType(slot, out var type);

            Assert.AreEqual(expectedKey != null, hasCard);
            if (hasCard)
            {
                Assert.AreEqual(expectedKey, type.Key);
            }

            Assert.AreEqual(expectedKey ?? ScoreCardSlots.NoneKey, ScoreCardSlots.Normalize(slot));
        }

        [TestMethod]
        public void PersistedSlot_NormalizesLegacyValueOnAssignment()
        {
            var settings = new PersistedSettings { OverviewScoreCardSlot1 = "EpicXp" };

            Assert.AreEqual("Points:Epic", settings.OverviewScoreCardSlot1);
        }

        [DataTestMethod]
        [DataRow("Xbox", null, "Xbox")]
        [DataRow("Xenia", null, "Xbox")]
        [DataRow("ShadPS4", null, "PSN")]
        [DataRow("Steam", null, "Steam")]
        [DataRow("Custom:abc", null, "Custom:abc")]
        [DataRow("Custom", null, null)]
        [DataRow("Unknown", null, null)]
        [DataRow("Custom", "Xenia", "Xbox")]
        [DataRow("Xbox", "None", null)]
        [DataRow("Xbox", "  ", "Xbox")]
        public void Resolve_FoldsAliasesAndAppliesOverride(string providerKey, string overrideFamily, string expected)
        {
            Assert.AreEqual(expected, PointsFamilies.Resolve(providerKey, overrideFamily));
        }

        [TestMethod]
        public void CustomData_PointsFamilySurvivesCloneAndPortableRoundTrip()
        {
            var data = new GameCustomDataFile { PointsFamily = "PSN" };

            Assert.AreEqual("PSN", data.Clone().PointsFamily);
            Assert.AreEqual("PSN", data.ToPortable().PointsFamily);
            Assert.AreEqual("PSN", data.ToPortable().Clone().PointsFamily);
            Assert.AreEqual("PSN", GameCustomDataFile.FromPortable(data.ToPortable(), System.Guid.NewGuid(), null, null).PointsFamily);
        }

        [TestMethod]
        public void Curves_KeepPlatformScalesAndDefaultOthersToGamerscore()
        {
            var gamerscore = AchievementLevelCurveSettings.Gamerscore;
            var steam = ScoreCardTypes.Calculate(ScoreCardType.Points("Steam"), 5000);

            Assert.AreEqual(AchievementLevelCalculator.Calculate(5000, gamerscore).Rank, steam.Rank);
            Assert.AreEqual(
                AchievementLevelCalculator.Calculate(400, AchievementLevelCurveSettings.RetroAchievementsPoints).Rank,
                ScoreCardTypes.Calculate(ScoreCardType.RetroPoints, 400).Rank);
        }

        [TestMethod]
        public void Choices_ListPlatformFamiliesPublishedFamiliesAndTheCurrentCard()
        {
            var totals = new PlatformScoreTotals();
            totals.Add("PSN", null, 90);
            ScoreCardChoices.PublishFamiliesWithPoints(totals);
            try
            {
                var keys = ScoreCardChoices.Build(ScoreCardType.Points("GOG")).Select(card => card.Key).ToList();

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Collection", "Prestige", "Points:Xbox", "Points:Epic", "Points:RetroAchievements",
                        "Points:PSN", "Points:GOG"
                    },
                    keys);
            }
            finally
            {
                ScoreCardChoices.PublishFamiliesWithPoints(null);
            }
        }
    }
}
