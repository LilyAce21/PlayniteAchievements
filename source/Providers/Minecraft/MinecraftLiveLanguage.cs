using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// What an instance's game log announcements look like, in the language the game runs in: the
    /// announcement patterns built from the game's own chat templates (chat.type.advancement.task,
    /// .goal and .challenge, as the jar, mods and enabled resource packs define them) and advancement
    /// ids by their title in that language. A mod that rewords announcements through those keys, or
    /// a game in another language, is matched without a phrase list.
    /// </summary>
    internal sealed class MinecraftLiveLanguage
    {
        private static readonly string[] AnnouncementKeys =
        {
            "chat.type.advancement.task",
            "chat.type.advancement.goal",
            "chat.type.advancement.challenge"
        };

        private static readonly Regex TemplateToken = new Regex(@"%(?:(?<index>\d+)\$)?(?<kind>[sd%])", RegexOptions.CultureInvariant);

        private const string PlayerPattern = "(?<player>[A-Za-z0-9_]{1,16})";

        // The game wraps an advancement's name in square brackets in chat.
        private const string TitlePattern = @"\[(?<title>.+?)\]";

        public static MinecraftLiveLanguage Empty { get; } = new MinecraftLiveLanguage();

        public IReadOnlyList<Regex> Announcements { get; private set; } = Array.Empty<Regex>();

        /// <summary>Advancement ids by display title in the game's language (case-insensitive).</summary>
        public Dictionary<string, List<string>> IdsByTitle { get; private set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        internal static MinecraftLiveLanguage Create(IEnumerable<string> templates, IDictionary<string, List<string>> idsByTitle)
        {
            return new MinecraftLiveLanguage
            {
                Announcements = (templates ?? Enumerable.Empty<string>()).Select(FromTemplate).Where(regex => regex != null).ToList(),
                IdsByTitle = new Dictionary<string, List<string>>(
                    idsByTitle ?? new Dictionary<string, List<string>>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        public static MinecraftLiveLanguage Build(MinecraftInstance instance, ILogger logger)
        {
            var locale = MinecraftPaths.ReadGameLanguage(instance.GameFolder);
            var locales = new[] { MinecraftAdvancementCatalog.DefaultLocale, locale }.Distinct().ToList();

            // Resource packs sit above the game and mods, as in-game.
            var sources = MinecraftScanner.LoadSources(instance, locales, logger, CancellationToken.None);
            sources.AddRange(MinecraftPaths.ReadEnabledResourcePacks(instance.GameFolder)
                .Select(pack => MinecraftResourceSource.Load(pack, locales, logger))
                .Where(source => source != null));

            var assetLanguage = MinecraftPaths.FindAssetLanguage(new[] { instance.GameFolder }, locale);
            var extraTables = assetLanguage == null ? null : new[] { assetLanguage };
            var catalog = MinecraftAdvancementCatalog.Build(sources, extraTables, locale);
            var translate = MinecraftAdvancementCatalog.BuildTranslations(sources, extraTables, locale);

            var language = new MinecraftLiveLanguage
            {
                Announcements = AnnouncementKeys
                    .Select(key => translate(key))
                    .Where(template => !string.IsNullOrWhiteSpace(template))
                    .Distinct(StringComparer.Ordinal)
                    .Select(FromTemplate)
                    .Where(regex => regex != null)
                    .ToList()
            };

            foreach (var advancement in catalog.Values)
            {
                var title = (string.IsNullOrWhiteSpace(advancement.Title) ? advancement.Id : advancement.Title).Trim();
                if (!language.IdsByTitle.TryGetValue(title, out var ids))
                {
                    ids = new List<string>();
                    language.IdsByTitle[title] = ids;
                }

                ids.Add(advancement.Id);
            }

            return language;
        }

        /// <summary>
        /// A pattern for one chat template such as <c>%s has made the advancement %s</c> or
        /// <c>%s hat den Fortschritt %s erzielt</c>: the first argument is the player, the second the
        /// bracketed advancement name, and the literal text must match exactly. Null for a template
        /// without exactly those two arguments.
        /// </summary>
        internal static Regex FromTemplate(string template)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return null;
            }

            var pattern = new StringBuilder(@"(?<![\w<\[*])");
            var next = 0;
            var position = 0;
            var players = 0;
            var titles = 0;
            foreach (Match token in TemplateToken.Matches(template))
            {
                pattern.Append(Regex.Escape(template.Substring(position, token.Index - position)));
                position = token.Index + token.Length;
                if (token.Groups["kind"].Value == "%")
                {
                    pattern.Append('%');
                    continue;
                }

                var index = token.Groups["index"].Success ? int.Parse(token.Groups["index"].Value) - 1 : next++;
                if (index == 0)
                {
                    pattern.Append(PlayerPattern);
                    players++;
                }
                else if (index == 1)
                {
                    pattern.Append(TitlePattern);
                    titles++;
                }
                else
                {
                    return null;
                }
            }

            pattern.Append(Regex.Escape(template.Substring(position)));
            pattern.Append(@"\s*$");
            return players == 1 && titles == 1
                ? new Regex(pattern.ToString(), RegexOptions.CultureInvariant)
                : null;
        }
    }
}
