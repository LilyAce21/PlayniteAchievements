using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Resolves an advancement's display item to its item or block texture, through the item's
    /// model when it has one and by name otherwise, and writes it to the plugin's icon cache,
    /// cropped to the first animation frame and scaled up with nearest neighbour so the pixel art
    /// stays sharp.
    /// </summary>
    internal sealed class MinecraftIconExtractor
    {
        public const int IconSize = 64;

        private static readonly string[] TextureFolders = { "item", "block", "items", "blocks" };

        // Items whose texture is a variant, and blocks whose plain name has no texture.
        private static readonly string[] TextureSuffixes = { string.Empty, "_00", "_standby", "_side", "_front", "_top" };

        private readonly string _cacheFolder;
        private readonly ILogger _logger;
        private readonly Dictionary<IReadOnlyList<MinecraftResourceSource>, Dictionary<string, string>> _resolved =
            new Dictionary<IReadOnlyList<MinecraftResourceSource>, Dictionary<string, string>>();

        public MinecraftIconExtractor(string pluginUserDataPath, ILogger logger)
        {
            _cacheFolder = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, "icon_cache", "minecraft");
            _logger = logger;
        }

        /// <summary>
        /// The cached icon file for a namespaced item id, or null when no source has its texture.
        /// <paramref name="sourcesByPriority"/> is ascending; the last source with the texture wins.
        /// </summary>
        public string GetIconPath(string itemId, float? customModelData, IReadOnlyList<MinecraftResourceSource> sourcesByPriority)
        {
            if (_cacheFolder == null || string.IsNullOrWhiteSpace(itemId) || sourcesByPriority == null)
            {
                return null;
            }

            // Lookups are per source list: each instance has its own game jar and mods.
            if (!_resolved.TryGetValue(sourcesByPriority, out var resolved))
            {
                resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _resolved[sourcesByPriority] = resolved;
            }

            var key = customModelData.HasValue ? $"{itemId}#{customModelData.Value}" : itemId;
            if (resolved.TryGetValue(key, out var known))
            {
                return known;
            }

            var path = Resolve(itemId, customModelData, sourcesByPriority);
            resolved[key] = path;
            return path;
        }

        private string Resolve(string itemId, float? customModelData, IReadOnlyList<MinecraftResourceSource> sources)
        {
            var separator = itemId.IndexOf(':');
            var ns = separator > 0 ? itemId.Substring(0, separator) : "minecraft";
            var name = separator > 0 ? itemId.Substring(separator + 1) : itemId;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            {
                return null;
            }

            var entries = MinecraftModelResolver.ResolveTextureEntries(ns, name, customModelData, sources)
                .Concat(CandidateEntries(ns, name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                for (var i = sources.Count - 1; i >= 0; i--)
                {
                    var source = sources[i];
                    if (!source.Textures.Contains(entry))
                    {
                        continue;
                    }

                    // Cached by texture, so two items drawn with the same texture share one file.
                    var relative = entry.Substring("assets/".Length).Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(_cacheFolder, relative);
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= source.StampUtc)
                    {
                        return target;
                    }

                    var bytes = source.ReadBytes(entry);
                    if (bytes != null && TryWriteIcon(bytes, target))
                    {
                        return target;
                    }
                }
            }

            return null;
        }

        /// <summary>Texture names guessed from the item id, for items without a resolvable model.</summary>
        private static IEnumerable<string> CandidateEntries(string ns, string name)
        {
            foreach (var suffix in TextureSuffixes)
            {
                foreach (var folder in TextureFolders)
                {
                    yield return $"assets/{ns}/textures/{folder}/{name}{suffix}.png".ToLowerInvariant();
                }
            }
        }

        private bool TryWriteIcon(byte[] png, string target)
        {
            try
            {
                var scaled = ScaleNearest(png, IconSize);
                if (scaled == null)
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var temp = target + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(scaled));
                    encoder.Save(stream);
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                _logger?.Debug(ex, $"[Minecraft] Could not write icon '{target}'.");
                return false;
            }
        }

        /// <summary>
        /// Decodes a texture, keeps the top square (animated textures stack frames vertically) and
        /// scales it to <paramref name="size"/> pixels with nearest neighbour.
        /// </summary>
        internal static BitmapSource ScaleNearest(byte[] png, int size)
        {
            BitmapSource decoded;
            using (var input = new MemoryStream(png))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                decoded = decoder.Frames.FirstOrDefault();
            }

            if (decoded == null || decoded.PixelWidth <= 0 || decoded.PixelHeight <= 0)
            {
                return null;
            }

            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = Math.Min(converted.PixelHeight, width);
            var sourceStride = width * 4;
            var sourcePixels = new byte[sourceStride * height];
            converted.CopyPixels(new System.Windows.Int32Rect(0, 0, width, height), sourcePixels, sourceStride, 0);

            var targetStride = size * 4;
            var targetPixels = new byte[targetStride * size];
            for (var y = 0; y < size; y++)
            {
                var sourceY = y * height / size;
                for (var x = 0; x < size; x++)
                {
                    var sourceX = x * width / size;
                    Buffer.BlockCopy(sourcePixels, sourceY * sourceStride + sourceX * 4, targetPixels, y * targetStride + x * 4, 4);
                }
            }

            var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, targetPixels, targetStride);
            result.Freeze();
            return result;
        }
    }
}
