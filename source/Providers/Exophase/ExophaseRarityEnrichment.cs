using Playnite.SDK.Models;
using PlayniteAchievements.Providers.EA;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Vita3K;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Providers.Xenia;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Maps servicing providers to their Exophase rarity enrichment opt-in. The provider set and
    /// per-provider flag mirror the scanner gates that construct <see cref="ExophaseMetadataEnricher"/>
    /// (e.g. XboxScanner.CreateRarityEnricherAsync); keep them in sync when a provider gains or
    /// loses enrichment support.
    /// </summary>
    internal static class ExophaseRarityEnrichment
    {
        public static bool IsEnabledForProvider(string providerKey)
        {
            switch ((providerKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                    return ProviderRegistry.Settings<XboxSettings>().UseExophaseForRarity;
                case "XENIA":
                    return ProviderRegistry.Settings<XeniaSettings>().UseExophaseForRarity;
                case "EA":
                    return ProviderRegistry.Settings<EASettings>().UseExophaseForRarity;
                case "RPCS3":
                    return ProviderRegistry.Settings<Rpcs3Settings>().UseExophaseForRarity;
                case "SHADPS4":
                    return ProviderRegistry.Settings<ShadPS4Settings>().UseExophaseForRarity;
                case "VITA3K":
                    return ProviderRegistry.Settings<Vita3KSettings>().UseExophaseForRarity;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The name-derived slug the enricher would try for this game, mirroring
        /// <see cref="ExophaseMetadataEnricher"/>'s default-slug generation and the scanners'
        /// platform slug choices. PSN slugs carry no platform suffix, so RPCS3/ShadPS4/Vita3K candidates
        /// are the bare normalized name. RPCS3 multipack collections (a '+'-joined provider game
        /// key) have no collection-level Exophase page and enrich per trophy set instead, so no
        /// candidate is offered.
        /// </summary>
        public static string GetCandidateSlug(string providerKey, Game game, string providerGameKey = null)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            if (providerGameKey?.IndexOf('+') >= 0)
            {
                return null;
            }

            var normalizedName = ExophaseGameNameMatcher.NormalizeGameNameForSlug(game.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            switch ((providerKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                    return $"{normalizedName}-{(XboxScanner.IsXbox360Game(game) ? "xbox-360" : "xbox")}";
                case "XENIA":
                    return $"{normalizedName}-xbox-360";
                case "EA":
                    return $"{normalizedName}-origin";
                case "RPCS3":
                case "SHADPS4":
                case "VITA3K":
                    return normalizedName;
                default:
                    return null;
            }
        }
    }
}
