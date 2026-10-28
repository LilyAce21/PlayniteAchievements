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
    /// One game folder's <c>logs/latest.log</c> during a play session: where reading stopped and how
    /// an announced title maps to a row.
    /// </summary>
    internal sealed class MinecraftLogTarget
    {
        public string LogPath { get; set; }

        public string GameFolder { get; set; }

        /// <summary>The instance key that prefixes this folder's row ids.</summary>
        public string InstanceKey { get; set; }

        /// <summary>
        /// Byte offset of the first unread line; negative until the first read decides between the
        /// log's start (written this session) and its end (left from an earlier session).
        /// </summary>
        public long Offset { get; set; } = -1;

        /// <summary>Row ids by display title (case-insensitive), from the cached rows of this instance.</summary>
        public Dictionary<string, List<string>> ApiNamesByTitle { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The game's announcement patterns and titles in the language it runs in, built from the
        /// instance's definitions on the first line that could be an announcement.
        /// </summary>
        public Lazy<MinecraftLiveLanguage> Language { get; set; }

        /// <summary>The only world whose unlocks count (a single-world override); null for every world.</summary>
        public string WorldFilter { get; set; }
    }

    /// <summary>
    /// Registration state for live tracking: the watched logs, the player names whose
    /// announcements count, and the unlocks seen this session. An empty name set accepts every
    /// player, for offline-mode accounts the launcher files do not name.
    /// </summary>
    internal sealed class MinecraftLogState
    {
        public List<MinecraftLogTarget> Targets { get; set; } = new List<MinecraftLogTarget>();

        public HashSet<string> PlayerNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every unlock announced this session, in order. Re-sent on each read: it costs nothing once
        /// applied, and it lands an unlock whose row the cache gains only after a refresh, or one a
        /// concurrent refresh write briefly reverted.
        /// </summary>
        public List<(string ApiName, DateTime? UnlockTimeUtc)> SessionUnlocks { get; } =
            new List<(string ApiName, DateTime? UnlockTimeUtc)>();
    }

    /// <summary>
    /// Reads advancement announcements from the game log as they are written. The game saves
    /// advancement files only on pause, autosave and quit, but announces each unlock in chat at
    /// once, and the line reaches latest.log immediately.
    /// <para>
    /// Only the integrated server's own line counts:
    /// <c>[17:10:04] [Server thread/INFO]: System chat: Steve has made the advancement [Stone Age]</c>.
    /// It is written only when the world runs on this PC (single player, or hosting LAN), whose
    /// saves the plugin reads. Joining a LAN world or a server logs only the client's
    /// <c>[Render thread/INFO]: [System] [CHAT]</c> line, so those unlocks, kept on the host, never
    /// count. Announcements are matched through the game's own chat templates in the language it runs
    /// in (see <see cref="MinecraftLiveLanguage"/>), with English wording as a fallback; advancements
    /// a mod or the announceAdvancements game rule keep out of chat wait for the next save.
    /// </para>
    /// </summary>
    internal static class MinecraftLogWatcher
    {
        private const int MaxReadBytes = 4 * 1024 * 1024;

        // Fallback for an announcement worded in code rather than through the game's chat templates
        // (see MinecraftLiveLanguage): English verbs and nouns that name an unlock, e.g. Legacy4J's
        // "has just earned the achievement". Other bracketed chat, such as a death message's
        // "[Diamond Sword]", never matches.
        private static readonly Regex FallbackAnnouncement = new Regex(
            @"(?<![\w<\[*])(?<player>[A-Za-z0-9_]{1,16}) has (?:just )?(?:made|earned|reached|completed|unlocked|gained|got|gotten) (?:the |an |a )?(?:advancement|achievement|goal|challenge) \[(?<title>.+)\]\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // [Server thread/INFO] in vanilla, Fabric and Forge/NeoForge logs alike.
        private const string ServerThreadMarker = "[Server thread/";

        // [20:33:08] in vanilla logs; [29Aug2025 12:13:01.067] in Forge/NeoForge logs.
        private static readonly Regex LeadingTime = new Regex(
            @"^\[(?:\d{2}[A-Za-z]{3}\d{4} )?(?<time>\d{2}:\d{2}:\d{2})",
            RegexOptions.CultureInvariant);

        public static MinecraftLogTarget CreateTarget(
            string gameFolder,
            string instanceKey,
            IEnumerable<(string ApiName, string Title)> rows,
            Func<MinecraftLiveLanguage> buildLanguage,
            string worldFilter)
        {
            var target = new MinecraftLogTarget
            {
                LogPath = Path.Combine(gameFolder, "logs", "latest.log"),
                GameFolder = gameFolder,
                InstanceKey = instanceKey,
                WorldFilter = worldFilter,
                Language = new Lazy<MinecraftLiveLanguage>(() => buildLanguage?.Invoke() ?? MinecraftLiveLanguage.Empty)
            };

            foreach (var row in rows ?? Enumerable.Empty<(string ApiName, string Title)>())
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
        /// Reads every watched log's new lines and returns all unlocks announced this session.
        /// </summary>
        public static IReadOnlyList<(string ApiName, DateTime? UnlockTimeUtc)> ReadUnlocks(
            MinecraftLogState state,
            DateTime nowLocal,
            DateTime sessionStartUtc)
        {
            if (state == null)
            {
                return Array.Empty<(string ApiName, DateTime? UnlockTimeUtc)>();
            }

            foreach (var target in state.Targets)
            {
                foreach (var line in ReadNewLines(target, sessionStartUtc))
                {
                    // The language is built only once a server line could carry a bracketed name.
                    if (!IsServerLine(line) || line.IndexOf(']', line.IndexOf(ServerThreadMarker, StringComparison.Ordinal) + ServerThreadMarker.Length) < 0)
                    {
                        continue;
                    }

                    var announcement = ParseAnnouncement(line, state.PlayerNames, nowLocal, target.Language.Value.Announcements);
                    if (!announcement.HasValue)
                    {
                        continue;
                    }

                    if (target.WorldFilter != null &&
                        !string.Equals(MinecraftPaths.FindActiveWorld(target.GameFolder), target.WorldFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var apiName = ResolveApiName(target, announcement.Value.Title);
                    if (apiName != null && state.SessionUnlocks.All(unlock => unlock.ApiName != apiName))
                    {
                        state.SessionUnlocks.Add((apiName, announcement.Value.UnlockTimeUtc));
                    }
                }
            }

            return state.SessionUnlocks;
        }

        /// <summary>
        /// The title and time of an integrated-server announcement by a listed player; null for any
        /// other line, including the client's copy and a LAN guest's or server's lines.
        /// </summary>
        internal static (string Title, DateTime? UnlockTimeUtc)? ParseAnnouncement(
            string line,
            ISet<string> playerNames,
            DateTime nowLocal,
            IReadOnlyList<Regex> templatePatterns = null)
        {
            if (!IsServerLine(line))
            {
                return null;
            }

            // The message follows the line's "]: " header; patterns must not see the header's brackets.
            var headerEnd = line.IndexOf("]: ", line.IndexOf(ServerThreadMarker, StringComparison.Ordinal), StringComparison.Ordinal);
            var message = headerEnd < 0 ? line : line.Substring(headerEnd + 3);
            if (message.StartsWith("System chat: ", StringComparison.Ordinal))
            {
                message = message.Substring("System chat: ".Length);
            }

            var match = (templatePatterns ?? Array.Empty<Regex>())
                .Concat(new[] { FallbackAnnouncement })
                .Select(pattern => pattern.Match(message))
                .FirstOrDefault(candidate => candidate.Success);
            if (match == null)
            {
                return null;
            }

            if (playerNames != null && playerNames.Count > 0 && !playerNames.Contains(match.Groups["player"].Value))
            {
                return null;
            }

            return (match.Groups["title"].Value.Trim(), ParseTime(line, nowLocal));
        }

        /// <summary>
        /// The row for a title: the advancement whose title in the game's language it is, when
        /// exactly one has it; otherwise the instance's cached row with that display title, when
        /// exactly one has it. Null when the title is unknown or shared.
        /// </summary>
        internal static string ResolveApiName(MinecraftLogTarget target, string title)
        {
            var language = target.Language?.Value;
            if (language != null && language.IdsByTitle.TryGetValue(title, out var defined))
            {
                return defined.Count == 1 ? target.InstanceKey + "|" + defined[0] : null;
            }

            return target.ApiNamesByTitle.TryGetValue(title, out var cached) && cached.Count == 1
                ? cached[0]
                : null;
        }

        private static bool IsServerLine(string line)
        {
            return line != null && line.IndexOf(ServerThreadMarker, StringComparison.Ordinal) >= 0;
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
        /// Complete lines written since the target's offset; a partial last line is left for the
        /// next read. On the first read, a log written during this session (the game starts a new
        /// one at launch) is read from its start and an older one from its end. A log shorter than
        /// the offset was rotated and is read from its start.
        /// </summary>
        private static IEnumerable<string> ReadNewLines(MinecraftLogTarget target, DateTime sessionStartUtc)
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
                    if (target.Offset < 0)
                    {
                        // Without a known session start, never replay: an older session's lines
                        // could notify again.
                        target.Offset = sessionStartUtc != default(DateTime) &&
                                        File.GetLastWriteTimeUtc(target.LogPath) >= sessionStartUtc
                            ? 0
                            : stream.Length;
                    }

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
    }
}
