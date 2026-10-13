using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// Running points sums: unlocked provider points per points family (see
    /// <see cref="PointsFamilies.Resolve"/>).
    /// </summary>
    public sealed class PlatformScoreTotals
    {
        private readonly Dictionary<string, int> _byFamily =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int Gamerscore => Get(PointsFamilies.Xbox);

        public int EpicXp => Get(PointsFamilies.Epic);

        public int RetroPoints => Get(PointsFamilies.RetroAchievements);

        /// <summary>Every family with points, largest total first.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> Families =>
            _byFamily
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Adds a game's points to the family it counts toward: <paramref name="overrideFamily"/>
        /// when the user set one, otherwise its effective provider key's family.
        /// </summary>
        public void Add(string effectiveProviderKey, string overrideFamily, int platformScorePoints) =>
            AddToFamily(PointsFamilies.Resolve(effectiveProviderKey, overrideFamily), platformScorePoints);

        /// <summary>Adds points to an already resolved family; a null family counts nowhere.</summary>
        public void AddToFamily(string family, int platformScorePoints)
        {
            var key = PointsFamilies.Normalize(family);
            if (platformScorePoints <= 0 || key == null)
            {
                return;
            }

            _byFamily.TryGetValue(key, out var current);
            _byFamily[key] = AddClamped(current, platformScorePoints);
        }

        /// <summary>The total for a points family; 0 for a family with no points.</summary>
        public int Get(string family)
        {
            var key = PointsFamilies.Normalize(family);
            return key != null && _byFamily.TryGetValue(key, out var total) ? total : 0;
        }

        /// <summary>The total a card shows; 0 for Collection and Prestige.</summary>
        public int Get(ScoreCardType type) => type.IsPoints ? Get(type.PointsFamily) : 0;

        private static int AddClamped(int current, int value)
        {
            return current > int.MaxValue - value ? int.MaxValue : current + value;
        }
    }
}
