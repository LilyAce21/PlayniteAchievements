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
            var tokens = Normalize(cleanName).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (pages == null || tokens.Length == 0) return found;
            if (tokens.Length == 1 && cleanName.Length < 6) return found;

            Regex flexible = null;
            if (tokens.Length >= 2)
            {
                flexible = new Regex(string.Join(@"[^A-Za-z0-9]+", tokens.Select(Regex.Escape)),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }

            var descriptionWords = Normalize(description).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4).Distinct().ToList();

            foreach (var page in pages)
            {
                if (string.IsNullOrEmpty(page?.Text)) continue;

                var index = page.Text.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase);
                var length = cleanName.Length;
                if (index < 0 && flexible != null)
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
