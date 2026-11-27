using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Research notes already gathered, by game and achievement. Research costs requests or search credits, and a limit can
    /// interrupt it part way, so what was found is kept and reused instead of being paid for twice. When a folder is set
    /// (<see cref="Configure"/>) each game's notes are also saved to a small file there, so they survive restarts; they are
    /// cleared when a game is regenerated or all generated data is deleted. Only public findings are stored: never a key,
    /// and nothing about the player.
    /// </summary>
    public static class ResearchNotesCache
    {
        /// <summary>Key (inside the notes) for the game-level findings; never an achievement's api name.</summary>
        public const string GameNotesKey = "__gameNotes";

        /// <summary>Key (inside the notes) that records the game-level searches were already done.</summary>
        public const string GameSearchedKey = "__gameSearched";

        private sealed class Entry
        {
            public Dictionary<string, string> Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Misses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class FileShape
        {
            public Dictionary<string, string> Notes { get; set; }
            public List<string> Misses { get; set; }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, Entry> Games = new Dictionary<Guid, Entry>();
        private static string _directory;

        /// <summary>Saves and loads per-game files in this folder. Pass null to keep everything in memory only.</summary>
        public static void Configure(string directory)
        {
            lock (Sync)
            {
                _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
                Games.Clear();
            }
        }

        /// <summary>The live notes for a game (api name to note). Created on first use, from disk when a file exists.</summary>
        public static IDictionary<string, string> For(Guid gameId)
        {
            lock (Sync) { return EntryFor(gameId).Notes; }
        }

        /// <summary>The achievements already searched for without finding anything, so they are not searched for again.</summary>
        public static ISet<string> MissesFor(Guid gameId)
        {
            lock (Sync) { return EntryFor(gameId).Misses; }
        }

        private static Entry EntryFor(Guid gameId)
        {
            if (Games.TryGetValue(gameId, out var entry)) return entry;

            entry = new Entry();
            try
            {
                var path = PathFor(gameId);
                if (path != null && File.Exists(path))
                {
                    var shape = JsonConvert.DeserializeObject<FileShape>(File.ReadAllText(path));
                    if (shape?.Notes != null) foreach (var pair in shape.Notes) entry.Notes[pair.Key] = pair.Value;
                    if (shape?.Misses != null) foreach (var miss in shape.Misses) entry.Misses.Add(miss);
                }
            }
            catch (Exception)
            {
                // An unreadable file is the same as no file.
            }

            Games[gameId] = entry;
            return entry;
        }

        /// <summary>Writes the game's notes to disk (when a folder is set). Never throws.</summary>
        public static void Save(Guid gameId)
        {
            lock (Sync)
            {
                try
                {
                    var path = PathFor(gameId);
                    if (path == null || !Games.TryGetValue(gameId, out var entry)) return;

                    if (entry.Notes.Count == 0 && entry.Misses.Count == 0)
                    {
                        if (File.Exists(path)) File.Delete(path);
                        return;
                    }

                    Directory.CreateDirectory(_directory);
                    File.WriteAllText(path, JsonConvert.SerializeObject(new FileShape { Notes = entry.Notes, Misses = entry.Misses.ToList() }));
                }
                catch (Exception)
                {
                    // Best effort: the cache saves money, nothing depends on it.
                }
            }
        }

        public static void Clear(Guid gameId)
        {
            lock (Sync)
            {
                Games.Remove(gameId);
                try
                {
                    var path = PathFor(gameId);
                    if (path != null && File.Exists(path)) File.Delete(path);
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
        }

        public static void ClearAll()
        {
            lock (Sync)
            {
                Games.Clear();
                try
                {
                    if (_directory != null && Directory.Exists(_directory))
                    {
                        foreach (var file in Directory.GetFiles(_directory, "*.json")) File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
        }

        private static string PathFor(Guid gameId) =>
            _directory == null ? null : Path.Combine(_directory, gameId.ToString("N") + ".json");
    }
}
