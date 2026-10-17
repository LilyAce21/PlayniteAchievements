using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Tests.TestInfrastructure;
using System.IO;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AnimatedImageKeyframeTests
    {
        private const int Red = 0xFF0000;
        private const int Green = 0x00FF00;
        private const int Blue = 0x0000FF;

        [TestMethod]
        public void FrameIndexAt_PicksTheFrameShowingAtThatTime_AndHoldsTheLastPastTheEnd()
        {
            var delays = new[] { 100, 200, 300 };

            Assert.AreEqual(0, AnimatedImageKeyframe.FrameIndexAt(delays, 0));
            Assert.AreEqual(0, AnimatedImageKeyframe.FrameIndexAt(delays, 99));
            Assert.AreEqual(1, AnimatedImageKeyframe.FrameIndexAt(delays, 100));
            Assert.AreEqual(1, AnimatedImageKeyframe.FrameIndexAt(delays, 299));
            Assert.AreEqual(2, AnimatedImageKeyframe.FrameIndexAt(delays, 300));
            Assert.AreEqual(2, AnimatedImageKeyframe.FrameIndexAt(delays, 5000));
        }

        [TestMethod]
        public void Gif_TimelineAndStills_FollowTheFrameDelays()
        {
            var path = GifFixture.WriteTempGif(GifFixture.BuildGif(
                1, 1, new[] { Red, Green, Blue },
                Frame(0, 10), Frame(1, 20), Frame(2, 30)));
            try
            {
                Assert.AreEqual(600, AnimatedImageKeyframe.DurationMs(path));
                Assert.AreEqual(Red, PixelAt(path, 0));
                Assert.AreEqual(Green, PixelAt(path, 150));
                Assert.AreEqual(Blue, PixelAt(path, 300));
                Assert.AreEqual(Blue, PixelAt(path, 10000), "past the end holds the last frame");
            }
            finally
            {
                GifFixture.DeleteTempPayload(path);
            }
        }

        [TestMethod]
        public void SingleFrameAndMissingFiles_HaveNoTimeline()
        {
            var single = GifFixture.WriteTempGif(GifFixture.BuildGif(1, 1, new[] { Red }, Frame(0, 10)));
            try
            {
                Assert.AreEqual(0, AnimatedImageKeyframe.DurationMs(single));
                Assert.IsNull(AnimatedImageKeyframe.TryDecodeAt(single, 0));
            }
            finally
            {
                GifFixture.DeleteTempPayload(single);
            }

            var missing = Path.Combine(Path.GetTempPath(), "PlayAchMissing.gif");
            Assert.AreEqual(0, AnimatedImageKeyframe.DurationMs(missing));
            Assert.IsNull(AnimatedImageKeyframe.TryDecodeAt(missing, 0));
        }

        private static GifFixture.Frame Frame(byte colorIndex, int delayCentiseconds)
        {
            return new GifFixture.Frame
            {
                Width = 1,
                Height = 1,
                Indices = new[] { colorIndex },
                DelayCentiseconds = delayCentiseconds
            };
        }

        private static int PixelAt(string path, int timeMs)
        {
            var still = AnimatedImageKeyframe.TryDecodeAt(path, timeMs);
            Assert.IsNotNull(still);
            var pixels = new int[1];
            new FormatConvertedBitmap(still, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                .CopyPixels(pixels, 4, 0);
            return pixels[0] & 0xFFFFFF;
        }
    }
}
