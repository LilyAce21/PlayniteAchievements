using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;
using PlayniteAchievements.Views.Showcase;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: General section. Hosts grid defaults, the achievement icon cache options
    /// and the reset-to-defaults action. Spoiler masking lives in <see cref="SpoilersSection"/>.
    /// </summary>
    public partial class DisplayGeneralSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly Action _onDisplaySettingsReset;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public DisplayGeneralSection()
        {
            InitializeComponent();
        }

        internal DisplayGeneralSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Action onDisplaySettingsReset)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _onDisplaySettingsReset = onDisplaySettingsReset;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                OnSettingsReloaded);

            UpdateGlowTierTexts();
            UpdateScoreCardNames();
            BuildRarityIconOverrideRows();
        }

        /// <summary>The first Overview score card's type name, labeling its show toggle.</summary>
        public static readonly DependencyProperty ScoreCard1NameProperty =
            DependencyProperty.Register(nameof(ScoreCard1Name), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string ScoreCard1Name
        {
            get => (string)GetValue(ScoreCard1NameProperty);
            set => SetValue(ScoreCard1NameProperty, value);
        }

        /// <summary>The second Overview score card's type name, labeling its show toggle.</summary>
        public static readonly DependencyProperty ScoreCard2NameProperty =
            DependencyProperty.Register(nameof(ScoreCard2Name), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string ScoreCard2Name
        {
            get => (string)GetValue(ScoreCard2NameProperty);
            set => SetValue(ScoreCard2NameProperty, value);
        }

        /// <summary>Summary text for the soft-glow tier selector button.</summary>
        public static readonly DependencyProperty SoftGlowTiersTextProperty =
            DependencyProperty.Register(nameof(SoftGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string SoftGlowTiersText
        {
            get => (string)GetValue(SoftGlowTiersTextProperty);
            set => SetValue(SoftGlowTiersTextProperty, value);
        }

        /// <summary>Summary text for the ray tier selector button.</summary>
        public static readonly DependencyProperty RayGlowTiersTextProperty =
            DependencyProperty.Register(nameof(RayGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string RayGlowTiersText
        {
            get => (string)GetValue(RayGlowTiersTextProperty);
            set => SetValue(RayGlowTiersTextProperty, value);
        }

        private readonly List<Action> _iconRowRefreshers = new List<Action>();

        private void BuildRarityIconOverrideRows()
        {
            if (RarityIconOverridesHost == null || _settings == null)
            {
                return;
            }

            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Rarity_Common"), o => o.Common, (o, v) => o.Common = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon"), o => o.Uncommon, (o, v) => o.Uncommon = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Rarity_Rare"), o => o.Rare, (o, v) => o.Rare = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare"), o => o.UltraRare, (o, v) => o.UltraRare = v);
            AddIconRow("Completed", o => o.Completed, (o, v) => o.Completed = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze"), o => o.TrophyBronze, (o, v) => o.TrophyBronze = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Trophy_Silver"), o => o.TrophySilver, (o, v) => o.TrophySilver = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Trophy_Gold"), o => o.TrophyGold, (o, v) => o.TrophyGold = v);
            AddIconRow(ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum"), o => o.TrophyPlatinum, (o, v) => o.TrophyPlatinum = v);
        }

        private void AddIconRow(
            string label,
            Func<RarityIconOverrideSettings, string> read,
            Action<RarityIconOverrideSettings, string> write)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var path = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 0, 6, 0) };
            var browse = new Button { Content = "Browse...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            var clear = new Button { Content = "Default", Padding = new Thickness(10, 2, 10, 2) };
            Grid.SetColumn(path, 1);
            Grid.SetColumn(browse, 2);
            Grid.SetColumn(clear, 3);
            grid.Children.Add(name);
            grid.Children.Add(path);
            grid.Children.Add(browse);
            grid.Children.Add(clear);

            void Refresh()
            {
                var current = read(_settings?.Persisted?.RarityIconOverrides ?? RarityIconOverrideSettings.CreateDefault());
                path.Text = string.IsNullOrWhiteSpace(current) ? "(built-in icon)" : current;
                clear.IsEnabled = !string.IsNullOrWhiteSpace(current);
            }

            void Commit(string value)
            {
                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    return;
                }

                // The property setter clones, so edit a copy and assign it back to raise the change.
                var next = (persisted.RarityIconOverrides ?? RarityIconOverrideSettings.CreateDefault()).Clone();
                write(next, value);
                persisted.RarityIconOverrides = next;
                Refresh();
            }

            browse.Click += (s, e) =>
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|PNG (*.png)|*.png|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Commit(dialog.FileName);
                }
            };
            clear.Click += (s, e) => Commit(null);

            _iconRowRefreshers.Add(Refresh);
            Refresh();
            RarityIconOverridesHost.Children.Add(grid);
        }

        private void RefreshRarityIconOverrideRows()
        {
            foreach (var refresh in _iconRowRefreshers)
            {
                refresh();
            }
        }

        private void SoftGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowSoftTiers ?? RaritySelectionExtensions.DefaultSoftGlowTiers,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowSoftTiers = value; } },
                UpdateGlowTierTexts,
                includeCompleted: true);
        }

        private void RayGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowRayTiers = value; } },
                UpdateGlowTierTexts,
                includeCompleted: true);
        }

        private void UpdateGlowTierTexts()
        {
            var persisted = _settings?.Persisted;
            SoftGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowSoftTiers ?? RaritySelectionExtensions.DefaultSoftGlowTiers,
                includeCompleted: true);
            RayGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                includeCompleted: true);
        }

        private void OnSettingsReloaded()
        {
            UpdateGlowTierTexts();
            UpdateScoreCardNames();
            RefreshRarityIconOverrideRows();
        }

        private void UpdateScoreCardNames()
        {
            var persisted = _settings?.Persisted;
            ScoreCard1Name = ShowcaseUiText.ScoreCardTypeName(
                ShowcaseWidgetOptions.GetScoreCardType(persisted?.OverviewScoreCard1));
            ScoreCard2Name = ShowcaseUiText.ScoreCardTypeName(
                ShowcaseWidgetOptions.GetScoreCardType(persisted?.OverviewScoreCard2));
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.ShowCompletedProgressColoring) ||
                e.PropertyName == nameof(PersistedSettings.TintMissableLocks))
            {
                RarityAppearanceHelper.ApplyBadgeApplicationResources(_settings?.Persisted);
            }
            else if (e.PropertyName == nameof(PersistedSettings.OverviewScoreCard1) ||
                     e.PropertyName == nameof(PersistedSettings.OverviewScoreCard2))
            {
                UpdateScoreCardNames();
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info("Resetting Display tab settings to defaults.");

                _settings.Persisted.ResetDisplaySettingsToDefaults();
                _onDisplaySettingsReset?.Invoke();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
