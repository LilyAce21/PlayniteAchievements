using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// The folders a Playnite game's advancements come from.
    /// </summary>
    internal sealed class MinecraftGameSource
    {
        /// <summary>Game folders (holding saves/ and mods/), used to find the game jar and mods.</summary>
        public List<string> GameFolders { get; set; } = new List<string>();

        /// <summary>World folders whose advancement progress is merged.</summary>
        public List<string> Worlds { get; set; } = new List<string>();

        public string ResolvedBy { get; set; }
    }

    /// <summary>
    /// Maps a Playnite game to Minecraft folders, first match wins: the per-game folder override,
    /// a Prism/MultiMC/PolyMC launch action, the game's install folder, then (for games named
    /// Minecraft) the configured game folders or %AppData%\.minecraft.
    /// </summary>
    internal static class MinecraftGameResolver
    {
        private static readonly string[] InstanceLaunchers = { "prismlauncher", "multimc", "polymc" };

        // Titles that contain "Minecraft" but are not Java Edition.
        private static readonly string[] NonJavaTitleTokens =
        {
            "hypixel", "dungeons", "legends", "story mode", "bedrock", "education", "for windows",
            "pocket edition", "earth", "windows 10"
        };

        public static bool IsJavaTitle(string name)
        {
            var value = (name ?? string.Empty).ToLowerInvariant();
            return value.Contains("minecraft") && !NonJavaTitleTokens.Any(value.Contains);
        }

        public static MinecraftGameSource Resolve(Game game, MinecraftSettings settings, IPlayniteAPI playniteApi)
        {
            if (game == null)
            {
                return null;
            }

            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, MinecraftDataProvider.Key, out var overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                return FromFolder(PathExpansion.ExpandGamePath(playniteApi, game, overridePath.Trim()), "folder override");
            }

            foreach (var instanceFolder in EnumerateLauncherInstances(game, playniteApi))
            {
                var source = FromFolder(instanceFolder, "launcher instance");
                if (source != null)
                {
                    return source;
                }
            }

            var installDirectory = PathExpansion.ExpandGamePath(playniteApi, game, game.InstallDirectory);
            var installFolder = MinecraftPaths.ResolveGameFolder(installDirectory);
            if (installFolder != null && MinecraftPaths.HasAdvancementWorld(installFolder))
            {
                return FromGameFolders(new[] { installFolder }, "install folder");
            }

            if (!IsJavaTitle(game.Name))
            {
                return null;
            }

            var configured = ProviderPathList.Normalize(settings?.GameFolders)
                .Select(MinecraftPaths.ResolveGameFolder)
                .Where(folder => folder != null)
                .ToList();
            if (configured.Count > 0)
            {
                return FromGameFolders(configured, "configured game folders");
            }

            var defaultFolder = MinecraftPaths.ResolveGameFolder(MinecraftPaths.DefaultGameFolder);
            return defaultFolder == null ? null : FromGameFolders(new[] { defaultFolder }, "default .minecraft");
        }

        /// <summary>
        /// A world folder, or a .minecraft folder or launcher instance. Null when neither.
        /// </summary>
        internal static MinecraftGameSource FromFolder(string folder, string resolvedBy)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            if (MinecraftPaths.IsWorldFolder(folder))
            {
                var world = Path.GetFullPath(folder).TrimEnd('\\', '/');
                var saves = Path.GetDirectoryName(world);
                var gameFolder = string.Equals(Path.GetFileName(saves), "saves", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(saves)
                    : world;
                return new MinecraftGameSource
                {
                    GameFolders = new List<string> { gameFolder },
                    Worlds = new List<string> { world },
                    ResolvedBy = resolvedBy
                };
            }

            var resolved = MinecraftPaths.ResolveGameFolder(folder);
            return resolved == null ? null : FromGameFolders(new[] { resolved }, resolvedBy);
        }

        private static MinecraftGameSource FromGameFolders(IEnumerable<string> gameFolders, string resolvedBy)
        {
            var folders = gameFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new MinecraftGameSource
            {
                GameFolders = folders,
                Worlds = folders.SelectMany(MinecraftPaths.EnumerateWorlds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ResolvedBy = resolvedBy
            };
        }

        /// <summary>
        /// Instance folders named by <c>-l</c>/<c>--launch</c> on a Prism, MultiMC or PolyMC action.
        /// </summary>
        internal static IEnumerable<string> EnumerateLauncherInstances(Game game, IPlayniteAPI playniteApi)
        {
            foreach (var action in game?.GameActions ?? Enumerable.Empty<GameAction>())
            {
                var executable = PathExpansion.ExpandGamePath(playniteApi, game, action?.Path);
                var launcher = string.IsNullOrWhiteSpace(executable)
                    ? null
                    : Path.GetFileNameWithoutExtension(executable)?.ToLowerInvariant();
                if (launcher == null || !InstanceLaunchers.Contains(launcher))
                {
                    continue;
                }

                var instanceId = ParseLaunchInstanceId(action.Arguments);
                if (instanceId == null)
                {
                    continue;
                }

                foreach (var instances in EnumerateInstanceFolders(launcher, Path.GetDirectoryName(executable)))
                {
                    var instanceFolder = Path.Combine(instances, instanceId);
                    if (Directory.Exists(instanceFolder))
                    {
                        yield return instanceFolder;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateInstanceFolders(string launcher, string executableFolder)
        {
            var dataFolders = new List<string>();
            if (!string.IsNullOrWhiteSpace(executableFolder))
            {
                dataFolders.Add(executableFolder);
            }

            dataFolders.Add(Path.Combine(MinecraftPaths.AppData, "PrismLauncher"));
            dataFolders.Add(Path.Combine(MinecraftPaths.AppData, "PolyMC"));

            foreach (var dataFolder in dataFolders)
            {
                var configured = ReadConfiguredInstanceFolder(dataFolder, launcher);
                if (configured != null)
                {
                    yield return configured;
                }

                yield return Path.Combine(dataFolder, "instances");
            }
        }

        /// <summary>The InstanceDir from the launcher's .cfg, resolved against its data folder.</summary>
        private static string ReadConfiguredInstanceFolder(string dataFolder, string launcher)
        {
            var cfg = Path.Combine(dataFolder, launcher + ".cfg");
            try
            {
                if (!File.Exists(cfg))
                {
                    return null;
                }

                var line = File.ReadLines(cfg)
                    .FirstOrDefault(text => text.StartsWith("InstanceDir=", StringComparison.OrdinalIgnoreCase));
                var value = line?.Substring("InstanceDir=".Length).Trim();
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                return Path.IsPathRooted(value) ? value : Path.Combine(dataFolder, value);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>The instance id after <c>-l</c>, <c>--launch</c> or <c>--launch=</c>; null when absent.</summary>
        internal static string ParseLaunchInstanceId(string arguments)
        {
            var tokens = Tokenize(arguments);
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.StartsWith("--launch=", StringComparison.Ordinal))
                {
                    return NullIfBlank(token.Substring("--launch=".Length));
                }

                if ((token == "-l" || token == "--launch") && i + 1 < tokens.Count)
                {
                    return NullIfBlank(tokens[i + 1]);
                }
            }

            return null;
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static List<string> Tokenize(string arguments)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            foreach (var c in arguments ?? string.Empty)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }
    }
}
