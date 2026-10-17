using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// Running points sums per points family (see <see cref="PointsFamilies.FromProviderKey"/>): the
    /// unlocked points earned, and the points of every achievement, unlocked or not.
    /// </summary>
    public sealed class PlatformScoreTotals
    {
        private readonly Dictionary<string, int> _earnedByFamily =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int> _possibleByFamily =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int Gamerscore => Get(PointsFamilies.Xbox);

        public int EpicXp => Get(PointsFamilies.Epic);

        public int RetroPoints => Get(PointsFamilies.RetroAchievements);

        public int GamerscoreTotal => GetPossible(PointsFamilies.Xbox);

        public int EpicXpTotal => GetPossible(PointsFamilies.Epic);

        public int RetroPointsTotal => GetPossible(PointsFamilies.RetroAchievements);

        /// <summary>Every family with points to earn, most earned first.</summary>
        public IReadOnlyList<string> Families =>
            _possibleByFamily.Keys
                .Union(_earnedByFamily.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(family => Get(family) > 0 || GetPossible(family) > 0)
                .OrderByDescending(Get)
                .ThenByDescending(GetPossible)
                .ThenBy(family => family, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Adds a game's points to the family of its effective provider key.</summary>
        public void Add(string effectiveProviderKey, int earnedPoints, int possiblePoints) =>
            AddToFamily(PointsFamilies.FromProviderKey(effectiveProviderKey), earnedPoints, possiblePoints);

        /// <summary>Adds points to an already resolved family; a null family counts nowhere.</summary>
        public void AddToFamily(string family, int earnedPoints, int possiblePoints)
        {
            var key = PointsFamilies.Normalize(family);
            if (key == null)
            {
                return;
            }

            AddClamped(_earnedByFamily, key, earnedPoints);
            AddClamped(_possibleByFamily, key, possiblePoints);
        }

        /// <summary>The points earned toward a family; 0 for a family with no points.</summary>
        public int Get(string family) => Lookup(_earnedByFamily, family);

        /// <summary>The points of every achievement in a family's games, unlocked or not.</summary>
        public int GetPossible(string family) => Lookup(_possibleByFamily, family);

        /// <summary>The points a card shows as earned; 0 for Collection and Prestige.</summary>
        public int Get(ScoreCardType type) => type.IsPoints ? Get(type.PointsFamily) : 0;

        /// <summary>The points a card shows as possible; 0 for Collection and Prestige.</summary>
        public int GetPossible(ScoreCardType type) => type.IsPoints ? GetPossible(type.PointsFamily) : 0;

        private static int Lookup(Dictionary<string, int> totals, string family)
        {
            var key = PointsFamilies.Normalize(family);
            return key != null && totals.TryGetValue(key, out var total) ? total : 0;
        }

        private static void AddClamped(Dictionary<string, int> totals, string key, int value)
        {
            if (value <= 0)
            {
                return;
            }

            totals.TryGetValue(key, out var current);
            totals[key] = current > int.MaxValue - value ? int.MaxValue : current + value;
        }
    }
}
