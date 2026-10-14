using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Images.Webm;
using PlayniteAchievements.Views.Helpers.Gif;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// One moment of an animated notification image: the frame showing at a time into its own
    /// timeline, held on the last frame past the end. Frame timing comes from the same delays the
    /// players use (GIF and WebM frame delays, normalized WebP durations), so a keyframe matches
    /// what plays. GIF, animated WebP and WebM are supported; anything else has no timeline.
    /// </summary>
    internal static class AnimatedImageKeyframe
    {
        private const int CacheCapacity = 32;

        private static readonly object CacheSync = new object();
        private static readonly LinkedList<KeyValuePair<string, BitmapSource>> CacheOrder =
            new LinkedList<KeyValuePair<string, BitmapSource>>();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> CacheIndex =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The image's frame delays in milliseconds, or null when it is a still image, a single
        /// frame, missing, or unreadable.
        /// </summary>
        internal static IReadOnlyList<int> TryReadFrameDelays(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var extension = ImageFormats.GetExtension(path);
                IReadOnlyList<int> delays = null;
                if (ImageFormats.IsGifExtension(extension))
                {
                    var frames = GifImage.Parse(File.ReadAllBytes(path)).Frames;
                    delays = Array.ConvertAll(frames, frame => frame.DelayMs);
                }
                else if (ImageFormats.IsWebmExtension(extension))
                {
                    var frames = WebmContainer.Parse(File.ReadAllBytes(path)).Frames;
                    delays = Array.ConvertAll(frames, frame => frame.DelayMs);
                }
                else if (ImageFormats.IsWebpExtension(extension) &&
                         WebpAnimationInfo.TryReadFrameDurations(path, out var durations))
                {
                    delays = durations;
                }

                return delays != null && delays.Count > 1 ? delays : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The length of one loop in milliseconds; 0 for an image with no timeline.</summary>
        internal static int DurationMs(string path)
        {
            var delays = TryReadFrameDelays(path);
            if (delays == null)
            {
                return 0;
            }

            var total = 0;
            foreach (var delay in delays)
            {
                total += delay;
            }

            return total;
        }

        /// <summary>
        /// The frame showing <paramref name="timeMs"/> into the timeline: the last frame whose
        /// start is at or before it, so a time past the end holds the last frame.
        /// </summary>
        internal static int FrameIndexAt(IReadOnlyList<int> delays, int timeMs)
        {
            if (delays == null || delays.Count == 0)
            {
                return 0;
            }

            var start = 0;
            for (var index = 0; index < delays.Count; index++)
            {
                start += delays[index];
                if (start > timeMs)
                {
                    return index;
                }
            }

            return delays.Count - 1;
        }

        /// <summary>
        /// The frozen frame of the animated image at <paramref name="path"/> showing
        /// <paramref name="timeMs"/> into its timeline, at full size. Null when the image has no
        /// timeline or cannot be decoded, so callers keep their still-image path. Results are
        /// cached by file identity and time, so repeated reads of an unchanged file cost nothing.
        /// </summary>
        internal static BitmapSource TryDecodeAt(string path, int timeMs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var info = new FileInfo(path);
                var key = string.Concat(
                    info.FullName, "|", info.LastWriteTimeUtc.Ticks.ToString(), "|",
                    info.Length.ToString(), "|", Math.Max(0, timeMs).ToString());
                if (TryGetCached(key, out var cached))
                {
                    return cached;
                }

                var decoded = Decode(path, Math.Max(0, timeMs));
                if (decoded != null)
                {
                    AddCached(key, decoded);
                }

                return decoded;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource Decode(string path, int timeMs)
        {
            var extension = ImageFormats.GetExtension(path);
            if (ImageFormats.IsGifExtension(extension))
            {
                return DecodeGif(GifImage.Parse(File.ReadAllBytes(path)), timeMs);
            }

            if (ImageFormats.IsWebmExtension(extension))
            {
                var image = WebmContainer.Parse(File.ReadAllBytes(path));
                if (image.Frames.Length < 2)
                {
                    return null;
                }

                var index = FrameIndexAt(Array.ConvertAll(image.Frames, frame => frame.DelayMs), timeMs);
                return WebmStill.DecodeFrame(image, index, 0);
            }

            if (ImageFormats.IsWebpExtension(extension))
            {
                return DecodeWebp(path, timeMs);
            }

            return null;
        }

        private static BitmapSource DecodeGif(GifImage image, int timeMs)
        {
            if (image.Frames.Length < 2)
            {
                return null;
            }

            // Frames composite in order (disposal depends on the previous frame), so every frame
            // up to the target is drawn on the way.
            var index = FrameIndexAt(Array.ConvertAll(image.Frames, frame => frame.DelayMs), timeMs);
            var canvas = new GifCanvas(image);
            for (var frame = 0; frame <= index; frame++)
            {
                canvas.Render(frame);
            }

            var bitmap = BitmapSource.Create(
                image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, canvas.Pixels, image.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource DecodeWebp(string path, int timeMs)
        {
            if (!WebpCodecProbe.IsSupported || !WebpAnimationInfo.TryReadFrameDurations(path, out var durations) ||
                durations.Count < 2)
            {
                return null;
            }

            // WIC hands back each WebP frame already composited to the full canvas.
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var index = Math.Min(FrameIndexAt(durations, timeMs), decoder.Frames.Count - 1);
            var frame = decoder.Frames[index];
            frame.Freeze();
            return frame;
        }

        private static bool TryGetCached(string key, out BitmapSource bitmap)
        {
            lock (CacheSync)
            {
                if (CacheIndex.TryGetValue(key, out var node))
                {
                    CacheOrder.Remove(node);
                    CacheOrder.AddFirst(node);
                    bitmap = node.Value.Value;
                    return true;
                }
            }

            bitmap = null;
            return false;
        }

        private static void AddCached(string key, BitmapSource bitmap)
        {
            lock (CacheSync)
            {
                if (CacheIndex.ContainsKey(key))
                {
                    return;
                }

                CacheIndex[key] = CacheOrder.AddFirst(new KeyValuePair<string, BitmapSource>(key, bitmap));
                while (CacheOrder.Count > CacheCapacity)
                {
                    var last = CacheOrder.Last;
                    CacheOrder.RemoveLast();
                    CacheIndex.Remove(last.Value.Key);
                }
            }
        }
    }
}
