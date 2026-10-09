using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>One game-window picture taken the moment the other app announced an unlock.</summary>
    internal sealed class NotificationSnapshot
    {
        public Guid GameId { get; set; }

        /// <summary>Normalised achievement display name (see <see cref="NotificationSnapshotStore.NormalizeKey"/>).</summary>
        public string NameKey { get; set; }

        /// <summary>Normalised internal achievement name.</summary>
        public string ApiKey { get; set; }

        public DateTime CapturedUtc { get; set; }

        /// <summary>The picture as PNG bytes (small, and independent of any live bitmap).</summary>
        public byte[] Png { get; set; }
    }

    /// <summary>
    /// A short-lived memory of recent notification snapshots. When this add-on's own unlock event
    /// arrives, <see cref="TakeBest"/> hands back the snapshot that belongs to it — matched by game,
    /// then by achievement name, then (only if no name matches) by how close in time it was — and
    /// removes it so one picture is never used for two achievements. Entries expire, and the
    /// store is capped, so a notification this add-on never hears about cannot leak memory.
    /// </summary>
    internal sealed class NotificationSnapshotStore
    {
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(4);

        /// <summary>
        /// How far from the unlock's time a snapshot may be when no name matched. Deliberately
        /// tight: a wrong picture is worse than the normal live capture.
        /// </summary>
        public static readonly TimeSpan TimeOnlyTolerance = TimeSpan.FromSeconds(20);

        private readonly object _gate = new object();
        private readonly List<NotificationSnapshot> _entries = new List<NotificationSnapshot>();
        private readonly int _maxEntries;
        private readonly TimeSpan _lifetime;
        private readonly Func<DateTime> _utcNow;

        public NotificationSnapshotStore(int maxEntries = 6, TimeSpan? lifetime = null, Func<DateTime> utcNow = null)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _lifetime = lifetime ?? DefaultLifetime;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public void Add(NotificationSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Png == null || snapshot.Png.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                PruneLocked();
                _entries.Add(snapshot);
                while (_entries.Count > _maxEntries)
                {
                    _entries.RemoveAt(0);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// The snapshot for an unlock, removed from the store, or null when none fits.
        /// <paramref name="anchorUtc"/> is the unlock's own time when known (otherwise "now").
        /// </summary>
        public NotificationSnapshot TakeBest(Guid gameId, string displayName, string apiName, DateTime? anchorUtc)
        {
            var nameKey = NormalizeKey(displayName);
            var apiKey = NormalizeKey(apiName);
            lock (_gate)
            {
                PruneLocked();
                var anchor = anchorUtc ?? _utcNow();
                var sameGame = _entries
                    .Where(e => gameId == Guid.Empty || e.GameId == Guid.Empty || e.GameId == gameId)
                    .ToList();

                var byName = sameGame
                    .Where(e =>
                        (nameKey.Length > 0 && e.NameKey == nameKey) ||
                        (apiKey.Length > 0 && e.ApiKey == apiKey))
                    .OrderBy(e => Math.Abs((e.CapturedUtc - anchor).Ticks))
                    .FirstOrDefault();
                if (byName != null)
                {
                    _entries.Remove(byName);
                    return byName;
                }

                // Names can differ between the two apps (language, punctuation), so fall back to
                // time — but only when the nearest snapshot is clearly unambiguous.
                var byTime = sameGame
                    .Where(e => Math.Abs((e.CapturedUtc - anchor).Ticks) <= TimeOnlyTolerance.Ticks)
                    .OrderBy(e => Math.Abs((e.CapturedUtc - anchor).Ticks))
                    .ToList();
                if (byTime.Count == 1)
                {
                    _entries.Remove(byTime[0]);
                    return byTime[0];
                }

                return null;
            }
        }

        private void PruneLocked()
        {
            var cutoff = _utcNow() - _lifetime;
            _entries.RemoveAll(e => e.CapturedUtc < cutoff);
        }

        /// <summary>Lower-case letters and digits only, so "Don't  Panic!" and "dont panic" compare equal.</summary>
        public static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }
    }
}
