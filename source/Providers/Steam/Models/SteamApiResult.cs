using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Steam.Models
{
    /// <summary>
    /// How a Steam Web API service call ended. Steam reports the outcome in the x-eresult
    /// response header; HTTP status alone does not distinguish "no data" from "not allowed".
    /// </summary>
    internal enum SteamApiOutcome
    {
        /// <summary>EResult 1 with a readable body.</summary>
        Ok,

        /// <summary>EResult 42 (no match): the app has no achievements.</summary>
        NoAchievements,

        /// <summary>EResult 15 or 18: the profile or its game details are not visible.</summary>
        AccessDenied,

        /// <summary>HTTP 401/403: the access token was rejected.</summary>
        Unauthorized,

        /// <summary>Network failure, timeout, rate limit, server error or unreadable body.</summary>
        Transient
    }

    internal sealed class SteamApiResult<T>
    {
        private SteamApiResult(SteamApiOutcome outcome, T value, int? eResult, int statusCode)
        {
            Outcome = outcome;
            Value = value;
            EResult = eResult;
            StatusCode = statusCode;
        }

        public SteamApiOutcome Outcome { get; }

        public T Value { get; }

        public int? EResult { get; }

        public int StatusCode { get; }

        public bool IsOk => Outcome == SteamApiOutcome.Ok;

        public static SteamApiResult<T> Ok(T value, int? eResult = 1, int statusCode = 200) =>
            new SteamApiResult<T>(SteamApiOutcome.Ok, value, eResult, statusCode);

        public static SteamApiResult<T> Fail(SteamApiOutcome outcome, int? eResult = null, int statusCode = 0) =>
            new SteamApiResult<T>(outcome, default(T), eResult, statusCode);

        public SteamApiResult<TOther> As<TOther>(TOther value = default(TOther)) =>
            new SteamApiResult<TOther>(Outcome, value, EResult, StatusCode);

        public override string ToString() =>
            $"{Outcome} (eresult={EResult?.ToString() ?? "-"}, status={StatusCode})";
    }

    /// <summary>
    /// A player's unlock state for one app, keyed by Steam's numeric achievement key. An
    /// achievement absent from <see cref="ByInternalKey"/> is locked.
    /// </summary>
    internal sealed class SteamUserAchievements
    {
        public Dictionary<int, UserAchievement> ByInternalKey { get; set; } =
            new Dictionary<int, UserAchievement>();

        public Dictionary<int, UserAchievementGroup> Groups { get; set; } =
            new Dictionary<int, UserAchievementGroup>();

        public long? SchemaHash { get; set; }

        // Where the data came from, for logs: "WebApi" or "SsrPage".
        public string Source { get; set; }
    }
}
