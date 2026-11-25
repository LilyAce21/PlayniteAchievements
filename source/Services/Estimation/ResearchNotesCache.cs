using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Research notes already gathered this session, by game and achievement. Research costs several requests per
    /// game and a rate limit can interrupt it part way, so what was already found is kept in memory and reused on the
    /// next attempt instead of being paid for twice. Never written to disk, and cleared when a game is regenerated.
    /// </summary>
    public static class ResearchNotesCache
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, Dictionary<string, string>> Games = new Dictionary<Guid, Dictionary<string, string>>();

        /// <summary>The live notes for a game (api name to note). Created empty on first use.</summary>
        public static IDictionary<string, string> For(Guid gameId)
        {
            lock (Sync)
            {
                if (!Games.TryGetValue(gameId, out var notes))
                {
                    notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    Games[gameId] = notes;
                }

                return notes;
            }
        }

        public static void Clear(Guid gameId)
        {
            lock (Sync) { Games.Remove(gameId); }
        }

        public static void ClearAll()
        {
            lock (Sync) { Games.Clear(); }
        }
    }
}
