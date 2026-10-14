using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>What fetching a game's PlayStation Platinum came to.</summary>
    internal sealed class PsnCapstoneFetchResult
    {
        public PsnPlatinum Platinum { get; set; }

        public byte[] Icon { get; set; }

        /// <summary>The reason nothing was fetched, in words the user can act on.</summary>
        public string Error { get; set; }

        public PsnFailure? Failure { get; set; }

        public bool Success => Platinum != null;
    }

    /// <summary>
    /// Everything PSN Capstones need from PlayStation Network and from the settings: the NPSSO
    /// token (kept encrypted), the NPWR IDs remembered per game, and fetching a Platinum with its
    /// icon. One request per step, no retries, and it stops at the first sign PSN is unhappy.
    /// </summary>
    internal sealed class PsnCapstoneService
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(25);

        private readonly Func<PersistedSettings> _settings;
        private readonly Action _persist;
        private readonly Func<PsnClient> _clientFactory;
        private readonly ILogger _logger;
        private readonly object _sync = new object();

        private string _accessToken;
        private DateTime _accessTokenUtc;

        public PsnCapstoneService(
            Func<PersistedSettings> settings,
            Action persist,
            ILogger logger,
            Func<PsnClient> clientFactory = null)
        {
            _settings = settings;
            _persist = persist;
            _logger = logger;
            _clientFactory = clientFactory ?? (() => new PsnClient());
        }

        public bool HasNpsso => !string.IsNullOrWhiteSpace(NpssoProtector.Unprotect(_settings()?.PsnCapstoneNpsso));

        /// <summary>Stores the token encrypted. An empty value clears it.</summary>
        public void SetNpsso(string npsso)
        {
            var settings = _settings();
            if (settings == null)
            {
                return;
            }

            settings.PsnCapstoneNpsso = string.IsNullOrWhiteSpace(npsso)
                ? string.Empty
                : NpssoProtector.Protect(npsso);
            lock (_sync)
            {
                _accessToken = null;
            }

            _persist?.Invoke();
        }

        // ---- remembered NPWR IDs -------------------------------------------------------------

        /// <summary>The NPWR IDs remembered for a game, empty when there are none.</summary>
        public IReadOnlyList<string> GetNpwrs(Guid gameId)
        {
            var prefix = gameId.ToString("D") + "=";
            var entry = (_settings()?.PsnCapstoneNpwrMap ?? new List<string>())
                .FirstOrDefault(line => line != null && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            return entry.Substring(prefix.Length)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().ToUpperInvariant())
                .Where(PsnNpwr.IsValid)
                .Distinct()
                .ToList();
        }

        /// <summary>Remembers the NPWR IDs for a game, replacing what was there.</summary>
        public void RememberNpwrs(Guid gameId, IEnumerable<string> npwrs)
        {
            var settings = _settings();
            if (settings == null || gameId == Guid.Empty)
            {
                return;
            }

            var ids = (npwrs ?? Enumerable.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim().ToUpperInvariant())
                .Where(PsnNpwr.IsValid)
                .Distinct()
                .ToList();
            var prefix = gameId.ToString("D") + "=";
            var map = (settings.PsnCapstoneNpwrMap ?? new List<string>())
                .Where(line => line != null && !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ids.Count > 0)
            {
                map.Add(prefix + string.Join(",", ids));
            }

            settings.PsnCapstoneNpwrMap = map;
            _persist?.Invoke();
        }

        public IReadOnlyList<Guid> GamesWithNpwrs()
        {
            var games = new List<Guid>();
            foreach (var line in _settings()?.PsnCapstoneNpwrMap ?? new List<string>())
            {
                var separator = line?.IndexOf('=') ?? -1;
                if (separator > 0 && Guid.TryParse(line.Substring(0, separator), out var id) && id != Guid.Empty)
                {
                    games.Add(id);
                }
            }

            return games;
        }

        // ---- PlayStation Network -------------------------------------------------------------

        /// <summary>Signs in and says whether PlayStation accepted the token.</summary>
        public async Task<string> TestConnectionAsync(CancellationToken token = default(CancellationToken))
        {
            try
            {
                using (var client = _clientFactory())
                {
                    await EnsureTokenAsync(client, token).ConfigureAwait(false);
                }

                return null;
            }
            catch (PsnException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// The Platinum of whichever of the NPWR IDs has one, preferring a PS5 set over a PS4
        /// one, with its icon downloaded. Never throws for a PSN-side failure.
        /// </summary>
        public async Task<PsnCapstoneFetchResult> FetchAsync(
            IEnumerable<string> npwrs,
            CancellationToken token = default(CancellationToken))
        {
            var ids = (npwrs ?? Enumerable.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim().ToUpperInvariant())
                .Where(PsnNpwr.IsValid)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return new PsnCapstoneFetchResult
                {
                    Error = "No valid NPWR ID was given (it looks like NPWR12345_00).",
                    Failure = PsnFailure.NotFound
                };
            }

            try
            {
                using (var client = _clientFactory())
                {
                    var accessToken = await EnsureTokenAsync(client, token).ConfigureAwait(false);

                    PsnPlatinum best = null;
                    PsnException lastFailure = null;
                    foreach (var id in ids)
                    {
                        try
                        {
                            var found = await client.GetPlatinumAsync(accessToken, id, token).ConfigureAwait(false);
                            if (best == null ||
                                (found.Platform == "PS5" && best.Platform != "PS5"))
                            {
                                best = found;
                            }
                        }
                        catch (PsnException ex) when (ex.Kind == PsnFailure.NotFound || ex.Kind == PsnFailure.NoPlatinum)
                        {
                            lastFailure = ex;
                        }
                    }

                    if (best == null)
                    {
                        return new PsnCapstoneFetchResult
                        {
                            Error = lastFailure?.Message ?? "No Platinum trophy was found.",
                            Failure = lastFailure?.Kind
                        };
                    }

                    byte[] icon = null;
                    if (PsnIconUrl.IsAllowed(best.IconUrl))
                    {
                        try
                        {
                            icon = await client.DownloadIconAsync(best.IconUrl, token).ConfigureAwait(false);
                        }
                        catch (PsnException ex)
                        {
                            // The capstone is still worth having without its picture.
                            _logger?.Warn("PSN Capstone icon could not be downloaded: " + ex.Message);
                        }
                    }

                    return new PsnCapstoneFetchResult { Platinum = best, Icon = icon };
                }
            }
            catch (PsnException ex)
            {
                // Never includes the token: PsnClient's messages are written without it.
                return new PsnCapstoneFetchResult { Error = ex.Message, Failure = ex.Kind };
            }
        }

        /// <summary>Downloads an icon from a PlayStation address; null when it cannot be had.</summary>
        public async Task<byte[]> DownloadIconAsync(string iconUrl, CancellationToken token = default(CancellationToken))
        {
            if (!PsnIconUrl.IsAllowed(iconUrl))
            {
                return null;
            }

            try
            {
                using (var client = _clientFactory())
                {
                    return await client.DownloadIconAsync(iconUrl, token).ConfigureAwait(false);
                }
            }
            catch (PsnException ex)
            {
                _logger?.Warn("PSN Capstone icon could not be downloaded: " + ex.Message);
                return null;
            }
        }

        private async Task<string> EnsureTokenAsync(PsnClient client, CancellationToken token)
        {
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow - _accessTokenUtc < TokenLifetime)
                {
                    return _accessToken;
                }
            }

            var npsso = NpssoProtector.Unprotect(_settings()?.PsnCapstoneNpsso);
            if (string.IsNullOrWhiteSpace(npsso))
            {
                throw new PsnException(
                    PsnFailure.InvalidNpsso,
                    "No NPSSO token is set. Enter it under Settings > General > Editor.");
            }

            var accessToken = await client.AuthenticateAsync(npsso, token).ConfigureAwait(false);
            lock (_sync)
            {
                _accessToken = accessToken;
                _accessTokenUtc = DateTime.UtcNow;
            }

            return accessToken;
        }
    }
}
