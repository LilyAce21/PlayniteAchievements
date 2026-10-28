using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Player UUIDs whose advancement files count: the configured one, or every Minecraft profile
    /// in the official launcher's and Prism/MultiMC/PolyMC's account files.
    /// </summary>
    internal static class MinecraftAccounts
    {
        private static readonly string[] AccountFileNames =
        {
            "launcher_accounts_microsoft_store.json",
            "launcher_accounts.json",
            "accounts.json"
        };

        private static readonly string[] ProfilePropertyNames = { "minecraftProfile", "profile" };

        /// <summary>
        /// The configured UUID when valid; otherwise every profile id found in account files next
        /// to the game folders, up to four levels above them, and in the default launcher folders.
        /// </summary>
        public static HashSet<string> Resolve(string configuredUuid, IEnumerable<string> gameFolders)
        {
            var configured = NormalizeUuid(configuredUuid);
            if (configured != null)
            {
                return new HashSet<string>(StringComparer.Ordinal) { configured };
            }

            return new HashSet<string>(ReadAllProfiles(gameFolders).Select(profile => profile.Uuid), StringComparer.Ordinal);
        }

        /// <summary>
        /// The in-game names of the players <see cref="Resolve"/> selects, as they appear in chat.
        /// Empty when no account file names them, such as an offline-mode UUID.
        /// </summary>
        public static HashSet<string> ResolveNames(string configuredUuid, IEnumerable<string> gameFolders)
        {
            var configured = NormalizeUuid(configuredUuid);
            return new HashSet<string>(
                ReadAllProfiles(gameFolders)
                    .Where(profile => configured == null || profile.Uuid == configured)
                    .Select(profile => profile.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<(string Uuid, string Name)> ReadAllProfiles(IEnumerable<string> gameFolders)
        {
            foreach (var folder in EnumerateAccountFolders(gameFolders))
            {
                foreach (var fileName in AccountFileNames)
                {
                    foreach (var profile in ReadProfiles(Path.Combine(folder, fileName)))
                    {
                        yield return profile;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateAccountFolders(IEnumerable<string> gameFolders)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameFolder in gameFolders ?? Enumerable.Empty<string>())
            {
                foreach (var folder in MinecraftPaths.SelfAndAncestors(gameFolder, 4))
                {
                    if (seen.Add(folder))
                    {
                        yield return folder;
                    }
                }
            }

            foreach (var folder in MinecraftPaths.DefaultLauncherDataFolders())
            {
                if (seen.Add(folder))
                {
                    yield return folder;
                }
            }
        }

        internal static IEnumerable<string> ReadProfileIds(string path)
        {
            return ReadProfiles(path).Select(profile => profile.Uuid).Distinct().ToList();
        }

        /// <summary>Every Minecraft profile (UUID and in-game name) in one account file.</summary>
        internal static List<(string Uuid, string Name)> ReadProfiles(string path)
        {
            var result = new List<(string Uuid, string Name)>();
            if (!File.Exists(path))
            {
                return result;
            }

            JToken json;
            try
            {
                json = JToken.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return result;
            }

            if (!(json is JContainer container))
            {
                return result;
            }

            foreach (var profile in container
                .Descendants()
                .OfType<JProperty>()
                .Where(property => ProfilePropertyNames.Contains(property.Name, StringComparer.Ordinal))
                .Select(property => property.Value as JObject)
                .Where(profile => profile != null))
            {
                var uuid = NormalizeUuid((string)(profile["id"] as JValue));
                if (uuid != null)
                {
                    result.Add((uuid, ((string)(profile["name"] as JValue))?.Trim()));
                }
            }

            return result;
        }

        /// <summary>32 lowercase hex digits without dashes, or null when not a UUID.</summary>
        public static string NormalizeUuid(string raw)
        {
            var compact = (raw ?? string.Empty).Trim().Replace("-", string.Empty).ToLowerInvariant();
            return compact.Length == 32 && compact.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                ? compact
                : null;
        }
    }
}
