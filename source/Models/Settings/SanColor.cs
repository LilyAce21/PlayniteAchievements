using System.Globalization;
using System.Windows.Media;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Converts colours between SAN's CSS notation (#RRGGBB or #RRGGBBAA, alpha last) and WPF's
    /// (#AARRGGBB, alpha first), which the plugin's colour picker uses.
    /// </summary>
    public static class SanColor
    {
        /// <summary>A SAN colour as WPF text, or the input unchanged when it is not a hex colour.</summary>
        public static string ToWpf(string san)
        {
            var text = (san ?? string.Empty).Trim();
            if (text.Length == 9 && text[0] == '#' && IsHex(text, 1))
            {
                return "#" + text.Substring(7, 2) + text.Substring(1, 6);
            }

            return text;
        }

        /// <summary>WPF colour text (#AARRGGBB or #RRGGBB) as SAN text; opaque colours drop the alpha.</summary>
        public static string FromWpf(string wpf)
        {
            var text = (wpf ?? string.Empty).Trim();
            if (text.Length == 9 && text[0] == '#' && IsHex(text, 1))
            {
                var alpha = text.Substring(1, 2);
                var rgb = text.Substring(3, 6);
                return string.Equals(alpha, "FF", System.StringComparison.OrdinalIgnoreCase)
                    ? "#" + rgb
                    : "#" + rgb + alpha;
            }

            return text;
        }

        /// <summary>Parses SAN colour text for a preview swatch.</summary>
        public static bool TryParse(string san, out Color color)
        {
            try
            {
                color = (Color)ColorConverter.ConvertFromString(ToWpf(san));
                return true;
            }
            catch
            {
                color = Colors.Transparent;
                return false;
            }
        }

        private static bool IsHex(string text, int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (!uint.TryParse(text[i].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
