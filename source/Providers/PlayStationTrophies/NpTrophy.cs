using System;

namespace PlayniteAchievements.Providers.PlayStationTrophies
{
    /// <summary>
    /// Internal DTO for a parsed NP trophy (RPCS3 and Vita3K).
    /// Represents a single trophy from TROPCONF.SFM combined with unlock data from TROPUSR.DAT.
    /// </summary>
    internal sealed class NpTrophy
    {
        /// <summary>
        /// The trophy ID (0-indexed).
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Trophy type: B (Bronze), S (Silver), G (Gold), P (Platinum).
        /// </summary>
        public string TrophyType { get; set; }

        /// <summary>
        /// Whether the trophy is hidden (secret).
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// Trophy name from TROPCONF.SFM.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Trophy description from TROPCONF.SFM.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Group ID for DLC trophies.
        /// </summary>
        public string GroupId { get; set; }

        /// <summary>
        /// Group name for DLC trophies (resolved from group ID).
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// Whether the trophy has been unlocked.
        /// </summary>
        public bool Unlocked { get; set; }

        /// <summary>
        /// UTC timestamp when the trophy was unlocked, if applicable.
        /// Each emulator converts its own timestamp format to UTC.
        /// </summary>
        public DateTime? UnlockTimeUtc { get; set; }
    }
}
