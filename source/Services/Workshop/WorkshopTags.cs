using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Workshop
{
    /// <summary>Tag normalization shared by the share dialog and the Browse tag filter.</summary>
    public static class WorkshopTags
    {
        private static readonly char[] Separators = { ',', ';' };

        /// <summary>Splits comma- or semicolon-separated text into trimmed, lowercase, distinct tags.</summary>
        public static IReadOnlyList<string> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize)
                .Where(tag => tag.Length > 0)
                .Distinct()
                .ToList();
        }

        public static string Normalize(string tag) => (tag ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// An empty filter matches everything. Include matches items carrying any filter tag;
        /// exclude matches items carrying none of them.
        /// </summary>
        public static bool Matches(ICollection<string> itemTags, IReadOnlyList<string> filter, bool exclude)
        {
            if (filter == null || filter.Count == 0)
            {
                return true;
            }

            var hasAny = itemTags != null && filter.Any(itemTags.Contains);
            return exclude ? !hasAny : hasAny;
        }
    }
}
