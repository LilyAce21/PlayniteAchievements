using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    public enum LlmFailureKind
    {
        None = 0,

        /// <summary>Worth trying again later: rate limit, server trouble, timeout, no network.</summary>
        Transient,

        /// <summary>Retrying will not help until something changes: missing or bad key, model not available, request blocked.</summary>
        Permanent,

        /// <summary>This one request was refused (for example blocked by a content filter). Other games may work.</summary>
        Content
    }

    public sealed class LlmCallResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public LlmFailureKind Failure { get; set; }

        /// <summary>A short, safe explanation for the user. Never contains the API key.</summary>
        public string Error { get; set; }

        /// <summary>True when the model stopped because it ran out of output room (the text is likely cut off).</summary>
        public bool Truncated { get; set; }

        /// <summary>How many web searches Gemini reports it ran for this answer (research calls only).</summary>
        public int SearchQueries { get; set; }

        /// <summary>How many web sources Gemini reports using for this answer (research calls only).</summary>
        public int Sources { get; set; }

        /// <summary>How long Gemini says to wait before trying again (from its error body), when it says.</summary>
        public TimeSpan? RetryAfter { get; set; }

        /// <summary>True when the free daily quota is used up, so retrying today cannot help.</summary>
        public bool DailyQuotaExhausted { get; set; }

        /// <summary>Which quota was hit (Google's own quota id and limit), for the log. Never sensitive.</summary>
        public string QuotaDetail { get; set; }

        /// <summary>Gemini reports a limit of zero: this model or tool is not included in the plan, so retrying cannot help.</summary>
        public bool QuotaIsZero { get; set; }

        /// <summary>
        /// Web research (Google Search) is not available for this key and model, so the caller should carry on without
        /// it rather than fail. Set only on research calls.
        /// </summary>
        public bool ResearchUnavailable { get; set; }

        public static LlmCallResult Ok(string text, bool truncated = false) =>
            new LlmCallResult { Success = true, Text = text, Truncated = truncated };

        public static LlmCallResult Fail(LlmFailureKind kind, string error) =>
            new LlmCallResult { Failure = kind, Error = error };
    }

    public interface IEstimationLlmClient
    {
        Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken cancellationToken);
    }

    /// <summary>A client that can research with web search and answer in plain text (see <see cref="GeminiClient"/>).</summary>
    public interface IEstimationResearchClient
    {
        Task<LlmCallResult> ResearchAsync(string requestJson, CancellationToken cancellationToken);
    }

    public sealed class GeminiClientOptions
    {
        /// <summary>Supplies the API key at call time, so it is never held in this object or in a log.</summary>
        public Func<string> ApiKey { get; set; }

        /// <summary>
        /// Model id. Stays configurable because quotas and lineups change. Google points new projects
        /// to the 3.5 Flash-Lite / 3.8 Flash line; older 2.5 models can return 404 on new keys.
        /// </summary>
        public string Model { get; set; } = "gemini-3.5-flash-lite";

        public int MaxAttempts { get; set; } = 6;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Minimum gap between research requests. Search-grounded calls are heavier and some of their limits are
        /// tighter than plain ones, so they are spaced further apart.
        /// </summary>
        public TimeSpan ResearchMinRequestInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>The most the client will slow itself down after rate limits (see adaptive pacing).</summary>
        public TimeSpan MaxAdaptiveInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Minimum gap between requests. Free-tier limits are a handful of requests per minute.</summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(8);

        public double Temperature { get; set; } = 0.2;
        public int MaxOutputTokens { get; set; } = 16384;

        public int ResearchMaxOutputTokens { get; set; } = 12288;

        /// <summary>
        /// The model for the research step, or blank to use <see cref="Model"/>. Google Search grounding is not available
        /// on the free tier for Gemini 3 models, so a free key may need a different model here (Google lists free grounding
        /// for Gemini 2.5 Flash and Flash-Lite), or research left off.
        /// </summary>
        public string ResearchModel { get; set; }

        /// <summary>Research gives up sooner than plain requests: if Google Search is not available, retrying only wastes time.</summary>
        public int ResearchMaxAttempts { get; set; } = 3;
    }

    /// <summary>
    /// A small client for the Gemini API's generateContent endpoint. The key travels in a header,
    /// never in the URL; requests are paced for free-tier limits; rate limits and server trouble are
    /// retried with backoff (honouring Retry-After); and every request is checked against the privacy
    /// allowlist again immediately before sending.
    /// </summary>
    public sealed class GeminiClient : IEstimationLlmClient, IEstimationResearchClient
    {
        internal const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

        private static readonly Regex SafeModel = new Regex(@"^[A-Za-z0-9._\-]+$", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly GeminiClientOptions _options;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;
        private readonly Random _random = new Random();

        // Adaptive pacing: after a rate limit the gap between requests grows, and it eases off again as requests succeed.
        // Plain and research requests are limited separately, so each has its own, and a search problem never slows scoring.
        private TimeSpan _adaptivePlain = TimeSpan.Zero;
        private TimeSpan _adaptiveResearch = TimeSpan.Zero;
        private int _researchRateLimits;

        public GeminiClient(
            HttpClient http,
            GeminiClientOptions options,
            Action<string> log = null,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<DateTime> utcNow = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? (_ => { });
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken cancellationToken) =>
            CallAsync(requestJson, research: false, cancellationToken: cancellationToken);

        /// <summary>
        /// The research step: Google Search is switched on and the answer comes back as plain lines. Strict JSON is
        /// deliberately not requested here, because search and structured output have not always worked together.
        /// </summary>
        public Task<LlmCallResult> ResearchAsync(string requestJson, CancellationToken cancellationToken) =>
            CallAsync(requestJson, research: true, cancellationToken: cancellationToken);

        private async Task<LlmCallResult> CallAsync(string requestJson, bool research, CancellationToken cancellationToken)
        {
            var key = _options.ApiKey?.Invoke();
            if (string.IsNullOrWhiteSpace(key))
            {
                return LlmCallResult.Fail(LlmFailureKind.Permanent, "No Gemini API key is set.");
            }

            var model = NormalizeModel(research && !string.IsNullOrWhiteSpace(_options.ResearchModel) ? _options.ResearchModel : _options.Model);
            if (model == null)
            {
                return LlmCallResult.Fail(LlmFailureKind.Permanent, "The Gemini model name is not valid.");
            }

            if (research && ResearchAvailability.IsUnavailable(model, out var knownReason))
            {
                // Refused earlier this session: do not ask again until it is time to re-check.
                var skipped = LlmCallResult.Fail(LlmFailureKind.Transient, knownReason);
                skipped.ResearchUnavailable = true;
                return skipped;
            }

            // Defence in depth: the builder already checked, and nothing outside the allowlist leaves.
            try
            {
                PrivacyFilter.AssertSafe(requestJson);
            }
            catch (InvalidOperationException ex)
            {
                return LlmCallResult.Fail(LlmFailureKind.Permanent, ex.Message);
            }

            var body = research ? BuildResearchBody(requestJson) : BuildBody(requestJson);
            var url = BaseUrl + model + ":generateContent";

            var attempts = Math.Max(1, research ? Math.Min(_options.MaxAttempts, _options.ResearchMaxAttempts) : _options.MaxAttempts);
            _researchRateLimits = 0;
            LlmCallResult last = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForTurnAsync(research, cancellationToken).ConfigureAwait(false);

                TimeSpan? retryAfter = null;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Headers.Add("x-goog-api-key", key);
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                        using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            _log("Gemini " + model + (research ? " research" : string.Empty) + " attempt " + attempt + ": HTTP " + (int)response.StatusCode);

                            last = Interpret(response.StatusCode, text, key, model);
                            if (last.Success && research)
                            {
                                // Counts only: proof that searches really ran, never any content.
                                _log("Gemini research: " + last.SearchQueries + " web search(es), " + last.Sources + " source(s).");
                            }
                            if (last.Success)
                            {
                                if (research) ResearchAvailability.MarkWorked(model);
                                EaseOffPacing(model, research);
                                return last;
                            }

                            if (research && IsToolRefused((int)response.StatusCode, text))
                            {
                                // 404/403 (or a "tool not supported" 400): this key or model cannot use Google Search at all.
                                return RefuseResearch(model, last, "Gemini did not accept Google Search for the model \"" + model + "\" (HTTP " + (int)response.StatusCode + ")" + ErrorDetail(text, key));
                            }

                            if (last.Failure == LlmFailureKind.Permanent) return last;

                            if ((int)response.StatusCode == 429)
                            {
                                _log("Gemini " + model + (research ? " research" : string.Empty) + " rate limit: " + (last.QuotaDetail ?? "no detail given") +
                                     (last.RetryAfter.HasValue ? "; Gemini says retry in " + (int)Math.Ceiling(last.RetryAfter.Value.TotalSeconds) + "s" : string.Empty) + ".");

                                if (research)
                                {
                                    _researchRateLimits++;
                                    var neverWorked = !ResearchAvailability.HasWorked(model);
                                    if (last.QuotaIsZero || last.DailyQuotaExhausted || (neverWorked && _researchRateLimits >= 2))
                                    {
                                        // A refusal that does not clear is not a rate limit: on the free tier Google lists search
                                        // grounding as unavailable for Gemini 3 models, and it answers with a quota error.
                                        return RefuseResearch(model, last,
                                            "Google Search research is not available for the model \"" + model + "\" on this key: Gemini keeps answering with a quota error" +
                                            (last.QuotaDetail != null ? " (" + last.QuotaDetail + ")" : string.Empty) +
                                            ". Google lists search grounding as paid-only for Gemini 3 models. Set a different \"Research model\" (for example gemini-2.5-flash-lite) or turn research off.");
                                    }
                                }
                                else if (last.DailyQuotaExhausted)
                                {
                                    return last; // retrying today cannot help
                                }

                                SlowDownPacing(model, research);
                            }

                            // Gemini names the wait in the error body; the standard header is used if that is all there is.
                            var header = response.Headers.RetryAfter?.Delta;
                            retryAfter = header.HasValue && last.RetryAfter.HasValue
                                ? (header.Value > last.RetryAfter.Value ? header : last.RetryAfter)
                                : header ?? last.RetryAfter;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is System.IO.IOException)
                {
                    _log("Gemini " + model + " attempt " + attempt + ": network problem (" + ex.GetType().Name + ")");
                    last = LlmCallResult.Fail(LlmFailureKind.Transient, "Could not reach the Gemini API (network problem or timeout).");
                }

                if (attempt < attempts)
                {
                    await _delay(BackoffDelay(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
                }
            }

            return last ?? LlmCallResult.Fail(LlmFailureKind.Transient, "The Gemini request did not complete.");
        }

        internal static string NormalizeModel(string model)
        {
            var name = (model ?? string.Empty).Trim();
            if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) name = name.Substring("models/".Length);
            return SafeModel.IsMatch(name) ? name : null;
        }

        private string BuildBody(string requestJson)
        {
            var body = new JObject
            {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = EstimationPrompt.SystemInstruction }) },
                ["contents"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["parts"] = new JArray(new JObject { ["text"] = requestJson })
                }),
                ["generationConfig"] = new JObject
                {
                    ["temperature"] = _options.Temperature,
                    ["maxOutputTokens"] = _options.MaxOutputTokens,
                    ["responseMimeType"] = "application/json",
                    ["responseSchema"] = EstimationPrompt.ResponseSchema()
                }
            };
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string BuildResearchBody(string requestJson)
        {
            var body = new JObject
            {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = EstimationPrompt.ResearchInstruction }) },
                ["contents"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["parts"] = new JArray(new JObject { ["text"] = requestJson })
                }),
                ["tools"] = new JArray(new JObject { ["google_search"] = new JObject() }),
                ["generationConfig"] = new JObject
                {
                    ["temperature"] = _options.Temperature,
                    ["maxOutputTokens"] = _options.ResearchMaxOutputTokens
                }
            };
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Paces requests so free-tier per-minute limits are not hit by our own bursts.</summary>
        private async Task WaitForTurnAsync(bool research, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var interval = research && _options.ResearchMinRequestInterval > _options.MinRequestInterval
                    ? _options.ResearchMinRequestInterval
                    : _options.MinRequestInterval;
                var adaptive = research ? _adaptiveResearch : _adaptivePlain;
                if (adaptive > interval) interval = adaptive;

                var wait = _lastRequestUtc + interval - _utcNow();
                if (wait > TimeSpan.Zero && _lastRequestUtc != DateTime.MinValue)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }

                _lastRequestUtc = _utcNow();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>After a rate limit, widen the gap between requests (up to a cap) so the rest of the run stays under it.</summary>
        private void SlowDownPacing(string model, bool research)
        {
            var current = research ? _adaptiveResearch : _adaptivePlain;
            var next = TimeSpan.FromSeconds(Math.Max(15, current.TotalSeconds * 2));
            if (next > _options.MaxAdaptiveInterval) next = _options.MaxAdaptiveInterval;
            if (next == current) return;

            if (research) _adaptiveResearch = next; else _adaptivePlain = next;
            _log("Gemini " + model + (research ? " research" : string.Empty) + ": slowing down to one request every " + (int)next.TotalSeconds + "s after a rate limit.");
        }

        /// <summary>Each successful request lets the gap shrink again, back to the normal spacing.</summary>
        private void EaseOffPacing(string model, bool research)
        {
            var current = research ? _adaptiveResearch : _adaptivePlain;
            if (current <= TimeSpan.Zero) return;

            var next = TimeSpan.FromSeconds(current.TotalSeconds * 0.75);
            if (next < TimeSpan.FromSeconds(2)) next = TimeSpan.Zero;
            if (research) _adaptiveResearch = next; else _adaptivePlain = next;
            if (next == TimeSpan.Zero) _log("Gemini " + model + (research ? " research" : string.Empty) + ": back to normal pacing.");
        }

        /// <summary>True when the response says this key or model cannot use the search tool at all.</summary>
        private static bool IsToolRefused(int status, string body)
        {
            if (status == 404 || status == 403) return true;
            if (status != 400) return false;

            var lower = (body ?? string.Empty).ToLowerInvariant();
            return lower.Contains("google_search") || lower.Contains("search as a tool") || lower.Contains("tool is not supported") || lower.Contains("tools are not supported");
        }

        /// <summary>Research cannot be used right now: remember that, log it once, and tell the caller to carry on without it.</summary>
        private LlmCallResult RefuseResearch(string model, LlmCallResult basis, string reason)
        {
            ResearchAvailability.MarkUnavailable(model, reason);
            _log("Gemini " + model + ": web research is unavailable (" + (basis?.QuotaDetail ?? "refused") + "); carrying on without it, and not asking again for " +
                 (int)ResearchAvailability.RememberFor.TotalHours + " hours.");

            var result = LlmCallResult.Fail(LlmFailureKind.Transient, reason);
            result.ResearchUnavailable = true;
            result.QuotaDetail = basis?.QuotaDetail;
            return result;
        }

        internal TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                // The wait Gemini named, plus a second of margin and a little jitter so a window edge is not hit again.
                var named = retryAfter.Value + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(_random.Next(0, 500));
                return named > _options.MaxDelay ? _options.MaxDelay : named;
            }

            var exponential = TimeSpan.FromMilliseconds(_options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            var jitter = TimeSpan.FromMilliseconds(_random.Next(0, 500));
            var total = exponential + jitter;
            return total > _options.MaxDelay ? _options.MaxDelay : total;
        }

        private static LlmCallResult Interpret(HttpStatusCode status, string body, string key, string model)
        {
            var code = (int)status;
            if (code >= 200 && code < 300) return ReadSuccess(body, key);

            var detail = ErrorDetail(body, key);
            switch (code)
            {
                case 400:
                    return LlmCallResult.Fail(LlmFailureKind.Permanent,
                        "Gemini rejected the request or the API key is not valid" + detail + ".");
                case 401:
                case 403:
                    return LlmCallResult.Fail(LlmFailureKind.Permanent,
                        "Gemini did not accept the API key, or the key may not use this model" + detail + ".");
                case 404:
                    return LlmCallResult.Fail(LlmFailureKind.Permanent,
                        "The model \"" + model + "\" is not available to this key. Google limits older models for new " +
                        "projects, so try a current one (for example gemini-3.5-flash-lite or gemini-3.8-flash).");
                case 429:
                    return RateLimited(body, key);
                default:
                    if (code >= 500)
                    {
                        return LlmCallResult.Fail(LlmFailureKind.Transient, "Gemini had a server problem (HTTP " + code + ")" + detail + ".");
                    }

                    return LlmCallResult.Fail(LlmFailureKind.Permanent, "Gemini returned HTTP " + code + detail + ".");
            }
        }

        private static LlmCallResult ReadSuccess(string body, string key)
        {
            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception)
            {
                return LlmCallResult.Fail(LlmFailureKind.Transient, "Gemini returned a response that could not be read.");
            }

            var blockReason = (string)root["promptFeedback"]?["blockReason"];
            if (!string.IsNullOrWhiteSpace(blockReason))
            {
                return LlmCallResult.Fail(LlmFailureKind.Content, "Gemini blocked the request (" + Scrub(blockReason, key) + ").");
            }

            var candidate = (root["candidates"] as JArray)?.FirstOrDefault() as JObject;
            var finish = (string)candidate?["finishReason"];
            var text = string.Concat((candidate?["content"]?["parts"] as JArray ?? new JArray())
                .OfType<JObject>().Select(part => (string)part["text"] ?? string.Empty));

            if (string.IsNullOrWhiteSpace(text))
            {
                var why = string.IsNullOrWhiteSpace(finish) ? "no text" : finish;
                return LlmCallResult.Fail(LlmFailureKind.Transient, "Gemini returned no usable text (" + Scrub(why, key) + ").");
            }

            var result = LlmCallResult.Ok(text, string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase));
            var grounding = candidate?["groundingMetadata"] as JObject;
            result.SearchQueries = (grounding?["webSearchQueries"] as JArray)?.Count ?? 0;
            result.Sources = (grounding?["groundingChunks"] as JArray)?.Count ?? 0;
            return result;
        }

        private static string ErrorDetail(string body, string key)
        {
            try
            {
                var message = (string)JObject.Parse(body)["error"]?["message"];
                if (string.IsNullOrWhiteSpace(message)) return string.Empty;
                message = Scrub(message, key);
                return ": " + (message.Length > 200 ? message.Substring(0, 200) + "…" : message);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static readonly Regex RetryInMessage = new Regex(@"retry in (\d+(?:\.\d+)?)\s*s", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// A 429 with what Gemini says about it: how long to wait, which quota was hit and whether it is a daily one.
        /// </summary>
        internal static LlmCallResult RateLimited(string body, string key)
        {
            TimeSpan? retryAfter = null;
            var quotas = new List<string>();
            string message = null;

            try
            {
                var error = JObject.Parse(body)["error"] as JObject;
                message = (string)error?["message"];

                foreach (var detail in (error?["details"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var delay = (string)detail["retryDelay"];
                    if (!string.IsNullOrWhiteSpace(delay) &&
                        double.TryParse(delay.Trim().TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        retryAfter = TimeSpan.FromSeconds(seconds);
                    }

                    foreach (var violation in (detail["violations"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var id = (string)violation["quotaId"] ?? (string)violation["quotaMetric"];
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        var value = (string)violation["quotaValue"];
                        quotas.Add(string.IsNullOrWhiteSpace(value) ? id : id + " (limit " + value + ")");
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable body just means no detail.
            }

            if (!retryAfter.HasValue && message != null)
            {
                var match = RetryInMessage.Match(message);
                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    retryAfter = TimeSpan.FromSeconds(seconds);
                }
            }

            // Quota names are Google's own identifiers (long, but not secret), so only the key is stripped from them.
            var which = string.Join(", ", quotas.Distinct());
            if (!string.IsNullOrEmpty(key)) which = which.Replace(key, "[key]");

            // No structured quota fields? Then Google's own words are the only detail there is, so keep them.
            if (which.Length == 0 && !string.IsNullOrWhiteSpace(message))
            {
                which = Scrub(message.Trim(), key);
                if (which.Length > 240) which = which.Substring(0, 240) + "…";
            }

            var zero = quotas.Any(q => q.IndexOf("(limit 0)", StringComparison.OrdinalIgnoreCase) >= 0) ||
                       (message != null && message.IndexOf("limit: 0", StringComparison.OrdinalIgnoreCase) >= 0);
            var daily = quotas.Any(q => q.IndexOf("PerDay", StringComparison.OrdinalIgnoreCase) >= 0 || q.IndexOf("per_day", StringComparison.OrdinalIgnoreCase) >= 0);

            var text = daily
                ? "Gemini's daily free quota is used up" + (which.Length > 0 ? " (" + which + ")" : string.Empty) +
                  ". It resets once a day, so try again tomorrow, switch to another model, or turn off \"Research each achievement\" (search grounding has its own limits)."
                : "Gemini's free-tier rate limit was reached" + (which.Length > 0 ? " (" + which + ")" : string.Empty) +
                  ". It usually clears within a minute. Raising \"Seconds between requests\", using bigger research batches or turning off \"Research each achievement\" uses fewer requests.";

            var result = LlmCallResult.Fail(LlmFailureKind.Transient, text);
            result.RetryAfter = retryAfter;
            result.DailyQuotaExhausted = daily;
            result.QuotaIsZero = zero;
            result.QuotaDetail = which.Length > 0 ? which : null;
            return result;
        }

        /// <summary>Removes the key (and anything credential-shaped) from text that may be shown or logged.</summary>
        internal static string Scrub(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cleaned = string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]");
            return PrivacyFilter.Clean(cleaned);
        }
    }
}
