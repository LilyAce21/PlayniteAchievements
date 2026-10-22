using Playnite.SDK;
using PlayniteAchievements.Providers.PlayStationTrophies;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Vita3K
{
    /// <summary>
    /// Finds a PS Vita title id for a game and the trophy sets the installed app ships.
    /// Vita3K itself never maps a title id to an NP communication id (the game passes it
    /// at runtime), so the trophy set folders under ux0/app/&lt;TITLEID&gt;/sce_sys/trophy
    /// are the mapping.
    /// </summary>
    internal static class Vita3KTitleIdResolver
    {
        // Vita3K's -r/--installed-path takes the app folder name under ux0/app (config.cpp).
        private static readonly Regex InstalledPathArgumentPattern = new Regex(
            @"(?:^|\s)(?:-r|--installed-path)(?:\s+|=)[""']?(?<id>[A-Za-z]{4}\d{5})[""']?(?=\s|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TitleIdInPathPattern = new Regex(
            @"(?<![A-Za-z0-9])(?<id>[A-Za-z]{4}\d{5})(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string FromArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            var match = InstalledPathArgumentPattern.Match(arguments);
            return match.Success ? Vita3KMatchIdHelper.NormalizeTitleId(match.Groups["id"].Value) : null;
        }

        /// <summary>
        /// The last title-id-shaped path segment token, so ux0\app\PCSE00001\eboot.bin and
        /// "Game (PCSE00001).vpk" both resolve while a parent folder name does not win.
        /// </summary>
        public static string FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var matches = TitleIdInPathPattern.Matches(path);
            return matches.Count == 0
                ? null
                : Vita3KMatchIdHelper.NormalizeTitleId(matches[matches.Count - 1].Groups["id"].Value);
        }

        /// <summary>
        /// Reads TITLE_ID from sce_sys/param.sfo inside a .vpk (a zip archive).
        /// Returns null when the file is not a readable .vpk; never throws.
        /// </summary>
        public static string FromVpk(string vpkPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(vpkPath) ||
                !vpkPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(vpkPath))
            {
                return null;
            }

            try
            {
                using (var archive = ZipFile.OpenRead(vpkPath))
                {
                    var entry = archive.Entries.FirstOrDefault(candidate =>
                        string.Equals(candidate.FullName.Replace('\\', '/'), "sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase));
                    if (entry == null || entry.Length <= 0 || entry.Length > 1024 * 1024)
                    {
                        return null;
                    }

                    using (var stream = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        var values = ParamSfoReader.ReadStringValues(memory.ToArray(), logger);
                        return values.TryGetValue("TITLE_ID", out var titleId)
                            ? Vita3KMatchIdHelper.NormalizeTitleId(titleId)
                            : null;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                logger?.Debug(ex, $"[Vita3K] Failed to read param.sfo from '{vpkPath}'");
                return null;
            }
        }

        /// <summary>
        /// The NP communication ids an installed app ships trophy sets for, sorted.
        /// </summary>
        public static List<string> GetAppNpCommIds(Vita3KInstallationContext installation, string titleId)
        {
            var result = new List<string>();
            if (installation == null || string.IsNullOrWhiteSpace(titleId))
            {
                return result;
            }

            var trophyFolder = installation.GetAppTrophyFolder(titleId);
            if (!Directory.Exists(trophyFolder))
            {
                return result;
            }

            try
            {
                result.AddRange(Directory.GetDirectories(trophyFolder)
                    .Select(directory => Vita3KMatchIdHelper.NormalizeNpCommId(Path.GetFileName(directory)))
                    .Where(npCommId => npCommId != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(npCommId => npCommId, StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            return result;
        }
    }
}
