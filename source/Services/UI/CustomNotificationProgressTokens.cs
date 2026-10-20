using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>Expands one notification's progress without modifying saved style templates.</summary>
    internal static class CustomNotificationProgressTokens
    {
        private static readonly Regex Tokens = new Regex(
            @"<(progress|progressCurrent|progressMax|progressPercent)>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string Expand(string template, int? current, int? maximum)
        {
            if (string.IsNullOrEmpty(template)) return template;
            var hasProgress = current.HasValue && maximum > 0;
            var count = hasProgress ? Math.Min(Math.Max(current.Value, 0), maximum.Value) : 0;
            return Tokens.Replace(template, match =>
            {
                if (!hasProgress) return string.Empty;
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "progresscurrent": return count.ToString(CultureInfo.InvariantCulture);
                    case "progressmax": return maximum.Value.ToString(CultureInfo.InvariantCulture);
                    case "progresspercent": return (count * 100d / maximum.Value).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                    default: return count.ToString(CultureInfo.InvariantCulture) + "/" + maximum.Value.ToString(CultureInfo.InvariantCulture);
                }
            });
        }

        internal static LocalSettings CreateSettingsCopy(LocalSettings settings, int? current, int? maximum, bool isProgressUpdate = false)
        {
            // Capture and preview may share the already-prepared live notification settings.
            if (settings?.AchievementProgressPrepared == true) return settings;
            var copy = (LocalSettings)(settings ?? new LocalSettings()).Clone();
            var lines = new[] { copy.OverlayCustomTitleTemplate, copy.OverlayCustomGameNameTemplate,
                copy.OverlayCustomAchievementTemplate, copy.OverlayCustomLine4Template,
                copy.OverlayCustomLine5Template, copy.OverlayCustomLine6Template };
            var visible = new[] { copy.OverlayCustomShowLine1, copy.OverlayCustomShowGameName,
                copy.OverlayCustomShowMeta, copy.OverlayCustomShowLine4,
                copy.OverlayCustomShowLine5, copy.OverlayCustomShowLine6 };
            var indices = Enumerable.Range(0, lines.Length).Where(i => visible[i]).ToList();
            if (current.HasValue && maximum > 0)
            {
                // Prefer the achievement's name wherever the user's layout puts it.
                var target = indices.Where(i => (lines[i] ?? string.Empty).IndexOf("<achievementName>", StringComparison.OrdinalIgnoreCase) >= 0)
                    .DefaultIfEmpty(-1).First();
                if (target >= 0)
                {
                    // Progress on another line must not change what the name displays.
                    var hasExplicitCount = Regex.IsMatch(lines[target], @"<progress>", RegexOptions.IgnoreCase) ||
                        (Regex.IsMatch(lines[target], @"<progressCurrent>", RegexOptions.IgnoreCase) &&
                         Regex.IsMatch(lines[target], @"<progressMax>", RegexOptions.IgnoreCase));
                    if (!hasExplicitCount)
                        lines[target] = Regex.Replace(lines[target], "<achievementName>",
                            match => match.Value + " (<progress>)", RegexOptions.IgnoreCase);
                }
                else if (!indices.Any(i => Tokens.IsMatch(lines[i] ?? string.Empty)))
                {
                    target = indices.Where(i => !string.IsNullOrWhiteSpace(lines[i])).DefaultIfEmpty(-1).First();
                    if (target < 0) target = indices.DefaultIfEmpty(-1).First();
                    if (target >= 0) lines[target] = string.IsNullOrWhiteSpace(lines[target])
                        ? "<progress>" : lines[target] + " (<progress>)";
                }
            }
            copy.OverlayCustomTitleTemplate = lines[0];
            copy.OverlayCustomGameNameTemplate = lines[1];
            copy.OverlayCustomAchievementTemplate = lines[2];
            copy.OverlayCustomLine4Template = lines[3];
            copy.OverlayCustomLine5Template = lines[4];
            copy.OverlayCustomLine6Template = lines[5];
            copy.OverlayCustomTitleTemplate = Expand(copy.OverlayCustomTitleTemplate, current, maximum);
            copy.OverlayCustomGameNameTemplate = Expand(copy.OverlayCustomGameNameTemplate, current, maximum);
            copy.OverlayCustomAchievementTemplate = Expand(copy.OverlayCustomAchievementTemplate, current, maximum);
            copy.OverlayCustomMetaTemplate = Expand(copy.OverlayCustomMetaTemplate, current, maximum);
            copy.OverlayCustomLine4Template = Expand(copy.OverlayCustomLine4Template, current, maximum);
            copy.OverlayCustomLine5Template = Expand(copy.OverlayCustomLine5Template, current, maximum);
            copy.OverlayCustomLine6Template = Expand(copy.OverlayCustomLine6Template, current, maximum);
            if (isProgressUpdate)
            {
                copy.EnableInAppUnlockNotifications = false;
                copy.UnlockSoundLeadMilliseconds = 0;
            }
            copy.AchievementProgressPrepared = true;
            return copy;
        }
    }
}
