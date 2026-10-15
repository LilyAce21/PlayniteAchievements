using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.Library;

namespace PlayniteAchievements.ViewModels.Library
{
    /// <summary>
    /// How a place an item is used in reads, wherever the "Used in" list is shown (the Library
    /// page and Workshop Browse): a label naming the place and a state line with the version
    /// applied there and whether it was edited or is behind.
    /// </summary>
    internal static class LibraryUseLabels
    {
        public static string Target(PlayniteAchievementsPlugin plugin, string key)
        {
            if (string.Equals(key, LibraryTargetKeys.Colors, StringComparison.OrdinalIgnoreCase))
            {
                return L("LOCPlayAch_Settings_Display_Colors");
            }

            if (string.Equals(key, LibraryTargetKeys.Sounds, StringComparison.OrdinalIgnoreCase))
            {
                return L("LOCPlayAch_Workshop_Share_Sounds");
            }

            if (LibraryTargetKeys.IsGameData(key) && LibraryTargetKeys.TryGetGameId(key, out var dataGameId))
            {
                return GameName(plugin, dataGameId);
            }

            var surface = key.StartsWith("sounds:", StringComparison.OrdinalIgnoreCase)
                ? L("LOCPlayAch_Workshop_Share_Sounds")
                : key.StartsWith("frame:", StringComparison.OrdinalIgnoreCase)
                    ? L("LOCPlayAch_Settings_FrameHeader")
                    : L("LOCPlayAch_Settings_Style_ToastTab");
            if (string.Equals(key, LibraryTargetKeys.ToastGlobal, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, LibraryTargetKeys.FrameGlobal, StringComparison.OrdinalIgnoreCase))
            {
                return surface;
            }

            const string providerSegment = ":provider:";
            var providerAt = key.IndexOf(providerSegment, StringComparison.OrdinalIgnoreCase);
            if (providerAt >= 0)
            {
                return surface + " · " + ProviderName(key.Substring(providerAt + providerSegment.Length));
            }

            if (key.StartsWith("showcase:", StringComparison.OrdinalIgnoreCase))
            {
                var pageId = key.Substring("showcase:".Length);
                var page = plugin?.Settings?.Persisted?.Showcase?.Pages?.FirstOrDefault(candidate =>
                    string.Equals(candidate?.PageId, pageId, StringComparison.OrdinalIgnoreCase));
                return string.Format(L("LOCPlayAch_Workshop_Share_ShowcasePage"), page?.Name ?? pageId);
            }

            if (LibraryTargetKeys.TryGetGameId(key, out var gameId))
            {
                return surface + " · " + GameName(plugin, gameId);
            }

            return key;
        }

        public static string GameName(PlayniteAchievementsPlugin plugin, Guid gameId)
        {
            return plugin?.PlayniteApi?.Database?.Games?.Get(gameId)?.Name ?? gameId.ToString();
        }

        /// <summary>The version applied at the place (for a Workshop item), then Edited and Update available as they apply.</summary>
        public static string State(LibraryItem item, LibraryTargetUse use)
        {
            var applied = string.IsNullOrWhiteSpace(use.Link?.AppliedVersion) ? item.Version : use.Link.AppliedVersion;
            var parts = new List<string>();
            if (item.IsWorkshop && !string.IsNullOrWhiteSpace(applied))
            {
                parts.Add("v" + applied);
            }

            if (use.IsEdited)
            {
                parts.Add(L("LOCPlayAch_Library_Edited"));
            }

            if (use.IsUpdateAvailable)
            {
                parts.Add(L("LOCPlayAch_Workshop_UpdateAvailable"));
            }

            return string.Join(" · ", parts);
        }

        private static string ProviderName(string providerKey)
        {
            var name = ResourceProvider.GetString("LOCPlayAch_Provider_" + providerKey);
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("<!", StringComparison.Ordinal) ? providerKey : name;
        }

        private static string L(string key) => ResourceProvider.GetString(key);
    }
}
