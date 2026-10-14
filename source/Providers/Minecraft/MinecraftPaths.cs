using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Folder layout of the official launcher, Prism/MultiMC/PolyMC, CurseForge and Modrinth:
    /// game folders, worlds, the game jar for an instance, and vanilla translations.
    /// </summary>
    internal static class MinecraftPaths
    {
        // Version jars smaller than this are loader stubs (Fabric, Quilt), not the game.
        private const long MinGameJarBytes = 1024 * 1024;

        private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> AssetLanguageCache =
            new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>%AppData%\.minecraft.</summary>
        public static string DefaultGameFolder => Path.Combine(AppData, ".minecraft");

        // Where launchers keep their instances, relative to the launcher's data folder: Prism,
        // MultiMC, PolyMC, ATLauncher, FTB App and GDLauncher use instances/ (CurseForge Instances/),
        // Modrinth profiles/, Technic modpacks/, GDLauncher Carbon data/instances/.
        private static readonly string[] InstanceContainers =
        {
            "instances",
            "profiles",
            "modpacks",
            Path.Combine("data", "instances")
        };

        // The game folder inside an instance, when it is not the instance itself.
        private static readonly string[] GameFolderChildren = { ".minecraft", "minecraft", "instance" };

        public static IEnumerable<string> DefaultLauncherDataFolders()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return DefaultGameFolder;
            yield return Path.Combine(AppData, "PrismLauncher");
            yield return Path.Combine(AppData, "PolyMC");
            yield return Path.Combine(AppData, "ModrinthApp");
            yield return Path.Combine(AppData, "ATLauncher");
            yield return Path.Combine(AppData, "gdlauncher_next");
            yield return Path.Combine(AppData, "gdlauncher_carbon");
            yield return Path.Combine(AppData, ".technic");
            yield return Path.Combine(localAppData, ".ftba");
            yield return Path.Combine(userProfile, "curseforge", "minecraft");
        }

        /// <summary>The folder and up to <paramref name="levels"/> of its parents.</summary>
        public static IEnumerable<string> SelfAndAncestors(string folder, int levels)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                yield break;
            }

            DirectoryInfo current;
            try
            {
                current = new DirectoryInfo(folder);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                yield break;
            }

            for (var level = 0; current != null && level <= levels; level++)
            {
                yield return current.FullName.TrimEnd('\\', '/');
                current = current.Parent;
            }
        }

        /// <summary>
        /// The game folder (the one holding saves/) for a .minecraft folder or a launcher instance:
        /// the folder itself, or its .minecraft, minecraft or instance child. Null when none has saves/.
        /// </summary>
        public static string ResolveGameFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            foreach (var candidate in new[] { folder }.Concat(GameFolderChildren.Select(child => Path.Combine(folder, child))))
            {
                if (Directory.Exists(Path.Combine(candidate, "saves")))
                {
                    return Path.GetFullPath(candidate).TrimEnd('\\', '/');
                }
            }

            return null;
        }

        /// <summary>
        /// Game folders for a configured folder: the folder itself when it is a .minecraft folder or
        /// an instance, otherwise every instance in a launcher data folder (see
        /// <see cref="InstanceContainers"/>) or directly under it (an instances folder itself).
        /// </summary>
        public static List<string> ExpandGameFolders(string folder)
        {
            var single = ResolveGameFolder(folder);
            if (single != null)
            {
                return new List<string> { single };
            }

            if (string.IsNullOrWhiteSpace(folder))
            {
                return new List<string>();
            }

            var containers = InstanceContainers
                .Select(container => Path.Combine(folder, container))
                .Where(Directory.Exists)
                .ToList();
            if (containers.Count == 0)
            {
                containers.Add(folder);
            }

            return containers
                .SelectMany(SafeDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(ResolveGameFolder)
                .Where(path => path != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The instance folder for a game folder: its parent when the game folder is an instance's
        /// .minecraft/minecraft/instance child, otherwise the game folder itself.
        /// </summary>
        private static string GetInstanceFolder(string gameFolder)
        {
            var trimmed = (gameFolder ?? string.Empty).TrimEnd('\\', '/');
            var leaf = Path.GetFileName(trimmed);
            var isChild = GameFolderChildren.Contains(leaf, StringComparer.OrdinalIgnoreCase) &&
                          !string.Equals(trimmed, DefaultGameFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            return isChild ? Path.GetDirectoryName(trimmed) ?? trimmed : trimmed;
        }

        /// <summary>The instance folder's name, which keys the instance's rows.</summary>
        public static string GetInstanceKey(string gameFolder)
        {
            var name = Path.GetFileName(GetInstanceFolder(gameFolder));
            return string.IsNullOrWhiteSpace(name) ? ".minecraft" : name;
        }

        /// <summary>
        /// The name the launcher shows for an instance: Prism/MultiMC instance.cfg, CurseForge
        /// minecraftinstance.json, Modrinth profile.json, or ATLauncher/FTB App/GDLauncher
        /// instance.json. Null when none names it.
        /// </summary>
        public static string ReadInstanceName(string gameFolder)
        {
            var folder = GetInstanceFolder(gameFolder);
            try
            {
                var cfg = Path.Combine(folder, "instance.cfg");
                if (File.Exists(cfg))
                {
                    var line = File.ReadLines(cfg).FirstOrDefault(text => text.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
                    var fromCfg = line?.Substring("name=".Length).Trim();
                    if (!string.IsNullOrEmpty(fromCfg))
                    {
                        return fromCfg;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            var candidates = new[]
            {
                ReadJson(Path.Combine(folder, "minecraftinstance.json"))?["name"],
                ReadJson(Path.Combine(folder, "profile.json"))?["name"],
                ReadJson(Path.Combine(folder, "profile.json"))?["metadata"]?["name"],
                ReadJson(Path.Combine(folder, "instance.json"))?["launcher"]?["name"],
                ReadJson(Path.Combine(folder, "instance.json"))?["name"]
            };
            return candidates
                .OfType<JValue>()
                .Select(value => value.ToString().Trim())
                .FirstOrDefault(value => value.Length > 0);
        }

        public static bool IsWorldFolder(string folder)
        {
            return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "level.dat"));
        }

        public static List<string> EnumerateWorlds(string gameFolder)
        {
            var saves = Path.Combine(gameFolder ?? string.Empty, "saves");
            try
            {
                return Directory.Exists(saves)
                    ? Directory.GetDirectories(saves).Where(IsWorldFolder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// True when a world under the folder has an advancements folder, which only Java Minecraft writes.
        /// </summary>
        public static bool HasAdvancementWorld(string gameFolder)
        {
            return EnumerateWorlds(gameFolder).Any(world => GetAdvancementsFolder(world) != null);
        }

        /// <summary>
        /// A world's player advancement folder: players/advancements from 26.x, advancements before.
        /// Null when the world has neither.
        /// </summary>
        public static string GetAdvancementsFolder(string worldFolder)
        {
            if (string.IsNullOrWhiteSpace(worldFolder))
            {
                return null;
            }

            foreach (var candidate in new[] { Path.Combine(worldFolder, "players", "advancements"), Path.Combine(worldFolder, "advancements") })
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// The game jar for a game folder: the version named by the instance metadata when its jar
        /// is found, otherwise the most recently written game jar near the folder or in
        /// %AppData%\.minecraft. Null when there is none.
        /// </summary>
        public static string FindGameJar(string gameFolder)
        {
            var candidates = EnumerateGameJars(gameFolder).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var hint = ReadVersionHint(gameFolder);
            if (hint != null)
            {
                var match = candidates.FirstOrDefault(candidate => string.Equals(candidate.Version, hint, StringComparison.OrdinalIgnoreCase));
                if (match.Path != null)
                {
                    return match.Path;
                }
            }

            return candidates
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.Path))
                .First()
                .Path;
        }

        private static IEnumerable<(string Version, string Path)> EnumerateGameJars(string gameFolder)
        {
            var bases = SelfAndAncestors(gameFolder, 4)
                .Concat(new[] { DefaultGameFolder })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in bases)
            {
                foreach (var versions in new[] { Path.Combine(root, "versions"), Path.Combine(root, "Install", "versions"), Path.Combine(root, "meta", "versions") })
                {
                    foreach (var folder in SafeDirectories(versions))
                    {
                        var version = Path.GetFileName(folder);
                        var jar = Path.Combine(folder, version + ".jar");
                        if (IsGameJar(jar) && seen.Add(jar))
                        {
                            yield return (version, jar);
                        }
                    }
                }

                foreach (var folder in SafeDirectories(Path.Combine(root, "libraries", "com", "mojang", "minecraft")))
                {
                    var version = Path.GetFileName(folder);
                    var jar = Path.Combine(folder, $"minecraft-{version}-client.jar");
                    if (IsGameJar(jar) && seen.Add(jar))
                    {
                        yield return (version, jar);
                    }
                }
            }
        }

        private static bool IsGameJar(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length >= MinGameJarBytes;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The Minecraft version an instance declares: Prism/MultiMC mmc-pack.json, CurseForge
        /// minecraftinstance.json or Modrinth profile.json. Null when none is found.
        /// </summary>
        internal static string ReadVersionHint(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(gameFolder);
            var mmcPack = ReadJson(Path.Combine(parent ?? gameFolder, "mmc-pack.json"));
            var fromMmc = (mmcPack?["components"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(component => string.Equals((string)(component["uid"] as JValue), "net.minecraft", StringComparison.Ordinal))?
                ["version"];
            if (fromMmc != null)
            {
                return fromMmc.ToString();
            }

            var curseForge = ReadJson(Path.Combine(gameFolder, "minecraftinstance.json"));
            if (curseForge?["gameVersion"] is JValue curseForgeVersion)
            {
                return curseForgeVersion.ToString();
            }

            var modrinth = ReadJson(Path.Combine(gameFolder, "profile.json"));
            var modrinthVersion = modrinth?["game_version"] ?? modrinth?["metadata"]?["game_version"];
            return modrinthVersion is JValue ? modrinthVersion.ToString() : null;
        }

        /// <summary>
        /// Vanilla translations for a non-English locale. Only en_us ships inside the game jar; the
        /// others are asset objects named by the launcher's asset index.
        /// </summary>
        public static IReadOnlyDictionary<string, string> FindAssetLanguage(IEnumerable<string> gameFolders, string locale)
        {
            if (string.IsNullOrWhiteSpace(locale) ||
                string.Equals(locale, MinecraftAdvancementCatalog.DefaultLocale, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var key = $"minecraft/lang/{locale.ToLowerInvariant()}.json";
            var roots = (gameFolders ?? Enumerable.Empty<string>())
                .SelectMany(folder => SelfAndAncestors(folder, 4))
                .Concat(DefaultLauncherDataFolders())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                foreach (var assets in new[] { Path.Combine(root, "assets"), Path.Combine(root, "Install", "assets"), Path.Combine(root, "meta", "assets") })
                {
                    var indexes = SafeFiles(Path.Combine(assets, "indexes"), "*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc);
                    foreach (var index in indexes)
                    {
                        var hash = (string)(ReadJson(index)?["objects"]?[key]?["hash"] as JValue);
                        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                        {
                            continue;
                        }

                        var objectPath = Path.Combine(assets, "objects", hash.Substring(0, 2), hash);
                        if (!File.Exists(objectPath))
                        {
                            continue;
                        }

                        return AssetLanguageCache.GetOrAdd(objectPath, path =>
                        {
                            var table = new Dictionary<string, string>(StringComparer.Ordinal);
                            try
                            {
                                MinecraftResourceSource.MergeLanguage(table, File.ReadAllText(path), isLegacy: false);
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                            }

                            return table;
                        });
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Minecraft's locale code for the plugin's achievement language (Steam-style names).
        /// </summary>
        public static string MapGlobalLanguageToLocale(string globalLanguage)
        {
            switch ((globalLanguage ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "french": return "fr_fr";
                case "spanish": return "es_es";
                case "latam": return "es_mx";
                case "german": return "de_de";
                case "italian": return "it_it";
                case "japanese": return "ja_jp";
                case "dutch": return "nl_nl";
                case "portuguese": return "pt_pt";
                case "brazilian": return "pt_br";
                case "russian": return "ru_ru";
                case "korean":
                case "koreana": return "ko_kr";
                case "chinese":
                case "schinese": return "zh_cn";
                case "tchinese": return "zh_tw";
                case "polish": return "pl_pl";
                case "danish": return "da_dk";
                case "finnish": return "fi_fi";
                case "norwegian": return "no_no";
                case "swedish": return "sv_se";
                case "turkish": return "tr_tr";
                case "czech": return "cs_cz";
                case "hungarian": return "hu_hu";
                case "greek": return "el_gr";
                case "ukrainian": return "uk_ua";
                case "thai": return "th_th";
                case "vietnamese": return "vi_vn";
                case "romanian": return "ro_ro";
                case "bulgarian": return "bg_bg";
                case "arabic": return "ar_sa";
                case "indonesian": return "id_id";
                default: return MinecraftAdvancementCatalog.DefaultLocale;
            }
        }

        private static JObject ReadJson(string path)
        {
            try
            {
                return File.Exists(path) ? JToken.Parse(File.ReadAllText(path)) as JObject : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        internal static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        internal static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
