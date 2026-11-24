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

        public static LlmCallResult Ok(string text, bool truncated = false) =>
            new LlmCallResult { Success = true, Text = text, Truncated = truncated };

        public static LlmCallResult Fail(LlmFailureKind kind, string error) =>
            new LlmCallResult { Failure = kind, Error = error };
    }

    public interface IEstimationLlmClient
    {
        Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken cancellationToken);
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

        public int MaxAttempts { get; set; } = 4;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>Minimum gap between requests. Free-tier limits are a handful of requests per minute.</summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(8);

        public double Temperature { get; set; } = 0.2;
        public int MaxOutputTokens { get; set; } = 16384;
    }

    /// <summary>
    /// A small client for the Gemini API's generateContent endpoint. The key travels in a header,
    /// never in the URL; requests are paced for free-tier limits; rate limits and server trouble are
    /// retried with backoff (honouring Retry-After); and every request is checked against the privacy
    /// allowlist again immediately before sending.
    /// </summary>
    public sealed class GeminiClient : IEstimationLlmClient
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

        public async Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        {
            var key = _options.ApiKey?.Invoke();
            if (string.IsNullOrWhiteSpace(key))
            {
                return LlmCallResult.Fail(LlmFailureKind.Permanent, "No Gemini API key is set.");
            }

            var model = NormalizeModel(_options.Model);
            if (model == null)
            {
                return LlmCallResult.Fail(LlmFailureKind.Permanent, "The Gemini model name is not valid.");
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

            var body = BuildBody(requestJson);
            var url = BaseUrl + model + ":generateContent";

            var attempts = Math.Max(1, _options.MaxAttempts);
            LlmCallResult last = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

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
                            _log("Gemini " + model + " attempt " + attempt + ": HTTP " + (int)response.StatusCode);

                            last = Interpret(response.StatusCode, text, key, model);
                            if (last.Success || last.Failure == LlmFailureKind.Permanent) return last;

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

        /// <summary>Paces requests so free-tier per-minute limits are not hit by our own bursts.</summary>
        private async Task WaitForTurnAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastRequestUtc + _options.MinRequestInterval - _utcNow();
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

        internal TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                return retryAfter.Value > _options.MaxDelay ? _options.MaxDelay : retryAfter.Value;
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
                    return LlmCallResult.Fail(LlmFailureKind.Transient,
                        "Gemini's free-tier rate limit or daily quota was reached" + detail + ".");
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

            return LlmCallResult.Ok(text, string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase));
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

        /// <summary>Removes the key (and anything credential-shaped) from text that may be shown or logged.</summary>
        internal static string Scrub(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cleaned = string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]");
            return PrivacyFilter.Clean(cleaned);
        }
    }
}
