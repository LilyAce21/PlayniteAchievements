using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>A block model ready to draw: its element boxes, texture map and gui view rotation.</summary>
    internal sealed class MinecraftBlockModel
    {
        public string ModelId { get; set; }
        public JArray Elements { get; set; }
        public Dictionary<string, string> Textures { get; set; }
        public double RotationX { get; set; }
        public double RotationY { get; set; }
    }

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
            foreach (var modelId in CandidateModelIds(ns, name, customModelData, sources))
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
        /// The item's model ids in the order the game would pick them: a custom-model-data override,
        /// the 1.21.4+ item definition's model, item/&lt;name&gt;, then block/&lt;name&gt;.
        /// </summary>
        private static IEnumerable<string> CandidateModelIds(
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
            return modelIds.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The item's block model when it is drawn as one: the first model in the chain that has
        /// element boxes, with the merged texture map and the gui display rotation. Null for items
        /// drawn as a flat sprite (a layer0 texture), models built in code, or no model at all.
        /// </summary>
        public static MinecraftBlockModel ResolveBlockModel(
            string ns,
            string name,
            float? customModelData,
            IReadOnlyList<MinecraftResourceSource> sources)
        {
            foreach (var modelId in CandidateModelIds(ns, name, customModelData, sources))
            {
                var textures = new Dictionary<string, string>(StringComparer.Ordinal);
                JArray elements = null;
                double[] guiRotation = null;
                var found = false;
                var current = NormalizeModelId(modelId);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var depth = 0; current != null && depth < MaxParentDepth && visited.Add(current); depth++)
                {
                    var model = Parse(Find(sources, source => source.Models, current));
                    if (model == null)
                    {
                        break;
                    }

                    found = true;
                    if (model["textures"] is JObject slots)
                    {
                        foreach (var slot in slots.Properties().Where(slot => !textures.ContainsKey(slot.Name)))
                        {
                            var value = ReadTextureSlot(slot.Value);
                            if (value != null)
                            {
                                textures[slot.Name] = value;
                            }
                        }
                    }

                    if (elements == null && model["elements"] is JArray modelElements && modelElements.Count > 0)
                    {
                        elements = modelElements;
                    }

                    if (guiRotation == null && model["display"]?["gui"]?["rotation"] is JArray rotation && rotation.Count == 3)
                    {
                        guiRotation = rotation.Select(value => value.Type == JTokenType.Integer || value.Type == JTokenType.Float ? (double)value : 0).ToArray();
                    }

                    current = NormalizeModelId((string)(model["parent"] as JValue));
                }

                if (!found)
                {
                    continue;
                }

                // A flat sprite, even one whose chain also has boxes, stays flat.
                if (elements == null || textures.ContainsKey("layer0"))
                {
                    return null;
                }

                return new MinecraftBlockModel
                {
                    ModelId = NormalizeModelId(modelId),
                    Elements = elements,
                    Textures = textures,
                    RotationX = guiRotation?[0] ?? 30,
                    RotationY = guiRotation?[1] ?? 225
                };
            }

            return null;
        }

        /// <summary>
        /// A texture slot's value: a texture id or <c>#reference</c> string, or from 26.x an object
        /// whose <c>sprite</c> holds it (with options such as force_translucent). Null otherwise.
        /// </summary>
        private static string ReadTextureSlot(JToken value)
        {
            if (value?.Type == JTokenType.String)
            {
                return (string)value;
            }

            return value is JObject slot && slot["sprite"]?.Type == JTokenType.String ? (string)slot["sprite"] : null;
        }

        /// <summary>The texture entry a face's texture reference (such as #side) names, or null.</summary>
        internal static string ResolveFaceTexture(MinecraftBlockModel model, string reference)
        {
            var value = reference != null && reference.StartsWith("#", StringComparison.Ordinal)
                ? Dereference(model.Textures, reference)
                : reference;
            return ToTextureEntry(value);
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
                        var value = ReadTextureSlot(slot.Value);
                        if (value != null && !textures.ContainsKey(slot.Name))
                        {
                            textures[slot.Name] = value;
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
