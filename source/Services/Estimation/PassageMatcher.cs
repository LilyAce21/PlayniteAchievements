using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>One web page whose text may mention the achievements.</summary>
    public sealed class CorpusPage
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string Domain { get; set; }

        /// <summary>How much this page looks like an achievement or trophy guide (higher is better).</summary>
        public double Weight { get; set; }
    }

    public sealed class Passage
    {
        public string Text { get; set; }
        public string Domain { get; set; }
        public string Url { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// Finds where an achievement is discussed in the text of guide pages. A single guide usually covers a whole game's
    /// achievements, so one page read once can supply a note for dozens of them, which is what makes a free search
    /// allowance go far.
    /// </summary>
    public static class PassageMatcher
    {
        private static readonly string[] GuideWords = { "guide", "walkthrough", "trophy", "trophies", "achievement", "wiki", "roadmap", "tips", "unlock" };

        internal const int MaxPassageLength = 280;

        public static string Domain(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host.Substring(4) : host;
        }

        /// <summary>Lowercase letters and digits only, single-spaced. For comparing names across punctuation differences.</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length);
            var space = true;
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }

            return sb.ToString().Trim();
        }

        /// <summary>How guide-like a page looks from its address and title.</summary>
        public static double PageWeight(string url, string title)
        {
            var text = ((url ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();
            return 1.0 + 0.4 * GuideWords.Count(word => text.Contains(word));
        }

        /// <summary>
        /// The best passages in the pages that mention the achievement by name. Names that are too short or generic to
        /// match reliably (a single short word) find nothing rather than something misleading.
        /// </summary>
        public static List<Passage> Find(IList<CorpusPage> pages, string name, string description, int max = 2)
        {
            var found = new List<Passage>();
            var cleanName = CleanName(name);

            // Apostrophes come and go between listings ("Let's be Friends" on one site, "Lets be Friends" on another), so
            // they are dropped from the name and allowed anywhere inside a word when searching the text.
            var tokens = Normalize(cleanName.Replace("'", string.Empty).Replace("\u2019", string.Empty))
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (pages == null || tokens.Length == 0) return found;

            var descriptionWords = Normalize(description).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4).Distinct().ToList();

            // A single short word ("Love", "Elite") appears all over a guide, so a bare mention proves nothing. It counts
            // only where the text around it also repeats what the achievement asks for.
            if (tokens.Length == 1 && cleanName.Length < 6) return FindShortName(pages, tokens[0], descriptionWords, max);

            var wordPattern = string.Join(@"[^A-Za-z0-9]+", tokens.Select(t => string.Join("['\u2019]?", t.Select(c => Regex.Escape(c.ToString())))));
            var flexible = new Regex(wordPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            foreach (var page in pages)
            {
                if (string.IsNullOrEmpty(page?.Text)) continue;

                var index = page.Text.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase);
                var length = cleanName.Length;
                if (index < 0)
                {
                    try
                    {
                        var match = flexible.Match(page.Text);
                        if (match.Success) { index = match.Index; length = match.Length; }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // A pathological page: skip it.
                    }
                }

                if (index < 0) continue;

                var text = Window(page.Text, index, length);
                var overlap = descriptionWords.Count == 0 ? 0 : descriptionWords.Count(w => text.ToLowerInvariant().Contains(w)) / (double)descriptionWords.Count;
                found.Add(new Passage { Text = text, Domain = page.Domain, Url = page.Url, Score = page.Weight + 0.5 * overlap });
            }

            // Best first, preferring different sites so the note is not one page repeated.
            var ordered = found.OrderByDescending(p => p.Score).ToList();
            var chosen = new List<Passage>();
            foreach (var passage in ordered)
            {
                if (chosen.Count >= max) break;
                if (chosen.All(c => c.Domain != passage.Domain)) chosen.Add(passage);
            }

            foreach (var passage in ordered)
            {
                if (chosen.Count >= max) break;
                if (!chosen.Contains(passage)) chosen.Add(passage);
            }

            return chosen;
        }

        private static List<Passage> FindShortName(IList<CorpusPage> pages, string token, IList<string> descriptionWords, int max)
        {
            var found = new List<Passage>();
            if (descriptionWords.Count < 2) return found; // nothing to corroborate with: better to find nothing

            Regex word;
            try
            {
                word = new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(token) + @"(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                return found;
            }

            foreach (var page in pages)
            {
                if (string.IsNullOrEmpty(page?.Text)) continue;

                Passage best = null;
                try
                {
                    var seen = 0;
                    foreach (Match m in word.Matches(page.Text))
                    {
                        if (++seen > 300) break;
                        var text = Window(page.Text, m.Index, m.Length);
                        var lower = text.ToLowerInvariant();
                        var hits = descriptionWords.Count(w => lower.Contains(w));
                        var overlap = hits / (double)descriptionWords.Count;
                        if (hits < 2 || overlap < 0.5) continue;
                        var score = page.Weight + 0.5 * overlap;
                        if (best == null || score > best.Score) best = new Passage { Text = text, Domain = page.Domain, Url = page.Url, Score = score };
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological page: skip it.
                }

                if (best != null) found.Add(best);
            }

            return found.OrderByDescending(p => p.Score).Take(max).ToList();
        }

        private static string CleanName(string name)
        {
            return (name ?? string.Empty).Trim().Trim('"', '\'', '.', '!', '?', ':', ' ');
        }

        /// <summary>A short stretch of text around a match, tidied to start and end on natural breaks.</summary>
        internal static string Window(string text, int index, int matchLength)
        {
            var start = Math.Max(0, index - 120);
            var back = text.LastIndexOf('\n', index, index - start);
            if (back >= 0) start = back + 1;
            else
            {
                var space = text.IndexOf(' ', start, Math.Max(0, index - start));
                if (space >= 0 && space < index) start = space + 1;
            }

            var end = Math.Min(text.Length, index + matchLength + 200);
            var forward = text.IndexOf('\n', index + matchLength, Math.Max(0, end - (index + matchLength)));
            if (forward >= 0) end = forward;

            var snippet = Regex.Replace(text.Substring(start, Math.Max(0, end - start)), @"\s+", " ").Trim();
            if (snippet.Length > MaxPassageLength) snippet = snippet.Substring(0, MaxPassageLength).TrimEnd() + "…";
            return snippet;
        }
    }
}
