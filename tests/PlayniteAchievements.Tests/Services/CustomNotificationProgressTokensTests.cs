using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CustomNotificationProgressTokensTests
    {
        [TestMethod]
        public void AutomaticallyAddsProgressToTheVisibleAchievementName()
        {
            var settings = new LocalSettings { OverlayCustomTitleTemplate = "<achievementName>" };
            var withProgress = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 8, 46);
            var withoutProgress = CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null);
            Assert.AreEqual("<achievementName> (8/46)", withProgress.OverlayCustomTitleTemplate);
            Assert.AreEqual("<achievementName>", withoutProgress.OverlayCustomTitleTemplate);
            Assert.AreEqual("<achievementName>", settings.OverlayCustomTitleTemplate);
            Assert.AreEqual(withProgress.OverlayCustomAchievementTemplate, withoutProgress.OverlayCustomAchievementTemplate);
            var nested = CustomNotificationProgressTokens.CreateSettingsCopy(withProgress, 16, 100);
            Assert.AreEqual("<achievementName> (8/46)", nested.OverlayCustomTitleTemplate);
        }

        [TestMethod]
        public void HiddenProgressTokensDoNotSuppressTheAutomaticCounter()
        {
            var settings = new LocalSettings
            {
                OverlayCustomGameNameTemplate = "<achievementName>",
                OverlayCustomShowGameName = true,
                OverlayCustomLine4Template = "<progress>",
                OverlayCustomShowLine4 = false
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 100);
            Assert.AreEqual("<achievementName> (16/100)", copy.OverlayCustomGameNameTemplate);
        }

        [TestMethod]
        public void FallsBackToAVisibleLineWithoutEnablingHiddenLines()
        {
            var settings = new LocalSettings
            {
                OverlayCustomShowLine1 = true,
                OverlayCustomTitleTemplate = "Achievement progress",
                OverlayCustomShowGameName = false,
                OverlayCustomShowMeta = false
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 8, 46);
            Assert.AreEqual("Achievement progress (8/46)", copy.OverlayCustomTitleTemplate);
            Assert.IsFalse(copy.OverlayCustomShowMeta);
            Assert.IsFalse(copy.OverlayCustomShowGameName);
            Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(copy).Contains("AchievementProgressPrepared"));
        }

        [TestMethod]
        public void ProgressOnAnotherLineDoesNotSuppressTheNameCounter()
        {
            var settings = new LocalSettings
            {
                OverlayCustomGameNameTemplate = "<achievementName>",
                OverlayCustomShowGameName = true,
                OverlayCustomLine4Template = "<progress>",
                OverlayCustomShowLine4 = true
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 100, 100);
            Assert.AreEqual("<achievementName> (100/100)", copy.OverlayCustomGameNameTemplate);
            Assert.AreEqual("100/100", copy.OverlayCustomLine4Template);
        }

        [TestMethod]
        public void NameAndDescriptionKeepCounterBesideNameWithPercentOnAnotherLine()
        {
            var settings = new LocalSettings
            {
                OverlayCustomTitleTemplate = "<achievementName> - <achievementDescription>",
                OverlayCustomShowLine4 = true,
                OverlayCustomLine4Template = "Progress - <progressPercent>"
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 28, 100);
            Assert.AreEqual("<achievementName> (28/100) - <achievementDescription>", copy.OverlayCustomTitleTemplate);
            Assert.AreEqual("Progress - 28%", copy.OverlayCustomLine4Template);
        }

        [TestMethod]
        public void ExplicitCountOnNameLineIsNotDuplicated()
        {
            var settings = new LocalSettings { OverlayCustomTitleTemplate = "<achievementName> - <progress>" };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 28, 100);
            Assert.AreEqual("<achievementName> - 28/100", copy.OverlayCustomTitleTemplate);
        }

        [TestMethod]
        public void ExpandsProgressAndLeavesOtherTokensForTheRenderer()
        {
            Assert.AreEqual("<achievementName>: 16/100 (16 of 100, 16%)",
                CustomNotificationProgressTokens.Expand("<achievementName>: <progress> (<progressCurrent> of <progressMax>, <progressPercent>)", 16, 100));
            Assert.AreEqual("100/100", CustomNotificationProgressTokens.Expand("<PROGRESS>", 100, 100));
            Assert.AreEqual("0/100", CustomNotificationProgressTokens.Expand("<progress>", -5, 100));
            Assert.AreEqual("100/100", CustomNotificationProgressTokens.Expand("<progress>", 101, 100));
        }

        [TestMethod]
        public void MissingProgressDoesNotInventACounter()
        {
            Assert.AreEqual("<achievementName>", CustomNotificationProgressTokens.Expand("<achievementName><progress><progressCurrent><progressMax><progressPercent>", null, 100));
            Assert.AreEqual("", CustomNotificationProgressTokens.Expand("<progress>", 16, 0));
        }

        [TestMethod]
        public void EachNotificationHasItsOwnValuesAndSavedSettingsAreUntouched()
        {
            var settings = new LocalSettings
            {
                OverlayCustomAchievementTemplate = "<achievementName> — <progress>",
                OverlayCustomLine4Template = "<progressPercent>",
                EnableInAppUnlockNotifications = true,
                UnlockSoundLeadMilliseconds = 250
            };
            var first = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 100, true);
            var second = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 8, 46);
            Assert.AreEqual("<achievementName> — 16/100", first.OverlayCustomAchievementTemplate);
            Assert.AreEqual("16%", first.OverlayCustomLine4Template);
            Assert.AreEqual("<achievementName> — 8/46", second.OverlayCustomAchievementTemplate);
            Assert.IsFalse(first.EnableInAppUnlockNotifications);
            Assert.AreEqual(0, first.UnlockSoundLeadMilliseconds);
            Assert.IsTrue(second.EnableInAppUnlockNotifications);
            Assert.AreEqual("<achievementName> — <progress>", settings.OverlayCustomAchievementTemplate);
            Assert.IsTrue(settings.EnableInAppUnlockNotifications);
            Assert.AreEqual(250, settings.UnlockSoundLeadMilliseconds);
        }
    }
}
