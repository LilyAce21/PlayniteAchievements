using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers how package images are written: portable formats as they are, anything else as PNG.
    /// </summary>
    [TestClass]
    public class PackageImageFormatTests
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        [TestMethod]
        public void IsPortable_RecognizesPortableSignatures()
        {
            Assert.IsTrue(PackageImageFormat.IsPortable(PngSignature));
            Assert.IsTrue(PackageImageFormat.IsPortable(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
            Assert.IsTrue(PackageImageFormat.IsPortable(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }));
            Assert.IsTrue(PackageImageFormat.IsPortable(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }));
            Assert.IsTrue(PackageImageFormat.IsPortable(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }));
        }

        [TestMethod]
        public void IsPortable_RejectsIconBitmapAndRiffAudio()
        {
            Assert.IsFalse(PackageImageFormat.IsPortable(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }));
            Assert.IsFalse(PackageImageFormat.IsPortable(new byte[] { 0x42, 0x4D, 0, 0 }));
            Assert.IsFalse(PackageImageFormat.IsPortable(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }));
            Assert.IsFalse(PackageImageFormat.IsPortable(new byte[0]));
        }

        [TestMethod]
        public void CopyPortable_KeepsPngBytes()
        {
            var png = EncodePng(4, 4);
            var copied = Copy(png, ".png");

            CollectionAssert.AreEqual(png, copied);
        }

        [TestMethod]
        public void CopyPortable_ConvertsBitmapToPng()
        {
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Solid(5, 3)));
            var bmp = Save(encoder);

            var copied = Copy(bmp, ".png");

            AssertPng(copied, 5, 3);
        }

        [TestMethod]
        public void CopyPortable_ConvertsIconToPngOfItsLargestImage()
        {
            // An icon directory holding two PNG-compressed images, the smaller one first.
            var small = EncodePng(16, 16);
            var large = EncodePng(32, 32);
            var ico = BuildIcon(new[] { small, large }, new[] { 16, 32 });

            var copied = Copy(ico, ".png");

            AssertPng(copied, 32, 32);
        }

        private static byte[] Copy(byte[] bytes, string extension)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
            try
            {
                File.WriteAllBytes(path, bytes);
                using (var destination = new MemoryStream())
                {
                    PackageImageFormat.CopyPortable(path, destination);
                    return destination.ToArray();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertPng(byte[] bytes, int width, int height)
        {
            Assert.IsTrue(PackageImageFormat.IsPortable(bytes));
            CollectionAssert.AreEqual(PngSignature, bytes.Take(8).ToArray());
            using (var stream = new MemoryStream(bytes))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Assert.AreEqual(width, frame.PixelWidth);
                Assert.AreEqual(height, frame.PixelHeight);
            }
        }

        private static BitmapSource Solid(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x40;
                pixels[i + 1] = 0x80;
                pixels[i + 2] = 0xC0;
                pixels[i + 3] = 0xFF;
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        }

        private static byte[] EncodePng(int width, int height)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Solid(width, height)));
            return Save(encoder);
        }

        private static byte[] Save(BitmapEncoder encoder)
        {
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static byte[] BuildIcon(byte[][] images, int[] sizes)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)images.Length);
                var offset = 6 + 16 * images.Length;
                for (var i = 0; i < images.Length; i++)
                {
                    writer.Write((byte)sizes[i]);
                    writer.Write((byte)sizes[i]);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }

                foreach (var image in images)
                {
                    writer.Write(image);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
