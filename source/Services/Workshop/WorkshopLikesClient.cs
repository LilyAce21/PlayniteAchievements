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
    /// <summary>What the Workshop service knows about likes: every item's count, and the items
    /// this install's submitter key has liked.</summary>
    public sealed class WorkshopLikeSnapshot
    {
        public Dictionary<string, long> Counts { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Mine { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and writes likes through the Workshop's service (the Cloudflare Worker). A like is
    /// identified only by the SHA-256 of this install's submitter key, the same value a submission
    /// carries, so there is no account and nothing else about the user is sent.
    /// </summary>
    public sealed class WorkshopLikesClient
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly Func<string> _getServiceUrl;
        private readonly ILogger _logger;
        private readonly HttpClient _http;

        public WorkshopLikesClient(Func<string> getServiceUrl, ILogger logger = null, HttpMessageHandler handler = null)
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
        /// Every item's like count, and the items <paramref name="voterHash"/> has liked when one
        /// is given. Null when the service cannot be reached, which is what hides the like button
        /// rather than showing a count that is not known to be true.
        /// </summary>
        public Task<WorkshopLikeSnapshot> FetchAsync(string voterHash, CancellationToken cancel)
            => Task.Run(() => FetchCoreAsync(voterHash, cancel), cancel);

        private async Task<WorkshopLikeSnapshot> FetchCoreAsync(string voterHash, CancellationToken cancel)
        {
            var url = ServiceUrl + "/v1/likes";
            if (!string.IsNullOrWhiteSpace(voterHash))
            {
                url += "?voter=" + Uri.EscapeDataString(voterHash.Trim().ToLowerInvariant());
            }

            try
            {
                using (var response = await _http.GetAsync(url, cancel).ConfigureAwait(false))
                {
                    var json = await ReadJsonOrThrowAsync(response).ConfigureAwait(false);
                    return ParseSnapshot(json);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Workshop likes unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records or withdraws this install's like for an item and returns the item's count as
        /// the service now holds it, or null when the call failed.
        /// </summary>
        public Task<long?> SetAsync(string itemId, string voterHash, bool liked, CancellationToken cancel)
            => Task.Run(() => SetCoreAsync(itemId, voterHash, liked, cancel), cancel);

        private async Task<long?> SetCoreAsync(string itemId, string voterHash, bool liked, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(voterHash))
            {
                return null;
            }

            var body = JsonConvert.SerializeObject(new
            {
                itemId = itemId.Trim(),
                voterHash = voterHash.Trim().ToLowerInvariant(),
                liked
            });

            try
            {
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync(ServiceUrl + "/v1/likes", content, cancel).ConfigureAwait(false))
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
                _logger?.Debug($"Failed recording a Workshop like for {itemId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The service's answer as counts and the voter's own likes; unparsable entries are skipped.</summary>
        public static WorkshopLikeSnapshot ParseSnapshot(JObject json)
        {
            var snapshot = new WorkshopLikeSnapshot();
            if (json == null)
            {
                return snapshot;
            }

            if (json["counts"] is JObject counts)
            {
                foreach (var pair in counts)
                {
                    var count = pair.Value?.Value<long?>();
                    if (!string.IsNullOrWhiteSpace(pair.Key) && count.HasValue && count.Value > 0)
                    {
                        snapshot.Counts[pair.Key] = count.Value;
                    }
                }
            }

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
