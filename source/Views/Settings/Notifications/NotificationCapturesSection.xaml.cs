using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notification settings: the Captures page. Unlock screenshots (three independent variants)
    /// and unlock recordings, plus the shared capture delay. Each feature has its own master
    /// switch and none of them is gated on the notifications master switch, matching
    /// <see cref="PlayniteAchievements.Services.UI.ProviderNotificationPolicy"/>, which ANDs
    /// captures against their own masters only.
    /// </summary>
    public partial class NotificationCapturesSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly GameNotificationOverrideScope<GameCaptureOverride> _gameScope;

        public NotificationCapturesSection()
        {
            InitializeComponent();
        }

        internal NotificationCapturesSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateRarityTexts);

            UpdateRarityTexts();
        }

        /// <summary>
        /// Game mode: edits the game's own <see cref="GameCaptureOverride"/> for
        /// <paramref name="gameId"/>, with its own capture folder, and hides the settings that stay
        /// app-wide (file naming, resolutions, folders, clip length, quality and audio sources).
        /// </summary>
        internal NotificationCapturesSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Guid gameId,
            string providerKey)
            : this(settings, plugin)
        {
            _gameScope = new GameNotificationOverrideScope<GameCaptureOverride>(
                plugin.GameCustomDataStore,
                gameId,
                data => data.CaptureOverride,
                (data, value) => data.CaptureOverride = value,
                () => GameCaptureOverride.FromEffective(
                    settings.Persisted,
                    settings.Persisted?.GetProviderNotificationOverride(providerKey)),
                value => value.Clone(),
                plugin.PlayniteApi,
                logger);
            _gameScope.Changed += (s, e) => ApplyGameScope();

            ContentPanel.Margin = new Thickness(0);
            GameCustomizeCheckBox.Visibility = Visibility.Visible;
            GameFolderPanel.Visibility = Visibility.Visible;
            ScreenshotGlobalPanel.Visibility = Visibility.Collapsed;
            RecordingGlobalPanel.Visibility = Visibility.Collapsed;
            AudioGlobalPanel.Visibility = Visibility.Collapsed;

            _gameScope.Load();
        }

        /// <summary>The rarity filters the selectors edit: the game's own in game mode, else the globals.</summary>
        private IUnlockCaptureRarities RarityTarget =>
            _gameScope != null ? (IUnlockCaptureRarities)_gameScope.Current : _settings?.Persisted;

        /// <summary>Re-reads the game's capture settings after its custom data changed elsewhere.</summary>
        internal void RefreshGameData()
        {
            _gameScope?.Load();
        }

        private void ApplyGameScope()
        {
            SettingsBody.DataContext = new GameNotificationOverrideContext<GameCaptureOverride>(_gameScope.Current);
            SettingsBody.IsEnabled = _gameScope.IsCustomized;
            GameCustomizeCheckBox.IsChecked = _gameScope.IsCustomized;
            UpdateRarityTexts();
        }

        private void GameCustomizeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_gameScope != null && !_gameScope.SetCustomized(GameCustomizeCheckBox.IsChecked == true))
            {
                GameCustomizeCheckBox.IsChecked = _gameScope.IsCustomized;
            }
        }

        private void GameCaptureDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var target = _gameScope?.IsCustomized == true ? _gameScope.Current : null;
            if (target == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                target.CaptureDirectory = selected;
            }
        }

        public static readonly DependencyProperty CleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(CleanRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string CleanRaritiesText
        {
            get => (string)GetValue(CleanRaritiesTextProperty);
            set => SetValue(CleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty WithToastRaritiesTextProperty =
            DependencyProperty.Register(nameof(WithToastRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string WithToastRaritiesText
        {
            get => (string)GetValue(WithToastRaritiesTextProperty);
            set => SetValue(WithToastRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty FramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(FramedRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string FramedRaritiesText
        {
            get => (string)GetValue(FramedRaritiesTextProperty);
            set => SetValue(FramedRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingRaritiesText
        {
            get => (string)GetValue(RecordingRaritiesTextProperty);
            set => SetValue(RecordingRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingCleanRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingCleanRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingCleanRaritiesText
        {
            get => (string)GetValue(RecordingCleanRaritiesTextProperty);
            set => SetValue(RecordingCleanRaritiesTextProperty, value);
        }

        public static readonly DependencyProperty RecordingFramedRaritiesTextProperty =
            DependencyProperty.Register(nameof(RecordingFramedRaritiesText), typeof(string), typeof(NotificationCapturesSection),
                new PropertyMetadata(string.Empty));

        public string RecordingFramedRaritiesText
        {
            get => (string)GetValue(RecordingFramedRaritiesTextProperty);
            set => SetValue(RecordingFramedRaritiesTextProperty, value);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e?.PropertyName)
            {
                case nameof(PersistedSettings.UnlockScreenshotCleanRarities):
                case nameof(PersistedSettings.UnlockScreenshotWithToastRarities):
                case nameof(PersistedSettings.UnlockScreenshotFramedRarities):
                case nameof(PersistedSettings.UnlockRecordingRarities):
                case nameof(PersistedSettings.UnlockRecordingCleanRarities):
                case nameof(PersistedSettings.UnlockRecordingFramedRarities):
                    UpdateRarityTexts();
                    break;
            }
        }

        private void CleanRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockScreenshotCleanRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockScreenshotCleanRarities = value; } });
        }

        private void WithToastRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockScreenshotWithToastRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockScreenshotWithToastRarities = value; } });
        }

        private void FramedRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockScreenshotFramedRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockScreenshotFramedRarities = value; } });
        }

        private void RecordingRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockRecordingRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockRecordingRarities = value; } });
        }

        private void RecordingCleanRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockRecordingCleanRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockRecordingCleanRarities = value; } });
        }

        private void RecordingFramedRaritiesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRaritySelector(
                sender as Button,
                () => RarityTarget?.UnlockRecordingFramedRarities ?? RaritySelection.All,
                value => { if (RarityTarget != null) { RarityTarget.UnlockRecordingFramedRarities = value; } });
        }

        private void OpenRaritySelector(Button button, Func<RaritySelection> get, Action<RaritySelection> set)
        {
            RaritySelectorMenu.Open(button, get, set, UpdateRarityTexts);
        }

        private void UpdateRarityTexts()
        {
            var persisted = RarityTarget;
            CleanRaritiesText = FormatRarities(persisted?.UnlockScreenshotCleanRarities ?? RaritySelection.All);
            WithToastRaritiesText = FormatRarities(persisted?.UnlockScreenshotWithToastRarities ?? RaritySelection.All);
            FramedRaritiesText = FormatRarities(persisted?.UnlockScreenshotFramedRarities ?? RaritySelection.All);
            RecordingRaritiesText = FormatRarities(persisted?.UnlockRecordingRarities ?? RaritySelection.All);
            RecordingCleanRaritiesText = FormatRarities(persisted?.UnlockRecordingCleanRarities ?? RaritySelection.All);
            RecordingFramedRaritiesText = FormatRarities(persisted?.UnlockRecordingFramedRarities ?? RaritySelection.All);
        }

        private static string FormatRarities(RaritySelection selection)
        {
            return RaritySelectorMenu.Format(selection);
        }

        private void ScreenshotDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockScreenshotDirectory = selected;
            }
        }

        private void RecordingDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockRecordingDirectory = selected;
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _gameScope?.Dispose();
        }
    }
}
