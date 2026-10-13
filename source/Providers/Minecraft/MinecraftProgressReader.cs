using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// One advancement's progress merged across worlds.
    /// </summary>
    internal sealed class MinecraftAdvancementProgress
    {
        public bool Done { get; set; }

        /// <summary>Earliest completion across worlds; a world completes at its latest criterion time.</summary>
        public DateTime? CompletedUtc { get; set; }

        /// <summary>The obtained criteria of each world, kept apart so progress is never pooled across worlds.</summary>
        public List<HashSet<string>> CriteriaByWorld { get; } = new List<HashSet<string>>();
    }

    /// <summary>
    /// Reads <c>saves/&lt;world&gt;/advancements/&lt;uuid&gt;.json</c> for the selected player and
    /// merges the worlds into one progress record per advancement id.
    /// </summary>
    internal static class MinecraftProgressReader
    {
        private static readonly Regex CompactOffset = new Regex(@"([+-])(\d{2})(\d{2})$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Progress by advancement id, or null when a selected file could not be read; callers keep
        /// cached data then rather than writing unlocks as gone. Worlds with no file for the player
        /// contribute nothing.
        /// </summary>
        public static Dictionary<string, MinecraftAdvancementProgress> Read(
            IEnumerable<string> worldFolders,
            ISet<string> playerUuids,
            Action<string> log,
            out int worldsRead)
        {
            worldsRead = 0;
            var result = new Dictionary<string, MinecraftAdvancementProgress>(StringComparer.Ordinal);
            foreach (var world in worldFolders ?? Enumerable.Empty<string>())
            {
                var files = SelectPlayerFiles(world, playerUuids);
                if (files.Count == 0)
                {
                    continue;
                }

                var worldCriteria = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    if (!TryReadFile(file, out var entries, out var error))
                    {
                        log?.Invoke($"could not read '{file}' ({error})");
                        return null;
                    }

                    foreach (var entry in entries)
                    {
                        if (!result.TryGetValue(entry.Id, out var progress))
                        {
                            progress = new MinecraftAdvancementProgress();
                            result[entry.Id] = progress;
                        }

                        if (entry.Done)
                        {
                            progress.Done = true;
                            if (entry.CompletedUtc.HasValue &&
                                (!progress.CompletedUtc.HasValue || entry.CompletedUtc.Value < progress.CompletedUtc.Value))
                            {
                                progress.CompletedUtc = entry.CompletedUtc;
                            }
                        }

                        if (!worldCriteria.TryGetValue(entry.Id, out var criteria))
                        {
                            criteria = new HashSet<string>(StringComparer.Ordinal);
                            worldCriteria[entry.Id] = criteria;
                        }

                        criteria.UnionWith(entry.Criteria);
                    }
                }

                foreach (var pair in worldCriteria)
                {
                    result[pair.Key].CriteriaByWorld.Add(pair.Value);
                }

                worldsRead++;
            }

            return result;
        }

        /// <summary>
        /// The player's file: the one whose name is a known UUID, or the only file in the world.
        /// </summary>
        internal static List<string> SelectPlayerFiles(string worldFolder, ISet<string> playerUuids)
        {
            var folder = Path.Combine(worldFolder ?? string.Empty, "advancements");
            if (!Directory.Exists(folder))
            {
                return new List<string>();
            }

            List<string> files;
            try
            {
                files = Directory.GetFiles(folder, "*.json").ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }

            if (files.Count <= 1)
            {
                return files;
            }

            return files
                .Where(file => playerUuids != null &&
                               playerUuids.Contains(MinecraftAccounts.NormalizeUuid(Path.GetFileNameWithoutExtension(file)) ?? string.Empty))
                .ToList();
        }

        internal sealed class FileEntry
        {
            public string Id { get; set; }
            public bool Done { get; set; }
            public DateTime? CompletedUtc { get; set; }
            public List<string> Criteria { get; set; }
        }

        internal static bool TryReadFile(string path, out List<FileEntry> entries, out string error)
        {
            entries = new List<FileEntry>();
            error = null;
            JObject json;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    json = JToken.Parse(reader.ReadToEnd()) as JObject;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                error = ex.GetType().Name;
                return false;
            }

            if (json == null)
            {
                error = "not a JSON object";
                return false;
            }

            foreach (var property in json.Properties())
            {
                if (!(property.Value is JObject value) ||
                    property.Name.IndexOf("recipes/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var criteria = new List<string>();
                DateTime? latest = null;
                if (value["criteria"] is JObject criteriaObject)
                {
                    foreach (var criterion in criteriaObject.Properties())
                    {
                        criteria.Add(criterion.Name);
                        var time = ParseTimestamp(criterion.Value?.ToString());
                        if (time.HasValue && (!latest.HasValue || time.Value > latest.Value))
                        {
                            latest = time;
                        }
                    }
                }

                var done = value["done"]?.Type == JTokenType.Boolean && (bool)value["done"];
                entries.Add(new FileEntry
                {
                    Id = MinecraftAdvancementCatalog.NormalizeId(property.Name),
                    Done = done,
                    CompletedUtc = done ? latest : null,
                    Criteria = criteria
                });
            }

            return true;
        }

        /// <summary>
        /// Parses <c>yyyy-MM-dd HH:mm:ss Z</c> (offset written as +0000) to UTC.
        /// </summary>
        internal static DateTime? ParseTimestamp(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var normalized = CompactOffset.Replace(raw.Trim(), "$1$2:$3");
            return DateTimeOffset.TryParseExact(
                normalized,
                "yyyy-MM-dd HH:mm:ss zzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed.UtcDateTime
                : (DateTime?)null;
        }
    }
}
