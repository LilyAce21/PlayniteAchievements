using Microsoft.Win32;
using Playnite.SDK;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam.Local
{
    internal static class SteamInstallLocator
    {
        public static string ResolveSteamPath(string overridePath, ILogger logger = null)
        {
            var configured = Normalize(overridePath);
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                return configured;
            }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    return Normalize(key?.GetValue("SteamPath") as string);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamLocal] Failed to resolve the Steam install path.");
                return null;
            }
        }

        public static string BuildUserGameStatsPath(string steamPath, uint accountId3, int appId)
        {
            return string.IsNullOrWhiteSpace(steamPath) || appId <= 0
                ? null
                : Path.Combine(
                    steamPath,
                    "appcache",
                    "stats",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "UserGameStats_{0}_{1}.bin",
                        accountId3,
                        appId));
        }

        public static string BuildSchemaPath(string steamPath, int appId)
        {
            return string.IsNullOrWhiteSpace(steamPath) || appId <= 0
                ? null
                : Path.Combine(
                    steamPath,
                    "appcache",
                    "stats",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "UserGameStatsSchema_{0}.bin",
                        appId));
        }

        /// <summary>
        /// Finds the local stats and schema files for a game across <b>every</b> Steam install on the
        /// machine (for example one on C: and a second one on E:). The install the game itself lives
        /// in is tried first, then the configured override, the registry install and any Steam folder
        /// at the root of a fixed drive. Returns false when no install holds the stats yet.
        /// </summary>
        public static bool TryResolveStatsFiles(
            string overridePath,
            string gameInstallDirectory,
            uint accountId3,
            int appId,
            out string statsPath,
            out string schemaPath,
            ILogger logger = null)
        {
            statsPath = null;
            schemaPath = null;
            if (appId <= 0)
            {
                return false;
            }

            var roots = new List<string>();
            void Add(string root)
            {
                root = Normalize(root);
                if (!string.IsNullOrWhiteSpace(root) &&
                    Directory.Exists(Path.Combine(root, "appcache", "stats")) &&
                    !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(root);
                }
            }

            Add(FindSteamRootFromGameFolder(gameInstallDirectory));
            Add(overridePath);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    Add(key?.GetValue("SteamPath") as string);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamLocal] Failed to read the registry Steam path.");
            }

            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    Add(Path.Combine(drive.Name, "Steam"));
                    Add(Path.Combine(drive.Name, "steam"));
                    Add(Path.Combine(drive.Name, "Program Files (x86)", "Steam"));
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamLocal] Failed to enumerate drives for Steam installs.");
            }

            string fallbackStats = null;
            foreach (var root in roots)
            {
                var exact = BuildUserGameStatsPath(root, accountId3, appId);
                if (File.Exists(exact))
                {
                    statsPath = exact;
                    schemaPath = FindSchema(roots, root, appId);
                    return schemaPath != null;
                }

                if (fallbackStats == null)
                {
                    // Same game, but written under another Steam account id: use the newest one.
                    try
                    {
                        fallbackStats = Directory
                            .GetFiles(Path.Combine(root, "appcache", "stats"), "UserGameStats_*_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                    }
                    catch
                    {
                    }
                }
            }

            if (fallbackStats != null)
            {
                statsPath = fallbackStats;
                var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(fallbackStats)));
                schemaPath = FindSchema(roots, root, appId);
                return schemaPath != null;
            }

            // The game has not written any stats yet (no unlocks so far): watch where the file will
            // appear, in the first install that already holds this game's schema.
            foreach (var root in roots)
            {
                var schema = BuildSchemaPath(root, appId);
                if (File.Exists(schema))
                {
                    statsPath = BuildUserGameStatsPath(root, accountId3, appId);
                    schemaPath = schema;
                    return true;
                }
            }

            return false;
        }

        private static string FindSchema(List<string> roots, string preferredRoot, int appId)
        {
            foreach (var root in new[] { preferredRoot }.Concat(roots))
            {
                var path = BuildSchemaPath(root, appId);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>Walks up from a game folder to the Steam folder that owns its steamapps library.</summary>
        private static string FindSteamRootFromGameFolder(string gameFolder)
        {
            try
            {
                var dir = new DirectoryInfo(Normalize(gameFolder) ?? string.Empty);
                while (dir != null && dir.Exists)
                {
                    if (string.Equals(dir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        return dir.Parent?.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch
            {
                return path.Trim().Trim('"');
            }
        }
    }
}
