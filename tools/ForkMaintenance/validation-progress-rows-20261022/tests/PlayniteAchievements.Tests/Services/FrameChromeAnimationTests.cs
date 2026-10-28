using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class FrameChromeAnimationTests
    {
        [TestMethod]
        public void StateAt_LoopsEachImageFromItsFirstFrame()
        {
            var animation = new FrameChromeAnimation(
                new List<IReadOnlyList<int>>
                {
                    new[] { 100, 100 },      // 200 ms loop
                    new[] { 100, 100, 100 }  // 300 ms loop
                },
                _ => null);

            Assert.AreEqual("0,0", animation.StateAt(0));
            Assert.AreEqual("1,1", animation.StateAt(150));
            Assert.AreEqual("0,2", animation.StateAt(250));
            Assert.AreEqual("1,0", animation.StateAt(350), "each image loops on its own length");
            Assert.AreEqual(animation.StateAt(50), animation.StateAt(650), "the combined loop repeats");
        }

        [TestMethod]
        public void RenderAt_StopsDrawingAfterAFailure()
        {
            var calls = 0;
            var animation = new FrameChromeAnimation(
                new List<IReadOnlyList<int>> { new[] { 100, 100 } },
                _ =>
                {
                    calls++;
                    return null;
                });

            Assert.IsNull(animation.RenderAt(0));
            Assert.IsNull(animation.RenderAt(100));
            Assert.AreEqual(1, calls, "a failed draw is not retried for every frame");
        }

        [TestMethod]
        public void RenderAt_PassesTheTimeThrough()
        {
            long seen = -1;
            var animation = new FrameChromeAnimation(
                new List<IReadOnlyList<int>> { new[] { 100, 100 } },
                timeMs =>
                {
                    seen = timeMs;
                    return new FrameChromeImage(new byte[4], 1, 1);
                });

            Assert.IsNotNull(animation.RenderAt(1234));
            Assert.AreEqual(1234, seen);
        }
    }
}
