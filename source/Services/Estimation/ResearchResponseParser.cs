using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Reads the research step's plain lines ("a0001 | what it requires | how hard | kind") into notes by id.
    /// Nothing is trusted: ids must belong to the request, notes are trimmed and capped, anything that looks like
    /// a credential is removed, and a line that says no sources were found does not count as research.
    /// </summary>
    public static class ResearchResponseParser
    {
        internal const int MaxNoteLength = 600;

        private static readonly Regex Line = new Regex(@"^\s*(?:[-*•]\s*)?(?:\*\*|`)?(a\d{4})(?:\*\*|`)?\s*[|:]\s*(.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The notes found, by id. Ids the response did not usefully cover are simply absent.</summary>
        public static Dictionary<string, string> Parse(string text, IDictionary<string, string> idToApiName)
        {
            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text) || idToApiName == null) return notes;

            foreach (var raw in text.Replace("\r", "\n").Split('\n'))
            {
                var match = Line.Match(raw);
                if (!match.Success) continue;

                var id = match.Groups[1].Value.ToLowerInvariant();
                if (!idToApiName.ContainsKey(id) || notes.ContainsKey(id)) continue;

                var note = Regex.Replace(match.Groups[2].Value, @"\s+", " ").Trim().Trim('|').Trim();
                if (note.Length < 12) continue;
                if (note.IndexOf("no sources found", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                note = PrivacyFilter.Clean(note);
                if (note.Length > MaxNoteLength) note = note.Substring(0, MaxNoteLength).TrimEnd() + "…";
                notes[id] = note;
            }

            return notes;
        }
    }
}
