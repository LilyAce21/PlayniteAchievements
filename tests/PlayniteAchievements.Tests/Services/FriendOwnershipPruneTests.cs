using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class FriendOwnershipPruneTests
    {
        private static readonly FriendGameOwnership[] OneGame =
        {
            new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "76561198000000002", AppId = 620 }
        };

        [TestMethod]
        public void CompleteNonEmptyList_PrunesStaleSharedOwnership()
        {
            Assert.IsTrue(FriendRefreshWorkPolicy.ShouldPruneStaleSharedOwnership(new FriendRefreshOptions(), OneGame));
        }

        [TestMethod]
        public void PartialList_NeverPrunes()
        {
            // A list holding only the rows a games page rendered must not delete the shared games
            // it does not show.
            Assert.IsFalse(FriendRefreshWorkPolicy.ShouldPruneStaleSharedOwnership(
                new FriendRefreshOptions(),
                OneGame,
                ownershipIsPartial: true));
        }

        [TestMethod]
        public void EmptyList_NeverPrunes()
        {
            Assert.IsFalse(FriendRefreshWorkPolicy.ShouldPruneStaleSharedOwnership(
                new FriendRefreshOptions(),
                new FriendGameOwnership[0]));
        }
    }
}
