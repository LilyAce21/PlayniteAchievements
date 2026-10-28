using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Providers.Vita3K
{
    /// <summary>
    /// Reads the TROPUSR.DAT progress file Vita3K writes under
    /// ux0/user/&lt;id&gt;/trophy/data/&lt;NPCOMMID&gt;/. The name matches Sony's file, but the
    /// layout is Vita3K's own fixed 1648-byte little-endian record
    /// (vita3k/np/src/trophy/context.cpp, save_trophy_progress_file):
    /// magic u32 0x12D5819A, unlocked bitset 4×u32, hidden bitset 4×u32,
    /// group count u32, trophy count u32, platinum id i32, per-group counts 16×u32,
    /// unlock times 128×u64 (Unix seconds, 0 when unknown), grades 128×i32.
    /// Trophy id N is bit (N &amp; 31) of bitset word (N &gt;&gt; 5).
    /// </summary>
    internal static class Vita3KTropusrReader
    {
        internal const uint Magic = 0x12D5819A;
        internal const int MaxTrophies = 128;
        internal const int UnlockedBitsetOffset = 0x004;
        internal const int TrophyCountOffset = 0x028;
        internal const int UnlockTimesOffset = 0x070;
        internal const int FileSize = 0x670;

        // 2100-01-01 00:00:00 UTC; larger values are not plausible unlock times.
        private const ulong MaxReasonableUnixSeconds = 4102444800;

        /// <summary>
        /// Reads every unlocked trophy id with its unlock time (null when Vita3K recorded none).
        /// Returns false when the file is missing, unreadable, or not a current-format
        /// Vita3K progress file; callers keep previously known progress in that case.
        /// </summary>
        public static bool TryRead(string path, out Dictionary<int, DateTime?> unlockedById, out string error)
        {
            unlockedById = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "file not found";
                return false;
            }

            byte[] bytes;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }

            return TryRead(bytes, out unlockedById, out error);
        }

        public static bool TryRead(byte[] bytes, out Dictionary<int, DateTime?> unlockedById, out string error)
        {
            unlockedById = null;
            error = null;

            if (bytes == null || bytes.Length != FileSize)
            {
                error = $"unexpected file size {bytes?.Length ?? 0} (expected {FileSize})";
                return false;
            }

            if (BitConverter.ToUInt32(bytes, 0) != Magic)
            {
                error = "unexpected file magic";
                return false;
            }

            var trophyCount = BitConverter.ToUInt32(bytes, TrophyCountOffset);
            if (trophyCount > MaxTrophies)
            {
                error = $"trophy count {trophyCount} out of range";
                return false;
            }

            var result = new Dictionary<int, DateTime?>();
            for (var id = 0; id < (int)trophyCount; id++)
            {
                var word = BitConverter.ToUInt32(bytes, UnlockedBitsetOffset + ((id >> 5) * 4));
                if ((word & (1u << (id & 31))) == 0)
                {
                    continue;
                }

                var seconds = BitConverter.ToUInt64(bytes, UnlockTimesOffset + (id * 8));
                result[id] = seconds > 0 && seconds <= MaxReasonableUnixSeconds
                    ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime
                    : (DateTime?)null;
            }

            unlockedById = result;
            return true;
        }
    }
}
