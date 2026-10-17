using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers.Gif;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class GifPixelCopyTests
    {
        [DataTestMethod]
        [DataRow(0, 0, 4, 3, 4)] // Full canvas, contiguous fast path.
        [DataRow(0, 1, 4, 1, 4)] // Full-width band, preserve rows above and below.
        [DataRow(1, 1, 2, 2, 4)] // Partial GIF frame, preserve neighbors.
        [DataRow(0, 0, 4, 3, 6)] // Padded destination rows.
        [DataRow(2, 0, 1, 3, 6)] // Partial frame with padded rows.
        public void UploadChangesOnlyTheDirtyPixels(int x, int y, int width, int height, int stridePixels)
        {
            const int canvasWidth = 4, canvasHeight = 3;
            var pixels = Enumerable.Range(0, canvasWidth * canvasHeight).Select(i => unchecked((int)0x80302010) + i).ToArray();
            var expected = Enumerable.Repeat(unchecked((int)0xdeadbeef), stridePixels * canvasHeight).ToArray();
            var memory = Marshal.AllocHGlobal(expected.Length * 4);
            try
            {
                Marshal.Copy(expected, 0, memory, expected.Length);
                GifPlayer.CopyPixels(pixels, canvasWidth, new Int32Rect(x, y, width, height), memory, stridePixels * 4);
                for (var row = y; row < y + height; row++)
                    for (var column = x; column < x + width; column++)
                        expected[row * stridePixels + column] = pixels[row * canvasWidth + column];
                var actual = new int[expected.Length];
                Marshal.Copy(memory, actual, 0, actual.Length);
                CollectionAssert.AreEqual(expected, actual);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}
