using Playnite.SDK;
using PlayniteAchievements.Providers.PlayStationTrophies;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Parses RPCS3 trophy progress (PS3 TROPUSR.DAT).
    /// Definitions are parsed by the shared TrophyConfParser.
    /// </summary>
    internal static class Rpcs3TrophyParser
    {
        // TROPUSR.DAT is a big-endian table file. These layouts mirror RPCS3's
        // TROPUSRHeader, TROPUSRTableHeader, and TROPUSREntry6 definitions.
        private const uint TropusrMagic = 0x818F54AD;
        private const int TropusrHeaderSize = 0x30;
        private const int TropusrTableHeaderSize = 0x20;
        private const uint TrophyStateTableType = 6;
        private const uint TrophyStateEntryContentsSize = 0x60;
        private const int TrophyStateEntryHeaderSize = 0x10;

        // Magic-byte patterns used by the live in-game progress reader (below), which
        // scans TROPUSR.DAT heuristically while a game is running; the authoritative
        // unlock-state table is parsed separately by TryReadTropusrStates.
        private static readonly byte[][] ProgressMagicBytePatterns =
        {
            new byte[] { 0, 0, 0, 4, 0, 0, 0, 0x50, 0, 0, 0 },
            new byte[] { 0, 0, 0, 6, 0, 0, 0, 0x60, 0, 0, 0 }
        };
        private const int TrophyStateEntrySize = TrophyStateEntryHeaderSize + (int)TrophyStateEntryContentsSize;

        /// <summary>
        /// Parses the authoritative unlock-state table in TROPUSR.DAT. Returns
        /// false without changing <paramref name="trophies"/> when the file is
        /// malformed, incomplete, or internally inconsistent. Callers use that
        /// distinction to preserve previously known progress instead of treating a
        /// failed parse as an all-locked trophy list.
        /// </summary>
        internal static bool TryParseTrophyUnlockData(string tropusrPath, List<NpTrophy> trophies, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(tropusrPath) || !File.Exists(tropusrPath) || trophies == null || trophies.Count == 0)
            {
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(tropusrPath);
                if (!TryReadTropusrStates(bytes, out var states, out var error))
                {
                    logger?.Warn($"[RPCS3] Ignoring invalid TROPUSR.DAT at '{tropusrPath}': {error}. Existing achievement progress will be preserved.");
                    return false;
                }

                var trophyById = trophies
                    .Where(trophy => trophy != null)
                    .GroupBy(trophy => trophy.Id)
                    .ToDictionary(group => group.Key, group => group.First());

                var unmatchedStateIds = states.Keys
                    .Where(id => !trophyById.ContainsKey(id))
                    .OrderBy(id => id)
                    .ToList();

                // Do not mutate a definition until every table and record has been
                // checked. This keeps an invalid file from producing partial state.
                foreach (var state in states)
                {
                    if (!trophyById.TryGetValue(state.Key, out var trophy))
                    {
                        continue;
                    }

                    trophy.Unlocked = state.Value.Unlocked;
                    trophy.UnlockTimeUtc = state.Value.UnlockTimeUtc;
                }

                if (unmatchedStateIds.Count > 0)
                {
                    var reportedIds = string.Join(", ", unmatchedStateIds.Take(16));
                    var suffix = unmatchedStateIds.Count > 16 ? ", ..." : string.Empty;
                    logger?.Warn(
                        $"[RPCS3] TROPUSR.DAT at '{tropusrPath}' has {unmatchedStateIds.Count} state record(s) " +
                        $"not present in its trophy definitions: [{reportedIds}{suffix}].");
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"[RPCS3] Failed to parse TROPUSR.DAT at '{tropusrPath}'; existing achievement progress will be preserved.");
                return false;
            }
        }

        private static bool TryReadTropusrStates(
            byte[] bytes,
            out Dictionary<int, TrophyUnlockState> states,
            out string error)
        {
            states = null;
            error = null;

            if (bytes == null || bytes.Length < TropusrHeaderSize)
            {
                error = "file is shorter than the TROPUSR header";
                return false;
            }

            if (ReadUInt32BigEndian(bytes, 0) != TropusrMagic)
            {
                error = "unexpected file magic";
                return false;
            }

            var tableCount = ReadUInt32BigEndian(bytes, 8);
            if (tableCount == 0 || tableCount > (uint)((bytes.Length - TropusrHeaderSize) / TropusrTableHeaderSize))
            {
                error = "invalid table count";
                return false;
            }

            var table6Seen = false;
            var parsedStates = new Dictionary<int, TrophyUnlockState>();

            for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
            {
                var tableOffset = TropusrHeaderSize + ((long)tableIndex * TropusrTableHeaderSize);
                if (!HasRange(bytes, (ulong)tableOffset, TropusrTableHeaderSize))
                {
                    error = "table header extends beyond the file";
                    return false;
                }

                var type = ReadUInt32BigEndian(bytes, (int)tableOffset);
                var contentsSize = ReadUInt32BigEndian(bytes, (int)tableOffset + 4);
                var entryCount = ReadUInt32BigEndian(bytes, (int)tableOffset + 12);
                var entriesOffset = ReadUInt64BigEndian(bytes, (int)tableOffset + 16);

                if (contentsSize > int.MaxValue - TrophyStateEntryHeaderSize)
                {
                    error = "entry size is too large";
                    return false;
                }

                var entrySize = (long)contentsSize + TrophyStateEntryHeaderSize;
                if (entrySize <= 0 ||
                    entriesOffset > (ulong)bytes.Length ||
                    entryCount > 0 &&
                    ((ulong)entrySize > (ulong)bytes.Length ||
                     (ulong)entryCount > ((ulong)bytes.Length - entriesOffset) / (ulong)entrySize))
                {
                    error = "table entries extend beyond the file";
                    return false;
                }

                if (type != TrophyStateTableType)
                {
                    continue;
                }

                if (table6Seen || contentsSize != TrophyStateEntryContentsSize)
                {
                    error = table6Seen ? "multiple trophy-state tables" : "unexpected trophy-state entry size";
                    return false;
                }

                table6Seen = true;
                for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    var entryOffset = entriesOffset + ((ulong)entryIndex * (ulong)TrophyStateEntrySize);
                    if (!HasRange(bytes, entryOffset, TrophyStateEntrySize))
                    {
                        error = "trophy-state entry extends beyond the file";
                        return false;
                    }

                    if (ReadUInt32BigEndian(bytes, (int)entryOffset) != TrophyStateTableType ||
                        ReadUInt32BigEndian(bytes, (int)entryOffset + 4) != TrophyStateEntryContentsSize)
                    {
                        error = "invalid trophy-state entry header";
                        return false;
                    }

                    var trophyId = ReadUInt32BigEndian(bytes, (int)entryOffset + 16);
                    if (trophyId > int.MaxValue || parsedStates.ContainsKey((int)trophyId))
                    {
                        error = trophyId > int.MaxValue ? "trophy id is out of range" : "duplicate trophy id";
                        return false;
                    }

                    var trophyState = ReadUInt32BigEndian(bytes, (int)entryOffset + 20);
                    var timestamp2 = ReadUInt64BigEndian(bytes, (int)entryOffset + 40);
                    DateTime? unlockTimeUtc = null;
                    if (trophyState != 0 && timestamp2 > 0)
                    {
                        if (timestamp2 > (ulong)(DateTime.MaxValue.Ticks / 10))
                        {
                            error = "unlock timestamp is out of range";
                            return false;
                        }

                        unlockTimeUtc = new DateTime((long)(timestamp2 * 10), DateTimeKind.Utc);
                    }

                    parsedStates.Add((int)trophyId, new TrophyUnlockState
                    {
                        Unlocked = trophyState != 0,
                        UnlockTimeUtc = unlockTimeUtc
                    });
                }
            }

            if (!table6Seen)
            {
                error = "trophy-state table is missing";
                return false;
            }

            states = parsedStates;
            return true;
        }

        private static bool HasRange(byte[] bytes, ulong offset, long length)
        {
            return bytes != null &&
                   length >= 0 &&
                   offset <= (ulong)bytes.Length &&
                   (ulong)length <= (ulong)bytes.Length - offset;
        }

        private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static ulong ReadUInt64BigEndian(byte[] bytes, int offset)
        {
            return ((ulong)bytes[offset] << 56) |
                   ((ulong)bytes[offset + 1] << 48) |
                   ((ulong)bytes[offset + 2] << 40) |
                   ((ulong)bytes[offset + 3] << 32) |
                   ((ulong)bytes[offset + 4] << 24) |
                   ((ulong)bytes[offset + 5] << 16) |
                   ((ulong)bytes[offset + 6] << 8) |
                   bytes[offset + 7];
        }

        private sealed class TrophyUnlockState
        {
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
        }

        /// <summary>
        /// Progress-only TROPHY reader used while a game is running. Trophy ids come from the
        /// existing cache schema, so this never reads TROPCONF or any icon metadata.
        /// </summary>
        internal static bool TryParseTrophyProgress(
            string tropusrPath,
            IReadOnlyCollection<int> trophyIds,
            out Dictionary<int, DateTime?> unlockedById)
        {
            unlockedById = new Dictionary<int, DateTime?>();
            var ids = (trophyIds ?? Array.Empty<int>())
                .Where(id => id >= 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (string.IsNullOrWhiteSpace(tropusrPath) || !File.Exists(tropusrPath) || ids.Count == 0)
            {
                return false;
            }

            try
            {
                byte[] bytes;
                using (var stream = new FileStream(
                    tropusrPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }

                var entryOffsets = FindProgressEntryOffsets(bytes);
                if (entryOffsets.Count < ids.Count)
                {
                    return false;
                }

                var relevantOffsets = entryOffsets
                    .Skip(entryOffsets.Count - ids.Count)
                    .ToList();
                var parsedIds = new HashSet<int>();
                for (var index = 0; index < relevantOffsets.Count; index++)
                {
                    var entryOffset = relevantOffsets[index];
                    var entryEnd = index + 1 < relevantOffsets.Count
                        ? relevantOffsets[index + 1] - ProgressMagicBytePatterns[0].Length
                        : bytes.Length;
                    if (entryOffset < 0 || entryEnd - entryOffset < 29)
                    {
                        unlockedById.Clear();
                        return false;
                    }

                    var trophyId = bytes[entryOffset];
                    if (!ids.Contains(trophyId))
                    {
                        unlockedById.Clear();
                        return false;
                    }

                    parsedIds.Add(trophyId);
                    var unlocked =
                        bytes[entryOffset + 9] == 0 &&
                        bytes[entryOffset + 10] == 0 &&
                        bytes[entryOffset + 11] == 0 &&
                        bytes[entryOffset + 12] == 1;
                    if (unlocked)
                    {
                        DateTime? unlockTimeUtc = null;
                        ulong rawTimestamp = 0;
                        for (var timestampIndex = 22; timestampIndex < 29; timestampIndex++)
                        {
                            rawTimestamp =
                                (rawTimestamp << 8) |
                                bytes[entryOffset + timestampIndex];
                        }

                        if (rawTimestamp > 0 && rawTimestamp <= (ulong)(long.MaxValue / 10))
                        {
                            var ticks = (long)rawTimestamp * 10L;
                            if (ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                            {
                                unlockTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
                            }
                        }

                        unlockedById[trophyId] = unlockTimeUtc;
                    }
                }

                if (parsedIds.Count != ids.Count)
                {
                    unlockedById.Clear();
                    return false;
                }

                return true;
            }
            catch
            {
                unlockedById.Clear();
                return false;
            }
        }

        private static List<int> FindProgressEntryOffsets(byte[] bytes)
        {
            var offsets = new List<int>();
            if (bytes == null || bytes.Length < ProgressMagicBytePatterns[0].Length)
            {
                return offsets;
            }

            for (var index = 0; index <= bytes.Length - ProgressMagicBytePatterns[0].Length; index++)
            {
                foreach (var pattern in ProgressMagicBytePatterns)
                {
                    var matches = true;
                    for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
                    {
                        if (bytes[index + patternIndex] != pattern[patternIndex])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (!matches)
                    {
                        continue;
                    }

                    offsets.Add(index + pattern.Length);
                    index += pattern.Length - 1;
                    break;
                }
            }

            return offsets;
        }
    }
}