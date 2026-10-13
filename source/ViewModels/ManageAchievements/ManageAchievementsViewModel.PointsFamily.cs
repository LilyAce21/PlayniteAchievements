using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Which points card the game's points count toward: Default follows the family its provider
    /// implies, a family redirects them, and None counts them toward no card.
    /// </summary>
    public sealed partial class ManageAchievementsViewModel
    {
        private const string PointsFamilyDefaultValue = "";

        public sealed class PointsFamilyOption
        {
            public PointsFamilyOption(string value, string displayName)
            {
                Value = value;
                DisplayName = displayName;
            }

            public string Value { get; }

            public string DisplayName { get; }
        }

        private IReadOnlyList<PointsFamilyOption> _pointsFamilyOptions = Array.Empty<PointsFamilyOption>();
        private string _pointsFamily = PointsFamilyDefaultValue;

        public IReadOnlyList<PointsFamilyOption> PointsFamilyOptions
        {
            get => _pointsFamilyOptions;
            private set => SetValue(ref _pointsFamilyOptions, value);
        }

        /// <summary>The stored override: empty for Default, a family key, or PointsFamilies.NoneOverride.</summary>
        public string PointsFamily
        {
            get => _pointsFamily;
            set
            {
                var normalized = NormalizePointsFamilySelection(value);
                if (!HasGame ||
                    _achievementOverridesService == null ||
                    string.Equals(normalized, _pointsFamily, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pointsFamily = normalized;
                OnPropertyChanged();
                _achievementOverridesService.SetPointsFamily(_gameId, normalized);
                RefreshCustomDataState();
            }
        }

        /// <summary>Rebuilds the choices and the selection from the stored record and the game's provider.</summary>
        private void ReloadPointsFamilyState(GameCustomDataFile customData, string effectiveProviderKey)
        {
            var stored = NormalizePointsFamilySelection(customData?.PointsFamily);
            var automatic = PointsFamilies.FromProviderKey(effectiveProviderKey);
            var defaultName = automatic == null
                ? L("LOCNone")
                : PointsFamilyNames.GetLabel(automatic);

            var options = new List<PointsFamilyOption>
            {
                new PointsFamilyOption(
                    PointsFamilyDefaultValue,
                    string.Format(L("LOCPlayAch_Format_Pair"), L("LOCDefault"), defaultName))
            };

            var storedFamily = PointsFamilies.IsNoneOverride(stored) ? null : stored;
            options.AddRange(ScoreCardChoices
                .BuildFamilies(automatic, storedFamily)
                .Select(family => new PointsFamilyOption(family, PointsFamilyNames.GetLabel(family)))
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            options.Add(new PointsFamilyOption(PointsFamilies.NoneOverride, L("LOCNone")));

            PointsFamilyOptions = options;
            SetValue(ref _pointsFamily, stored, nameof(PointsFamily));
        }

        private static string NormalizePointsFamilySelection(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return PointsFamilyDefaultValue;
            }

            return PointsFamilies.IsNoneOverride(value)
                ? PointsFamilies.NoneOverride
                : PointsFamilies.Normalize(value) ?? PointsFamilyDefaultValue;
        }
    }
}
