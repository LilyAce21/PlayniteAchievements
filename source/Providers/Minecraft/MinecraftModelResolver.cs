using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Follows an item to the texture it is drawn with: the 1.21.4+ item definition names a model,
    /// otherwise the model is <c>item/&lt;name&gt;</c>; the model and its parents (block models
    /// included) supply a texture map whose <c>#references</c> are resolved.
    /// </summary>
    internal static class MinecraftModelResolver
    {
        private const int MaxParentDepth = 12;

        // Which texture slot best stands for the item: a flat item layer, then the face a player
        // sees first on a block, then whatever the model offers.
        private static readonly string[] PreferredSlots =
        {
            "layer0", "all", "side", "front", "north", "texture", "top", "end", "cross", "plant", "particle"
        };

        /// <summary>
        /// Texture entry names (<c>assets/ns/textures/....png</c>, lowercase) in preference order;
        /// empty when the item has no model in <paramref name="sources"/>.
        /// </summary>
        public static IEnumerable<string> ResolveTextureEntries(
            string ns,
            string name,
            float? customModelData,
            IReadOnlyList<MinecraftResourceSource> sources)
        {
            var itemId = $"{ns}:{name}".ToLowerInvariant();
            var itemModelId = $"{ns}:item/{name}".ToLowerInvariant();
            var modelIds = new List<string>();
            var overrideModel = customModelData.HasValue
                ? SelectOverride(Parse(Find(sources, source => source.Models, itemModelId)), customModelData.Value)
                : null;
            if (overrideModel != null)
            {
                modelIds.Add(overrideModel);
            }

            var definitionModel = ReadDefinitionModel(Find(sources, source => source.ItemDefinitions, itemId));
            if (definitionModel != null)
            {
                modelIds.Add(definitionModel);
            }

            modelIds.Add(itemModelId);
            modelIds.Add($"{ns}:block/{name}".ToLowerInvariant());

            foreach (var modelId in modelIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var textures = CollectTextures(modelId, sources, out var isFluidContainer);
                if (textures == null)
                {
                    continue;
                }


                var slots = PreferredSlots
                    .Where(textures.ContainsKey)
                    .Concat(textures.Keys.Where(key => !PreferredSlots.Contains(key)).OrderBy(key => key, StringComparer.Ordinal));
                foreach (var slot in slots)
                {
                    var texture = Dereference(textures, textures[slot]);
                    var entry = ToTextureEntry(texture);
                    if (entry != null)
                    {
                        yield return entry;
                    }
                }

                // Forge/NeoForge bucket models draw from the loader's own bucket model, which no
                // mod jar carries; the vanilla bucket stands in.
                if (isFluidContainer)
                {
                    yield return "assets/minecraft/textures/item/bucket.png";
                }

                yield break;
            }
        }

        /// <summary>
        /// The merged texture map of a model and its parents (child slots win), or null when the
        /// model is not in any source.
        /// </summary>
        private static Dictionary<string, string> CollectTextures(
            string modelId,
            IReadOnlyList<MinecraftResourceSource> sources,
            out bool isFluidContainer)
        {
            isFluidContainer = false;
            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            var current = NormalizeModelId(modelId);
            var found = false;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; current != null && depth < MaxParentDepth && visited.Add(current); depth++)
            {
                var model = Parse(Find(sources, source => source.Models, current));
                if (model == null)
                {
                    break;
                }

                found = true;
                var loader = (string)(model["loader"] as JValue) ?? string.Empty;
                var parent = (string)(model["parent"] as JValue) ?? string.Empty;
                if (loader.EndsWith("fluid_container", StringComparison.OrdinalIgnoreCase) ||
                    parent.EndsWith("item/bucket", StringComparison.OrdinalIgnoreCase) && !parent.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
                {
                    isFluidContainer = true;
                }

                if (model["textures"] is JObject slots)
                {
                    foreach (var slot in slots.Properties())
                    {
                        if (slot.Value.Type == JTokenType.String && !textures.ContainsKey(slot.Name))
                        {
                            textures[slot.Name] = (string)slot.Value;
                        }
                    }
                }

                current = NormalizeModelId((string)(model["parent"] as JValue));
            }

            return found ? textures : null;
        }

        /// <summary>
        /// The model of the last override whose custom_model_data predicate the value meets, as
        /// the game picks it; null when none applies.
        /// </summary>
        private static string SelectOverride(JObject itemModel, float customModelData)
        {
            string selected = null;
            foreach (var entry in (itemModel?["overrides"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var predicate = entry["predicate"] as JObject;
                var threshold = predicate?["minecraft:custom_model_data"] ?? predicate?["custom_model_data"];
                if (threshold != null &&
                    (threshold.Type == JTokenType.Integer || threshold.Type == JTokenType.Float) &&
                    customModelData >= (float)threshold &&
                    entry["model"]?.Type == JTokenType.String)
                {
                    selected = (string)entry["model"];
                }
            }

            return selected;
        }

        /// <summary>Follows <c>#slot</c> references within the texture map.</summary>
        private static string Dereference(IReadOnlyDictionary<string, string> textures, string value)
        {
            for (var depth = 0; value != null && value.StartsWith("#", StringComparison.Ordinal) && depth < 8; depth++)
            {
                textures.TryGetValue(value.Substring(1), out value);
            }

            return value != null && value.StartsWith("#", StringComparison.Ordinal) ? null : value;
        }

        /// <summary><c>ns:block/stone</c> or <c>block/stone</c> to its texture entry name.</summary>
        internal static string ToTextureEntry(string textureId)
        {
            var trimmed = (textureId ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Contains(".."))
            {
                return null;
            }

            var separator = trimmed.IndexOf(':');
            var ns = separator > 0 ? trimmed.Substring(0, separator) : "minecraft";
            var path = separator > 0 ? trimmed.Substring(separator + 1) : trimmed;
            return $"assets/{ns}/textures/{path}.png".ToLowerInvariant();
        }

        /// <summary>
        /// The first model id an item definition points at; definitions nest it under conditions,
        /// selects and composites, and the first one is the default look.
        /// </summary>
        private static string ReadDefinitionModel(string json)
        {
            var definition = Parse(json);
            return definition?
                .Descendants()
                .OfType<JProperty>()
                .Where(property => property.Name == "model" && property.Value.Type == JTokenType.String)
                .Select(property => (string)property.Value)
                .FirstOrDefault();
        }

        private static string NormalizeModelId(string raw)
        {
            var trimmed = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length == 0 || trimmed.StartsWith("builtin/", StringComparison.Ordinal))
            {
                return null;
            }

            return trimmed.IndexOf(':') >= 0 ? trimmed : "minecraft:" + trimmed;
        }

        /// <summary>The value for <paramref name="key"/> from the highest-priority source that has it.</summary>
        private static string Find(
            IReadOnlyList<MinecraftResourceSource> sources,
            Func<MinecraftResourceSource, Dictionary<string, string>> select,
            string key)
        {
            for (var i = sources.Count - 1; i >= 0; i--)
            {
                if (select(sources[i]).TryGetValue(key, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static JObject Parse(string json)
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
