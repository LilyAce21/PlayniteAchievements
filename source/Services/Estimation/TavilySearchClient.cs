using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Tavily's web search (api.tavily.com), chosen because its free plan is the most generous that renews each month
    /// (1,000 credits, no card) and it returns relevant page text, not just links. A basic search costs one credit.
    /// The key goes in a header, never in the URL or a log; only the query text leaves the machine.
    /// </summary>
    public sealed class TavilySearchClient : ISearchProvider
    {
        internal const string Endpoint = "https://api.tavily.com/search";
        internal const int MaxQueryLength = 380;
        private const int Attempts = 3;

        private readonly HttpClient _http;
        private readonly Func<string> _apiKey;
        private readonly Action<string> _log;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public TavilySearchClient(
            HttpClient http,
            Func<string> apiKey,
            Action<string> log = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _log = log ?? (_ => { });
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
        {
            var key = _apiKey()?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return SearchResponse.Fail(SearchFailureKind.Unavailable, "No Tavily API key is set.");
            }

            var query = (request?.Query ?? string.Empty).Trim();
            if (query.Length == 0) return SearchResponse.Fail(SearchFailureKind.Transient, "The search query was empty.");
            if (query.Length > MaxQueryLength) query = query.Substring(0, MaxQueryLength);

            var body = new JObject
            {
                ["query"] = query,
                ["search_depth"] = "basic", // one credit; "advanced" costs two
                ["max_results"] = Math.Max(1, Math.Min(10, request.MaxResults)),
                ["include_answer"] = false,
                ["include_raw_content"] = request.IncludeRawContent ? (JToken)"text" : false
            }.ToString(Newtonsoft.Json.Formatting.None);

            SearchResponse last = null;
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? retryAfter = null;

                try
                {
                    using (var message = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                    {
                        message.Headers.Add("Authorization", "Bearer " + key);
                        message.Content = new StringContent(body, Encoding.UTF8, "application/json");

                        using (var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var status = (int)response.StatusCode;

                            if (status >= 200 && status < 300) return ReadResults(text, request.IncludeRawContent);

                            last = Classify(status, text, key);
                            if (last.Failure == SearchFailureKind.Unavailable) return last;
                            if (status != 429 && status < 500) return last; // a request Tavily rejected will not improve on retry

                            retryAfter = response.Headers.RetryAfter?.Delta;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is System.IO.IOException)
                {
                    last = SearchResponse.Fail(SearchFailureKind.Transient, "Could not reach Tavily (network problem or timeout).");
                }

                if (attempt < Attempts)
                {
                    await _delay(retryAfter ?? TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            return last ?? SearchResponse.Fail(SearchFailureKind.Transient, "The Tavily search did not complete.");
        }

        private static SearchResponse Classify(int status, string body, string key)
        {
            string detail = null;
            try
            {
                var parsed = JObject.Parse(body);
                detail = (string)parsed["detail"]?["error"] ?? (string)parsed["error"] ?? (string)parsed["message"];
            }
            catch (Exception)
            {
                // No readable detail.
            }

            detail = detail == null ? string.Empty : ": " + Scrub(detail, key);

            switch (status)
            {
                case 401:
                case 403:
                    return SearchResponse.Fail(SearchFailureKind.Unavailable, "Tavily did not accept the API key" + detail + ".");
                case 432:
                case 433:
                    return SearchResponse.Fail(SearchFailureKind.Unavailable, "The Tavily plan's credit limit has been reached" + detail + ". The free allowance renews monthly.");
                case 429:
                    return SearchResponse.Fail(SearchFailureKind.Transient, "Tavily's rate limit was reached" + detail + ".");
                default:
                    return SearchResponse.Fail(SearchFailureKind.Transient, "Tavily returned HTTP " + status + detail + ".");
            }
        }

        private static SearchResponse ReadResults(string body, bool rawRequested)
        {
            JObject parsed;
            try
            {
                parsed = JObject.Parse(body);
            }
            catch (Exception)
            {
                return SearchResponse.Fail(SearchFailureKind.Transient, "Tavily returned a response that could not be read.");
            }

            var response = new SearchResponse { Success = true };
            foreach (var item in (parsed["results"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var url = (string)item["url"];
                if (string.IsNullOrWhiteSpace(url)) continue;

                response.Hits.Add(new SearchHit
                {
                    Title = (string)item["title"],
                    Url = url,
                    Snippet = (string)item["content"],
                    RawContent = rawRequested ? (string)item["raw_content"] : null
                });
            }

            // The response reports what it cost when it can; a basic search is one credit.
            var credits = parsed["usage"]?["credits"];
            response.CreditsUsed = credits != null && (credits.Type == JTokenType.Integer || credits.Type == JTokenType.Float)
                ? Math.Max(1, (int)Math.Ceiling((double)credits))
                : 1;
            return response;
        }

        internal static string Scrub(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cleaned = string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]");
            return cleaned.Length > 200 ? cleaned.Substring(0, 200) + "…" : cleaned;
        }
    }
}
