using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class LocalNotificationTypographySettingsTests
    {
        [TestMethod]
        public void UpstreamNotificationTemplate_DefaultsToForkAndPersistsSelection()
        {
            var settings = new LocalSettings();
            Assert.IsFalse(settings.UseUpstreamNotificationTemplate);

            settings.UseUpstreamNotificationTemplate = true;
            var restored = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.IsTrue(restored.UseUpstreamNotificationTemplate);
            Assert.IsFalse(JsonConvert.DeserializeObject<LocalSettings>("{}").UseUpstreamNotificationTemplate);
        }

        [TestMethod]
        public void TypographySettings_DefaultAndNormalizeFontFamilies()
        {
            var settings = new LocalSettings();

            Assert.AreEqual("Default", settings.OverlayCustomLine1FontFamily);
            Assert.AreEqual(LocalOverlayTextFormattingMode.Auto, settings.OverlayCustomTextFormattingMode);

            settings.OverlayCustomLine1FontFamily = "  Segoe UI  ";
            settings.OverlayCustomLine2FontFamily = " ";
            settings.OverlayCustomTextFormattingMode = LocalOverlayTextFormattingMode.Ideal;

            Assert.AreEqual("Segoe UI", settings.OverlayCustomLine1FontFamily);
            Assert.AreEqual("Default", settings.OverlayCustomLine2FontFamily);
            Assert.AreEqual(LocalOverlayTextFormattingMode.Ideal, settings.OverlayCustomTextFormattingMode);
        }
    }
}
