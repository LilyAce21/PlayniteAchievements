using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Workshop
{
    /// <summary>
    /// What the Workshop service knows about an item: how many installs it has, how many likes,
    /// and which items this install's submitter key has liked.
    /// </summary>
    public sealed class WorkshopStatsSnapshot
    {
        public Dictionary<string, long> Likes { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Installs { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Mine { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and writes the Workshop's own statistics through its service (the Cloudflare Worker):
    /// install counts, which the extension reports once an install has succeeded, and likes, which
    /// are identified only by the SHA-256 of this install's submitter key, the same value a
    /// submission carries. Nothing else about the user is sent.
    /// </summary>
    public sealed class WorkshopStatsClient
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly Func<string> _getServiceUrl;
        private readonly ILogger _logger;
        private readonly HttpClient _http;

        public WorkshopStatsClient(Func<string> getServiceUrl, ILogger logger = null, HttpMessageHandler handler = null)
        {
            _getServiceUrl = getServiceUrl;
            _logger = logger;
            _http = handler == null ? HttpClientFactory.Create(Timeout) : HttpClientFactory.Create(handler, Timeout);
        }

        /// <summary>The service's base address; blank settings fall back to the submission service's.</summary>
        public string ServiceUrl
        {
            get
            {
                var configured = _getServiceUrl?.Invoke();
                var url = string.IsNullOrWhiteSpace(configured) ? WorkshopSubmissionClient.DefaultServiceUrl : configured.Trim();
                return url.TrimEnd('/');
            }
        }

        /// <summary>
        /// Every item's install and like counts, and the items <paramref name="voterHash"/> has
        /// liked when one is given. Null when the service cannot be reached, which is what leaves
        /// the counts out rather than showing a number that is not known to be true.
        /// </summary>
        public Task<WorkshopStatsSnapshot> FetchAsync(string voterHash, CancellationToken cancel)
            => Task.Run(() => FetchCoreAsync(voterHash, cancel), cancel);

        private async Task<WorkshopStatsSnapshot> FetchCoreAsync(string voterHash, CancellationToken cancel)
        {
            var url = ServiceUrl + "/v1/stats";
            if (!string.IsNullOrWhiteSpace(voterHash))
            {
                url += "?voter=" + Uri.EscapeDataString(voterHash.Trim().ToLowerInvariant());
            }

            try
            {
                using (var response = await _http.GetAsync(url, cancel).ConfigureAwait(false))
                {
                    return ParseSnapshot(await ReadJsonOrThrowAsync(response).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Workshop statistics unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records or withdraws this install's like for an item and returns the item's like count
        /// as the service now holds it, or null when the call failed. The like is sent with the
        /// submitter key, which the service hashes into the voter, so nobody else can like or
        /// unlike under this install's public hash.
        /// </summary>
        public Task<long?> SetLikeAsync(string itemId, string voterKey, bool liked, CancellationToken cancel)
            => Task.Run(() => SetLikeCoreAsync(itemId, voterKey, liked, cancel), cancel);

        private async Task<long?> SetLikeCoreAsync(string itemId, string voterKey, bool liked, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(voterKey))
            {
                return null;
            }

            return await PostAsync(
                "/v1/likes",
                new { itemId = itemId.Trim(), voterKey = voterKey.Trim().ToLowerInvariant(), liked },
                $"Failed recording a Workshop like for {itemId}",
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Adds this install to an item's install count and returns the count as the service now
        /// holds it, or null when the call failed. Called once an install has succeeded, which is
        /// what makes the number mean installs rather than package downloads.
        /// </summary>
        public Task<long?> RecordInstallAsync(string itemId, CancellationToken cancel)
            => Task.Run(() => RecordInstallCoreAsync(itemId, cancel), cancel);

        private async Task<long?> RecordInstallCoreAsync(string itemId, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            return await PostAsync(
                "/v1/installs",
                new { itemId = itemId.Trim() },
                $"Failed recording a Workshop install for {itemId}",
                cancel).ConfigureAwait(false);
        }

        private async Task<long?> PostAsync(string path, object body, string failureMessage, CancellationToken cancel)
        {
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync(ServiceUrl + path, content, cancel).ConfigureAwait(false))
                {
                    var json = await ReadJsonOrThrowAsync(response).ConfigureAwait(false);
                    return json.Value<long?>("count");
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"{failureMessage}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The service's answer as counts and the voter's own likes; unparsable entries are skipped.</summary>
        public static WorkshopStatsSnapshot ParseSnapshot(JObject json)
        {
            var snapshot = new WorkshopStatsSnapshot();
            if (json == null)
            {
                return snapshot;
            }

            ReadCounts(json["likes"] as JObject, snapshot.Likes);
            ReadCounts(json["installs"] as JObject, snapshot.Installs);
            if (json["mine"] is JArray mine)
            {
                foreach (var id in mine)
                {
                    var value = id?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        snapshot.Mine.Add(value);
                    }
                }
            }

            return snapshot;
        }

        private static void ReadCounts(JObject counts, Dictionary<string, long> into)
        {
            foreach (var pair in counts ?? new JObject())
            {
                var count = pair.Value?.Value<long?>();
                if (!string.IsNullOrWhiteSpace(pair.Key) && count.HasValue && count.Value > 0)
                {
                    into[pair.Key] = count.Value;
                }
            }
        }

        private static async Task<JObject> ReadJsonOrThrowAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JObject json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = json?.Value<string>("error");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                    ? $"The Workshop service answered HTTP {(int)response.StatusCode}."
                    : message);
            }

            return json ?? new JObject();
        }
    }
}
