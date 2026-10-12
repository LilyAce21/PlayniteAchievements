using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Psn
{
    internal enum PsnFailure
    {
        /// <summary>The NPSSO token was rejected (wrong, expired or signed out).</summary>
        InvalidNpsso,

        /// <summary>Signing in worked but PSN refused the following request.</summary>
        AuthRejected,

        /// <summary>Neither trophy service knows this NPWR ID.</summary>
        NotFound,

        /// <summary>The trophy set exists but has no Platinum trophy.</summary>
        NoPlatinum,

        /// <summary>PSN asked us to slow down.</summary>
        RateLimited,

        /// <summary>A network problem or an unreadable reply.</summary>
        Network,

        /// <summary>The icon could not be fetched safely.</summary>
        BadIcon
    }

    internal sealed class PsnException : Exception
    {
        public PsnFailure Kind { get; }

        public PsnException(PsnFailure kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    /// <summary>
    /// A small client for the few PlayStation Network calls a PSN Capstone needs: sign in with the
    /// user's own NPSSO token, list one trophy set, and download its Platinum icon. The endpoints
    /// and the sign-in steps follow the MIT-licensed psn-api library (the PlayStation mobile app's
    /// public client). It makes one request per step, never loops on failure, and keeps the NPSSO
    /// and access token in memory only.
    /// </summary>
    internal sealed class PsnClient : IDisposable
    {
        private const string AuthBase = "https://ca.account.sony.com/api/authz/v3/oauth";
        private const string TrophyBase = "https://m.np.playstation.com/api/trophy";
        private const string ClientId = "09515159-7237-4370-9b40-3806e67c0891";
        private const string BasicAuth = "MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
        private const string RedirectUri = "com.scee.psxandroid.scecompcall://redirect";
        private const string Scope = "psn:mobile.v2.core psn:clientapp";
        private const int MaxPages = 6;
        public const int MaxIconBytes = 2 * 1024 * 1024;

        private static readonly Regex CodePattern = new Regex(@"[?&]code=([^&#]+)", RegexOptions.CultureInvariant);

        private readonly HttpClient _http;
        private readonly HttpClient _iconHttp;

        public PsnClient(HttpMessageHandler handler = null)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
            }

            if (handler != null)
            {
                _http = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(30) };
                _iconHttp = _http;
            }
            else
            {
                // The sign-in step answers with a redirect to an app URL; it is read, not followed.
                _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                _iconHttp = new HttpClient(new HttpClientHandler { UseCookies = false })
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
            }
        }

        /// <summary>Signs in with the NPSSO token and returns an access token for this session.</summary>
        public async Task<string> AuthenticateAsync(string npsso, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(npsso))
            {
                throw new PsnException(PsnFailure.InvalidNpsso, "No NPSSO token is set.");
            }

            var authorizeUrl = AuthBase + "/authorize?" + Query(
                "access_type", "offline",
                "client_id", ClientId,
                "redirect_uri", RedirectUri,
                "response_type", "code",
                "scope", Scope);

            string code;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, authorizeUrl))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", "npsso=" + npsso.Trim());
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        string location = null;
                        if (response.Headers.TryGetValues("Location", out var values))
                        {
                            location = values.FirstOrDefault();
                        }

                        var match = location == null ? null : CodePattern.Match(location);
                        code = match != null && match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                throw new PsnException(PsnFailure.Network, "Could not reach PlayStation Network: " + ex.Message);
            }

            if (string.IsNullOrEmpty(code))
            {
                throw new PsnException(
                    PsnFailure.InvalidNpsso,
                    "PlayStation Network did not accept the NPSSO token. It may be wrong or expired; sign in at playstation.com and copy a fresh one.");
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, AuthBase + "/token"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth);
                    request.Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("code", code),
                        new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                        new KeyValuePair<string, string>("grant_type", "authorization_code"),
                        new KeyValuePair<string, string>("token_format", "jwt")
                    });
                    using (var response = await _http.SendAsync(request, token).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var accessToken = TryGetString(body, "access_token");
                        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(accessToken))
                        {
                            throw new PsnException(PsnFailure.AuthRejected, "PlayStation Network refused the sign-in.");
                        }

                        return accessToken;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                throw new PsnException(PsnFailure.Network, "Could not reach PlayStation Network: " + ex.Message);
            }
        }

        /// <summary>
        /// The Platinum trophy of a trophy set. PS5 sets live under the "trophy2" service and older
        /// ones under "trophy", so the PS5 service is tried first and the other only if the set is
        /// unknown to it. A set without a Platinum is reported as such, not retried.
        /// </summary>
        public async Task<PsnPlatinum> GetPlatinumAsync(string accessToken, string npwr, CancellationToken token = default(CancellationToken))
        {
            if (!PsnNpwr.IsValid(npwr))
            {
                throw new PsnException(PsnFailure.NotFound, "That is not a valid NPWR ID (it looks like NPWR12345_00).");
            }

            npwr = npwr.Trim().ToUpperInvariant();
            string lastError = null;
            foreach (var service in new[] { "trophy2", "trophy" })
            {
                var platform = service == "trophy2" ? "PS5" : "PS4";
                var offset = 0;
                for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
                {
                    var url = TrophyBase + "/v1/npCommunicationIds/" + npwr +
                              "/trophyGroups/all/trophies?" + Query(
                                  "npServiceName", service,
                                  "limit", "100",
                                  "offset", offset.ToString());
                    string body;
                    HttpStatusCode status;
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            using (var response = await _http.SendAsync(request, token).ConfigureAwait(false))
                            {
                                status = response.StatusCode;
                                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
                    {
                        throw new PsnException(PsnFailure.Network, "Could not reach PlayStation Network: " + ex.Message);
                    }

                    if ((int)status == 429)
                    {
                        throw new PsnException(PsnFailure.RateLimited, "PlayStation Network asked for a pause. Try again in a few minutes.");
                    }

                    if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                    {
                        throw new PsnException(PsnFailure.AuthRejected, "PlayStation Network rejected the sign-in for this request.");
                    }

                    var page = PsnTrophyResponse.Parse(body, npwr, platform);
                    if (page.Error != null)
                    {
                        lastError = page.Error;
                        break; // unknown to this service: try the other one
                    }

                    if (page.Platinum != null)
                    {
                        return page.Platinum;
                    }

                    if (!page.NextOffset.HasValue || page.NextOffset.Value <= offset)
                    {
                        throw new PsnException(
                            PsnFailure.NoPlatinum,
                            npwr + " is a " + platform + " trophy set without a Platinum trophy.");
                    }

                    offset = page.NextOffset.Value;
                }
            }

            throw new PsnException(
                PsnFailure.NotFound,
                "PlayStation Network has no trophy set " + npwr + (lastError != null ? " (" + lastError + ")" : string.Empty) + ".");
        }

        /// <summary>Downloads a trophy icon: HTTPS, PlayStation hosts only, image content, size capped.</summary>
        public async Task<byte[]> DownloadIconAsync(string iconUrl, CancellationToken token = default(CancellationToken))
        {
            if (!PsnIconUrl.IsAllowed(iconUrl))
            {
                throw new PsnException(PsnFailure.BadIcon, "The icon address is not a PlayStation HTTPS address.");
            }

            try
            {
                using (var response = await _iconHttp.GetAsync(iconUrl.Trim(), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new PsnException(PsnFailure.BadIcon, "The icon could not be downloaded (" + (int)response.StatusCode + ").");
                    }

                    var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PsnException(PsnFailure.BadIcon, "The icon download was not an image.");
                    }

                    if (response.Content.Headers.ContentLength > MaxIconBytes)
                    {
                        throw new PsnException(PsnFailure.BadIcon, "The icon is unexpectedly large.");
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        var chunk = new byte[16 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
                        {
                            buffer.Write(chunk, 0, read);
                            if (buffer.Length > MaxIconBytes)
                            {
                                throw new PsnException(PsnFailure.BadIcon, "The icon is unexpectedly large.");
                            }
                        }

                        return buffer.ToArray();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                throw new PsnException(PsnFailure.Network, "Could not download the icon: " + ex.Message);
            }
        }

        private static string Query(params string[] keysAndValues)
        {
            var parts = new List<string>();
            for (var i = 0; i + 1 < keysAndValues.Length; i += 2)
            {
                parts.Add(Uri.EscapeDataString(keysAndValues[i]) + "=" +
                          Uri.EscapeDataString(keysAndValues[i + 1]).Replace("%20", "+"));
            }

            return string.Join("&", parts);
        }

        private static string TryGetString(string json, string property)
        {
            try
            {
                return (string)JObject.Parse(json)[property];
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (!ReferenceEquals(_iconHttp, _http))
            {
                _iconHttp.Dispose();
            }

            _http.Dispose();
        }
    }
}
