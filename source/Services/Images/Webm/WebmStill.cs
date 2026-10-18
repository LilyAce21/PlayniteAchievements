using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images.Webm
{
    /// <summary>
    /// The first frame of a WebM file as a frozen bitmap, for every surface that shows a still:
    /// single-frame WebM files, thumbnails, and the fallback when playback is unavailable.
    /// </summary>
    internal static class WebmStill
    {
        /// <summary>
        /// Decodes the first frame of the WebM at <paramref name="pathOrUri"/> (a local path or file
        /// URI), scaled down to <paramref name="decodePixel"/> wide when that is smaller. Null when
        /// the file is missing, unreadable, or this machine lacks the decoder.
        /// </summary>
        internal static BitmapSource TryDecode(string pathOrUri, int decodePixel)
        {
            try
            {
                var path = ToLocalPath(pathOrUri);
                if (path == null || !File.Exists(path))
                {
                    return null;
                }

                return Decode(File.ReadAllBytes(path), decodePixel);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the file parses as a WebM whose codec this machine can decode.</summary>
        internal static bool IsReadable(string path)
        {
            try
            {
                var image = WebmContainer.Parse(File.ReadAllBytes(path));
                return WebmCodecProbe.IsCodecSupported(image.Codec);
            }
            catch
            {
                return false;
            }
        }

        private static BitmapSource Decode(byte[] payload, int decodePixel)
        {
            return DecodeFrame(WebmContainer.Parse(payload), 0, decodePixel);
        }

        /// <summary>
        /// Decodes frame <paramref name="frameIndex"/> of a parsed WebM (every frame before it is
        /// decoded on the way, as the codec requires), scaled down to <paramref name="decodePixel"/>
        /// wide when that is smaller; 0 keeps the full size. Throws when this machine lacks the decoder.
        /// </summary>
        internal static BitmapSource DecodeFrame(WebmImage image, int frameIndex, int decodePixel)
        {
            int[] pixels;
            using (var decoder = new WebmFrameDecoder(image))
            {
                decoder.Decode(frameIndex);
                pixels = decoder.Pixels;
            }

            BitmapSource bitmap = BitmapSource.Create(
                image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);

            if (decodePixel > 0 && decodePixel < image.Width)
            {
                var scale = (double)decodePixel / image.Width;
                var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                bitmap = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }

            bitmap.Freeze();
            return bitmap;
        }

        private static string ToLocalPath(string pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                return null;
            }

            if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri))
            {
                return uri.IsFile ? uri.LocalPath : null;
            }

            return pathOrUri;
        }
    }
}
