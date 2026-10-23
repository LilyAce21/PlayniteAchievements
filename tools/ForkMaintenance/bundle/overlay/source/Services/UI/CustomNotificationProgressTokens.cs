using System;
using System.Globalization;
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
            template = ExpandOptionalGroups(template, token => Tokens.IsMatch(token)
                ? Expand(token, current, maximum) : null, deferUnknown: true);
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

        // Resolve conditions before substituting values, so user data cannot become template syntax.
        // A null resolver result means unknown; preparation defers these to the final renderer.
        internal static string ExpandOptionalGroups(string template, Func<string, string> resolve, bool deferUnknown = false)
        {
            if (string.IsNullOrEmpty(template)) return template;
            return Regex.Replace(template, @"\[\[([^\[\]]*)\]\]", group =>
            {
                var content = group.Groups[1].Value;
                if (content.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = content.Substring(3).Split(new[] { "::" }, StringSplitOptions.None);
                    if (parts.Length < 2 || parts.Length > 3) return string.Empty;
                    var condition = EvaluateCondition(parts[0], resolve, deferUnknown);
                    if (!condition.HasValue)
                    {
                        // Keep known tests as booleans so progress substitution cannot destroy a deferred header.
                        var clauses = Regex.Split(parts[0], @"(\s+&&\s+|\s+\|\|\s+)");
                        for (var i = 0; i < clauses.Length; i += 2)
                        {
                            var known = EvaluateCondition(clauses[i], resolve, true);
                            if (known.HasValue) clauses[i] = known.Value ? "<conditionTrue> hasValue" : "<conditionFalse> hasValue";
                        }
                        parts[0] = string.Concat(clauses);
                        return "[[if " + string.Join("::", parts) + "]]";
                    }
                    return condition.Value ? parts[1] : (parts.Length == 3 ? parts[2] : string.Empty);
                }
                var unknown = false;
                foreach (Match token in Regex.Matches(content, @"<[a-zA-Z0-9]+>"))
                {
                    var value = resolve(token.Value);
                    if (value == null && deferUnknown) unknown = true;
                    else if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                }
                return unknown ? group.Value : content;
            });
        }

        internal static bool? EvaluateCondition(string expression, Func<string, string> resolve, bool deferUnknown = false)
        {
            // AND binds more tightly than OR. Unknown values are deferred during progress preparation.
            bool? result = false;
            foreach (var alternative in Regex.Split(expression, @"\s+\|\|\s+"))
            {
                bool? conjunction = true;
                foreach (var clause in Regex.Split(alternative, @"\s+&&\s+"))
                {
                    var match = Regex.Match(clause.Trim(), @"^(<[a-zA-Z0-9]+>)\s+(hasValue|empty|eq|ne|contains|gt|gte|lt|lte)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
                    if (!match.Success) return false;
                    var token = match.Groups[1].Value;
                    var value = token == "<conditionTrue>" ? "true" : token == "<conditionFalse>" ? "" : resolve(token);
                    bool? test = value == null && deferUnknown ? (bool?)null
                        : CompareCondition(value ?? string.Empty, match.Groups[2].Value, match.Groups[3].Value);
                    conjunction = conjunction == false || test == false ? false
                        : conjunction == null || test == null ? (bool?)null : true;
                }
                result = result == true || conjunction == true ? true
                    : result == null || conjunction == null ? (bool?)null : false;
            }
            return result;
        }

        private static bool CompareCondition(string value, string operation, string expected)
        {
            value = value.Trim();
            expected = expected.Trim();
            switch (operation.ToLowerInvariant())
            {
                case "hasvalue": return value.Length > 0;
                case "empty": return value.Length == 0;
                case "eq": return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
                case "ne": return !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
                case "contains": return value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            double actualNumber, expectedNumber;
            if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out actualNumber) ||
                !double.TryParse(expected.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out expectedNumber) ||
                double.IsNaN(actualNumber) || double.IsInfinity(actualNumber) || double.IsNaN(expectedNumber) || double.IsInfinity(expectedNumber)) return false;
            switch (operation.ToLowerInvariant())
            {
                case "gt": return actualNumber > expectedNumber;
                case "gte": return actualNumber >= expectedNumber;
                case "lt": return actualNumber < expectedNumber;
                case "lte": return actualNumber <= expectedNumber;
                default: return false;
            }
        }

        private static string PrepareTemplate(string template, int? current, int? maximum)
        {
            var expanded = Expand(template, current, maximum);
            // Preserve an intentionally empty expression until rendering so default text is not substituted.
            return !string.IsNullOrWhiteSpace(template) && string.IsNullOrWhiteSpace(expanded)
                ? "[[]]" : expanded;
        }

        internal static LocalSettings CreatePreviewSettingsCopy(LocalSettings settings, int? current=16, int? maximum=100)
        {
            settings=settings ?? new LocalSettings();
            var counters=new System.Collections.Generic.Dictionary<NotificationBarSource,NotificationBarCounter>
            {
                [NotificationBarSource.GameAchievements]=new NotificationBarCounter { Current=27,Maximum=100 },
                [NotificationBarSource.GamePoints]=new NotificationBarCounter { Current=120,Maximum=500 },
                [NotificationBarSource.GameCollectionScore]=new NotificationBarCounter { Current=180,Maximum=600 },
                [NotificationBarSource.GamePrestigeScore]=new NotificationBarCounter { Current=240,Maximum=800 },
                [NotificationBarSource.CollectionLevelExperience]=new NotificationBarCounter { Current=40,Maximum=100 },
                [NotificationBarSource.PrestigeLevelExperience]=new NotificationBarCounter { Current=65,Maximum=100 }
            };
            return CreateSettingsCopy(settings,settings.OverlayCustomPreviewShowProgress?current:null,settings.OverlayCustomPreviewShowProgress?maximum:null,counters:counters);
        }

        internal static LocalSettings CreateSettingsCopy(LocalSettings settings, int? current, int? maximum, bool isProgressUpdate = false, System.Collections.Generic.Dictionary<NotificationBarSource, NotificationBarCounter> counters = null)
        {
            // Capture and preview may share the already-prepared live notification settings.
            if (settings?.AchievementProgressPrepared == true) return settings;
            var copy = (LocalSettings)(settings ?? new LocalSettings()).Clone();
            copy.OverlayCustomTitleTemplate = PrepareTemplate(copy.OverlayCustomTitleTemplate, current, maximum);
            copy.OverlayCustomGameNameTemplate = PrepareTemplate(copy.OverlayCustomGameNameTemplate, current, maximum);
            copy.OverlayCustomAchievementTemplate = PrepareTemplate(copy.OverlayCustomAchievementTemplate, current, maximum);
            copy.OverlayCustomMetaTemplate = PrepareTemplate(copy.OverlayCustomMetaTemplate, current, maximum);
            copy.OverlayCustomLine4Template = PrepareTemplate(copy.OverlayCustomLine4Template, current, maximum);
            copy.OverlayCustomLine5Template = PrepareTemplate(copy.OverlayCustomLine5Template, current, maximum);
            copy.OverlayCustomLine6Template = PrepareTemplate(copy.OverlayCustomLine6Template, current, maximum);
            if (isProgressUpdate)
            {
                copy.EnableInAppUnlockNotifications = false;
                copy.UnlockSoundLeadMilliseconds = 0;
            }
            copy.NotificationBarCounters = counters;
            copy.AchievementProgressCurrent = current;
            copy.AchievementProgressMaximum = maximum;
            copy.AchievementProgressPrepared = true;
            return copy;
        }
    }
}
