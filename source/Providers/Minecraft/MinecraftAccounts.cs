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

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in EnumerateAccountFolders(gameFolders))
            {
                foreach (var fileName in AccountFileNames)
                {
                    result.UnionWith(ReadProfileIds(Path.Combine(folder, fileName)));
                }
            }

            return result;
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
            if (!File.Exists(path))
            {
                return Enumerable.Empty<string>();
            }

            JToken json;
            try
            {
                json = JToken.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return Enumerable.Empty<string>();
            }

            if (!(json is JContainer container))
            {
                return Enumerable.Empty<string>();
            }

            return container
                .Descendants()
                .OfType<JProperty>()
                .Where(property => ProfilePropertyNames.Contains(property.Name, StringComparer.Ordinal))
                .Select(property => property.Value as JObject)
                .Where(profile => profile != null)
                .Select(profile => NormalizeUuid((string)(profile["id"] as JValue)))
                .Where(uuid => uuid != null)
                .Distinct()
                .ToList();
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
