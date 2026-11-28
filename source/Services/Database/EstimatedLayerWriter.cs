using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>One achievement's generated values. Either value may be null (nothing generated for it).</summary>
    public sealed class EstimatedValueRow
    {
        public string ApiName { get; set; }

        /// <summary>Estimated Xbox Gamerscore.</summary>
        public int? Points { get; set; }

        public string PointsReason { get; set; }

        /// <summary>Estimated PSN trophy type: "bronze", "silver" or "gold". Never platinum.</summary>
        public string TrophyType { get; set; }

        public string TrophyReason { get; set; }

        public double? Confidence { get; set; }
    }

    /// <summary>
    /// Writes, purges and clears the estimated layer. Every write goes through the guarded
    /// statements of <see cref="AchievementEstimatedLayerSql"/>, which skip any slot another layer
    /// supplies, so a native value is never replaced and an estimate is never stored next to one.
    /// </summary>
    internal static class EstimatedLayerWriter
    {
        private sealed class IdRow
        {
            public long Id { get; set; }
        }

        private static object Db<T>(T? value) where T : struct =>
            value.HasValue ? (object)value.Value : DBNull.Value;

        private static object Db(string value) =>
            string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;

        private static bool IsAllowedTrophyType(string type)
        {
            return string.Equals(type, "bronze", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "silver", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "gold", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Stores the rows for every definition set of the Playnite game and returns how many values
        /// were actually written. Values for slots a native layer supplies are skipped silently.
        /// A trophy type that is not bronze, silver or gold (platinum included) is never stored.
        /// </summary>
        public static int Write(
            SQLiteDatabase db,
            Guid playniteGameId,
            string model,
            IReadOnlyList<EstimatedValueRow> rows)
        {
            if (db == null || playniteGameId == Guid.Empty || rows == null || rows.Count == 0) return 0;

            var gameIds = db.Load<IdRow>(
                    "SELECT Id AS Id FROM Games WHERE PlayniteGameId = ? COLLATE NOCASE;",
                    playniteGameId.ToString())
                .Select(row => row.Id)
                .ToList();
            if (gameIds.Count == 0) return 0;

            var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var written = 0;

            db.RunTransaction(() =>
            {
                foreach (var gameId in gameIds)
                {
                    foreach (var row in rows)
                    {
                        if (row == null || string.IsNullOrWhiteSpace(row.ApiName)) continue;

                        var wantsPoints = row.Points.HasValue && row.Points.Value >= 0; // 0 G is a real value (a failure achievement in DLC)
                        var wantsTrophy = !string.IsNullOrWhiteSpace(row.TrophyType) && IsAllowedTrophyType(row.TrophyType);
                        if (!wantsPoints && !wantsTrophy) continue;

                        db.ExecuteNonQuery(AchievementEstimatedLayerSql.EnsureRowSql, nowIso, gameId, row.ApiName);

                        if (wantsPoints)
                        {
                            written += db.ExecuteNonQuery(
                                AchievementEstimatedLayerSql.WritePointsSql,
                                row.Points.Value, Db(row.PointsReason), Db(row.Confidence), Db(model), nowIso,
                                gameId, row.ApiName);
                        }

                        if (wantsTrophy)
                        {
                            written += db.ExecuteNonQuery(
                                AchievementEstimatedLayerSql.WriteTrophyTypeSql,
                                row.TrophyType.Trim().ToLowerInvariant(), Db(row.TrophyReason), Db(row.Confidence), Db(model), nowIso,
                                gameId, row.ApiName);
                        }
                    }
                }

                // Rows the guard skipped are left holding nothing.
                db.ExecuteNonQuery(AchievementEstimatedLayerSql.DeleteEmptySql);
            });

            return written;
        }

        /// <summary>Drops every estimate a native or user layer has since superseded.</summary>
        public static void PurgeSuperseded(SQLiteDatabase db)
        {
            if (db == null) return;
            db.RunTransaction(() =>
            {
                foreach (var sql in AchievementEstimatedLayerSql.PurgeSupersededSql)
                {
                    db.ExecuteNonQuery(sql);
                }
            });
        }

        /// <summary>
        /// Deletes every generated value, returning how many rows went. Native data lives in other
        /// tables and is not touched. This is the roll-back for the whole feature.
        /// </summary>
        public static long ClearAll(SQLiteDatabase db)
        {
            if (db == null) return 0;

            long count = 0;
            db.RunTransaction(() =>
            {
                count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM " + AchievementEstimatedLayerSql.TableName + ";");
                db.ExecuteNonQuery(AchievementEstimatedLayerSql.ClearAllSql);
            });
            return count;
        }
    }
}
