using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// One instance's <c>logs/latest.log</c> during a play session: where reading stopped and which
    /// rows an announced title can mean.
    /// </summary>
    internal sealed class MinecraftLogTarget
    {
        public string LogPath { get; set; }

        /// <summary>Byte offset of the first unread line.</summary>
        public long Offset { get; set; }

        /// <summary>Row ids by display title (case-insensitive), within this instance only.</summary>
        public Dictionary<string, List<string>> ApiNamesByTitle { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registration state for live tracking: the instances' logs and the player names whose
    /// announcements count. An empty name set accepts every player, for offline-mode accounts the
    /// launcher files do not name.
    /// </summary>
    internal sealed class MinecraftLogState
    {
        public List<MinecraftLogTarget> Targets { get; set; } = new List<MinecraftLogTarget>();

        public HashSet<string> PlayerNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads advancement announcements from the game log as they are written. The game saves
    /// advancement files only on pause, autosave and quit, but announces each unlock in chat at
    /// once, and the chat line reaches latest.log immediately:
    /// <c>[20:33:08] [Render thread/INFO]: [System] [CHAT] Steve has made the advancement [Stone Age]</c>.
    /// Only English announcements are recognized, and advancements a mod or the
    /// announceAdvancements game rule keep out of chat wait for the next save.
    /// </summary>
    internal static class MinecraftLogWatcher
    {
        private const int MaxReadBytes = 4 * 1024 * 1024;

        private static readonly Regex Announcement = new Regex(
            @"(?<player>[A-Za-z0-9_]{1,16}) has (?:made the advancement|reached the goal|completed the challenge) \[(?<title>.+)\]\s*$",
            RegexOptions.CultureInvariant);

        // [20:33:08] in vanilla logs; [29Aug2025 12:13:01.067] in Forge/NeoForge logs.
        private static readonly Regex LeadingTime = new Regex(
            @"^\[(?:\d{2}[A-Za-z]{3}\d{4} )?(?<time>\d{2}:\d{2}:\d{2})",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// A target for an instance's log, starting after what the log already holds so a previous
        /// session's announcements are not replayed. A log the game rotates at launch is read from
        /// its start (see <see cref="ReadNewLines"/>).
        /// </summary>
        public static MinecraftLogTarget CreateTarget(string gameFolder, IEnumerable<(string ApiName, string Title)> rows)
        {
            var logPath = Path.Combine(gameFolder, "logs", "latest.log");
            var target = new MinecraftLogTarget
            {
                LogPath = logPath,
                Offset = SafeLength(logPath)
            };

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    continue;
                }

                if (!target.ApiNamesByTitle.TryGetValue(row.Title.Trim(), out var apiNames))
                {
                    apiNames = new List<string>();
                    target.ApiNamesByTitle[row.Title.Trim()] = apiNames;
                }

                apiNames.Add(row.ApiName);
            }

            return target;
        }

        /// <summary>
        /// Unlocks announced since the last read: (row id, local announcement time as UTC). A title
        /// two rows share is skipped, and so is a line from another player.
        /// </summary>
        public static List<(string ApiName, DateTime? UnlockTimeUtc)> ReadUnlocks(MinecraftLogState state, DateTime nowLocal)
        {
            var result = new List<(string ApiName, DateTime? UnlockTimeUtc)>();
            foreach (var target in state?.Targets ?? Enumerable.Empty<MinecraftLogTarget>())
            {
                foreach (var line in ReadNewLines(target))
                {
                    var unlock = ParseLine(line, state.PlayerNames, target.ApiNamesByTitle, nowLocal);
                    if (unlock.HasValue && result.All(existing => existing.ApiName != unlock.Value.ApiName))
                    {
                        result.Add(unlock.Value);
                    }
                }
            }

            return result;
        }

        internal static (string ApiName, DateTime? UnlockTimeUtc)? ParseLine(
            string line,
            ISet<string> playerNames,
            IReadOnlyDictionary<string, List<string>> apiNamesByTitle,
            DateTime nowLocal)
        {
            var match = Announcement.Match(line ?? string.Empty);
            if (!match.Success)
            {
                return null;
            }

            if (playerNames != null && playerNames.Count > 0 && !playerNames.Contains(match.Groups["player"].Value))
            {
                return null;
            }

            if (!apiNamesByTitle.TryGetValue(match.Groups["title"].Value.Trim(), out var apiNames) || apiNames.Count != 1)
            {
                return null;
            }

            return (apiNames[0], ParseTime(line, nowLocal));
        }

        /// <summary>
        /// The line's clock time on today's date, or yesterday's when that would be in the future
        /// (a session running past midnight), as UTC. Null when the line has no time.
        /// </summary>
        private static DateTime? ParseTime(string line, DateTime nowLocal)
        {
            var match = LeadingTime.Match(line);
            if (!match.Success ||
                !TimeSpan.TryParseExact(match.Groups["time"].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time))
            {
                return null;
            }

            var local = nowLocal.Date + time;
            if (local > nowLocal.AddMinutes(1))
            {
                local = local.AddDays(-1);
            }

            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }

        /// <summary>
        /// Complete lines written since <see cref="MinecraftLogTarget.Offset"/>; a partial last line
        /// is left for the next read. A log shorter than the offset was rotated and is read from its start.
        /// </summary>
        private static IEnumerable<string> ReadNewLines(MinecraftLogTarget target)
        {
            byte[] buffer;
            try
            {
                if (!File.Exists(target.LogPath))
                {
                    return Enumerable.Empty<string>();
                }

                using (var stream = new FileStream(target.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < target.Offset)
                    {
                        target.Offset = 0;
                    }

                    var available = (int)Math.Min(stream.Length - target.Offset, MaxReadBytes);
                    if (available <= 0)
                    {
                        return Enumerable.Empty<string>();
                    }

                    stream.Seek(target.Offset, SeekOrigin.Begin);
                    buffer = new byte[available];
                    var read = 0;
                    while (read < available)
                    {
                        var chunk = stream.Read(buffer, read, available - read);
                        if (chunk <= 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    if (read < available)
                    {
                        Array.Resize(ref buffer, read);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNewline < 0)
            {
                return Enumerable.Empty<string>();
            }

            target.Offset += lastNewline + 1;
            return Encoding.UTF8.GetString(buffer, 0, lastNewline + 1)
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static long SafeLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }
}
