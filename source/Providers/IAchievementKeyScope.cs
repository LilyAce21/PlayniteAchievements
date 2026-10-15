using PlayniteAchievements.Providers.Minecraft;
using System;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Implemented by a provider whose API names carry a local scope, such as the Minecraft
    /// instance a row comes from. The scope tells one copy of an achievement from another inside a
    /// single game; the portable key is the achievement itself, the same on every machine.
    /// Per-game custom data stays keyed by full API names; packages move between games through
    /// the portable keys (see <c>GameCustomDataPortableScoping</c>).
    /// </summary>
    internal interface IAchievementKeyScope
    {
        /// <summary>
        /// Splits a scoped API name into its scope and portable key. False for a name that carries
        /// no scope, which is then portable as it is.
        /// </summary>
        bool TrySplit(string apiName, out string scope, out string portableKey);
    }

    /// <summary>The key scope of each provider that has one.</summary>
    internal static class AchievementKeyScopes
    {
        /// <summary>The scope of <paramref name="providerKey"/>, or null when its API names are unscoped.</summary>
        public static IAchievementKeyScope For(string providerKey)
        {
            return string.Equals(providerKey, MinecraftDataProvider.Key, StringComparison.OrdinalIgnoreCase)
                ? MinecraftKeyScope.Instance
                : null;
        }
    }
}
