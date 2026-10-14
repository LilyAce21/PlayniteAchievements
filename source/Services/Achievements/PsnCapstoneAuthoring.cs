using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Psn;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Authors PSN Capstones: an auto capstone whose name, description and icon are the game's
    /// PlayStation Platinum. It is written as an ordinary auto capstone for the whole game, so
    /// the same maintenance keeps its unlock and rarity in step (base game only, as a
    /// PlayStation Platinum is), and it is hidden so the Platinum is not spoiled before it unlocks.
    /// </summary>
    internal sealed class PsnCapstoneAuthoring
    {
        private const string BaseId = "psn-capstone";

        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly AutoCapstoneAuthoring _autoAuthoring;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;
        private readonly Func<ManagedCustomIconService> _iconService;
        private readonly ILogger _logger;

        public PsnCapstoneAuthoring(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            AutoCapstoneAuthoring autoAuthoring,
            Func<Guid, GameAchievementData> resolveGameData,
            Func<ManagedCustomIconService> iconService,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _autoAuthoring = autoAuthoring;
            _resolveGameData = resolveGameData;
            _iconService = iconService;
            _logger = logger;
        }

        /// <summary>
        /// Whether the game already has a capstone of any kind: an auto or PSN capstone, or an
        /// achievement set as the capstone, filtered or not. Automatic generation skips such games.
        /// </summary>
        public bool HasAnyCapstone(Guid gameId)
        {
            var stored = _store != null && _store.TryLoad(gameId, out var data) ? data : null;
            if (stored?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) == true)
            {
                return true;
            }

            var achievements = _autoAuthoring?.LoadAchievementsInOrder(gameId);
            return achievements?.Any(achievement => achievement != null && achievement.IsCapstone) == true;
        }

        /// <summary>Whether the game already has a PSN Capstone.</summary>
        public bool HasPsnCapstone(Guid gameId)
        {
            return _store != null &&
                   _store.TryLoad(gameId, out var data) &&
                   data?.CustomAchievements?.Any(definition => definition?.IsPsnCapstone == true) == true;
        }

        /// <summary>
        /// Authors the PSN Capstone and makes it the game's capstone.
        /// </summary>
        /// <param name="replaceExisting">
        /// True to drop the game's auto and PSN capstones first, which is what a manual press does
        /// after the user said yes. Automatic generation never replaces.
        /// </param>
        /// <returns>The new capstone's ApiName, or null when it could not be written.</returns>
        public async Task<string> AuthorAsync(
            Guid gameId,
            PsnPlatinum platinum,
            byte[] icon,
            bool replaceExisting)
        {
            if (gameId == Guid.Empty || platinum == null || !platinum.HasText ||
                _store == null || _overridesService == null)
            {
                return null;
            }

            if (replaceExisting)
            {
                _overridesService.ClearAutoCapstones(gameId);
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;
            var gameData = _resolveGameData?.Invoke(gameId);

            // Worked out now so a game already finished gets a capstone already unlocked and the
            // next refresh sees no crossing to announce. A game with nothing scanned yet gets a
            // locked one, which the maintenance after its first refresh brings up to date.
            var derived = AutoCapstoneCalculator.Derive(gameData?.Achievements ?? new List<AchievementDetail>(), null);

            var existing = stored?.CustomAchievements?
                .Where(definition => definition != null)
                .ToList() ?? new List<CustomAchievementDefinition>();
            var id = ResolveUniqueId(existing);
            var apiName = CustomAchievementProjectionService.BuildApiName(id);

            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = platinum.TrophyName,
                Description = platinum.TrophyDetail ?? string.Empty,
                Unlocked = derived?.Unlocked == true,
                UnlockTimeUtc = derived?.Unlocked == true ? derived.UnlockTimeUtc : null,
                UnlockedIconPath = await MaterializeIconAsync(gameId, icon, existing, apiName).ConfigureAwait(false),
                TrophyType = AutoCapstoneTemplate.PlatinumTrophyType,
                Hidden = true,
                Rarity = derived?.Rarity ?? "Common",
                GlobalPercentUnlocked = derived?.GlobalPercentUnlocked,
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = true,
                IsPsnCapstone = true
            };

            _overridesService.AddGeneratedAutoCapstone(gameId, definition, derived?.Category);

            var nominated = _overridesService.SetCapstone(gameId, apiName, true);
            if (nominated?.Success != true)
            {
                _logger?.Warn($"Authored a PSN capstone for gameId={gameId} but could not nominate it.");
            }

            return apiName;
        }

        private static string ResolveUniqueId(IReadOnlyList<CustomAchievementDefinition> existing)
        {
            var used = new HashSet<string>(
                existing.Select(definition => CustomAchievementProjectionService.NormalizeId(definition.Id))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);

            var candidate = BaseId;
            for (var suffix = 2; used.Contains(candidate); suffix++)
            {
                candidate = BaseId + "-" + suffix;
            }

            return candidate;
        }

        /// <summary>
        /// Puts the downloaded icon in the game's icon cache the way the editor's save does for a
        /// dropped icon. Null when there is no icon, which leaves the capstone on the default one.
        /// </summary>
        private async Task<string> MaterializeIconAsync(
            Guid gameId,
            byte[] icon,
            IReadOnlyList<CustomAchievementDefinition> existing,
            string apiName)
        {
            var iconService = _iconService?.Invoke();
            if (icon == null || icon.Length == 0 || iconService == null)
            {
                return null;
            }

            var stems = AchievementIconCachePathBuilder.BuildFileStems(
                existing.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id))
                    .Concat(new[] { apiName }));
            if (!stems.TryGetValue(apiName, out var stem) || string.IsNullOrWhiteSpace(stem))
            {
                return null;
            }

            var temp = Path.Combine(Path.GetTempPath(), "playach-psn-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(temp, icon);
                var managed = await iconService
                    .MaterializeCustomIconAsync(
                        temp,
                        gameId.ToString("D"),
                        stem,
                        AchievementIconVariant.Unlocked,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(managed) ? null : managed;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed caching the PSN capstone icon for gameId={gameId}.");
                return null;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // A leftover temp file is harmless.
                }
            }
        }
    }
}
