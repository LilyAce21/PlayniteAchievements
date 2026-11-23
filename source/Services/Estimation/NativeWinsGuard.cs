using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Enforces hard rules 1 and 3: generated data never replaces native data, and an estimate is
    /// discarded the moment native data exists for the same slot. Every write of estimated values
    /// goes through <see cref="FilterWritable{T}"/>, so the rule holds at the data layer and does
    /// not depend on the UI.
    /// </summary>
    public static class NativeWinsGuard
    {
        /// <summary>
        /// Returns only the estimates whose achievement has no native value. Matching is by api
        /// name, case-insensitive. Estimates with a blank api name are dropped.
        /// </summary>
        public static List<T> FilterWritable<T>(
            IEnumerable<T> estimates,
            Func<T, string> apiNameOf,
            ISet<string> nativeApiNames)
        {
            if (estimates == null) return new List<T>();
            if (apiNameOf == null) throw new ArgumentNullException(nameof(apiNameOf));

            var native = nativeApiNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return estimates
                .Where(e => e != null)
                .Where(e =>
                {
                    var name = apiNameOf(e);
                    return !string.IsNullOrWhiteSpace(name) && !native.Contains(name);
                })
                .ToList();
        }

        /// <summary>
        /// The estimates that must be removed because native data has since arrived for them.
        /// </summary>
        public static List<string> StaleEstimates(IEnumerable<string> estimatedApiNames, ISet<string> nativeApiNames)
        {
            if (estimatedApiNames == null || nativeApiNames == null || nativeApiNames.Count == 0)
            {
                return new List<string>();
            }

            return estimatedApiNames
                .Where(name => !string.IsNullOrWhiteSpace(name) && nativeApiNames.Contains(name))
                .ToList();
        }
    }
}
