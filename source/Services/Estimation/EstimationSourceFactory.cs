using System;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Builds the planner's input from a loaded game. Cross-platform data is already merged onto each
    /// achievement by PA (metadata sources fill Points and TrophyType), so a row's own native values are
    /// the anchors. Values that are themselves estimates are never counted as native.
    /// </summary>
    internal static class EstimationSourceFactory
    {
        public static EstimationSourceGame From(GameAchievementData data)
        {
            if (data?.Achievements == null) return null;

            var game = data.Game;
            var genre = game?.Genres == null ? null : string.Join(", ", game.Genres.Select(g => g?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(3));

            var source = new EstimationSourceGame
            {
                PlayniteGameId = data.PlayniteGameId ?? game?.Id ?? Guid.Empty,
                Title = game?.Name ?? data.GameName,
                ReleaseYear = game?.ReleaseYear,
                Genre = genre,
                ProviderKey = data.EffectiveProviderKey,
                PointsAreGamerscore = string.Equals(
                    PointsFamilies.FromProviderKey(data.EffectiveProviderKey),
                    PointsFamilies.Xbox,
                    StringComparison.OrdinalIgnoreCase)
            };

            foreach (var a in data.Achievements.Where(a => a != null))
            {
                source.Achievements.Add(new EstimationSourceAchievement
                {
                    ApiName = a.ApiName,
                    DisplayName = a.DisplayName,
                    Description = a.Description,
                    IsHidden = a.Hidden,
                    Category = a.Category,
                    CategoryKind = a.CategoryKind,
                    RarityPercent = a.GlobalPercentUnlocked,
                    NativeTrophyType = a.IsTrophyTypeEstimated ? null : a.TrophyType,
                    NativePoints = a.IsPointsEstimated ? null : a.Points,
                    HasEstimatedTrophy = a.IsTrophyTypeEstimated,
                    HasEstimatedPoints = a.IsPointsEstimated
                });
            }

            // Only the Xbox metadata source supplies Points and only the PlayStation source supplies trophy grades, so a
            // native value that no platform layer supplied can only be those official values. Calibration counts such
            // games like native Xbox and PSN lists, since they are the real thing.
            var rows = data.Achievements.Where(a => a != null).ToList();
            var withPoints = rows.Where(a => !a.IsPointsEstimated && a.Points.HasValue).ToList();
            source.PointsFromXboxMetadata = withPoints.Count > 0 && withPoints.All(a =>
                a.PlatformLayer?.Points == null && a.MetadataLayer?.Points != null);
            var withTrophies = rows.Where(a => !a.IsTrophyTypeEstimated && !string.IsNullOrWhiteSpace(a.TrophyType)).ToList();
            source.TrophiesFromPlayStationMetadata = withTrophies.Count > 0 && withTrophies.All(a =>
                string.IsNullOrWhiteSpace(a.PlatformLayer?.TrophyType) && !string.IsNullOrWhiteSpace(a.MetadataLayer?.TrophyType));

            return source;
        }
    }
}
