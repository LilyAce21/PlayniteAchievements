using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Hides every capstone that is still locked, across all games with cached achievements.
    /// </summary>
    /// <remarks>
    /// A hidden achievement reveals itself once it unlocks, so this only touches locked ones, and
    /// only those not already hidden. It writes the same Hidden override the editor does, so a
    /// capstone the user later unhides stays unhidden until this is run again.
    /// </remarks>
    public static class LockedCapstoneHider
    {
        /// <summary>The outcome of one pass.</summary>
        public sealed class Result
        {
            public int GamesChanged { get; set; }

            public int CapstonesHidden { get; set; }
        }

        /// <param name="cachedGameIds">Ids of the games to scan (the cache's game ids).</param>
        /// <param name="resolveGameData">Merged achievement data for a game, capstone flags and overrides applied.</param>
        /// <param name="overrides">Where the Hidden override is written.</param>
        /// <param name="progress">Optional (done, total) callback after each game.</param>
        public static Result HideAllLocked(
            IReadOnlyList<string> cachedGameIds,
            Func<Guid, GameAchievementData> resolveGameData,
            AchievementOverridesService overrides,
            Action<int, int> progress = null)
        {
            var result = new Result();
            if (cachedGameIds == null || resolveGameData == null || overrides == null)
            {
                return result;
            }

            for (var i = 0; i < cachedGameIds.Count; i++)
            {
                if (Guid.TryParse(cachedGameIds[i], out var gameId) && gameId != Guid.Empty)
                {
                    var targets = LockedVisibleCapstones(resolveGameData(gameId));
                    if (targets.Count > 0)
                    {
                        overrides.SetAchievementFieldOverride(gameId, targets, AchievementEditableField.Hidden, (bool?)true);
                        result.GamesChanged++;
                        result.CapstonesHidden += targets.Count;
                    }
                }

                progress?.Invoke(i + 1, cachedGameIds.Count);
            }

            return result;
        }

        /// <summary>The api names of a game's capstones that are locked and not yet hidden.</summary>
        public static List<string> LockedVisibleCapstones(GameAchievementData data)
        {
            if (data?.Achievements == null)
            {
                return new List<string>();
            }

            return data.Achievements
                .Where(achievement => achievement != null &&
                                      achievement.IsCapstone &&
                                      !achievement.Unlocked &&
                                      !achievement.Hidden &&
                                      !string.IsNullOrWhiteSpace(achievement.ApiName))
                .Select(achievement => achievement.ApiName)
                .ToList();
        }
    }
}
