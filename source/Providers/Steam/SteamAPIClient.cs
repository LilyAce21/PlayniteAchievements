using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Steam.Models;
using Playnite.SDK;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamApiClient
    {
        private const string ServiceBaseUrl = "https://api.steampowered.com/IPlayerService/";

        // Steam EResult codes the achievement services return.
        internal const int EResultOk = 1;
        internal const int EResultAccessDenied = 15;
        internal const int EResultFileNotFound = 18;
        internal const int EResultNoMatch = 42;

        private readonly HttpClient _apiHttp;
        private readonly ILogger _logger;

        public SteamApiClient(HttpClient apiHttp, ILogger logger)
        {
            _apiHttp = apiHttp ?? throw new ArgumentNullException(nameof(apiHttp));
            _logger = logger;
        }

        public async Task<SchemaAndPercentages> GetSchemaForGameDetailedAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            var result = await GetGameSchemaAsync(accessToken, appId, language, ct).ConfigureAwait(false);
            return result.IsOk ? result.Value : null;
        }

        /// <summary>
        /// Fetches the achievement schema (definitions, sets and global percentages) through
        /// IPlayerService/GetGameAchievements. The access token is optional; the service answers
        /// without one. A failed localized fetch retries once in English.
        /// </summary>
        public async Task<SteamApiResult<SchemaAndPercentages>> GetGameSchemaAsync(
            string accessToken,
            int appId,
            string language,
            CancellationToken ct)
        {
            language = string.IsNullOrWhiteSpace(language) ? "english" : language.Trim();
            var result = await GetGameSchemaInternalAsync(accessToken, appId, language, ct).ConfigureAwait(false);
            if (result.Outcome == SteamApiOutcome.Transient &&
                !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Info($"Schema fetch failed for '{language}', retrying with 'english' for appId={appId}");
                result = await GetGameSchemaInternalAsync(accessToken, appId, "english", ct).ConfigureAwait(false);
            }

            return result;
        }

        public async Task<bool?> GetGameHasAchievementsAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            if (appId <= 0)
            {
                return false;
            }

            var result = await GetGameSchemaInternalAsync(
                accessToken,
                appId,
                string.IsNullOrWhiteSpace(language) ? "english" : language,
                ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SteamApiOutcome.Ok:
                    return result.Value?.Achievements?.Count > 0;
                case SteamApiOutcome.NoAchievements:
                    return false;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Fetches a player's unlocks through IPlayerService/GetUserAchievements. Public
        /// profiles answer without a token; the viewer's token is needed for profiles that are
        /// private or friends-only to that viewer. A successful response with no achievements
        /// means nothing is unlocked (including games the player does not own).
        /// </summary>
        public async Task<SteamApiResult<SteamUserAchievements>> GetUserAchievementsAsync(
            string accessToken,
            string steamId64,
            int appId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(steamId64) || appId <= 0)
            {
                return SteamApiResult<SteamUserAchievements>.Fail(SteamApiOutcome.AccessDenied);
            }

            var response = await SendServiceAsync<GetUserAchievementsRoot>(
                "GetUserAchievements",
                accessToken,
                new Dictionary<string, string>
                {
                    ["steamid"] = steamId64.Trim(),
                    ["appid"] = appId.ToString(CultureInfo.InvariantCulture)
                },
                ct).ConfigureAwait(false);
            if (!response.IsOk)
            {
                return response.As<SteamUserAchievements>();
            }

            var body = response.Value?.Response;
            var data = new SteamUserAchievements
            {
                SchemaHash = body?.SchemaHash,
                Source = "WebApi"
            };

            foreach (var achievement in body?.Achievements ?? Enumerable.Empty<UserAchievement>())
            {
                if (achievement != null)
                {
                    data.ByInternalKey[achievement.InternalKey] = achievement;
                }
            }

            foreach (var group in body?.Groups ?? Enumerable.Empty<UserAchievementGroup>())
            {
                if (group != null)
                {
                    data.Groups[group.GroupId] = group;
                }
            }

            return SteamApiResult<SteamUserAchievements>.Ok(data, response.EResult, response.StatusCode);
        }

        /// <summary>
        /// Fetches global unlock percentages keyed by internal key. Used when the schema omits
        /// player_percent_unlocked.
        /// </summary>
        public async Task<SteamApiResult<Dictionary<int, double>>> GetGlobalAchievementPercentagesAsync(
            string accessToken,
            int appId,
            CancellationToken ct)
        {
            if (appId <= 0)
            {
                return SteamApiResult<Dictionary<int, double>>.Fail(SteamApiOutcome.NoAchievements);
            }

            var response = await SendServiceAsync<GetGlobalAchievementPercentagesRoot>(
                "GetGlobalAchievementPercentages",
                accessToken,
                new Dictionary<string, string> { ["appid"] = appId.ToString(CultureInfo.InvariantCulture) },
                ct).ConfigureAwait(false);
            if (!response.IsOk)
            {
                return response.As<Dictionary<int, double>>();
            }

            var percentages = new Dictionary<int, double>();
            foreach (var item in response.Value?.Response?.Achievements ?? Enumerable.Empty<GlobalAchievementPercentage>())
            {
                if (item?.PlayerPercentUnlocked != null)
                {
                    percentages[item.InternalKey] = item.PlayerPercentUnlocked.Value;
                }
            }

            return SteamApiResult<Dictionary<int, double>>.Ok(percentages, response.EResult, response.StatusCode);
        }

        public async Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(
            string accessToken,
            string steamId64,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(steamId64))
            {
                return null;
            }

            try
            {
                var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                          $"?access_token={Uri.EscapeDataString(accessToken)}" +
                          $"&steamid={Uri.EscapeDataString(steamId64.Trim())}" +
                          $"&include_appinfo=true" +
                          $"&include_played_free_games=true" +
                          $"&format=json";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = JsonConvert.DeserializeObject<GetOwnedGamesRoot>(json);
                    var response = root?.Response;
                    if (response == null)
                    {
                        return null;
                    }

                    if (response.Games != null)
                    {
                        return response.Games
                            .Where(game => game != null && game.AppId > 0)
                            .ToList();
                    }

                    return response.GameCount.GetValueOrDefault() == 0
                        ? (IReadOnlyList<SteamOwnedGame>)Array.Empty<SteamOwnedGame>()
                        : null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"GetOwnedGames API request failed for steamId={steamId64}");
                return null;
            }
        }

        private async Task<SteamApiResult<SchemaAndPercentages>> GetGameSchemaInternalAsync(
            string accessToken,
            int appId,
            string language,
            CancellationToken ct)
        {
            if (appId <= 0)
            {
                _logger?.Warn($"GetGameSchemaInternalAsync: Invalid appId={appId}");
                return SteamApiResult<SchemaAndPercentages>.Fail(SteamApiOutcome.NoAchievements);
            }

            var response = await SendServiceAsync<GetGameAchievementsRoot>(
                "GetGameAchievements",
                accessToken,
                new Dictionary<string, string>
                {
                    ["appid"] = appId.ToString(CultureInfo.InvariantCulture),
                    ["language"] = language
                },
                ct).ConfigureAwait(false);
            if (!response.IsOk)
            {
                return response.As<SchemaAndPercentages>();
            }

            var schema = MapSchema(appId, response.Value?.Response);
            if (schema == null)
            {
                return SteamApiResult<SchemaAndPercentages>.Fail(
                    SteamApiOutcome.NoAchievements,
                    response.EResult,
                    response.StatusCode);
            }

            return SteamApiResult<SchemaAndPercentages>.Ok(schema, response.EResult, response.StatusCode);
        }

        internal static SchemaAndPercentages MapSchema(int appId, GetGameAchievementsResponse response)
        {
            var achievements = response?.Achievements;
            if (achievements == null || achievements.Count == 0)
            {
                return null;
            }

            var schemaAchievements = new List<SchemaAchievement>();
            var globalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var ach in achievements)
            {
                if (ach == null)
                {
                    continue;
                }

                var normalizedInternalName = NormalizeApiText(ach.InternalName);
                var (minProgress, maxProgress) = ResolveProgressBounds(ach);

                schemaAchievements.Add(new SchemaAchievement
                {
                    Name = normalizedInternalName,
                    DisplayName = NormalizeApiText(ach.LocalizedName),
                    Description = NormalizeApiText(ach.LocalizedDesc),
                    Icon = BuildAchievementIconUrl(appId, ach.Icon),
                    IconGray = BuildAchievementIconUrl(appId, ach.IconGray),
                    Hidden = ach.Hidden ? 1 : 0,
                    InternalKey = ach.InternalKey,
                    GroupId = ach.GroupId ?? 0,
                    Archived = ach.Archived,
                    MinProgress = minProgress,
                    MaxProgress = maxProgress
                });

                if (!string.IsNullOrWhiteSpace(normalizedInternalName) &&
                    double.TryParse(
                        ach.PlayerPercentUnlocked,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var percent))
                {
                    globalPercentages[normalizedInternalName] = percent;
                }
            }

            var groups = new Dictionary<int, SchemaAchievementGroup>();
            foreach (var group in response.Groups ?? Enumerable.Empty<GameAchievementGroup>())
            {
                if (group == null)
                {
                    continue;
                }

                groups[group.GroupId] = new SchemaAchievementGroup
                {
                    GroupId = group.GroupId,
                    Name = NormalizeApiText(group.LocalizedName),
                    DlcAppId = group.DlcAppId ?? 0,
                    Archived = group.Archived,
                    DeveloperOnly = group.DeveloperOnly,
                    IsPublic = group.IsPublic ?? true,
                    Order = group.Order ?? 0
                };
            }

            return new SchemaAndPercentages
            {
                Achievements = schemaAchievements,
                GlobalPercentages = globalPercentages,
                Groups = groups,
                SchemaHash = response.SchemaHash
            };
        }

        // Integer bounds win when present; float bounds cover float-stat achievements.
        private static (double? Min, double? Max) ResolveProgressBounds(Achievement achievement)
        {
            if (achievement?.MaxProgressInt.HasValue == true)
            {
                return (achievement.MinProgressInt ?? 0, achievement.MaxProgressInt.Value);
            }

            if (achievement?.MaxProgressFloat.HasValue == true)
            {
                return (achievement.MinProgressFloat ?? 0, achievement.MaxProgressFloat.Value);
            }

            return (null, null);
        }

        /// <summary>
        /// Calls one IPlayerService method and classifies the outcome from the HTTP status and
        /// the x-eresult header. Omits access_token when none is supplied.
        /// </summary>
        private async Task<SteamApiResult<TRoot>> SendServiceAsync<TRoot>(
            string method,
            string accessToken,
            IDictionary<string, string> parameters,
            CancellationToken ct)
            where TRoot : class
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                query.Add($"access_token={Uri.EscapeDataString(accessToken.Trim())}");
            }

            foreach (var parameter in parameters)
            {
                query.Add($"{parameter.Key}={Uri.EscapeDataString(parameter.Value ?? string.Empty)}");
            }

            var url = $"{ServiceBaseUrl}{method}/v1/?{string.Join("&", query)}";

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var statusCode = (int)resp.StatusCode;
                    var eResult = ReadEResult(resp);

                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        if (!string.IsNullOrWhiteSpace(accessToken))
                        {
                            // An expired or rejected token should not block data Steam serves
                            // anonymously (public profiles, every schema), so retry without it.
                            _logger?.Debug($"[SteamApi] {method} rejected the access token (status={statusCode}); retrying without it.");
                            return await SendServiceAsync<TRoot>(method, null, parameters, ct).ConfigureAwait(false);
                        }

                        return SteamApiResult<TRoot>.Fail(SteamApiOutcome.Unauthorized, eResult, statusCode);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        return SteamApiResult<TRoot>.Fail(SteamApiOutcome.Transient, eResult, statusCode);
                    }

                    var outcome = ClassifyEResult(eResult);
                    if (outcome != SteamApiOutcome.Ok)
                    {
                        _logger?.Debug($"[SteamApi] {method} returned eresult={eResult} for {DescribeParameters(parameters)}.");
                        return SteamApiResult<TRoot>.Fail(outcome, eResult, statusCode);
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return SteamApiResult<TRoot>.Fail(SteamApiOutcome.Transient, eResult, statusCode);
                    }

                    var root = JsonConvert.DeserializeObject<TRoot>(json);
                    return root == null
                        ? SteamApiResult<TRoot>.Fail(SteamApiOutcome.Transient, eResult, statusCode)
                        : SteamApiResult<TRoot>.Ok(root, eResult, statusCode);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Includes HttpClient timeouts (TaskCanceledException without a cancelled token)
                // and malformed JSON.
                _logger?.Debug(ex, $"[SteamApi] {method} request failed for {DescribeParameters(parameters)}.");
                return SteamApiResult<TRoot>.Fail(SteamApiOutcome.Transient);
            }
        }

        // A missing header is treated as success; the body decides from there.
        internal static SteamApiOutcome ClassifyEResult(int? eResult)
        {
            if (!eResult.HasValue || eResult.Value == EResultOk)
            {
                return SteamApiOutcome.Ok;
            }

            switch (eResult.Value)
            {
                case EResultNoMatch:
                    return SteamApiOutcome.NoAchievements;
                case EResultAccessDenied:
                case EResultFileNotFound:
                    return SteamApiOutcome.AccessDenied;
                default:
                    return SteamApiOutcome.Transient;
            }
        }

        private static int? ReadEResult(HttpResponseMessage resp)
        {
            if (resp?.Headers != null &&
                resp.Headers.TryGetValues("x-eresult", out var values))
            {
                var raw = values?.FirstOrDefault();
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string DescribeParameters(IDictionary<string, string> parameters)
        {
            return string.Join(", ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
        }

        private static string NormalizeApiText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        internal static string BuildAchievementIconUrl(int appId, string iconFile)
        {
            var normalizedIconFile = NormalizeApiText(iconFile);
            if (string.IsNullOrWhiteSpace(normalizedIconFile))
            {
                return string.Empty;
            }

            // The achievements page delivers absolute icon URLs; the Web API delivers file names.
            if (normalizedIconFile.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                normalizedIconFile.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedIconFile;
            }

            return $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{appId}/{normalizedIconFile}";
        }
    }
}
