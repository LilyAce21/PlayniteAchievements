using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Notifications
{
    /// <summary>
    /// A Steam Achievement Notifier (.san) theme, unpacked and prepared for the official SAN
    /// renderer page. The theme carries everything SAN itself would use to draw the notification:
    /// the preset (layout and animation), every customisation value, and the theme's own images.
    /// </summary>
    internal sealed class OfficialSanTheme
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string SourcePath { get; set; }
        public string CacheDir { get; set; }
        public string Preset { get; set; }
        public string PresetHtml { get; set; }
        public double BaseWidth { get; set; } = 300;
        public double BaseHeight { get; set; } = 50;
        public double Offset { get; set; } = 20;
        public string GlobalCssUrl { get; set; }
        public string BaseCssUrl { get; set; }
        public string BaseAnimCssUrl { get; set; }
        public string PresetCssUrl { get; set; }

        /// <summary>The theme's customisation block with asset paths resolved and runtime defaults filled in.</summary>
        public JObject Customisation { get; set; }

        public double ThemeScale => Math.Max(0.1, (Customisation?.Value<double?>("scale") ?? 100) / 100.0);
        public double ThemeDisplayTimeSeconds => Math.Max(1, Customisation?.Value<double?>("displaytime") ?? 8);
    }

    /// <summary>
    /// Loads .san theme archives (zip files holding <c>usertheme.json</c> and an <c>assets</c>
    /// folder), unpacks them into a cache folder and merges SAN's runtime defaults over them,
    /// the same way Steam Achievement Notifier prepares a theme before drawing it.
    /// </summary>
    internal static class OfficialSanThemeService
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, OfficialSanTheme> Cache =
            new Dictionary<string, OfficialSanTheme>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ttf", ".otf",
            ".wav", ".mp3", ".ogg", ".aac", ".flac", ".m4a"
        };

        private sealed class PresetDefaults
        {
            public string[] Logo;
            public string[][] Decoration;
            public string[] Elems;
        }

        private const string TrophyBronze = "sanlogotrophy_bronze.svg";
        private const string TrophySilver = "sanlogotrophy_silver.svg";
        private const string TrophyGold = "sanlogotrophy_gold.svg";

        private static string[][] TrophyTiers() => new[]
        {
            new[] { "img", TrophyBronze },
            new[] { "img", TrophySilver },
            new[] { "img", TrophyGold }
        };

        private static readonly Dictionary<string, PresetDefaults> Defaults = new Dictionary<string, PresetDefaults>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new PresetDefaults { Logo = new[] { "img", "sanlogosquare.svg" }, Decoration = new[] { new[] { "img", "sanlogotrophy.svg" } }, Elems = new[] { "unlockmsg", "title", "desc" } },
            ["xqjan"] = new PresetDefaults { Logo = new[] { "img", "steamlogonew.svg" }, Elems = new[] { "unlockmsg", "title", "desc" } },
            ["steamdeck"] = new PresetDefaults { Decoration = new[] { new[] { "img", "ribbonbw.svg" } }, Elems = new[] { "title", "desc" } },
            ["epicgames"] = new PresetDefaults { Decoration = TrophyTiers(), Elems = new[] { "title" } },
            ["xboxone"] = new PresetDefaults { Logo = new[] { "img", "sanlogotrophy.svg" }, Elems = new[] { "unlockmsg", "title", "desc" } },
            ["xbox360"] = new PresetDefaults { Logo = new[] { "img", "sanlogotrophy_small.svg" }, Elems = new[] { "unlockmsg", "title" } },
            ["ps5"] = new PresetDefaults { Logo = new[] { "img", "sanlogo.svg" }, Decoration = TrophyTiers(), Elems = new[] { "title", "desc" } },
            ["ps4"] = new PresetDefaults { Logo = new[] { "img", "sanlogo.svg" }, Decoration = TrophyTiers(), Elems = new[] { "unlockmsg", "title" } },
            ["ps3"] = new PresetDefaults { Decoration = TrophyTiers(), Elems = new[] { "unlockmsg", "title" } },
            ["windows"] = new PresetDefaults { Logo = new[] { "img", "sanlogotrophy.svg" }, Elems = new[] { "unlockmsg", "title", "desc" } },
            ["gfwl"] = new PresetDefaults { Logo = new[] { "img", "sanlogotrophy_small.svg" }, Elems = new[] { "unlockmsg", "title" } },
            ["os"] = new PresetDefaults { Logo = new[] { "img", "sanlogotrophy.svg" }, Elems = new[] { "unlockmsg", "title", "desc" } }
        };

        private static readonly Dictionary<string, double[]> FallbackDimensions = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new double[] { 300, 50 },
            ["xqjan"] = new double[] { 300, 50 },
            ["steamdeck"] = new double[] { 300, 50 },
            ["epicgames"] = new double[] { 300, 50 },
            ["xboxone"] = new double[] { 400, 100 },
            ["xbox360"] = new double[] { 400, 100 },
            ["ps5"] = new double[] { 300, 50 },
            ["ps4"] = new double[] { 300, 50 },
            ["ps3"] = new double[] { 300, 50 },
            ["windows"] = new double[] { 300, 80 },
            ["gfwl"] = new double[] { 300, 80 },
            ["os"] = new double[] { 360, 120 }
        };

        /// <summary>The folder the SAN runtime (notify, img, fonts ...) was bundled into.</summary>
        public static string FindRuntimeRoot(string pluginDirectory)
        {
            var candidates = new[]
            {
                Path.Combine(pluginDirectory ?? string.Empty, "Resources", "Tools", "OfficialSan", "runtime"),
                Path.Combine(pluginDirectory ?? string.Empty, "Resources", "Tools", "SAN-source")
            };

            foreach (var root in candidates)
            {
                if (File.Exists(Path.Combine(root, "notify", "base.css")) &&
                    Directory.Exists(Path.Combine(root, "notify", "presets")))
                {
                    return root;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Loads the theme at <paramref name="sanPath"/>. Returns a fresh copy every call so the
        /// caller can edit the customisation (rarity icons, scale ...) for one notification.
        /// </summary>
        public static OfficialSanTheme Load(string sanPath, string runtimeRoot, string cacheRoot)
        {
            if (string.IsNullOrWhiteSpace(sanPath) || !File.Exists(sanPath))
            {
                throw new FileNotFoundException("The .san theme file was not found.", sanPath);
            }

            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                throw new DirectoryNotFoundException("The bundled SAN runtime files were not found.");
            }

            var info = new FileInfo(sanPath);
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (Sync)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    return Clone(cached);
                }

                var theme = Build(info, runtimeRoot, cacheRoot, key);
                Cache[key] = theme;
                return Clone(theme);
            }
        }

        private static OfficialSanTheme Clone(OfficialSanTheme source)
        {
            return new OfficialSanTheme
            {
                Id = source.Id,
                Label = source.Label,
                SourcePath = source.SourcePath,
                CacheDir = source.CacheDir,
                Preset = source.Preset,
                PresetHtml = source.PresetHtml,
                BaseWidth = source.BaseWidth,
                BaseHeight = source.BaseHeight,
                Offset = source.Offset,
                GlobalCssUrl = source.GlobalCssUrl,
                BaseCssUrl = source.BaseCssUrl,
                BaseAnimCssUrl = source.BaseAnimCssUrl,
                PresetCssUrl = source.PresetCssUrl,
                Customisation = (JObject)source.Customisation.DeepClone()
            };
        }

        private static OfficialSanTheme Build(FileInfo file, string runtimeRoot, string cacheRoot, string key)
        {
            var id = HashId(key);
            var cacheDir = Path.Combine(cacheRoot, id);
            EnsureExtracted(file, cacheDir);

            var themeJson = File.ReadAllText(Path.Combine(cacheDir, "usertheme.json"), Encoding.UTF8);
            var theme = JObject.Parse(themeJson);
            var rawCustomisation = theme["customisation"] as JObject;
            if (rawCustomisation == null)
            {
                throw new InvalidDataException("The .san theme has no customisation block.");
            }

            var resolved = (JObject)RewriteAssetPaths(cacheDir, rawCustomisation);
            var customisation = MergeRuntimeDefaults(resolved, runtimeRoot);
            var preset = NormalizePresetKey(customisation.Value<string>("preset"));

            var presetDir = Path.Combine(runtimeRoot, "notify", "presets", preset);
            if (!Directory.Exists(presetDir))
            {
                preset = "default";
                presetDir = Path.Combine(runtimeRoot, "notify", "presets", preset);
                customisation["preset"] = preset;
            }

            var presetHtml = File.ReadAllText(Path.Combine(presetDir, "index.html"), Encoding.UTF8);
            var dims = ReadDimensions(presetHtml, preset);

            var label = (theme.Value<string>("label") ?? string.Empty).Trim();
            if (label.Length == 0)
            {
                label = Path.GetFileNameWithoutExtension(file.Name);
            }

            return new OfficialSanTheme
            {
                Id = id,
                Label = label,
                SourcePath = file.FullName,
                CacheDir = cacheDir,
                Preset = preset,
                PresetHtml = presetHtml,
                BaseWidth = dims.Item1,
                BaseHeight = dims.Item2,
                Offset = dims.Item3,
                GlobalCssUrl = ToFileUri(Path.Combine(runtimeRoot, "dist", "app", "global.css")),
                BaseCssUrl = ToFileUri(Path.Combine(runtimeRoot, "notify", "base.css")),
                BaseAnimCssUrl = ToFileUri(Path.Combine(runtimeRoot, "notify", "baseanim.css")),
                PresetCssUrl = ToFileUri(Path.Combine(presetDir, "styles.css")),
                Customisation = customisation
            };
        }

        private static Tuple<double, double, double> ReadDimensions(string presetHtml, string preset)
        {
            var match = Regex.Match(
                presetHtml ?? string.Empty,
                "<meta\\s+width\\s*=\\s*\"(\\d+)\"\\s+height\\s*=\\s*\"(\\d+)\"(?:\\s+offset\\s*=\\s*\"(\\d+)\")?\\s*/?>",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var w = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var h = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var o = match.Groups[3].Success ? double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 20;
                return Tuple.Create(w > 0 ? w : 300, h > 0 ? h : 50, o > 0 ? o : 20);
            }

            var fallback = FallbackDimensions.TryGetValue(preset, out var dims) ? dims : FallbackDimensions["default"];
            return Tuple.Create(fallback[0], fallback[1], 20.0);
        }

        private static string HashId(string key)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return string.Concat(bytes.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static void EnsureExtracted(FileInfo file, string cacheDir)
        {
            var marker = Path.Combine(cacheDir, ".source");
            var stamp = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            if (File.Exists(marker) && File.Exists(Path.Combine(cacheDir, "usertheme.json")) && File.ReadAllText(marker) == stamp)
            {
                return;
            }

            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }

            Directory.CreateDirectory(cacheDir);
            var root = Path.GetFullPath(cacheDir);
            using (var archive = ZipFile.OpenRead(file.FullName))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = (entry.FullName ?? string.Empty).Replace('\\', '/');
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") ||
                        name.Split('/').Any(part => part == ".."))
                    {
                        throw new InvalidDataException("Unsafe entry in .san archive: " + entry.FullName);
                    }

                    var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Unsafe extraction target in .san archive: " + entry.FullName);
                    }

                    if (name.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }

            if (!File.Exists(Path.Combine(cacheDir, "usertheme.json")))
            {
                throw new InvalidDataException("The .san archive is missing usertheme.json.");
            }

            File.WriteAllText(marker, stamp);
        }

        private static string NormalizePresetKey(string preset)
        {
            var key = Regex.Replace((preset ?? string.Empty).ToLowerInvariant(), "[^a-z0-9_-]", string.Empty);
            return key.Length == 0 ? "default" : key;
        }

        private static string ToFileUri(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : new Uri(path).AbsoluteUri;
        }

        private static string ResolveAssetPath(string cacheDir, string value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                var candidate = Path.Combine(cacheDir, "assets", Path.GetFileName(raw));
                return File.Exists(candidate) ? candidate : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static JToken RewriteAssetPaths(string cacheDir, JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var obj = new JObject();
                    foreach (var property in ((JObject)token).Properties())
                    {
                        obj[property.Name] = RewriteAssetPaths(cacheDir, property.Value);
                    }

                    return obj;
                case JTokenType.Array:
                    return new JArray(((JArray)token).Select(item => RewriteAssetPaths(cacheDir, item)));
                case JTokenType.String:
                    var text = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return token.DeepClone();
                    }

                    string extension;
                    try
                    {
                        extension = Path.GetExtension(text);
                    }
                    catch
                    {
                        return token.DeepClone();
                    }

                    if (!AssetExtensions.Contains(extension ?? string.Empty))
                    {
                        return token.DeepClone();
                    }

                    var resolved = ResolveAssetPath(cacheDir, text);
                    return new JValue(resolved.Length > 0 ? resolved : text);
                default:
                    return token.DeepClone();
            }
        }

        private static bool Truthy(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.String)
            {
                return !string.IsNullOrEmpty(token.Value<string>());
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return true;
        }

        private static string RuntimeFileIfExists(string runtimeRoot, params string[] parts)
        {
            var path = Path.Combine(new[] { runtimeRoot }.Concat(parts).ToArray());
            return File.Exists(path) ? path : string.Empty;
        }

        private static JToken RuntimeAssetValue(string runtimeRoot, PresetDefaults defaults, bool decoration)
        {
            if (!decoration)
            {
                return defaults.Logo == null
                    ? null
                    : new JValue(Path.Combine(new[] { runtimeRoot }.Concat(defaults.Logo).ToArray()));
            }

            if (defaults.Decoration == null)
            {
                return null;
            }

            if (defaults.Decoration.Length == 1)
            {
                return new JValue(Path.Combine(new[] { runtimeRoot }.Concat(defaults.Decoration[0]).ToArray()));
            }

            return new JArray(defaults.Decoration.Select(entry =>
                (JToken)new JValue(Path.Combine(new[] { runtimeRoot }.Concat(entry).ToArray()))));
        }

        private static void SetIfMissing(JObject target, string name, string fallbackPath)
        {
            if (!Truthy(target[name]))
            {
                target[name] = fallbackPath ?? string.Empty;
            }
        }

        private static JObject MergeRuntimeDefaults(JObject customisation, string runtimeRoot)
        {
            var presetRaw = customisation.Value<string>("preset");
            var presetKey = NormalizePresetKey(presetRaw);
            var defaults = Defaults.TryGetValue(presetKey, out var found) ? found : Defaults["default"];

            var icons = customisation["customicons"] as JObject ?? new JObject();
            var existing = (icons[presetRaw ?? string.Empty] ?? icons[presetKey]) as JObject ?? new JObject();

            var merged = new JObject();
            var logo = RuntimeAssetValue(runtimeRoot, defaults, false);
            if (logo != null) merged["logo"] = logo;
            var decoration = RuntimeAssetValue(runtimeRoot, defaults, true);
            if (decoration != null) merged["decoration"] = decoration;
            merged["elems"] = new JArray(defaults.Elems);
            foreach (var property in existing.Properties())
            {
                merged[property.Name] = property.Value.DeepClone();
            }

            icons[presetKey] = merged.DeepClone();
            if (!string.IsNullOrEmpty(presetRaw) && presetRaw != presetKey)
            {
                icons[presetRaw] = merged.DeepClone();
            }

            customisation["preset"] = presetKey;
            customisation["customicons"] = icons;
            customisation["iconanim"] = customisation["iconanim"] == null || customisation.Value<bool?>("iconanim") != false;
            customisation["showdecoration"] = customisation["showdecoration"] == null || customisation.Value<bool?>("showdecoration") != false;

            SetIfMissing(customisation, "bgimg", RuntimeFileIfExists(runtimeRoot, "img", "sanimgbg.png"));
            SetIfMissing(customisation, "base64", RuntimeFileIfExists(runtimeRoot, "img", "base64.png"));
            SetIfMissing(customisation, "gameicon", RuntimeFileIfExists(runtimeRoot, "img", "gameicon.png"));
            SetIfMissing(customisation, "maskimg", RuntimeFileIfExists(runtimeRoot, "img", "san_trophy_mask.png"));
            SetIfMissing(customisation, "iconborderimg", RuntimeFileIfExists(runtimeRoot, "img", "saniconborder.png"));
            SetIfMissing(customisation, "iconborderimgbronze", RuntimeFileIfExists(runtimeRoot, "img", "saniconborder_bronze.png"));
            SetIfMissing(customisation, "iconborderimgsilver", RuntimeFileIfExists(runtimeRoot, "img", "saniconborder_silver.png"));
            SetIfMissing(customisation, "percentbadgeimgbronze", RuntimeFileIfExists(runtimeRoot, "img", TrophyBronze));
            SetIfMissing(customisation, "percentbadgeimgsilver", RuntimeFileIfExists(runtimeRoot, "img", TrophySilver));
            SetIfMissing(customisation, "percentbadgeimggold", RuntimeFileIfExists(runtimeRoot, "img", TrophyGold));
            SetIfMissing(customisation, "hiddenicon", RuntimeFileIfExists(runtimeRoot, "icon", "lock.svg"));
            SetIfMissing(customisation, "defaultAchIcon", RuntimeFileIfExists(runtimeRoot, "img", "achicon.png"));
            return customisation;
        }

        /// <summary>The 10-by-10 grid of SAN screen positions the theme can be authored for.</summary>
        public static string PositionName(JObject customisation)
        {
            return (customisation?.Value<string>("pos") ?? "center-bottom").Trim().ToLowerInvariant();
        }

        public static string ToJson(JToken token)
        {
            return token.ToString(Formatting.None);
        }
    }
}
