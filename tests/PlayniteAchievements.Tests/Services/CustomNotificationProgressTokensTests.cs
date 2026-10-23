using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CustomNotificationProgressTokensTests
    {
        [TestMethod]
        public void ConditionsChooseBranchesAndCompareNumbers()
        {
            Assert.AreEqual("Halfway", CustomNotificationProgressTokens.Expand("[[if <progressPercent> gte 50::Halfway::Starting]]", 50, 100));
            Assert.AreEqual("Starting", CustomNotificationProgressTokens.Expand("[[if <progressPercent> gte 50::Halfway::Starting]]", null, null));
            Assert.AreEqual("No progress", CustomNotificationProgressTokens.Expand("[[if <progress> empty::No progress::In progress]]", null, null));
            Assert.AreEqual("In progress", CustomNotificationProgressTokens.Expand("[[if <progress> empty::No progress::In progress]]", 0, 100));
            Assert.AreEqual(true, CustomNotificationProgressTokens.EvaluateCondition("<points> gt 9 && <points> lte 10", _ => "10"));
            Assert.AreEqual(false, CustomNotificationProgressTokens.EvaluateCondition("<points> gt 9", _ => "not numeric"));
            Assert.AreEqual(true, CustomNotificationProgressTokens.EvaluateCondition("<provider> eq steam || <points> lt 0", token => token == "<provider>" ? "Steam" : "2"));
            Assert.AreEqual(true, CustomNotificationProgressTokens.EvaluateCondition("<provider> contains tea && <provider> ne epic", _ => "Steam"));
        }

        [TestMethod]
        public void CombinedProgressAndOtherConditionsSurvivePreparation()
        {
            const string template = "[[if <progressPercent> gte 50 && <points> gt 5::Great <progress>::Keep going]]";
            var prepared = CustomNotificationProgressTokens.Expand(template, 60, 100);
            Assert.AreEqual("Great 60/100", CustomNotificationProgressTokens.ExpandOptionalGroups(prepared, _ => "10"));
            Assert.AreEqual("Keep going", CustomNotificationProgressTokens.ExpandOptionalGroups(prepared, _ => "0"));
            var low = CustomNotificationProgressTokens.Expand(template, 20, 100);
            Assert.AreEqual("Keep going", low);
        }

        [TestMethod]
        public void EmptyOptionalRowDoesNotBecomeDefaultText()
        {
            var settings = new LocalSettings { OverlayCustomTitleTemplate = "[[ (<progress>)]]" };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null);
            Assert.IsFalse(string.IsNullOrWhiteSpace(copy.OverlayCustomTitleTemplate));
            Assert.AreEqual("", CustomNotificationProgressTokens.ExpandOptionalGroups(copy.OverlayCustomTitleTemplate, _ => null));
        }

        [TestMethod]
        public void OptionalProgressControlsSpacesAndPunctuation()
        {
            const string template = "<achievementName>[[ (<progress> -)]]";
            Assert.AreEqual("<achievementName> (0/100 -)", CustomNotificationProgressTokens.Expand(template, 0, 100));
            Assert.AreEqual("<achievementName>", CustomNotificationProgressTokens.Expand(template, null, 100));
            Assert.AreEqual("()", CustomNotificationProgressTokens.Expand("(<progress>)", null, null));
        }

        [TestMethod]
        public void OptionalGroupsRequireAllValuesAndPreserveZero()
        {
            Assert.AreEqual("A", CustomNotificationProgressTokens.ExpandOptionalGroups("A[[ <points> - <rarity>]]", token => token == "<points>" ? "0" : " "));
            Assert.AreEqual("A <points>", CustomNotificationProgressTokens.ExpandOptionalGroups("A[[ <points>]]", _ => "0"));
            Assert.AreEqual("A", CustomNotificationProgressTokens.ExpandOptionalGroups("A[[ <unknown>]]", _ => null));
            Assert.AreEqual("A[[broken", CustomNotificationProgressTokens.ExpandOptionalGroups("A[[broken", _ => ""));
        }

        [TestMethod]
        public void MixedConditionsAreDeferredUntilRendering()
        {
            const string template = "Name[[ (<progress> - <points>)]]";
            var prepared = CustomNotificationProgressTokens.Expand(template, 8, 46);
            Assert.AreEqual("Name[[ (8/46 - <points>)]]", prepared);
            Assert.AreEqual("Name", CustomNotificationProgressTokens.ExpandOptionalGroups(prepared, _ => ""));
            Assert.AreEqual("Name (8/46 - <points>)", CustomNotificationProgressTokens.ExpandOptionalGroups(prepared, _ => "10"));
            Assert.AreEqual("Name", CustomNotificationProgressTokens.Expand(template, null, null));
        }

        [TestMethod]
        public void LeavesAchievementNameUnchanged()
        {
            var settings = new LocalSettings { OverlayCustomTitleTemplate = "<achievementName>" };
            var withProgress = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 8, 46);
            var withoutProgress = CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null);
            Assert.AreEqual("<achievementName>", withProgress.OverlayCustomTitleTemplate);
            Assert.AreEqual("<achievementName>", withoutProgress.OverlayCustomTitleTemplate);
            Assert.AreEqual("<achievementName>", settings.OverlayCustomTitleTemplate);
            Assert.AreEqual(withProgress.OverlayCustomAchievementTemplate, withoutProgress.OverlayCustomAchievementTemplate);
            var nested = CustomNotificationProgressTokens.CreateSettingsCopy(withProgress, 16, 100);
            Assert.AreEqual("<achievementName>", nested.OverlayCustomTitleTemplate);
        }

        [TestMethod]
        public void HiddenProgressTokensLeaveNameUnchanged()
        {
            var settings = new LocalSettings
            {
                OverlayCustomGameNameTemplate = "<achievementName>",
                OverlayCustomShowGameName = true,
                OverlayCustomLine4Template = "<progress>",
                OverlayCustomShowLine4 = false
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 100);
            Assert.AreEqual("<achievementName>", copy.OverlayCustomGameNameTemplate);
        }

        [TestMethod]
        public void DoesNotAppendProgressToVisibleLines()
        {
            var settings = new LocalSettings
            {
                OverlayCustomShowLine1 = true,
                OverlayCustomTitleTemplate = "Achievement progress",
                OverlayCustomShowGameName = false,
                OverlayCustomShowMeta = false
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 8, 46);
            Assert.AreEqual("Achievement progress", copy.OverlayCustomTitleTemplate);
            Assert.IsFalse(copy.OverlayCustomShowMeta);
            Assert.IsFalse(copy.OverlayCustomShowGameName);
            Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(copy).Contains("AchievementProgressPrepared"));
        }

        [TestMethod]
        public void ProgressOnAnotherLineLeavesNameUnchanged()
        {
            var settings = new LocalSettings
            {
                OverlayCustomGameNameTemplate = "<achievementName>",
                OverlayCustomShowGameName = true,
                OverlayCustomLine4Template = "<progress>",
                OverlayCustomShowLine4 = true
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 100, 100);
            Assert.AreEqual("<achievementName>", copy.OverlayCustomGameNameTemplate);
            Assert.AreEqual("100/100", copy.OverlayCustomLine4Template);
        }

        [TestMethod]
        public void NameAndDescriptionRemainUnchanged()
        {
            var settings = new LocalSettings
            {
                OverlayCustomTitleTemplate = "<achievementName> - <achievementDescription>",
                OverlayCustomShowLine4 = true,
                OverlayCustomLine4Template = "Progress - <progressPercent>"
            };
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 28, 100);
            Assert.AreEqual("<achievementName> - <achievementDescription>", copy.OverlayCustomTitleTemplate);
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
