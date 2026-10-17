using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Where a game's achievement rarity came from. Rarity tiers default to Common, so this
    /// is what separates a game with no rarity data from one whose achievements are all Common.
    /// </summary>
    public enum RaritySource
    {
        /// <summary>The provider supplied no rarity; tiers are the Common default.</summary>
        None,

        /// <summary>The provider derived tiers from its own data (gamerscore, trophy type, frame).</summary>
        Tier,

        /// <summary>At least one achievement carries a global unlock percent.</summary>
        Percent
    }

    public static class RaritySourceResolver
    {
        /// <summary>
        /// Resolves the source a refreshed payload records. A percent is never a default, so any
        /// percent makes it Percent; otherwise the provider's own Tier declaration stands, and a
        /// provider that declared nothing supplied no rarity.
        /// </summary>
        public static RaritySource ResolveRefreshed(RaritySource? declared, IEnumerable<AchievementDetail> achievements)
        {
            if (achievements != null && achievements.Any(a => a?.GlobalPercentUnlocked.HasValue == true))
            {
                return RaritySource.Percent;
            }

            return declared ?? RaritySource.None;
        }
    }
}
