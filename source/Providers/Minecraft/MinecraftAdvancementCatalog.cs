using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// One displayed advancement after merging every source and resolving its text.
    /// </summary>
    internal sealed class MinecraftAdvancement
    {
        public string Id { get; set; }
        public string Namespace => Id.Substring(0, Id.IndexOf(':'));
        public string ParentId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        /// <summary>Namespaced item id of the display icon, e.g. <c>minecraft:diamond</c>.</summary>
        public string IconItemId { get; set; }

        /// <summary>The icon's custom model data, which picks a model override; null when unset.</summary>
        public float? IconCustomModelData { get; set; }

        /// <summary>task, goal or challenge.</summary>
        public string Frame { get; set; }
        public bool Hidden { get; set; }

        /// <summary>
        /// Requirement groups: the advancement is done when every group has at least one obtained
        /// criterion. Without an explicit list, each criterion is its own group.
        /// </summary>
        public List<List<string>> Requirements { get; set; } = new List<List<string>>();

        /// <summary>Id of the root advancement (the in-game tab) this one sits under.</summary>
        public string RootId { get; set; }
    }

    /// <summary>
    /// Merges advancement definitions and translations from game jars, mods and datapacks, in
    /// ascending priority, into the displayed advancements a player can see: those with a display
    /// whose tree root also has one.
    /// </summary>
    internal static class MinecraftAdvancementCatalog
    {
        public const string DefaultLocale = "en_us";

        private static readonly Regex FormatToken = new Regex(@"%(?:(?<index>\d+)\$)?(?<kind>[sd%])", RegexOptions.CultureInvariant);

        /// <summary>
        /// Displayed advancements by id. A later source replaces an earlier definition of the same
        /// id, as a datapack overrides the game; a replacement with no display hides it.
        /// </summary>
        public static Dictionary<string, MinecraftAdvancement> Build(
            IEnumerable<MinecraftResourceSource> sourcesByPriority,
            IEnumerable<IReadOnlyDictionary<string, string>> extraLanguageTables,
            string locale)
        {
            var sources = (sourcesByPriority ?? Enumerable.Empty<MinecraftResourceSource>())
                .Where(source => source != null)
                .ToList();

            var raw = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                foreach (var entry in source.Advancements)
                {
                    var parsed = TryParseObject(entry.Value);
                    if (parsed != null)
                    {
                        raw[entry.Key] = parsed;
                    }
                }
            }

            var translations = BuildTranslations(sources, extraLanguageTables, locale);
            var result = new Dictionary<string, MinecraftAdvancement>(StringComparer.Ordinal);
            foreach (var entry in raw)
            {
                if (!(entry.Value["display"] is JObject display))
                {
                    continue;
                }

                result[entry.Key] = new MinecraftAdvancement
                {
                    Id = entry.Key,
                    ParentId = NormalizeId((string)(entry.Value["parent"] as JValue)),
                    Title = ResolveText(display["title"], translations),
                    Description = ResolveText(display["description"], translations),
                    IconItemId = NormalizeId(ReadIconItem(display["icon"])),
                    IconCustomModelData = ReadIconCustomModelData(display["icon"]),
                    Frame = ((string)(display["frame"] as JValue) ?? "task").Trim().ToLowerInvariant(),
                    Hidden = display["hidden"]?.Type == JTokenType.Boolean && (bool)display["hidden"],
                    Requirements = ReadRequirements(entry.Value)
                };
            }

            foreach (var advancement in result.Values)
            {
                advancement.RootId = FindRoot(advancement, raw);
            }

            // The game opens a tab only for a root with a display, so a tree under a display-less
            // root (a mod's internal tracking advancements) never appears in-game.
            foreach (var hiddenTree in result.Values.Where(advancement => !result.ContainsKey(advancement.RootId)).ToList())
            {
                result.Remove(hiddenTree.Id);
            }

            return result;
        }

        /// <summary>
        /// Lookup order: the requested locale, then en_us, highest-priority source first.
        /// </summary>
        internal static Func<string, string> BuildTranslations(
            IReadOnlyList<MinecraftResourceSource> sources,
            IEnumerable<IReadOnlyDictionary<string, string>> extraLanguageTables,
            string locale)
        {
            var locales = new List<string>();
            if (!string.IsNullOrWhiteSpace(locale) && !string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
            {
                locales.Add(locale.ToLowerInvariant());
            }

            locales.Add(DefaultLocale);

            var tables = new List<IReadOnlyDictionary<string, string>>();
            foreach (var current in locales)
            {
                if (current != DefaultLocale && extraLanguageTables != null)
                {
                    tables.AddRange(extraLanguageTables.Where(table => table != null));
                }

                for (var i = sources.Count - 1; i >= 0; i--)
                {
                    if (sources[i].Languages.TryGetValue(current, out var table))
                    {
                        tables.Add(table);
                    }
                }
            }

            return key =>
            {
                foreach (var table in tables)
                {
                    if (table.TryGetValue(key, out var value))
                    {
                        return value;
                    }
                }

                return null;
            };
        }

        /// <summary>
        /// Flattens a text component: a plain string, an array, or an object with text/translate
        /// (with arguments and fallback) and extra children. 1.12 titles may be a JSON string of
        /// a component.
        /// </summary>
        internal static string ResolveText(JToken token, Func<string, string> translate)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return (string)token;
            }

            if (token is JArray array)
            {
                var builder = new StringBuilder();
                foreach (var child in array)
                {
                    builder.Append(ResolveText(child, translate));
                }

                return builder.ToString();
            }

            if (!(token is JObject component))
            {
                return token.ToString();
            }

            string text;
            if (component["text"] != null)
            {
                text = component["text"].ToString();
            }
            else if (component["translate"] != null)
            {
                var key = component["translate"].ToString();
                var template = translate?.Invoke(key) ?? (string)(component["fallback"] as JValue) ?? key;
                var arguments = (component["with"] as JArray)?.Select(argument => ResolveText(argument, translate)).ToList()
                                ?? new List<string>();
                text = Format(template, arguments);
            }
            else
            {
                text = string.Empty;
            }

            if (component["extra"] is JArray extra)
            {
                var builder = new StringBuilder(text);
                foreach (var child in extra)
                {
                    builder.Append(ResolveText(child, translate));
                }

                text = builder.ToString();
            }

            return text;
        }

        private static string Format(string template, IReadOnlyList<string> arguments)
        {
            if (template == null || template.IndexOf('%') < 0)
            {
                return template;
            }

            var next = 0;
            return FormatToken.Replace(template, match =>
            {
                if (match.Groups["kind"].Value == "%")
                {
                    return "%";
                }

                var index = match.Groups["index"].Success
                    ? int.Parse(match.Groups["index"].Value) - 1
                    : next++;
                return index >= 0 && index < arguments.Count ? arguments[index] : string.Empty;
            });
        }

        /// <summary>
        /// Custom model data from 1.20.5+ components (a number, or 1.21.4's floats list) or from a
        /// pre-1.20.5 NBT string such as <c>{CustomModelData:3}</c>.
        /// </summary>
        private static float? ReadIconCustomModelData(JToken icon)
        {
            if (!(icon is JObject iconObject))
            {
                return null;
            }

            var component = iconObject["components"]?["minecraft:custom_model_data"] ?? iconObject["components"]?["custom_model_data"];
            if (component != null)
            {
                if (component.Type == JTokenType.Integer || component.Type == JTokenType.Float)
                {
                    return (float)component;
                }

                var first = (component["floats"] as JArray)?.FirstOrDefault();
                if (first != null && (first.Type == JTokenType.Integer || first.Type == JTokenType.Float))
                {
                    return (float)first;
                }
            }

            var nbt = (string)(iconObject["nbt"] as JValue);
            var match = nbt == null ? null : Regex.Match(nbt, @"CustomModelData\s*:\s*(-?\d+)", RegexOptions.CultureInvariant);
            return match != null && match.Success
                ? float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : (float?)null;
        }

        private static string ReadIconItem(JToken icon)
        {
            if (icon is JObject iconObject)
            {
                return (string)(iconObject["id"] as JValue) ?? (string)(iconObject["item"] as JValue);
            }

            return icon?.Type == JTokenType.String ? (string)icon : null;
        }

        private static List<List<string>> ReadRequirements(JObject advancement)
        {
            var groups = new List<List<string>>();
            if (advancement["requirements"] is JArray requirements)
            {
                foreach (var group in requirements.OfType<JArray>())
                {
                    var names = group.Select(name => name?.ToString()).Where(name => !string.IsNullOrEmpty(name)).ToList();
                    if (names.Count > 0)
                    {
                        groups.Add(names);
                    }
                }
            }

            if (groups.Count == 0 && advancement["criteria"] is JObject criteria)
            {
                groups.AddRange(criteria.Properties().Select(property => new List<string> { property.Name }));
            }

            return groups;
        }

        private static string FindRoot(MinecraftAdvancement advancement, IReadOnlyDictionary<string, JObject> raw)
        {
            var current = advancement.Id;
            var parent = advancement.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { current };
            while (!string.IsNullOrEmpty(parent) && raw.TryGetValue(parent, out var parentJson) && visited.Add(parent))
            {
                current = parent;
                parent = NormalizeId((string)(parentJson["parent"] as JValue));
            }

            return current;
        }

        /// <summary>Lowercase namespaced id; a bare id gets the minecraft namespace.</summary>
        internal static string NormalizeId(string raw)
        {
            var trimmed = (raw ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            return trimmed.IndexOf(':') >= 0 ? trimmed.ToLowerInvariant() : "minecraft:" + trimmed.ToLowerInvariant();
        }

        private static JObject TryParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
