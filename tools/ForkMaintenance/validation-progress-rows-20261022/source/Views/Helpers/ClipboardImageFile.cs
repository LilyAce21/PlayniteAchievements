using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Saves raw image data from the clipboard (a screenshot, or "Copy image" in an app that
    /// carries no file) as a PNG file, so image slots can take it like any other local file.
    /// Every slot copies the file into its own storage, so the staging folder holds nothing
    /// the plugin depends on.
    /// </summary>
    public static class ClipboardImageFile
    {
        /// <summary>The clipboard format browsers and the Snipping Tool use for PNG bytes with alpha.</summary>
        public const string PngFormat = "PNG";

        /// <summary>Where pasted images are staged before a slot copies them.</summary>
        public static string DefaultDirectory =>
            Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "Pasted");

        /// <summary>
        /// Writes the payload's image to <paramref name="directory"/> as a PNG named by its content
        /// hash, so pasting the same image twice reuses one file. Prefers the PNG stream, which keeps
        /// transparency, over the bitmap format.
        /// </summary>
        public static bool TryWrite(IDataObject data, string directory, out string path)
        {
            path = null;
            if (data == null || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var png = ReadPngStream(data) ?? EncodeBitmap(data);
            if (png == null || png.Length == 0)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
                string name;
                using (var sha = SHA1.Create())
                {
                    name = BitConverter.ToString(sha.ComputeHash(png)).Replace("-", string.Empty).ToLowerInvariant() + ".png";
                }

                var target = Path.Combine(directory, name);
                if (!File.Exists(target))
                {
                    File.WriteAllBytes(target, png);
                }

                path = target;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static byte[] ReadPngStream(IDataObject data)
        {
            try
            {
                if (!data.GetDataPresent(PngFormat, false) || !(data.GetData(PngFormat, false) is Stream stream))
                {
                    return null;
                }

                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                byte[] bytes;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    bytes = copy.ToArray();
                }

                using (var check = new MemoryStream(bytes))
                {
                    BitmapDecoder.Create(check, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }

                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] EncodeBitmap(IDataObject data)
        {
            try
            {
                if (!data.GetDataPresent(DataFormats.Bitmap, true) ||
                    !(data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap))
                {
                    return null;
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
