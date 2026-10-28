using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// The advancement, language and texture entries of one jar, zip datapack or datapack folder.
    /// Archives are indexed once per (path, length, last write, locales) for the process lifetime,
    /// so a large mod folder is read again only when a file changes.
    /// </summary>
    internal sealed class MinecraftResourceSource
    {
        private const long MaxTextEntryBytes = 4L * 1024 * 1024;

        // data/<ns>/advancement/<path>.json (1.21+), data/<ns>/advancements/<path>.json (1.13-1.20),
        // assets/<ns>/advancements/<path>.json (1.12).
        private static readonly Regex AdvancementEntry = new Regex(
            @"^(?:data/(?<ns>[^/]+)/advancements?|assets/(?<ns>[^/]+)/advancements)/(?<path>.+)\.json$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex LangEntry = new Regex(
            @"^assets/(?<ns>[^/]+)/lang/(?<locale>[^/]+)\.(?<ext>json|lang)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Every texture: models also draw items with entity and custom-folder textures.
        private static readonly Regex TextureEntry = new Regex(
            @"^assets/[^/]+/textures/.+\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // assets/<ns>/models/item|block/<path>.json, keyed ns:item/<path> as models reference them.
        private static readonly Regex ModelEntry = new Regex(
            @"^assets/(?<ns>[^/]+)/models/(?<path>(?:item|items|block|blocks)/.+)\.json$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // assets/<ns>/items/<name>.json (1.21.4+): the item definition naming its model.
        private static readonly Regex ItemDefinitionEntry = new Regex(
            @"^assets/(?<ns>[^/]+)/items/(?<path>.+)\.json$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly ConcurrentDictionary<string, MinecraftResourceSource> ArchiveCache =
            new ConcurrentDictionary<string, MinecraftResourceSource>(StringComparer.OrdinalIgnoreCase);

        private MinecraftResourceSource(string path, bool isArchive, DateTime stampUtc)
        {
            Path = path;
            IsArchive = isArchive;
            StampUtc = stampUtc;
        }

        public string Path { get; }

        public bool IsArchive { get; }

        /// <summary>Last write time of the archive or folder, used to refresh extracted icons.</summary>
        public DateTime StampUtc { get; }

        /// <summary>Advancement JSON by id (<c>namespace:path</c>); recipe advancements are skipped.</summary>
        public Dictionary<string, string> Advancements { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Item and block model JSON by model id (<c>namespace:item/path</c>).</summary>
        public Dictionary<string, string> Models { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>1.21.4+ item definition JSON by item id (<c>namespace:path</c>).</summary>
        public Dictionary<string, string> ItemDefinitions { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Translations by locale (lowercase), then key.</summary>
        public Dictionary<string, Dictionary<string, string>> Languages { get; } =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Texture entry names, lowercase with forward slashes.</summary>
        public HashSet<string> Textures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Indexes a jar/zip or a datapack folder. Returns null when the path cannot be read.
        /// </summary>
        public static MinecraftResourceSource Load(string path, IReadOnlyCollection<string> locales, ILogger logger)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return LoadFolder(path, locales);
                }

                if (!File.Exists(path))
                {
                    return null;
                }

                var info = new FileInfo(path);
                var cacheKey = string.Join("|",
                    info.FullName,
                    info.Length.ToString(),
                    info.LastWriteTimeUtc.Ticks.ToString(),
                    string.Join(",", (locales ?? Array.Empty<string>()).OrderBy(l => l, StringComparer.OrdinalIgnoreCase)));
                if (ArchiveCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                var loaded = LoadArchive(info, locales);
                ArchiveCache[cacheKey] = loaded;
                return loaded;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                logger?.Debug(ex, $"[Minecraft] Could not read '{path}'.");
                return null;
            }
        }

        private static MinecraftResourceSource LoadArchive(FileInfo info, IReadOnlyCollection<string> locales)
        {
            var source = new MinecraftResourceSource(info.FullName, isArchive: true, info.LastWriteTimeUtc);
            using (var archive = ZipFile.OpenRead(info.FullName))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    var name = NormalizeEntryName(entry.FullName);
                    source.Accept(name, locales, entry.Length, () => ReadEntryText(entry));
                }
            }

            return source;
        }

        private static MinecraftResourceSource LoadFolder(string folder, IReadOnlyCollection<string> locales)
        {
            var root = new DirectoryInfo(folder);
            var source = new MinecraftResourceSource(root.FullName, isArchive: false, root.LastWriteTimeUtc);
            foreach (var top in new[] { "data", "assets" })
            {
                var topPath = System.IO.Path.Combine(root.FullName, top);
                if (!Directory.Exists(topPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(topPath, "*", SearchOption.AllDirectories))
                {
                    var name = NormalizeEntryName(file.Substring(root.FullName.Length));
                    var length = new FileInfo(file).Length;
                    source.Accept(name, locales, length, () => File.ReadAllText(file, Encoding.UTF8));
                }
            }

            return source;
        }

        private void Accept(string name, IReadOnlyCollection<string> locales, long length, Func<string> readText)
        {
            if (name == null)
            {
                return;
            }

            if (TextureEntry.IsMatch(name))
            {
                Textures.Add(name.ToLowerInvariant());
                return;
            }

            if (length > MaxTextEntryBytes)
            {
                return;
            }

            var advancement = AdvancementEntry.Match(name);
            if (advancement.Success)
            {
                var advancementPath = advancement.Groups["path"].Value;
                if (advancementPath.StartsWith("recipes/", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var id = (advancement.Groups["ns"].Value + ":" + advancementPath).ToLowerInvariant();
                if (!Advancements.ContainsKey(id))
                {
                    Advancements[id] = readText();
                }

                return;
            }

            var model = ModelEntry.Match(name);
            if (model.Success)
            {
                var id = (model.Groups["ns"].Value + ":" + model.Groups["path"].Value).ToLowerInvariant();
                if (!Models.ContainsKey(id))
                {
                    Models[id] = readText();
                }

                return;
            }

            var itemDefinition = ItemDefinitionEntry.Match(name);
            if (itemDefinition.Success)
            {
                var id = (itemDefinition.Groups["ns"].Value + ":" + itemDefinition.Groups["path"].Value).ToLowerInvariant();
                if (!ItemDefinitions.ContainsKey(id))
                {
                    ItemDefinitions[id] = readText();
                }

                return;
            }

            var lang = LangEntry.Match(name);
            if (lang.Success)
            {
                var locale = lang.Groups["locale"].Value.ToLowerInvariant();
                if (locales == null || !locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!Languages.TryGetValue(locale, out var table))
                {
                    table = new Dictionary<string, string>(StringComparer.Ordinal);
                    Languages[locale] = table;
                }

                var isLegacy = string.Equals(lang.Groups["ext"].Value, "lang", StringComparison.OrdinalIgnoreCase);
                MergeLanguage(table, readText(), isLegacy);
            }
        }

        /// <summary>
        /// Adds a .json (1.13+) or key=value .lang (1.12) translation file to <paramref name="table"/>;
        /// keys already present are kept.
        /// </summary>
        internal static void MergeLanguage(Dictionary<string, string> table, string text, bool isLegacy)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (isLegacy)
            {
                foreach (var rawLine in text.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    var separator = line.IndexOf('=');
                    if (line.StartsWith("#", StringComparison.Ordinal) || separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim();
                    if (!table.ContainsKey(key))
                    {
                        table[key] = line.Substring(separator + 1);
                    }
                }

                return;
            }

            JObject json;
            try
            {
                json = JObject.Parse(text);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return;
            }

            foreach (var property in json.Properties())
            {
                if (property.Value.Type == JTokenType.String && !table.ContainsKey(property.Name))
                {
                    table[property.Name] = (string)property.Value;
                }
            }
        }

        /// <summary>The bytes of an entry, or null when it is missing or unreadable.</summary>
        public byte[] ReadBytes(string entryName)
        {
            try
            {
                if (!IsArchive)
                {
                    var file = System.IO.Path.Combine(Path, entryName.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    return File.Exists(file) ? File.ReadAllBytes(file) : null;
                }

                using (var archive = ZipFile.OpenRead(Path))
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(NormalizeEntryName(e.FullName), entryName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null || entry.Length > MaxTextEntryBytes)
                    {
                        return null;
                    }

                    using (var stream = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                return null;
            }
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }

        private static string NormalizeEntryName(string value)
        {
            var normalized = (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
            return normalized.Length == 0 || normalized.Contains("../") ? null : normalized;
        }
    }
}
