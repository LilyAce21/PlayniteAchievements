using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>The Platinum trophy of one PlayStation trophy set: what a PSN Capstone is built from.</summary>
    internal sealed class PsnPlatinum
    {
        /// <summary>The trophy set's NP communication ID, for example NPWR39144_00.</summary>
        public string Npwr { get; set; }

        /// <summary>"PS5" for a trophy2 set, "PS4" for a classic set, empty when unknown (imported data).</summary>
        public string Platform { get; set; }

        public string TrophyName { get; set; }

        public string TrophyDetail { get; set; }

        public string IconUrl { get; set; }

        public bool HasText => !string.IsNullOrWhiteSpace(TrophyName);
    }

    /// <summary>Finds NPWR IDs in whatever text the user pastes (an ID, a page title, a table row).</summary>
    internal static class PsnNpwr
    {
        private static readonly Regex Pattern = new Regex(
            @"NPWR\d{5,6}_\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Exact = new Regex(
            @"^NPWR\d{5,6}_\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Every distinct NPWR ID in the text, upper-cased, in order of appearance.</summary>
        public static IReadOnlyList<string> Extract(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return found;
            }

            foreach (Match match in Pattern.Matches(text))
            {
                var id = match.Value.ToUpperInvariant();
                if (!found.Contains(id))
                {
                    found.Add(id);
                }
            }

            return found;
        }

        public static bool IsValid(string npwr) => !string.IsNullOrWhiteSpace(npwr) && Exact.IsMatch(npwr.Trim());
    }

    /// <summary>Icons are only ever fetched from PlayStation's own hosts, over HTTPS.</summary>
    internal static class PsnIconUrl
    {
        public static bool IsAllowed(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();
            return host.EndsWith(".playstation.net", StringComparison.Ordinal) ||
                   host.EndsWith(".playstation.com", StringComparison.Ordinal) ||
                   host.EndsWith(".sony.com", StringComparison.Ordinal);
        }
    }
}
