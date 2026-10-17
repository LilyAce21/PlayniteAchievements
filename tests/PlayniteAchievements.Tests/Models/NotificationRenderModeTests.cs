using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class NotificationRenderModeTests
    {
        [TestMethod]
        public void SavedValues_KeepTheirNumbers()
        {
            // The setting is saved numerically; a reordered enum would flip every saved choice.
            Assert.AreEqual(0, (int)NotificationRenderMode.Performance);
            Assert.AreEqual(1, (int)NotificationRenderMode.Quality);
        }

        [TestMethod]
        public void Default_IsPerformance()
        {
            Assert.AreEqual(NotificationRenderMode.Performance, new PersistedSettings().NotificationRenderMode);
        }
    }
}
