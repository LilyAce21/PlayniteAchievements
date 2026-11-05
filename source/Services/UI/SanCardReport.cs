using System;
using System.Globalization;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The page's card report, <c>san-card|left|top|width|height|radius|opacity|viewW|viewH</c>:
    /// the visible card's box, corner radius and opacity in CSS pixels of the page viewport.
    /// </summary>
    internal struct SanCardReport
    {
        public double Left, Top, Width, Height, Radius, Opacity, ViewWidth, ViewHeight;

        public static bool TryParse(string message, out SanCardReport report)
        {
            report = default(SanCardReport);
            if (!SanLiveBackdrop.IsCardReport(message))
            {
                return false;
            }

            var parts = message.Split('|');
            if (parts.Length < 9)
            {
                return false;
            }

            var v = new double[8];
            for (var i = 0; i < v.Length; i++)
            {
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) ||
                    double.IsNaN(v[i]) || double.IsInfinity(v[i]))
                {
                    return false;
                }
            }

            report = new SanCardReport
            {
                Left = v[0],
                Top = v[1],
                Width = v[2],
                Height = v[3],
                Radius = Math.Max(0, v[4]),
                Opacity = Math.Max(0, Math.Min(1, v[5])),
                ViewWidth = v[6],
                ViewHeight = v[7]
            };
            return true;
        }
    }
}
