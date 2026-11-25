using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// The estimated layer: PSN trophy grades and Xbox Gamerscore generated for achievements that
    /// have none of their own. It is the lowest layer, below the platform's and metadata sources'
    /// values and the user's overrides, and it lives in its own table that holds nothing native.
    /// </summary>
    /// <remarks>
    /// "Native wins" is enforced here, in the data layer, three ways: every write statement checks
    /// that no other layer supplies the slot, readers only fall back to the estimate when no layer
    /// supplies the slot, and <see cref="PurgeSupersededSql"/> drops an estimate once a layer does.
    /// A slot counts as supplied when the platform, a metadata source or the user's override has a
    /// value for it, or has deliberately cleared it. Emptying the table is one statement
    /// (<see cref="ClearAllSql"/>) and can never touch native data.
    /// </remarks>
    internal static class AchievementEstimatedLayerSql
    {
        public const string TableName = "AchievementEstimatedLayer";

        public const string CreateTableSql = @"CREATE TABLE IF NOT EXISTS AchievementEstimatedLayer (
                AchievementDefinitionId INTEGER PRIMARY KEY NOT NULL,
                Points INTEGER NULL,
                PointsReason TEXT NULL,
                TrophyType TEXT NULL,
                TrophyReason TEXT NULL,
                Confidence REAL NULL,
                Model TEXT NULL,
                GeneratedUtc TEXT NOT NULL,
                FOREIGN KEY (AchievementDefinitionId) REFERENCES PlatformDefinitions(Id) ON DELETE CASCADE
            );";

        // Definition deletes do not always run with foreign keys on, so a trigger drops the rows.
        public const string CreateTriggerSql = @"CREATE TRIGGER IF NOT EXISTS TR_AchievementDefinitions_DeleteEstimatedLayer
                AFTER DELETE ON PlatformDefinitions
                BEGIN
                    DELETE FROM AchievementEstimatedLayer WHERE AchievementDefinitionId = OLD.Id;
                END;";

        /// <summary>The definitions of a game with the layers that could supply a slot, as "ad", "m", "g" and "c".</summary>
        private const string DefinitionsWithLayersSql = @"
              FROM PlatformDefinitions ad
              LEFT JOIN AchievementMetadataLayer m ON m.AchievementDefinitionId = ad.Id
              LEFT JOIN Games g ON g.Id = ad.GameId
              LEFT JOIN " + AchievementLayerSql.AchievementsTable + @" c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0";

        /// <summary>
        /// True when no layer supplies the slot: the platform has no value, no metadata source set
        /// or cleared it, and the user's override neither set nor cleared it.
        /// </summary>
        private static string SlotFree(string column, AchievementField field)
        {
            var bit = (int)field;
            return "(ad." + column + " IS NULL" +
                   " AND m." + column + " IS NULL AND (COALESCE(m.Cleared, 0) & " + bit + ") = 0" +
                   " AND c." + column + " IS NULL AND (COALESCE(c.Cleared, 0) & " + bit + ") = 0)";
        }

        public static readonly string PointsFree = SlotFree("Points", AchievementField.Points);

        public static readonly string TrophyTypeFree = SlotFree("TrophyType", AchievementField.TrophyType);

        /// <summary>
        /// Makes sure the achievement has a row. Binds: generated UTC, game id (PlatformDefinitions.GameId), api name.
        /// </summary>
        public const string EnsureRowSql =
            @"INSERT OR IGNORE INTO AchievementEstimatedLayer (AchievementDefinitionId, GeneratedUtc)
              SELECT ad.Id, ? FROM PlatformDefinitions ad WHERE ad.GameId = ? AND ad.ApiName = ? COLLATE NOCASE;";

        /// <summary>
        /// Writes an estimated Gamerscore, only where no layer supplies Points.
        /// Binds: points, reason, confidence, model, generated UTC, game id, api name.
        /// </summary>
        public static readonly string WritePointsSql =
            @"UPDATE AchievementEstimatedLayer
              SET Points = ?, PointsReason = ?, Confidence = ?, Model = ?, GeneratedUtc = ?
              WHERE AchievementDefinitionId IN (
                  SELECT ad.Id" + DefinitionsWithLayersSql + @"
                  WHERE ad.GameId = ? AND ad.ApiName = ? COLLATE NOCASE AND " + PointsFree + ");";

        /// <summary>
        /// Writes an estimated trophy grade, only where no layer supplies TrophyType.
        /// Binds: trophy type, reason, confidence, model, generated UTC, game id, api name.
        /// </summary>
        public static readonly string WriteTrophyTypeSql =
            @"UPDATE AchievementEstimatedLayer
              SET TrophyType = ?, TrophyReason = ?, Confidence = ?, Model = ?, GeneratedUtc = ?
              WHERE AchievementDefinitionId IN (
                  SELECT ad.Id" + DefinitionsWithLayersSql + @"
                  WHERE ad.GameId = ? AND ad.ApiName = ? COLLATE NOCASE AND " + TrophyTypeFree + ");";

        /// <summary>
        /// Drops every estimate that a layer has since superseded (native data arrived, or the user
        /// set or cleared the slot), then rows left holding nothing. Safe to run at any time.
        /// </summary>
        public static readonly string[] PurgeSupersededSql =
        {
            @"UPDATE AchievementEstimatedLayer SET Points = NULL, PointsReason = NULL
              WHERE Points IS NOT NULL AND AchievementDefinitionId IN (
                  SELECT ad.Id" + DefinitionsWithLayersSql + " WHERE NOT " + PointsFree + ");",
            @"UPDATE AchievementEstimatedLayer SET TrophyType = NULL, TrophyReason = NULL
              WHERE TrophyType IS NOT NULL AND AchievementDefinitionId IN (
                  SELECT ad.Id" + DefinitionsWithLayersSql + " WHERE NOT " + TrophyTypeFree + ");",
            "DELETE FROM AchievementEstimatedLayer WHERE Points IS NULL AND TrophyType IS NULL;"
        };

        /// <summary>Removes rows left holding nothing after a write that the guard skipped.</summary>
        public const string DeleteEmptySql =
            "DELETE FROM AchievementEstimatedLayer WHERE Points IS NULL AND TrophyType IS NULL;";

        /// <summary>The roll-back: deletes every generated value. Native data lives in other tables.</summary>
        public const string ClearAllSql = "DELETE FROM AchievementEstimatedLayer;";

        /// <summary>Deletes one game's generated values. Binds: PlatformDefinitions.GameId.</summary>
        public const string ClearGameSql =
            @"DELETE FROM AchievementEstimatedLayer
              WHERE AchievementDefinitionId IN (SELECT Id FROM PlatformDefinitions WHERE GameId = ?);";
    }
}
