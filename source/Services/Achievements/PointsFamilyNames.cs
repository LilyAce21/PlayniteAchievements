using System;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Display names for points families. Xbox, Epic and RetroAchievements keep their platform
    /// score names; every other family is "&lt;provider&gt; Points".
    /// </summary>
    public static class PointsFamilyNames
    {
        /// <summary>The family's card name, e.g. Gamerscore or PlayStation Points.</summary>
        public static string GetLabel(string family)
        {
            var key = PointsFamilies.Normalize(family);
            if (IsFamily(key, PointsFamilies.Xbox))
            {
                return L("LOCPlayAch_Score_Gamerscore");
            }

            if (IsFamily(key, PointsFamilies.Epic))
            {
                return L("LOCPlayAch_Score_EpicXp");
            }

            if (IsFamily(key, PointsFamilies.RetroAchievements))
            {
                return L("LOCPlayAch_Score_RetroPoints");
            }

            return string.Format(L("LOCPlayAch_Score_ProviderPointsFormat"), GetProviderName(key));
        }

        /// <summary>The family's name without "Points", for compact tier lines.</summary>
        public static string GetShortLabel(string family)
        {
            var key = PointsFamilies.Normalize(family);
            if (IsFamily(key, PointsFamilies.Xbox))
            {
                return L("LOCPlayAch_Score_Gamerscore");
            }

            if (IsFamily(key, PointsFamilies.Epic))
            {
                return L("LOCPlayAch_Score_EpicXp");
            }

            return GetProviderName(key);
        }

        /// <summary>The provider name a family is named after, e.g. PlayStation for PSN.</summary>
        public static string GetProviderName(string family) =>
            ProviderRegistry.GetLocalizedName(PointsFamilies.Normalize(family));

        private static bool IsFamily(string key, string family) =>
            string.Equals(key, family, StringComparison.OrdinalIgnoreCase);

        private static string L(string key) => ResourceProvider.GetString(key);
    }
}
