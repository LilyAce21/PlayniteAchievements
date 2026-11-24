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
                    RarityPercent = a.GlobalPercentUnlocked,
                    NativeTrophyType = a.IsTrophyTypeEstimated ? null : a.TrophyType,
                    NativePoints = a.IsPointsEstimated ? null : a.Points,
                    HasEstimatedTrophy = a.IsTrophyTypeEstimated,
                    HasEstimatedPoints = a.IsPointsEstimated
                });
            }

            return source;
        }
    }
}
