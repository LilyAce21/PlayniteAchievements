using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Remembers, for this session, when web research (Google Search grounding) has been refused for a model, so the
    /// plugin does not keep asking. On the free tier Google lists search grounding as unavailable for the Gemini 3 models,
    /// so a free key typically gets an immediate "quota" error however slowly it asks. After a refusal research is skipped
    /// for a few hours, then tried once more; one success clears the memory. Nothing is written to disk.
    /// </summary>
    public static class ResearchAvailability
    {
        /// <summary>How long a refusal is remembered before research is tried again.</summary>
        public static readonly TimeSpan RememberFor = TimeSpan.FromHours(4);

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> Unavailable =
            new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Worked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The clock, replaceable in tests.</summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public static bool IsUnavailable(string model, out string reason)
        {
            lock (Sync)
            {
                reason = null;
                if (model == null || !Unavailable.TryGetValue(model, out var entry)) return false;

                if (UtcNow() >= entry.Key)
                {
                    Unavailable.Remove(model); // time to try again
                    return false;
                }

                reason = entry.Value;
                return true;
            }
        }

        public static void MarkUnavailable(string model, string reason)
        {
            if (model == null) return;
            lock (Sync) { Unavailable[model] = new KeyValuePair<DateTime, string>(UtcNow() + RememberFor, reason); }
        }

        /// <summary>A research request succeeded: research works for this model, whatever happened before.</summary>
        public static void MarkWorked(string model)
        {
            if (model == null) return;
            lock (Sync)
            {
                Worked.Add(model);
                Unavailable.Remove(model);
            }
        }

        public static bool HasWorked(string model)
        {
            lock (Sync) { return model != null && Worked.Contains(model); }
        }

        /// <summary>Forget everything, so the next run tries research afresh (used by Regenerate and by tests).</summary>
        public static void ResetAll()
        {
            lock (Sync)
            {
                Unavailable.Clear();
                Worked.Clear();
            }
        }
    }
}
