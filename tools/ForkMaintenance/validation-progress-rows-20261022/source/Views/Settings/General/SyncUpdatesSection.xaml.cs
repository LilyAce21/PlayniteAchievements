using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Sync &amp; Updates section. Binds against the inherited settings
    /// DataContext; the code-behind only drives the two saved-filter pickers.
    /// </summary>
    public partial class SyncUpdatesSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _api;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public SyncUpdatesSection()
        {
            InitializeComponent();
        }

        internal SyncUpdatesSection(PlayniteAchievementsSettings settings, IPlayniteAPI api)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _api = api;
            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateFilterTexts);
            UpdateFilterTexts();
        }

        public static readonly DependencyProperty RefreshExclusionFiltersTextProperty =
            DependencyProperty.Register(nameof(RefreshExclusionFiltersText), typeof(string),
                typeof(SyncUpdatesSection), new PropertyMetadata(string.Empty));

        /// <summary>Summary text for the refresh exclusion filter picker.</summary>
        public string RefreshExclusionFiltersText
        {
            get => (string)GetValue(RefreshExclusionFiltersTextProperty);
            set => SetValue(RefreshExclusionFiltersTextProperty, value);
        }

        public static readonly DependencyProperty SummaryExclusionFiltersTextProperty =
            DependencyProperty.Register(nameof(SummaryExclusionFiltersText), typeof(string),
                typeof(SyncUpdatesSection), new PropertyMetadata(string.Empty));

        /// <summary>Summary text for the summary exclusion filter picker.</summary>
        public string SummaryExclusionFiltersText
        {
            get => (string)GetValue(SummaryExclusionFiltersTextProperty);
            set => SetValue(SummaryExclusionFiltersTextProperty, value);
        }

        private void RefreshExclusionFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            FilterPresetSelectorMenu.Open(
                sender as Button,
                _api,
                () => _settings?.Persisted?.RefreshExclusionFilterPresetIds ?? new HashSet<Guid>(),
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RefreshExclusionFilterPresetIds = value; } },
                UpdateFilterTexts);
        }

        private void SummaryExclusionFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            FilterPresetSelectorMenu.Open(
                sender as Button,
                _api,
                () => _settings?.Persisted?.SummaryExclusionFilterPresetIds ?? new HashSet<Guid>(),
                value => { if (_settings?.Persisted != null) { _settings.Persisted.SummaryExclusionFilterPresetIds = value; } },
                UpdateFilterTexts);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(PersistedSettings.RefreshExclusionFilterPresetIds) ||
                e?.PropertyName == nameof(PersistedSettings.SummaryExclusionFilterPresetIds))
            {
                UpdateFilterTexts();
            }
        }

        private void UpdateFilterTexts()
        {
            var persisted = _settings?.Persisted;
            RefreshExclusionFiltersText = FilterPresetSelectorMenu.Format(_api, persisted?.RefreshExclusionFilterPresetIds);
            SummaryExclusionFiltersText = FilterPresetSelectorMenu.Format(_api, persisted?.SummaryExclusionFilterPresetIds);
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }
    }
}
