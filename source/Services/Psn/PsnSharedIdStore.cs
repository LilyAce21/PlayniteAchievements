using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>
    /// A small file of game to NPWR ID pairs shared with the Achievements app, so an ID entered in
    /// one is found by the other. Both read and write
    /// <c>%APPDATA%\PSN Platinum IDs\npwr-ids.json</c>:
    /// <code>{ "version": 1, "games": { "steam:1678": { "name": "Celeste", "npwrIds": ["NPWR14979_00"], "updatedAt": 1760400000000 } } }</code>
    /// A game is found by its Steam App ID when it has one ("steam:id"), or by its name with case,
    /// punctuation, accents and trademark signs ignored ("name:lettersanddigits"). An entry is written
    /// under both keys when both are known. Writes merge into what is on disk and replace the file
    /// atomically, so the two apps do not overwrite each other's games. The name rule matches the
    /// Achievements app's, which the tests on both sides pin.
    /// </summary>
    internal sealed class PsnSharedIdStore
    {
        public const string FolderName = "PSN Platinum IDs";
        public const string FileName = "npwr-ids.json";

        private readonly string _directory;
        private readonly Func<long> _nowMs;
        private readonly object _sync = new object();

        private JObject _cache;
        private DateTime _cacheWriteUtc;
        private long _cacheLength = -1;

        public PsnSharedIdStore(string directory = null, Func<long> nowMs = null)
        {
            _directory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName)
                : directory;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public string FilePath => Path.Combine(_directory, FileName);

        /// <summary>Lower case letters and digits only, after dropping trademark signs and accents.</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // Trademark signs go first: normalising them would turn a trademark sign into the letters "tm".
            var cleaned = name.Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty);
            var builder = new StringBuilder();
            foreach (var c in cleaned.Normalize(NormalizationForm.FormKD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                var lower = char.ToLowerInvariant(c);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
                {
                    builder.Append(lower);
                }
            }

            return builder.ToString();
        }

        public static IReadOnlyList<string> KeysFor(int steamAppId, string name)
        {
            var keys = new List<string>();
            if (steamAppId > 0)
            {
                keys.Add("steam:" + steamAppId.ToString(CultureInfo.InvariantCulture));
            }

            var normalized = NormalizeName(name);
            if (normalized.Length > 0)
            {
                keys.Add("name:" + normalized);
            }

            return keys;
        }

        /// <summary>The NPWR IDs known for a game, empty when none. A Steam App ID match wins over a name match.</summary>
        public IReadOnlyList<string> Lookup(int steamAppId, string name)
        {
            lock (_sync)
            {
                var games = Read(forWrite: false)["games"] as JObject;
                if (games == null)
                {
                    return Array.Empty<string>();
                }

                foreach (var key in KeysFor(steamAppId, name))
                {
                    var ids = CleanIds((games[key] as JObject)?["npwrIds"] as JArray);
                    if (ids.Count > 0)
                    {
                        return ids;
                    }
                }
            }

            return Array.Empty<string>();
        }

        /// <summary>Remembers a game's IDs under every key it has. False when nothing was saved.</summary>
        public bool Remember(int steamAppId, string name, IEnumerable<string> npwrIds)
        {
            var ids = (npwrIds ?? Enumerable.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim().ToUpperInvariant())
                .Where(PsnNpwr.IsValid)
                .Distinct()
                .ToList();
            var keys = KeysFor(steamAppId, name);
            if (ids.Count == 0 || keys.Count == 0)
            {
                return false;
            }

            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    var data = Read(forWrite: true);
                    var games = data["games"] as JObject ?? new JObject();
                    foreach (var key in keys)
                    {
                        games[key] = new JObject
                        {
                            ["name"] = (name ?? string.Empty).Trim(),
                            ["npwrIds"] = new JArray(ids),
                            ["updatedAt"] = _nowMs()
                        };
                    }

                    data["version"] = 1;
                    data["games"] = games;

                    var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, data.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                    }

                    File.Move(temp, FilePath);
                    _cache = null;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Reads the file, reusing the last read while it has not changed so scanning a big
        // library for known IDs does not read the file once per game. A read for lookup shares the
        // cached document (callers only read it); a read for writing gets its own copy.
        private JObject Read(bool forWrite)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists)
                {
                    return NewDocument();
                }

                if (_cache != null && info.LastWriteTimeUtc == _cacheWriteUtc && info.Length == _cacheLength)
                {
                    return forWrite ? (JObject)_cache.DeepClone() : _cache;
                }

                var parsed = JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                if (parsed["games"] is JObject)
                {
                    _cache = parsed;
                    _cacheWriteUtc = info.LastWriteTimeUtc;
                    _cacheLength = info.Length;
                    return forWrite ? (JObject)parsed.DeepClone() : parsed;
                }
            }
            catch (Exception)
            {
                // A missing, locked or corrupt file reads as empty; the next write replaces it.
            }

            return NewDocument();
        }

        private static JObject NewDocument() => new JObject { ["version"] = 1, ["games"] = new JObject() };

        private static List<string> CleanIds(JArray array)
        {
            if (array == null)
            {
                return new List<string>();
            }

            return array
                .Select(token => ((string)token ?? string.Empty).Trim().ToUpperInvariant())
                .Where(PsnNpwr.IsValid)
                .Distinct()
                .ToList();
        }
    }
}
