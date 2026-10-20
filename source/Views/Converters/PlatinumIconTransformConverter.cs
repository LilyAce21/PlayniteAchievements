using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Maps an icon resource key to a render transform that enlarges the Platinum trophy by the
    /// user's "Platinum icon size" setting. Other icons (and 100%) get no transform. Pair with
    /// RenderTransformOrigin="0.5,1" so the icon grows upward and stays bottom-aligned.
    /// </summary>
    public class PlatinumIconTransformConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value as string;
            if (string.Equals(key, "TrophyPlatinum", StringComparison.Ordinal) ||
                string.Equals(key, "CapstonePlatinum", StringComparison.Ordinal))
            {
                var scale = RarityAppearanceHelper.PlatinumIconScale;
                if (scale > 1.0)
                {
                    return new ScaleTransform(scale, scale);
                }
            }

            return Transform.Identity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
