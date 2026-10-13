using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Vita3K
{
    /// <summary>
    /// The Vita3K layout one refresh reads: the emulated filesystem root and the single
    /// active user whose trophies are scanned. Progress is never combined across users.
    /// </summary>
    internal sealed class Vita3KInstallationContext
    {
        public string EmulatorRoot { get; private set; }
        public string DataRoot { get; private set; }
        public string UserId { get; private set; }
        public string UserIdSource { get; private set; }

        public string AppRoot => Path.Combine(DataRoot, "ux0", "app");
        public string TrophyConfRoot => Path.Combine(DataRoot, "ux0", "user", UserId, "trophy", "conf");
        public string TrophyDataRoot => Path.Combine(DataRoot, "ux0", "user", UserId, "trophy", "data");

        public string CacheKey => (DataRoot ?? string.Empty).ToUpperInvariant() + "|" + UserId;

        public string GetConfFolder(string npCommId) => Path.Combine(TrophyConfRoot, npCommId);

        public string GetProgressPath(string npCommId) => Path.Combine(TrophyDataRoot, npCommId, "TROPUSR.DAT");

        public string GetAppTrophyFolder(string titleId) => Path.Combine(AppRoot, titleId, "sce_sys", "trophy");

        /// <summary>
        /// Resolves the layout for a Vita3K install folder (the folder holding Vita3K.exe).
        /// <paramref name="defaultDataRoot"/> replaces %APPDATA%\Vita3K\Vita3K in tests.
        /// Returns null when no data root or user can be resolved.
        /// </summary>
        internal static Vita3KInstallationContext Create(string emulatorRoot, ILogger logger, string defaultDataRoot = null)
        {
            if (string.IsNullOrWhiteSpace(emulatorRoot) || !Directory.Exists(emulatorRoot))
            {
                return null;
            }

            var dataRoot = ResolveDataRoot(emulatorRoot, defaultDataRoot, out var configPath);
            if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            {
                logger?.Debug($"[Vita3K] No emulated filesystem found for install '{emulatorRoot}' (resolved '{dataRoot}').");
                return null;
            }

            var configuredUserId = Vita3KConfigReader.ReadValue(configPath, "user-id");
            var userId = ResolveUserId(dataRoot, configuredUserId, out var userIdSource);
            if (userId == null)
            {
                logger?.Warn($"[Vita3K] No user profile exists under '{Path.Combine(dataRoot, "ux0", "user")}'. Trophy progress was not scanned.");
                return null;
            }

            return new Vita3KInstallationContext
            {
                EmulatorRoot = NormalizePath(emulatorRoot),
                DataRoot = NormalizePath(dataRoot),
                UserId = userId,
                UserIdSource = userIdSource
            };
        }

        /// <summary>
        /// Mirrors Vita3K's init_paths (vita3k/app/src/app_init.cpp): a portable folder next to
        /// the executable holds config.yml and the filesystem under portable\fs and ignores
        /// pref-path; otherwise config.yml sits next to the executable and a non-empty pref-path
        /// replaces the default %APPDATA%\Vita3K\Vita3K root.
        /// </summary>
        internal static string ResolveDataRoot(string emulatorRoot, string defaultDataRoot, out string configPath)
        {
            var portableRoot = Path.Combine(emulatorRoot, "portable");
            if (Directory.Exists(portableRoot))
            {
                configPath = Path.Combine(portableRoot, "config.yml");
                return Path.Combine(portableRoot, "fs");
            }

            configPath = Path.Combine(emulatorRoot, "config.yml");
            var prefPath = Vita3KConfigReader.ReadValue(configPath, "pref-path");
            if (!string.IsNullOrWhiteSpace(prefPath))
            {
                return prefPath;
            }

            return defaultDataRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vita3K",
                "Vita3K");
        }

        /// <summary>
        /// Uses the configured user-id when its folder exists; otherwise the alphabetically
        /// first user folder, which is the user Vita3K itself falls back to (app.cpp init_user).
        /// </summary>
        internal static string ResolveUserId(string dataRoot, string configuredUserId, out string source)
        {
            source = null;
            var usersRoot = Path.Combine(dataRoot, "ux0", "user");
            if (!Directory.Exists(usersRoot))
            {
                return null;
            }

            var configured = configuredUserId?.Trim();
            if (!string.IsNullOrWhiteSpace(configured) &&
                configured.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                Directory.Exists(Path.Combine(usersRoot, configured)))
            {
                source = "config.yml user-id";
                return configured;
            }

            List<string> userIds;
            try
            {
                userIds = Directory.GetDirectories(usersRoot)
                    .Where(directory => File.Exists(Path.Combine(directory, "user.xml")))
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            if (userIds.Count == 0)
            {
                return null;
            }

            source = "first user profile";
            return userIds[0];
        }

        internal static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }

    /// <summary>
    /// Reads top-level scalar keys from Vita3K's config.yml (written by yaml-cpp as
    /// "key: value" lines, with values optionally quoted).
    /// </summary>
    internal static class Vita3KConfigReader
    {
        public static string ReadValue(string configPath, string key)
        {
            if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(key) || !File.Exists(configPath))
            {
                return null;
            }

            try
            {
                foreach (var rawLine in File.ReadLines(configPath))
                {
                    if (string.IsNullOrEmpty(rawLine) || char.IsWhiteSpace(rawLine[0]) || rawLine[0] == '#')
                    {
                        continue;
                    }

                    var separator = rawLine.IndexOf(':');
                    if (separator <= 0 ||
                        !string.Equals(rawLine.Substring(0, separator).Trim(), key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return Unquote(rawLine.Substring(separator + 1).Trim());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            return null;
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");
            }

            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            var trimmed = (comment >= 0 ? value.Substring(0, comment) : value).Trim();
            return trimmed.Length == 0 || trimmed == "~" ? null : trimmed;
        }
    }

    /// <summary>
    /// Resolves a game's Vita3K install in a fixed order: the game's Vita3K emulator action,
    /// then the configured executables in order, then the single Vita3K emulator registered
    /// in Playnite. It does not guess between several registered installs.
    /// </summary>
    internal static class Vita3KInstallationResolver
    {
        internal static Vita3KInstallationContext Resolve(Game game, Vita3KSettings settings, IPlayniteAPI playniteApi, ILogger logger)
        {
            var actionRoot = ResolveGameActionRoot(game, playniteApi);
            if (actionRoot != null)
            {
                var actionContext = Vita3KInstallationContext.Create(actionRoot, logger);
                if (actionContext != null)
                {
                    return actionContext;
                }
            }

            foreach (var root in GetSettingsRoots(settings))
            {
                var context = Vita3KInstallationContext.Create(root, logger);
                if (context != null)
                {
                    return context;
                }
            }

            var registeredRoot = ResolveUniqueRegisteredRoot(playniteApi, logger);
            return registeredRoot == null ? null : Vita3KInstallationContext.Create(registeredRoot, logger);
        }

        internal static bool IsVita3KEmulator(Emulator emulator)
        {
            var builtInId = emulator?.BuiltInConfigId ?? string.Empty;
            var name = emulator?.Name ?? string.Empty;
            var installDir = emulator?.InstallDir ?? string.Empty;
            return builtInId.IndexOf("vita3k", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("vita3k", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   installDir.IndexOf("vita3k", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The Vita3K emulator actions of a game, play actions first.
        /// </summary>
        internal static List<GameAction> GetVita3KActions(Game game, IPlayniteAPI playniteApi)
        {
            return (game?.GameActions ?? Enumerable.Empty<GameAction>())
                .Where(action => action?.Type == GameActionType.Emulator &&
                    action.EmulatorId != Guid.Empty &&
                    IsVita3KEmulator(playniteApi?.Database?.Emulators?.Get(action.EmulatorId)))
                .OrderByDescending(action => action.IsPlayAction)
                .ToList();
        }

        private static string ResolveGameActionRoot(Game game, IPlayniteAPI playniteApi)
        {
            return GetVita3KActions(game, playniteApi)
                .Select(action => playniteApi?.Database?.Emulators?.Get(action.EmulatorId)?.InstallDir)
                .FirstOrDefault(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root));
        }

        private static IEnumerable<string> GetSettingsRoots(Vita3KSettings settings)
        {
            return ProviderPathList.Normalize(settings?.ExecutablePaths)
                .Select(GetInstallRoot)
                .Where(root => root != null);
        }

        private static string GetInstallRoot(string executablePath)
        {
            string root;
            try
            {
                root = Path.GetDirectoryName(executablePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
        }

        /// <summary>
        /// Settings-row check: the executable's folder exists and its active user has a trophy folder.
        /// </summary>
        internal static ProviderPathValidation ValidateExecutablePath(string executablePath)
        {
            var root = GetInstallRoot(executablePath);
            if (root == null)
            {
                return ProviderPathValidation.Invalid("LOCPlayAch_InvalidPath");
            }

            var context = Vita3KInstallationContext.Create(root, logger: null);
            return context != null && Directory.Exists(Path.GetDirectoryName(context.TrophyConfRoot))
                ? ProviderPathValidation.Valid
                : ProviderPathValidation.Invalid("LOCPlayAch_Rpcs3Validation_NoTrophyFolder");
        }

        private static string ResolveUniqueRegisteredRoot(IPlayniteAPI playniteApi, ILogger logger)
        {
            var roots = (playniteApi?.Database?.Emulators ?? Enumerable.Empty<Emulator>())
                .Where(IsVita3KEmulator)
                .Select(emulator => emulator.InstallDir)
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Select(Vita3KInstallationContext.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count > 1)
            {
                logger?.Warn($"[Vita3K] Multiple registered Vita3K installations were found: [{string.Join(", ", roots)}]. Configure Vita3K or select a game action to resolve trophy progress.");
                return null;
            }

            return roots.Count == 1 ? roots[0] : null;
        }
    }
}
