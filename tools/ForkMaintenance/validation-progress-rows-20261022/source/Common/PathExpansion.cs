using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Common
{
    public sealed class FolderPathVariable
    {
        public FolderPathVariable(string token, string description) { Token = token; Description = description; }
        public string Token { get; }
        public string Description { get; }
    }

    public static class FolderPathVariables
    {
        public static IReadOnlyList<FolderPathVariable> All { get; } = new[]
        {
            new FolderPathVariable("{InstallDir}", "Game installation folder configured in Playnite."),
            new FolderPathVariable("{GameDirName}", "Name of the game installation folder."),
            new FolderPathVariable("{PlayniteDir}", "Playnite application folder."),
            new FolderPathVariable("{EmulatorDir}", "Installation folder of the game's configured emulator."),
            new FolderPathVariable("{SteamAppId}", "Resolved Steam App ID, including the game's Local override."),
            new FolderPathVariable("{GameName}", "Playnite game title, with invalid folder characters replaced by underscores."),
            new FolderPathVariable("{ExecutableDir}", "Folder containing the first configured file-based Play action's executable."),
            new FolderPathVariable("{RomDir}", "Folder containing the first ROM configured in Playnite."),
            new FolderPathVariable("{GameId}", "Unique Playnite game ID (GUID).")
        };
    }

    /// <summary>
    /// Utility class for expanding path variables in game paths.
    /// </summary>
    internal static class PathExpansion
    {
        internal static bool IsCustomFolderTemplate(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            FolderPathVariables.All.Any(variable => path.IndexOf(variable.Token, StringComparison.OrdinalIgnoreCase) >= 0);

        internal static bool UsesGameFolderVariables(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            FolderPathVariables.All.Where(variable => variable.Token != "{PlayniteDir}")
                .Any(variable => path.IndexOf(variable.Token, StringComparison.OrdinalIgnoreCase) >= 0);

        internal static string ExpandFolderTemplate(string path, string installDir, string playniteDir, string emulatorDir,
            string steamAppId = null, string gameName = null, string executableDir = null, string romDir = null, string gameId = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var missing = false;
            var expanded = Regex.Replace(path.Trim(), @"\{(InstallDir|GameDirName|PlayniteDir|EmulatorDir|SteamAppId|GameName|ExecutableDir|RomDir|GameId)\}", match =>
            {
                string value;
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "installdir": value = installDir; break;
                    case "gamedirname":
                        value = string.IsNullOrWhiteSpace(installDir) ? null :
                            Path.GetFileName(installDir.TrimEnd('\\', '/'));
                        break;
                    case "playnitedir": value = playniteDir; break;
                    case "steamappid": value = steamAppId; break;
                    case "gamename":
                        value = string.IsNullOrWhiteSpace(gameName) ? null : new string(gameName.Select(
                            ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).TrimEnd(' ', '.');
                        break;
                    case "executabledir": value = executableDir; break;
                    case "romdir": value = romDir; break;
                    case "gameid": value = gameId; break;
                    default: value = emulatorDir; break;
                }
                if (string.IsNullOrWhiteSpace(value)) missing = true;
                return value ?? string.Empty;
            }, RegexOptions.IgnoreCase);
            expanded = Environment.ExpandEnvironmentVariables(expanded);
            if (missing || expanded.Contains("{") || expanded.Contains("}")) return null;
            try
            {
                return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : expanded;
            }
            catch
            {
                return null;
            }
        }

        internal static IEnumerable<string> ExpandCustomFolderPaths(IPlayniteAPI api, Game game, string path,
            Func<Game, string> resolveSteamAppId = null)
        {
            if (!UsesGameFolderVariables(path))
            {
                var expanded = ExpandFolderTemplate(path, null, api?.Paths?.ApplicationPath, null);
                if (!string.IsNullOrWhiteSpace(expanded)) yield return expanded;
                yield break;
            }

            var games = game != null ? new List<Game> { game } :
                api?.Database?.Games?.ToList() ?? new List<Game>();
            foreach (var context in games)
            {
                var installDir = ExpandGamePath(api, context, context.InstallDirectory);
                var emulatorDir = GetEmulatorInstallDir(api, context);
                installDir = ExpandFolderTemplate(installDir, null, api?.Paths?.ApplicationPath, null);
                emulatorDir = ExpandFolderTemplate(emulatorDir, null, api?.Paths?.ApplicationPath, null);
                string ResolveLaunchPath(string value, string baseDirectory)
                {
                    try
                    {
                        value = ExpandGamePath(api, context, value);
                        value = ExpandFolderTemplate(value, installDir, api?.Paths?.ApplicationPath, emulatorDir);
                        if (string.IsNullOrWhiteSpace(value)) return null;
                        if (!Path.IsPathRooted(value))
                        {
                            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
                            value = Path.Combine(baseDirectory, value);
                        }
                        return Path.GetFullPath(value);
                    }
                    catch { return null; }
                }
                string executableDir = null;
                string romDir = null;
                if (path.IndexOf("{ExecutableDir}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var action = context.GameActions?.FirstOrDefault(item => item != null && item.IsPlayAction && item.Type == GameActionType.File);
                    if (action != null)
                    {
                        var workingDir = ResolveLaunchPath(action.WorkingDir, installDir) ?? installDir;
                        var executable = ResolveLaunchPath(action.Path, workingDir);
                        if (executable != null) executableDir = Path.GetDirectoryName(executable);
                    }
                }
                if (path.IndexOf("{RomDir}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var rom = ResolveLaunchPath(context.Roms?.FirstOrDefault()?.Path, installDir);
                    if (rom != null) romDir = Path.GetDirectoryName(rom);
                }
                string steamAppId = null;
                if (path.IndexOf("{SteamAppId}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    steamAppId = resolveSteamAppId != null ? resolveSteamAppId(context) :
                        int.TryParse(context.GameId, out var numericId) && numericId > 0 ? context.GameId : null;
                }
                var expanded = ExpandFolderTemplate(path, installDir, api?.Paths?.ApplicationPath, emulatorDir,
                    steamAppId, context.Name, executableDir, romDir,
                    context.Id == Guid.Empty ? null : context.Id.ToString());
                if (!string.IsNullOrWhiteSpace(expanded)) yield return expanded;
            }
        }

        /// <summary>
        /// Expands path variables in game paths using Playnite's variable expansion.
        /// Handles variables like {InstallDir}, {Name}, {EmulatorDir}, etc.
        /// </summary>
        public static string ExpandGamePath(IPlayniteAPI playniteApi, Game game, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            try
            {
                var expanded = path;

                // Handle {EmulatorDir} BEFORE Playnite's expansion, which may strip it to empty string
                if (path.IndexOf("{EmulatorDir}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var emulatorDir = GetEmulatorInstallDir(playniteApi, game);
                    if (!string.IsNullOrWhiteSpace(emulatorDir))
                    {
                        expanded = ReplaceInsensitive(expanded, "{EmulatorDir}", emulatorDir);
                    }
                }

                // Use Playnite's built-in variable expansion for remaining variables
                expanded = playniteApi?.ExpandGameVariables(game, expanded) ?? expanded;

                // Handle additional custom variables
                if (expanded.IndexOf("{InstallDir}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var installDir = game?.InstallDirectory;
                    if (!string.IsNullOrWhiteSpace(installDir))
                    {
                        expanded = ReplaceInsensitive(expanded, "{InstallDir}", installDir);
                    }
                }

                return expanded;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Gets the emulator install directory from the game's emulator action.
        /// </summary>
        private static string GetEmulatorInstallDir(IPlayniteAPI playniteApi, Game game)
        {
            if (game?.GameActions == null || playniteApi?.Database?.Emulators == null)
            {
                return null;
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                {
                    var emulator = playniteApi.Database.Emulators.Get(action.EmulatorId);
                    if (emulator != null && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                    {
                        return emulator.InstallDir;
                    }
                }
            }

            return null;
        }

        private static string ReplaceInsensitive(string input, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            {
                return input;
            }

            var idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return input;
            }

            var sb = new StringBuilder(input.Length);
            var start = 0;
            while (idx >= 0)
            {
                sb.Append(input.Substring(start, idx - start));
                sb.Append(newValue ?? string.Empty);
                start = idx + oldValue.Length;
                idx = input.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            }

            sb.Append(input.Substring(start));
            return sb.ToString();
        }
    }
}
