using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.CustomProviders;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// Which points card a game's provider points count toward. A family is named by a provider
    /// key: Xbox covers Xbox and Xenia, PSN covers PSN, RPCS3 and ShadPS4, and every other
    /// provider, custom providers included, is its own family.
    /// </summary>
    public static class PointsFamilies
    {
        public const string Xbox = "Xbox";

        public const string PlayStation = "PSN";

        public const string Epic = "Epic";

        public const string RetroAchievements = "RetroAchievements";

        /// <summary>The per-game override value that counts a game's points toward no card.</summary>
        public const string NoneOverride = "None";

        /// <summary>The families every card picker lists, whether or not any game has their points.</summary>
        public static readonly IReadOnlyList<string> AlwaysListed = new[] { Xbox, Epic, RetroAchievements };

        private static readonly Dictionary<string, string> FamilyByProviderKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Xbox"] = Xbox,
                ["Xenia"] = Xbox,
                ["PSN"] = PlayStation,
                ["RPCS3"] = PlayStation,
                ["ShadPS4"] = PlayStation
            };

        /// <summary>
        /// The family a game's points count toward: <paramref name="overrideFamily"/> when the
        /// user set one (<see cref="NoneOverride"/> for none), otherwise the family of its
        /// effective provider key. Null means the points count toward no card.
        /// </summary>
        public static string Resolve(string effectiveProviderKey, string overrideFamily)
        {
            var normalizedOverride = overrideFamily?.Trim();
            if (string.IsNullOrEmpty(normalizedOverride))
            {
                return FromProviderKey(effectiveProviderKey);
            }

            return IsNoneOverride(normalizedOverride) ? null : Normalize(normalizedOverride);
        }

        /// <summary>
        /// The family a provider key belongs to. Null for a blank key, Unknown, and the plain
        /// Custom key of a custom-only game with no custom provider.
        /// </summary>
        public static string FromProviderKey(string effectiveProviderKey)
        {
            var key = effectiveProviderKey?.Trim();
            if (string.IsNullOrEmpty(key) ||
                string.Equals(key, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, CustomProviderKeys.BaseKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return FamilyByProviderKey.TryGetValue(key, out var family) ? family : key;
        }

        /// <summary>A family key with provider aliases folded in, or null when blank.</summary>
        public static string Normalize(string family)
        {
            var key = family?.Trim();
            if (string.IsNullOrEmpty(key) || IsNoneOverride(key))
            {
                return null;
            }

            return FromProviderKey(key);
        }

        public static bool IsNoneOverride(string value) =>
            string.Equals(value?.Trim(), NoneOverride, StringComparison.OrdinalIgnoreCase);

        public static bool AreSame(string left, string right) =>
            string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
