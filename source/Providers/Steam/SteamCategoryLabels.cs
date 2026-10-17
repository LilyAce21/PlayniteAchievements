using System;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Category types and label rules shared by every Steam category source (Steam's own
    /// achievement sets and SteamHunters groups), so both produce identical labels for the same
    /// DLC and the same default-art file keys.
    /// </summary>
    internal static class SteamCategoryLabels
    {
        internal const string BaseCategoryType = "Base";
        internal const string DlcCategoryType = "DLC";
        internal const string UpdateCategoryType = "Update";
        internal const string MultiplayerCategoryType = "Multiplayer";

        // Steam store DLC names usually repeat the game name ("Cyberpunk 2077: Phantom
        // Liberty"); drop that prefix so category labels read as just the DLC/update name.
        // Only strips when a separator follows the game name, so labels that merely start
        // with the game name ("Cyberpunk 2077 Ultimate") are left alone.
        internal static string StripGameNamePrefix(string label, string gameName)
        {
            var normalizedGameName = gameName?.Trim();
            if (string.IsNullOrWhiteSpace(label) ||
                string.IsNullOrWhiteSpace(normalizedGameName) ||
                label.Length <= normalizedGameName.Length ||
                !label.StartsWith(normalizedGameName, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }

            var remainder = label.Substring(normalizedGameName.Length).TrimStart();
            if (remainder.Length == 0 || (remainder[0] != ':' && remainder[0] != '-' && remainder[0] != '–' && remainder[0] != '—'))
            {
                return label;
            }

            var stripped = remainder.TrimStart(':', '-', '–', '—', ' ', '\t');
            return string.IsNullOrWhiteSpace(stripped) ? label : stripped;
        }
    }
}
