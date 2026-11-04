using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>How a SAN style setting is edited and stored.</summary>
    public enum SanStyleKind
    {
        Range,
        Bool,
        Select,
        Color
    }

    /// <summary>
    /// One setting of a SAN notification's <c>customisation</c> block that the per-profile style
    /// editor can override, with the ranges the SAN customiser itself uses. <see cref="IsPluginExtra"/>
    /// settings are not SAN keys: the host page implements them (the tint layer).
    /// </summary>
    public sealed class SanStyleOption
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Group { get; set; }
        public SanStyleKind Kind { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; } = 1;
        public string Unit { get; set; } = string.Empty;
        public string[] Options { get; set; } = Array.Empty<string>();
        public string Default { get; set; } = string.Empty;
        public string Tip { get; set; } = string.Empty;
        public bool IsPluginExtra { get; set; }

        /// <summary>The value as the JSON token SAN's page expects, or null when it cannot be parsed.</summary>
        public JToken ToToken(string text)
        {
            if (text == null)
            {
                return null;
            }

            switch (Kind)
            {
                case SanStyleKind.Bool:
                    return bool.TryParse(text, out var flag) ? new JValue(flag) : null;
                case SanStyleKind.Range:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        double.IsNaN(number) || double.IsInfinity(number))
                    {
                        return null;
                    }

                    number = Math.Max(Min, Math.Min(Max, number));
                    return Math.Abs(number - Math.Round(number)) < 1e-9
                        ? new JValue((long)Math.Round(number))
                        : new JValue(number);
                case SanStyleKind.Select:
                    return Options.Contains(text) ? new JValue(text) : null;
                default:
                    return string.IsNullOrWhiteSpace(text) ? null : new JValue(text.Trim());
            }
        }
    }

    /// <summary>The SAN settings the style editor offers, grouped as the editor shows them.</summary>
    public static class SanStyleCatalog
    {
        private static readonly List<SanStyleOption> Items = Build();

        public static IReadOnlyList<SanStyleOption> All => Items;

        public static SanStyleOption Find(string key)
        {
            return string.IsNullOrEmpty(key) ? null : Items.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
        }

        private static List<SanStyleOption> Build()
        {
            var list = new List<SanStyleOption>();

            list.Add(new SanStyleOption { Key = "bgstyle", Label = "Background style", Group = "Background", Kind = SanStyleKind.Select, Options = new[] { "solid", "gradient", "bgimg", "gameart" }, Default = "solid", Tip = "What fills the card: a solid colour, a gradient, the theme's background image, or the game art." });
            list.Add(new SanStyleOption { Key = "primarycolor", Label = "Primary colour", Group = "Background", Kind = SanStyleKind.Color, Default = "#000000", Tip = "Solid background colour, or the gradient's start colour. Not used by image backgrounds." });
            list.Add(new SanStyleOption { Key = "secondarycolor", Label = "Secondary colour", Group = "Background", Kind = SanStyleKind.Color, Default = "#000000", Tip = "The gradient's end colour." });
            list.Add(new SanStyleOption { Key = "gradientangle", Label = "Gradient angle", Group = "Background", Kind = SanStyleKind.Range, Min = 0, Max = 360, Step = 1, Unit = "°", Default = "90", Tip = "Direction of the gradient." });
            list.Add(new SanStyleOption { Key = "bgimgbrightness", Label = "Background image brightness", Group = "Background", Kind = SanStyleKind.Range, Min = 10, Max = 100, Step = 5, Unit = "%", Default = "100", Tip = "Darkens the theme's background image." });
            list.Add(new SanStyleOption { Key = "brightness", Label = "Game art brightness", Group = "Background", Kind = SanStyleKind.Range, Min = 10, Max = 100, Step = 5, Unit = "%", Default = "100", Tip = "Darkens the game art when it is the background." });
            list.Add(new SanStyleOption { Key = "blur", Label = "Background image blur", Group = "Background", Kind = SanStyleKind.Range, Min = 0, Max = 100, Step = 5, Unit = "%", Default = "0", Tip = "Blurs the theme's own background image. This is not the live blur of the game behind the card." });
            list.Add(new SanStyleOption { Key = "opacity", Label = "Notification opacity", Group = "Background", Kind = SanStyleKind.Range, Min = 10, Max = 100, Step = 5, Unit = "%", Default = "100", Tip = "Overall opacity of the notification." });
            list.Add(new SanStyleOption { Key = "bgonly", Label = "Opacity affects only the background", Group = "Background", Kind = SanStyleKind.Bool, Default = "false", Tip = "Keeps the text and icons at full opacity." });
            list.Add(new SanStyleOption { Key = "roundness", Label = "Corner roundness", Group = "Background", Kind = SanStyleKind.Range, Min = 0, Max = 100, Step = 5, Unit = "%", Default = "25", Tip = "How round the card's corners are." });
            list.Add(new SanStyleOption { Key = "bgachicon", Label = "Use the achievement icon as background", Group = "Background", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "paTintColor", Label = "Tint colour", Group = "Background tint and image (added by Playnite Achievements)", Kind = SanStyleKind.Color, Default = "#203E7A", Tip = "A colour laid over the card background, between the live blur and the text. SAN has no tint for image backgrounds, so this is how to change the colour of a glass theme.", IsPluginExtra = true });
            list.Add(new SanStyleOption { Key = "paTintOpacity", Label = "Tint strength", Group = "Background tint and image (added by Playnite Achievements)", Kind = SanStyleKind.Range, Min = 0, Max = 100, Step = 1, Unit = "%", Default = "0", Tip = "0 turns the tint off.", IsPluginExtra = true });
            list.Add(new SanStyleOption { Key = "paBgImageOpacity", Label = "Background image opacity", Group = "Background tint and image (added by Playnite Achievements)", Kind = SanStyleKind.Range, Min = 0, Max = 100, Step = 1, Unit = "%", Default = "100", Tip = "Fades the theme's background image so more of the live blur and the tint shows through.", IsPluginExtra = true });
            list.Add(new SanStyleOption { Key = "fontsize", Label = "Font size", Group = "Text", Kind = SanStyleKind.Range, Min = 50, Max = 100, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "usecustomfontsizes", Label = "Use separate sizes per line", Group = "Text", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "unlockmsgfontsize", Label = "Header size", Group = "Text", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "titlefontsize", Label = "Title size", Group = "Text", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "descfontsize", Label = "Description size", Group = "Text", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "textvspace", Label = "Line spacing", Group = "Text", Kind = SanStyleKind.Range, Min = -5, Max = 5, Step = 0.5, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "fontcolor", Label = "Text colour", Group = "Text", Kind = SanStyleKind.Color, Default = "#FFFFFF" });
            list.Add(new SanStyleOption { Key = "usecustomfontcolors", Label = "Use separate colours per line", Group = "Text", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "unlockmsgfontcolor", Label = "Header colour", Group = "Text", Kind = SanStyleKind.Color, Default = "#FFFFFF" });
            list.Add(new SanStyleOption { Key = "titlefontcolor", Label = "Title colour", Group = "Text", Kind = SanStyleKind.Color, Default = "#FFFFFF" });
            list.Add(new SanStyleOption { Key = "descfontcolor", Label = "Description colour", Group = "Text", Kind = SanStyleKind.Color, Default = "#FFFFFF" });
            list.Add(new SanStyleOption { Key = "fontoutline", Label = "Text outline", Group = "Text outline and shadow", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "fontoutlinecolor", Label = "Text outline colour", Group = "Text outline and shadow", Kind = SanStyleKind.Color, Default = "#000000" });
            list.Add(new SanStyleOption { Key = "fontoutlinescale", Label = "Text outline thickness", Group = "Text outline and shadow", Kind = SanStyleKind.Range, Min = 0.1d, Max = 2, Step = 0.1, Unit = "px", Default = "1" });
            list.Add(new SanStyleOption { Key = "fontshadow", Label = "Text shadow", Group = "Text outline and shadow", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "fontshadowcolor", Label = "Text shadow colour", Group = "Text outline and shadow", Kind = SanStyleKind.Color, Default = "#000000" });
            list.Add(new SanStyleOption { Key = "fontshadowscale", Label = "Text shadow thickness", Group = "Text outline and shadow", Kind = SanStyleKind.Range, Min = 0.5, Max = 3, Step = 0.1, Unit = "px", Default = "1.5" });
            list.Add(new SanStyleOption { Key = "fontshadowx", Label = "Text shadow horizontal offset", Group = "Text outline and shadow", Kind = SanStyleKind.Range, Min = -3, Max = 3, Step = 1, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "fontshadowy", Label = "Text shadow vertical offset", Group = "Text outline and shadow", Kind = SanStyleKind.Range, Min = -3, Max = 3, Step = 1, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "useoutline", Label = "Outline", Group = "Notification outline", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "outline", Label = "Outline style", Group = "Notification outline", Kind = SanStyleKind.Select, Options = new[] { "solid", "dashed", "dotted" }, Default = "solid" });
            list.Add(new SanStyleOption { Key = "outlinecolor", Label = "Outline colour", Group = "Notification outline", Kind = SanStyleKind.Color, Default = "#8A2BE2" });
            list.Add(new SanStyleOption { Key = "outlinewidth", Label = "Outline width", Group = "Notification outline", Kind = SanStyleKind.Range, Min = 5, Max = 100, Step = 5, Unit = "%", Default = "50" });
            list.Add(new SanStyleOption { Key = "glow", Label = "Glow", Group = "Glow", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "glowcolor", Label = "Glow colour", Group = "Glow", Kind = SanStyleKind.Color, Default = "#8A2BE2" });
            list.Add(new SanStyleOption { Key = "glowsize", Label = "Glow size", Group = "Glow", Kind = SanStyleKind.Range, Min = 5, Max = 100, Step = 5, Unit = "%", Default = "50" });
            list.Add(new SanStyleOption { Key = "glowx", Label = "Glow horizontal offset", Group = "Glow", Kind = SanStyleKind.Range, Min = -50, Max = 50, Step = 5, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "glowy", Label = "Glow vertical offset", Group = "Glow", Kind = SanStyleKind.Range, Min = -50, Max = 50, Step = 5, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "glowanim", Label = "Glow animation", Group = "Glow", Kind = SanStyleKind.Select, Options = new[] { "off", "pulse", "double", "focus", "orbit", "fluorescent", "rainbow" }, Default = "off" });
            list.Add(new SanStyleOption { Key = "glowspeed", Label = "Glow animation speed", Group = "Glow", Kind = SanStyleKind.Range, Min = 5, Max = 125, Step = 5, Default = "50", Tip = "Lower is faster." });
            list.Add(new SanStyleOption { Key = "glowrarity", Label = "Glow colour follows rarity", Group = "Glow", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "glowcolorbronze", Label = "Glow colour: common", Group = "Glow", Kind = SanStyleKind.Color, Default = "#A05526" });
            list.Add(new SanStyleOption { Key = "glowcolorsilver", Label = "Glow colour: uncommon", Group = "Glow", Kind = SanStyleKind.Color, Default = "#828282" });
            list.Add(new SanStyleOption { Key = "glowcolorgold", Label = "Glow colour: rare", Group = "Glow", Kind = SanStyleKind.Color, Default = "#B4904A" });
            list.Add(new SanStyleOption { Key = "iconscale", Label = "Icon scale", Group = "Achievement icon", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "iconroundness", Label = "Icon corner roundness", Group = "Achievement icon", Kind = SanStyleKind.Range, Min = 0, Max = 100, Step = 5, Unit = "%", Default = "0" });
            list.Add(new SanStyleOption { Key = "iconanim", Label = "Rare icon border and animation", Group = "Achievement icon", Kind = SanStyleKind.Bool, Default = "true" });
            list.Add(new SanStyleOption { Key = "iconshadowcolor", Label = "Rare icon shadow colour", Group = "Achievement icon", Kind = SanStyleKind.Color, Default = "#FFB84E99" });
            list.Add(new SanStyleOption { Key = "iconanimcolor", Label = "Rare icon animation colour", Group = "Achievement icon", Kind = SanStyleKind.Color, Default = "#FFB84E" });
            list.Add(new SanStyleOption { Key = "usegameicon", Label = "Use the game icon instead", Group = "Achievement icon", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "gameicontype", Label = "Game icon type", Group = "Achievement icon", Kind = SanStyleKind.Select, Options = new[] { "icon", "logo" }, Default = "icon" });
            list.Add(new SanStyleOption { Key = "showiconborder", Label = "Icon border", Group = "Achievement icon", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "iconborderrarity", Label = "Icon border follows rarity", Group = "Achievement icon", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "iconborderpos", Label = "Icon border position", Group = "Achievement icon", Kind = SanStyleKind.Select, Options = new[] { "front", "back" }, Default = "front" });
            list.Add(new SanStyleOption { Key = "iconborderscale", Label = "Icon border scale", Group = "Achievement icon", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "iconborderx", Label = "Icon border horizontal offset", Group = "Achievement icon", Kind = SanStyleKind.Range, Min = -5, Max = 5, Step = 0.5, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "iconbordery", Label = "Icon border vertical offset", Group = "Achievement icon", Kind = SanStyleKind.Range, Min = -5, Max = 5, Step = 0.5, Unit = "px", Default = "0" });
            list.Add(new SanStyleOption { Key = "logoscale", Label = "Logo scale", Group = "Logo and decoration", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "decorationscale", Label = "Decoration scale", Group = "Logo and decoration", Kind = SanStyleKind.Range, Min = 50, Max = 150, Step = 5, Unit = "%", Default = "100" });
            list.Add(new SanStyleOption { Key = "decorationshadow", Label = "Decoration shadow", Group = "Logo and decoration", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "replacelogo", Label = "Replace the logo with the decoration", Group = "Logo and decoration", Kind = SanStyleKind.Bool, Default = "false" });
            list.Add(new SanStyleOption { Key = "mask", Label = "Mask", Group = "Logo and decoration", Kind = SanStyleKind.Bool, Default = "false", Tip = "Masks parts of the notification with the theme's mask image." });
            list.Add(new SanStyleOption { Key = "showhiddenicon", Label = "Show the hidden-achievement icon", Group = "Logo and decoration", Kind = SanStyleKind.Bool, Default = "true" });
            list.Add(new SanStyleOption { Key = "scrollrepeat", Label = "Scrolling text repeats", Group = "Logo and decoration", Kind = SanStyleKind.Range, Min = 1, Max = 3, Step = 1, Unit = "x", Default = "1" });

            return list;
        }
    }
}
