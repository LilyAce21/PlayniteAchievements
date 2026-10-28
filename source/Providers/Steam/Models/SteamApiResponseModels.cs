using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.Steam.Models
{
    [DataContract]
    internal sealed class OwnedGamesEnvelope
    {
        [DataMember(Name = "response")]
        public OwnedGamesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGamesResponse
    {
        [DataMember(Name = "games")]
        public List<OwnedGame> Games { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGame
    {
        [DataMember(Name = "appid")]
        public int? AppId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "type")]
        public string AppType { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int? PlaytimeForever { get; set; }

        [DataMember(Name = "rtime_last_played")]
        public long? RTimeLastPlayed { get; set; }

        public string LibrarySourceName { get; set; }
    }

    [DataContract]
    internal sealed class SteamAsyncConfigResponse
    {
        [DataMember(Name = "success")]
        public int? Success { get; set; }

        [DataMember(Name = "data")]
        public SteamAsyncConfigData Data { get; set; }
    }

    [DataContract]
    internal sealed class SteamAsyncConfigData
    {
        [DataMember(Name = "webapi_token")]
        public string WebApiToken { get; set; }
    }

    [DataContract]
    internal sealed class SchemaAchievement
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "icon")]
        public string Icon { get; set; }

        [DataMember(Name = "icongray")]
        public string IconGray { get; set; }

        [DataMember(Name = "hidden")]
        public int Hidden { get; set; }

        [DataMember(Name = "globalPercent")]
        public double? GlobalPercent { get; set; }

        [DataMember(Name = "points")]
        public int? Points { get; set; }

        // Internal-only bridge fields used when matching English and localized
        // community rows before copying localized text onto SteamHunters entries.
        public string LocalizedDisplayName { get; set; }

        public string LocalizedDescription { get; set; }
        // Steam's numeric achievement key; joins schema rows to GetUserAchievements and
        // GetGlobalAchievementPercentages, which identify achievements only by this key.
        public int? InternalKey { get; set; }

        // Achievement set (group) id; 0 is the base set that holds achievements without a set.
        public int GroupId { get; set; }

        // Retired by the developer: hidden from players who did not earn it and excluded from
        // completion.
        public bool Archived { get; set; }

        // Progress bar bounds when the achievement tracks a stat (max greater than 1).
        public double? MinProgress { get; set; }

        public double? MaxProgress { get; set; }
    }

    // One Steam achievement set. Group 0 is the base set; its name is usually empty.
    internal sealed class SchemaAchievementGroup
    {
        public int GroupId { get; set; }

        public string Name { get; set; }

        // Non-zero when the set belongs to a DLC product.
        public int DlcAppId { get; set; }

        public bool Archived { get; set; }

        public bool DeveloperOnly { get; set; }

        public bool IsPublic { get; set; } = true;

        public int Order { get; set; }
    }

    [DataContract]
    internal sealed class GetSchemaForGameRoot
    {
        [DataMember(Name = "response")]
        public GetSchemaForGameResponse Response { get; set; }

        // Some Steam Web API gateways return the v2 payload without the response envelope.
        [DataMember(Name = "game")]
        public GetSchemaForGameGame Game { get; set; }
    }

    [DataContract]
    internal sealed class GetSchemaForGameResponse
    {
        [DataMember(Name = "game")]
        public GetSchemaForGameGame Game { get; set; }
    }

    [DataContract]
    internal sealed class GetSchemaForGameGame
    {
        [DataMember(Name = "availableGameStats")]
        public GetSchemaForGameAvailableStats AvailableGameStats { get; set; }
    }

    [DataContract]
    internal sealed class GetSchemaForGameAvailableStats
    {
        [DataMember(Name = "achievements")]
        public List<SchemaAchievement> Achievements { get; set; }
    }

    [DataContract]
    internal sealed class GetGameAchievementsRoot
    {
        [DataMember(Name = "response")]
        public GetGameAchievementsResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetGameAchievementsResponse
    {
        [DataMember(Name = "achievements")]
        public List<Achievement> Achievements { get; set; }

        [DataMember(Name = "groups")]
        public List<GameAchievementGroup> Groups { get; set; }

        [DataMember(Name = "schema_hash")]
        public long? SchemaHash { get; set; }

        [DataMember(Name = "schema_version")]
        public long? SchemaVersion { get; set; }
    }

    [DataContract]
    internal sealed class GameAchievementGroup
    {
        [DataMember(Name = "groupid")]
        public int GroupId { get; set; }

        [DataMember(Name = "localized_name")]
        public string LocalizedName { get; set; }

        [DataMember(Name = "dlcappid")]
        public int? DlcAppId { get; set; }

        [DataMember(Name = "archived")]
        public bool Archived { get; set; }

        [DataMember(Name = "developeronly")]
        public bool DeveloperOnly { get; set; }

        [DataMember(Name = "order")]
        public int? Order { get; set; }

        [DataMember(Name = "ispublic")]
        public bool? IsPublic { get; set; }

        [DataMember(Name = "total_achievements")]
        public int? TotalAchievements { get; set; }

        [DataMember(Name = "completion_achievements")]
        public int? CompletionAchievements { get; set; }
    }

    [DataContract]
    internal sealed class GetUserAchievementsRoot
    {
        [DataMember(Name = "response")]
        public GetUserAchievementsResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetUserAchievementsResponse
    {
        [DataMember(Name = "achievements")]
        public List<UserAchievement> Achievements { get; set; }

        [DataMember(Name = "groups")]
        public List<UserAchievementGroup> Groups { get; set; }

        [DataMember(Name = "schema_hash")]
        public long? SchemaHash { get; set; }

        [DataMember(Name = "schema_version")]
        public long? SchemaVersion { get; set; }
    }

    [DataContract]
    internal sealed class UserAchievement
    {
        [DataMember(Name = "internal_key")]
        public int InternalKey { get; set; }

        [DataMember(Name = "unlocked")]
        public bool Unlocked { get; set; }

        [DataMember(Name = "unlock_time")]
        public long? UnlockTime { get; set; }

        [DataMember(Name = "progress_int")]
        public long? ProgressInt { get; set; }

        [DataMember(Name = "progress_float")]
        public double? ProgressFloat { get; set; }
    }

    [DataContract]
    internal sealed class UserAchievementGroup
    {
        [DataMember(Name = "groupid")]
        public int GroupId { get; set; }

        [DataMember(Name = "is_achievable")]
        public bool? IsAchievable { get; set; }

        [DataMember(Name = "completed_achievements")]
        public int? CompletedAchievements { get; set; }

        [DataMember(Name = "is_completed")]
        public bool? IsCompleted { get; set; }

        [DataMember(Name = "time_completed")]
        public long? TimeCompleted { get; set; }
    }

    [DataContract]
    internal sealed class GetGlobalAchievementPercentagesRoot
    {
        [DataMember(Name = "response")]
        public GetGlobalAchievementPercentagesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetGlobalAchievementPercentagesResponse
    {
        [DataMember(Name = "achievements")]
        public List<GlobalAchievementPercentage> Achievements { get; set; }
    }

    [DataContract]
    internal sealed class GlobalAchievementPercentage
    {
        [DataMember(Name = "internal_key")]
        public int InternalKey { get; set; }

        [DataMember(Name = "player_percent_unlocked")]
        public double? PlayerPercentUnlocked { get; set; }
    }

    [DataContract]
    internal sealed class Achievement
    {
        [DataMember(Name = "internal_name")]
        public string InternalName { get; set; }

        [DataMember(Name = "localized_name")]
        public string LocalizedName { get; set; }

        [DataMember(Name = "localized_desc")]
        public string LocalizedDesc { get; set; }

        [DataMember(Name = "icon")]
        public string Icon { get; set; }

        [DataMember(Name = "icon_gray")]
        public string IconGray { get; set; }

        [DataMember(Name = "hidden")]
        public bool Hidden { get; set; }

        [DataMember(Name = "player_percent_unlocked")]
        public string PlayerPercentUnlocked { get; set; }

        [DataMember(Name = "internal_key")]
        public int? InternalKey { get; set; }

        [DataMember(Name = "groupid")]
        public int? GroupId { get; set; }

        [DataMember(Name = "archived")]
        public bool Archived { get; set; }

        [DataMember(Name = "progress_type")]
        public int? ProgressType { get; set; }

        [DataMember(Name = "min_progress_int")]
        public long? MinProgressInt { get; set; }

        [DataMember(Name = "max_progress_int")]
        public long? MaxProgressInt { get; set; }

        [DataMember(Name = "min_progress_float")]
        public double? MinProgressFloat { get; set; }

        [DataMember(Name = "max_progress_float")]
        public double? MaxProgressFloat { get; set; }
    }

    internal class SchemaAndPercentages
    {
        // Override rows on a separate container so clearing a game's custom schema
        // exposes the original cached Steam rows again.
        public SchemaAndPercentages WithAchievements(List<SchemaAchievement> achievements)
        {
            return new SchemaAndPercentages
            {
                Achievements = achievements,
                GlobalPercentages = GlobalPercentages,
                Groups = Groups,
                SchemaHash = SchemaHash
            };
        }

        public List<SchemaAchievement> Achievements { get; set; }
        public Dictionary<string, double> GlobalPercentages { get; set; }

        // Achievement sets keyed by group id. Empty for schemas from sources without set data.
        public Dictionary<int, SchemaAchievementGroup> Groups { get; set; } =
            new Dictionary<int, SchemaAchievementGroup>();

        public long? SchemaHash { get; set; }

        // True when the schema has at least one real set beyond the unnamed base set, so set
        // names are meaningful as categories.
        public bool HasAchievementSets =>
            Groups != null && Groups.Values.Any(group => group.GroupId != 0 || !string.IsNullOrWhiteSpace(group.Name));
    }

    [DataContract]
    internal sealed class SteamOwnedGame
    {
        [DataMember(Name = "appid")]
        public int AppId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int PlaytimeForever { get; set; }

        [DataMember(Name = "playtime_2weeks")]
        public int? Playtime2Weeks { get; set; }

        [DataMember(Name = "rtime_last_played")]
        public long? LastPlayedUnixSeconds { get; set; }

        public int? AchievementsEarned { get; set; }
        public int? AchievementsTotal { get; set; }
    }

    [DataContract]
    internal sealed class GetOwnedGamesRoot
    {
        [DataMember(Name = "response")]
        public GetOwnedGamesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetOwnedGamesResponse
    {
        [DataMember(Name = "game_count")]
        public int? GameCount { get; set; }

        [DataMember(Name = "games")]
        public List<SteamOwnedGame> Games { get; set; }
    }
}
