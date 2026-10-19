using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    [TestCategory("Views")]
    public class ClipboardImagePasteTests
    {
        private string _directory;

        [TestInitialize]
        public void Initialize()
        {
            _directory = Path.Combine(Path.GetTempPath(), "PlayAchPasteTests", Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [TestMethod]
        public void TryWrite_SavesThePngStreamAsIs_AndReusesTheFileForTheSameBytes()
        {
            var png = EncodePng(CreateBitmap(3, 2));
            var data = new DataObject();
            data.SetData(ClipboardImageFile.PngFormat, new MemoryStream(png));

            Assert.IsTrue(ClipboardImageFile.TryWrite(data, _directory, out var first));
            CollectionAssert.AreEqual(png, File.ReadAllBytes(first));
            Assert.AreEqual(".png", Path.GetExtension(first));

            var again = new DataObject();
            again.SetData(ClipboardImageFile.PngFormat, new MemoryStream(png));
            Assert.IsTrue(ClipboardImageFile.TryWrite(again, _directory, out var second));
            Assert.AreEqual(first, second);
            Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
        }

        [TestMethod]
        public void TryWrite_EncodesABitmapAsPng_WhenNoPngStreamIsPresent()
        {
            var data = new DataObject();
            data.SetImage(CreateBitmap(4, 5));

            Assert.IsTrue(ClipboardImageFile.TryWrite(data, _directory, out var path));
            using (var stream = File.OpenRead(path))
            {
                var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Assert.AreEqual(4, frame.PixelWidth);
                Assert.AreEqual(5, frame.PixelHeight);
            }
        }

        [TestMethod]
        public void TryWrite_SkipsAPngStreamThatDoesNotDecode_AndFallsBackToTheBitmap()
        {
            var data = new DataObject();
            data.SetData(ClipboardImageFile.PngFormat, new MemoryStream(new byte[] { 1, 2, 3, 4 }));
            data.SetImage(CreateBitmap(2, 2));

            Assert.IsTrue(ClipboardImageFile.TryWrite(data, _directory, out var path));
            Assert.IsTrue(File.ReadAllBytes(path).Length > 4);
        }

        [TestMethod]
        public void TryWrite_IsFalseWithoutImageData()
        {
            var data = new DataObject(DataFormats.UnicodeText, "just text");

            Assert.IsFalse(ClipboardImageFile.TryWrite(data, _directory, out var path));
            Assert.IsNull(path);
            Assert.IsFalse(ClipboardImageFile.TryWrite(null, _directory, out _));
            Assert.IsFalse(Directory.Exists(_directory), "nothing is written when there is no image");
        }

        [TestMethod]
        public void TryGetLocalImageFile_PrefersACopiedImageFile()
        {
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, "copied.png");
            File.WriteAllBytes(file, EncodePng(CreateBitmap(2, 2)));
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file });
            data.SetImage(CreateBitmap(6, 6));

            Assert.IsTrue(FilePickTarget.TryGetLocalImageFile(data, _directory, out var path));
            Assert.AreEqual(file, path);
        }

        [TestMethod]
        public void TryGetLocalImageFile_SkipsAUrlForTheImageDataBesideIt()
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "https://example.com/cover.png");
            data.SetImage(CreateBitmap(3, 3));

            Assert.IsTrue(FilePickTarget.TryGetLocalImageFile(data, _directory, out var path));
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(path.StartsWith(_directory, StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void TryGetLocalImageFile_IsFalseForAUrlAlone()
        {
            var data = new DataObject(DataFormats.UnicodeText, "https://example.com/cover.png");

            Assert.IsFalse(FilePickTarget.TryGetLocalImageFile(data, _directory, out _));
        }

        private static BitmapSource CreateBitmap(int width, int height)
        {
            var stride = width * 4;
            var pixels = Enumerable.Range(0, stride * height).Select(i => (byte)(i * 37)).ToArray();
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static byte[] EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }
    }
}
