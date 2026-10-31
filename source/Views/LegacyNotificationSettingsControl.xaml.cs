// LegacyNotificationSettingsControl.xaml.cs
using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Net;
using System.Globalization;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ThemeMigration;
using Playnite.SDK;
using Playnite.SDK.Models;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinForms = System.Windows.Forms;

namespace PlayniteAchievements.Views
{
    public partial class LegacyNotificationSettingsControl : UserControl, IDisposable
    {
        // -----------------------------
        // Mock data for settings preview
        // -----------------------------

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactListItems;
        private readonly ObservableCollection<NotificationCustomIconOption> _notificationCustomIconOptions = new ObservableCollection<NotificationCustomIconOption>();

        public ObservableCollection<NotificationCustomIconOption> NotificationCustomIconOptions => _notificationCustomIconOptions;

        public IReadOnlyList<string> NotificationFontFamilies { get; } = new[]
        {
            "Default",
            "Segoe UI",
            "Titillium Web",
            "Titillium Web Regular",
            "Roboto",
            "Source Sans Pro Light",
            "Source Sans Pro ExtraLight",
            "Noto Sans",
            "Noto Sans Medium",
            "Noto Sans Light",
            "Noto Sans ExtraLight",
            "Open Sans",
            "Mandali",
            "VT323",
            "JetBrains Mono"
        };

        public sealed class NotificationCustomIconOption
        {
            public string DisplayName { get; set; }
            public string Path { get; set; }
            public string SourcePath { get; set; }
            public ImageSource PreviewSource { get; set; }
            public string SlotUsage { get; set; }
        }

        public sealed class SvgRepoSearchResult
        {
            public string Title { get; set; }
            public string PageUrl { get; set; }
            public string SvgUrl { get; set; }
            public string PreviewUrl { get; set; }
            public ImageSource PreviewSource { get; set; }
        }

        /// <summary>
        /// Gets mock achievement items for compact list preview in settings.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactListItems
        {
            get
            {
                if (_mockCompactListItems == null)
                {
                    _mockCompactListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockCompactListItems(
                            GetShowRarityBar(), GetShowRarityGlow(),
                            GetShowHiddenIcon(), GetShowHiddenTitle(),
                            GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon()));
                }
                return _mockCompactListItems;
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactUnlockedListItems;

        /// <summary>
        /// Gets mock unlocked achievement items for unlocked list preview.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactUnlockedListItems
        {
            get
            {
                if (_mockCompactUnlockedListItems == null)
                {
                    _mockCompactUnlockedListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockUnlockedListItems(
                            GetShowRarityBar(), GetShowRarityGlow(), GetShowLockedIcon()));
                }
                return _mockCompactUnlockedListItems;
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> _mockCompactLockedListItems;

        /// <summary>
        /// Gets mock locked achievement items for locked list preview.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem> MockCompactLockedListItems
        {
            get
            {
                if (_mockCompactLockedListItems == null)
                {
                    _mockCompactLockedListItems = new System.Collections.ObjectModel.ObservableCollection<AchievementDisplayItem>(
                        MockDataHelper.CreateMockLockedListItems(
                            GetShowRarityBar(), GetShowRarityGlow(),
                            GetShowHiddenIcon(), GetShowHiddenTitle(),
                            GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon()));
                }
                return _mockCompactLockedListItems;
            }
        }

        private List<AchievementDisplayItem> _mockDataGridItems;

        /// <summary>
        /// Gets mock achievement items for datagrid preview in settings.
        /// </summary>
        public List<AchievementDisplayItem> MockDataGridItems
        {
            get
            {
                if (_mockDataGridItems == null)
                {
                    _mockDataGridItems = MockDataHelper.CreateMockDataGridItems(
                        GetShowRarityBar(), GetShowRarityGlow(),
                        GetShowHiddenIcon(), GetShowHiddenTitle(),
                        GetShowHiddenDescription(), GetShowHiddenSuffix(), GetShowLockedIcon());
                }
                return _mockDataGridItems;
            }
        }

        private ModernThemeBindings _previewThemeData;
        private ModernThemeBindings _unlockedPreviewThemeData;
        private ModernThemeBindings _hiddenPreviewThemeData;
        private ModernThemeBindings _lockedPreviewThemeData;

        /// <summary>
        /// Gets modern theme bindings populated with mock achievements for modern control previews.
        /// Used by modern controls via ThemeDataOverride binding.
        /// </summary>
        public ModernThemeBindings PreviewThemeData
        {
            get
            {
                if (_previewThemeData == null)
                {
                    _previewThemeData = MockDataHelper.GetPreviewThemeData();
                }
                return _previewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single unlocked achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings UnlockedPreviewThemeData
        {
            get
            {
                if (_unlockedPreviewThemeData == null)
                {
                    _unlockedPreviewThemeData = MockDataHelper.GetUnlockedPreviewThemeData();
                }
                return _unlockedPreviewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single hidden achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings HiddenPreviewThemeData
        {
            get
            {
                if (_hiddenPreviewThemeData == null)
                {
                    _hiddenPreviewThemeData = MockDataHelper.GetHiddenPreviewThemeData();
                }
                return _hiddenPreviewThemeData;
            }
        }

        /// <summary>
        /// Gets modern theme bindings with a single locked achievement for visibility preview.
        /// </summary>
        public ModernThemeBindings LockedPreviewThemeData
        {
            get
            {
                if (_lockedPreviewThemeData == null)
                {
                    _lockedPreviewThemeData = MockDataHelper.GetLockedPreviewThemeData();
                }
                return _lockedPreviewThemeData;
            }
        }

        // Helper methods to get settings values with defaults
        private bool GetShowRarityBar() => _settingsViewModel?.Settings?.Persisted?.ShowCompactListRarityBar ?? true;
        private bool GetShowRarityGlow() => _settingsViewModel?.Settings?.Persisted?.ShowRarityGlow ?? true;
        private bool GetShowHiddenIcon() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenIcon ?? true;
        private bool GetShowHiddenTitle() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenTitle ?? true;
        private bool GetShowHiddenDescription() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenDescription ?? true;
        private bool GetShowHiddenSuffix() => _settingsViewModel?.Settings?.Persisted?.ShowHiddenSuffix ?? true;
        private bool GetShowLockedIcon() => _settingsViewModel?.Settings?.Persisted?.ShowLockedIcon ?? true;

        /// <summary>
        /// Refreshes mock preview items to reflect current settings.
        /// Repopulates collections with new items that have updated visibility settings.
        /// </summary>
        public void RefreshMockPreviews()
        {
            var settings = _settingsViewModel?.Settings?.Persisted;
            if (settings == null) return;

            // Repopulate compact list items
            if (_mockCompactListItems != null)
            {
                _mockCompactListItems.Clear();
                var newItems = MockDataHelper.CreateMockCompactListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactListItems.Add(item);
            }

            // Repopulate unlocked list items
            if (_mockCompactUnlockedListItems != null)
            {
                _mockCompactUnlockedListItems.Clear();
                var newItems = MockDataHelper.CreateMockUnlockedListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactUnlockedListItems.Add(item);
            }

            // Repopulate locked list items
            if (_mockCompactLockedListItems != null)
            {
                _mockCompactLockedListItems.Clear();
                var newItems = MockDataHelper.CreateMockLockedListItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                foreach (var item in newItems)
                    _mockCompactLockedListItems.Add(item);
            }

            // Repopulate datagrid items
            if (_mockDataGridItems != null)
            {
                _mockDataGridItems = MockDataHelper.CreateMockDataGridItems(
                    settings.ShowCompactListRarityBar, settings.ShowRarityGlow,
                    settings.ShowHiddenIcon, settings.ShowHiddenTitle,
                    settings.ShowHiddenDescription, settings.ShowHiddenSuffix, settings.ShowLockedIcon);
                // For List<T>, need to raise property changed - but since binding uses ItemsSource,
                // we'll assign a new list which triggers refresh
            }

            // Refresh the preview modern theme bindings used by modern controls
            _previewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _unlockedPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _hiddenPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
            _lockedPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowRarityGlow, settings.ShowCompactListRarityBar);
        }

        // Theme migration UI state properties
        public static readonly DependencyProperty AvailableThemesProperty =
            DependencyProperty.Register(
                nameof(AvailableThemes),
                typeof(System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null));

        public System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo> AvailableThemes
        {
            get => (System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>)GetValue(AvailableThemesProperty);
            set => SetValue(AvailableThemesProperty, value);
        }

        public static readonly DependencyProperty RevertableThemesProperty =
            DependencyProperty.Register(
                nameof(RevertableThemes),
                typeof(System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null));

        public System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo> RevertableThemes
        {
            get => (System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>)GetValue(RevertableThemesProperty);
            set => SetValue(RevertableThemesProperty, value);
        }

        public static readonly DependencyProperty SelectedThemePathProperty =
            DependencyProperty.Register(
                nameof(SelectedThemePath),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty, OnSelectedThemePathChanged));

        public string SelectedThemePath
        {
            get => (string)GetValue(SelectedThemePathProperty);
            set => SetValue(SelectedThemePathProperty, value);
        }

        public static readonly DependencyProperty SelectedRevertThemePathProperty =
            DependencyProperty.Register(
                nameof(SelectedRevertThemePath),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string SelectedRevertThemePath
        {
            get => (string)GetValue(SelectedRevertThemePathProperty);
            set => SetValue(SelectedRevertThemePathProperty, value);
        }

        public static readonly DependencyProperty HasThemesToMigrateProperty =
            DependencyProperty.Register(
                nameof(HasThemesToMigrate),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool HasThemesToMigrate
        {
            get => (bool)GetValue(HasThemesToMigrateProperty);
            set => SetValue(HasThemesToMigrateProperty, value);
        }

        public static readonly DependencyProperty UseScrollableAchievementsForThemeMigrationProperty =
            DependencyProperty.Register(
                nameof(UseScrollableAchievementsForThemeMigration),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false, OnUseScrollableAchievementsForThemeMigrationChanged));

        public bool UseScrollableAchievementsForThemeMigration
        {
            get => (bool)GetValue(UseScrollableAchievementsForThemeMigrationProperty);
            set => SetValue(UseScrollableAchievementsForThemeMigrationProperty, value);
        }

        public static readonly DependencyProperty HighlightLatestAchievementForThemeMigrationProperty =
            DependencyProperty.Register(
                nameof(HighlightLatestAchievementForThemeMigration),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(true, OnHighlightLatestAchievementForThemeMigrationChanged));

        public bool HighlightLatestAchievementForThemeMigration
        {
            get => (bool)GetValue(HighlightLatestAchievementForThemeMigrationProperty);
            set => SetValue(HighlightLatestAchievementForThemeMigrationProperty, value);
        }

        public static readonly DependencyProperty HasRevertableThemesProperty =
            DependencyProperty.Register(
                nameof(HasRevertableThemes),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool HasRevertableThemes
        {
            get => (bool)GetValue(HasRevertableThemesProperty);
            set => SetValue(HasRevertableThemesProperty, value);
        }

        public static readonly DependencyProperty ShowNoThemesMessageProperty =
            DependencyProperty.Register(
                nameof(ShowNoThemesMessage),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(true));

        public bool ShowNoThemesMessage
        {
            get => (bool)GetValue(ShowNoThemesMessageProperty);
            set => SetValue(ShowNoThemesMessageProperty, value);
        }

        public static readonly DependencyProperty ShowNoRevertableThemesMessageProperty =
            DependencyProperty.Register(
                nameof(ShowNoRevertableThemesMessage),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(true));

        public bool ShowNoRevertableThemesMessage
        {
            get => (bool)GetValue(ShowNoRevertableThemesMessageProperty);
            set => SetValue(ShowNoRevertableThemesMessageProperty, value);
        }

        public static readonly DependencyProperty HasStartPageInstalledProperty =
            DependencyProperty.Register(
                nameof(HasStartPageInstalled),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool HasStartPageInstalled
        {
            get => (bool)GetValue(HasStartPageInstalledProperty);
            set => SetValue(HasStartPageInstalledProperty, value);
        }

        public static readonly DependencyProperty ShowStartPageNotInstalledMessageProperty =
            DependencyProperty.Register(
                nameof(ShowStartPageNotInstalledMessage),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(true));

        public bool ShowStartPageNotInstalledMessage
        {
            get => (bool)GetValue(ShowStartPageNotInstalledMessageProperty);
            set => SetValue(ShowStartPageNotInstalledMessageProperty, value);
        }

        public static readonly DependencyProperty HasStartPageBackupProperty =
            DependencyProperty.Register(
                nameof(HasStartPageBackup),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool HasStartPageBackup
        {
            get => (bool)GetValue(HasStartPageBackupProperty);
            set => SetValue(HasStartPageBackupProperty, value);
        }

        public ObservableCollection<ThemeMigrationElementOption> ThemeMigrationCustomOptions { get; } =
            new ObservableCollection<ThemeMigrationElementOption>();

        public static readonly DependencyProperty LegacyManualImportStatusProperty =
            DependencyProperty.Register(
                nameof(LegacyManualImportStatus),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_CustomRefresh_ProviderStatus_Ready")));

        public string LegacyManualImportStatus
        {
            get => (string)GetValue(LegacyManualImportStatusProperty);
            set => SetValue(LegacyManualImportStatusProperty, value);
        }

        public static readonly DependencyProperty LegacyManualImportBusyProperty =
            DependencyProperty.Register(
                nameof(LegacyManualImportBusy),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false));

        public bool LegacyManualImportBusy
        {
            get => (bool)GetValue(LegacyManualImportBusyProperty);
            set => SetValue(LegacyManualImportBusyProperty, value);
        }

        public static readonly DependencyProperty StartPageActivityScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageActivityScopeText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string StartPageActivityScopeText
        {
            get => (string)GetValue(StartPageActivityScopeTextProperty);
            set => SetValue(StartPageActivityScopeTextProperty, value);
        }

        public static readonly DependencyProperty StartPageProgressScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageProgressScopeText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string StartPageProgressScopeText
        {
            get => (string)GetValue(StartPageProgressScopeTextProperty);
            set => SetValue(StartPageProgressScopeTextProperty, value);
        }

        public static readonly DependencyProperty ViewAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ViewAchievementsHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string ViewAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ViewAchievementsHotkeyButtonTextProperty);
            set => SetValue(ViewAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty ManageAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ManageAchievementsHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string ManageAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ManageAchievementsHotkeyButtonTextProperty);
            set => SetValue(ManageAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty OverviewHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(OverviewHotkeyButtonText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string OverviewHotkeyButtonText
        {
            get => (string)GetValue(OverviewHotkeyButtonTextProperty);
            set => SetValue(OverviewHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty HotkeyCaptureStatusTextProperty =
            DependencyProperty.Register(
                nameof(HotkeyCaptureStatusText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty));

        public string HotkeyCaptureStatusText
        {
            get => (string)GetValue(HotkeyCaptureStatusTextProperty);
            set => SetValue(HotkeyCaptureStatusTextProperty, value);
        }

        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PlayniteAchievementsSettingsViewModel _settingsViewModel;
        private readonly ILogger _logger;
        private readonly ThemeDiscoveryService _themeDiscovery;
        private readonly ThemeMigrationService _themeMigration;
        private readonly StartPageCompatibilityService _startPageCompatibility;
        private readonly ProviderRegistry _providerRegistry;

        /// <summary>
        /// Exposes the Exophase provider settings edit copy so XAML can bind to it via
        /// <c>ElementName=LegacyNotificationSettingsControlRoot</c>.  Populated lazily when the Achievement
        /// Notifications tab is first initialised; may be null if Exophase is not configured.
        /// </summary>
        public Providers.Exophase.ExophaseSettings ExophaseEditSettings { get; private set; }
        public Providers.RetroAchievements.RetroAchievementsSettings RetroAchievementsEditSettings { get; private set; }
        public static readonly DependencyProperty ApiVerificationActiveMonitoringProperty =
            DependencyProperty.Register(
                nameof(ApiVerificationActiveMonitoring),
                typeof(bool),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(false, OnApiVerificationActiveMonitoringChanged));

        public bool ApiVerificationActiveMonitoring
        {
            get => (bool)GetValue(ApiVerificationActiveMonitoringProperty);
            set => SetValue(ApiVerificationActiveMonitoringProperty, value);
        }

        private bool _isRefreshingApiVerificationControls;

        private static void OnApiVerificationActiveMonitoringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
            {
                control.ApplyApiVerificationActiveMonitoring((bool)e.NewValue);
            }
        }

        private readonly ObservableCollection<NotificationSoundOption> _notificationSoundOptions = new ObservableCollection<NotificationSoundOption>();
        private Window _settingsHostWindow;
        private Providers.Local.LocalSettings _notificationPreviewSettings;
        private bool _isRefreshingNotificationSoundSelection;
        private bool _achievementNotificationDebugSettingReady;
        private ICollectionView _providerNavigationView;
        private bool _providerNavigationBuilt;
        private bool _themeMigrationLoaded;
        private HotkeyCaptureTarget? _capturingHotkey;
        private const string DefaultSteamSoundPath = @"Resources\Sounds\Steam.wav";

        private sealed class NotificationSoundOption
        {
            public string DisplayName { get; set; }

            public string SoundPath { get; set; }

            public bool IsDefault { get; set; }
        }






        private enum HotkeyCaptureTarget
        {
            ViewAchievements,
            ManageAchievements,
            Overview
        }

        public static readonly DependencyProperty LegacyProviderNavigationItemsProperty =
            DependencyProperty.Register(
                nameof(LegacyProviderNavigationItems),
                typeof(ObservableCollection<LegacyProviderNavigationItem>),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null));

        public ObservableCollection<LegacyProviderNavigationItem> LegacyProviderNavigationItems
        {
            get => (ObservableCollection<LegacyProviderNavigationItem>)GetValue(LegacyProviderNavigationItemsProperty);
            set => SetValue(LegacyProviderNavigationItemsProperty, value);
        }

        public static readonly DependencyProperty SelectedLegacyProviderNavigationItemProperty =
            DependencyProperty.Register(
                nameof(SelectedLegacyProviderNavigationItem),
                typeof(LegacyProviderNavigationItem),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(null, OnSelectedLegacyProviderNavigationItemChanged));

        public LegacyProviderNavigationItem SelectedLegacyProviderNavigationItem
        {
            get => (LegacyProviderNavigationItem)GetValue(SelectedLegacyProviderNavigationItemProperty);
            set => SetValue(SelectedLegacyProviderNavigationItemProperty, value);
        }

        public static readonly DependencyProperty ProviderSearchTextProperty =
            DependencyProperty.Register(
                nameof(ProviderSearchText),
                typeof(string),
                typeof(LegacyNotificationSettingsControl),
                new PropertyMetadata(string.Empty, OnProviderSearchTextChanged));

        public string ProviderSearchText
        {
            get => (string)GetValue(ProviderSearchTextProperty);
            set => SetValue(ProviderSearchTextProperty, value);
        }

        /// <summary>
        /// Set before opening settings to navigate directly to a provider tab.
        /// Cleared after use.
        /// </summary>
        public static string PendingNavigationProviderKey { get; set; }
        public static string PendingNavigationTabName { get; set; }
        public static string CurrentSelectedTabName { get; private set; }
        public static string CurrentSelectedProviderKey { get; private set; }

        public LegacyNotificationSettingsControl(
            PlayniteAchievementsSettingsViewModel settingsViewModel,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry)
        {
            _settingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));

            _themeDiscovery = new ThemeDiscoveryService(_logger, plugin.PlayniteApi);
            _themeMigration = new ThemeMigrationService(
                _logger,
                _settingsViewModel.Settings,
                () =>
                {
                    _plugin.SavePluginSettings(_settingsViewModel.Settings);
                    NotificationPublisher.ClosePersistentSettingsPreview();
                });
            _startPageCompatibility = new StartPageCompatibilityService(_logger, _plugin, plugin.PlayniteApi);

            Unloaded += LegacyNotificationSettingsControl_Unloaded;

            InitializeComponent();

            // Initialize provider navigation overview
            LegacyProviderNavigationItems = new ObservableCollection<LegacyProviderNavigationItem>();

            // Playnite does not reliably set DataContext for settings views.
            // Bind directly to the settings model so XAML uses {Binding SomeSetting}.
            DataContext = _settingsViewModel.Settings;

            // Initialize theme collections
            AvailableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            RevertableThemes = new System.Collections.ObjectModel.ObservableCollection<ThemeDiscoveryService.ThemeInfo>();
            UseScrollableAchievementsForThemeMigration = _settingsViewModel.Settings.Persisted.ThemeMigrationUseScrollableAchievements;
            HighlightLatestAchievementForThemeMigration = _settingsViewModel.Settings.Persisted.ThemeMigrationHighlightLatestAchievement;
            InitializeThemeMigrationCustomOptions();

            // Subscribe to settings property changes to refresh mock previews
            _settingsViewModel.Settings.Persisted.PropertyChanged += OnSettingsPropertyChanged;
            UpdateStartPageScopeTexts();
            UpdateHotkeyButtonTexts();

            // Debug logging to verify DataContext and Settings values
            ForkLog.Wrap(_logger)?.Info($"LegacyNotificationSettingsControl created. DataContext type: {DataContext?.GetType().Name}");
            ForkLog.Wrap(_logger)?.Info($"Settings.EnablePeriodicUpdates: {_settingsViewModel.Settings.Persisted.EnablePeriodicUpdates}");

            Loaded += (s, e) =>
            {
                // Ensure DataContext is still correct after Playnite initialization
                if (DataContext is not PlayniteAchievementsSettings)
                {
                    ForkLog.Wrap(_logger)?.Info($"DataContext was wrong type in Loaded event: {DataContext?.GetType().Name}, fixing...");
                    DataContext = _settingsViewModel.Settings;
                    ForkLog.Wrap(_logger)?.Info($"DataContext fixed to: {DataContext?.GetType().Name}");
                }
                else
                {
                    ForkLog.Wrap(_logger)?.Info($"DataContext verified correct in Loaded event: {DataContext?.GetType().Name}");
                }

                // Load themes on initial load
                LoadThemes();
                RefreshStartPageCompatibilityState();

                // Initialize Achievement Notifications tab with Local provider settings
                InitializeAchievementNotificationsTab();

                // Build provider navigation items for sidebar
                BuildLegacyProviderNavigationItems();

                // Navigate to a specific provider item if requested (e.g., from auth notification click).
                NavigateToPendingProvider();
            };
        }


        private void LegacyNotificationSettingsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            NotificationPublisher.ClosePersistentSettingsPreview();
        }

        private void NotificationSettingsContent_Loaded(object sender, RoutedEventArgs e)
        {
            var hostWindow = Window.GetWindow(sender as DependencyObject);
            if (ReferenceEquals(_settingsHostWindow, hostWindow))
            {
                return;
            }

            if (_settingsHostWindow != null)
            {
                _settingsHostWindow.Closed -= SettingsHostWindow_Closed;
            }

            _settingsHostWindow = hostWindow;
            if (_settingsHostWindow != null)
            {
                _settingsHostWindow.Closed += SettingsHostWindow_Closed;
            }
        }

        private void SettingsHostWindow_Closed(object sender, EventArgs e)
        {
            StopSanViewPreview(closeOverlay: true);
            if (_settingsHostWindow != null)
            {
                _settingsHostWindow.Closed -= SettingsHostWindow_Closed;
                _settingsHostWindow = null;
            }
        }
















        private void InitializeAchievementNotificationsTab()
        {
            try
            {
                var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
                if (localSettings == null)
                {
                    ForkLog.Wrap(_logger)?.Warn("Could not get Local provider settings for Achievement Notifications tab.");
                    return;
                }

                var achievementNotificationsTab = SettingsTabControl?.Items?.Cast<TabItem>()
                    ?.FirstOrDefault(t => string.Equals(t.Name, "AchievementNotificationsTab", StringComparison.OrdinalIgnoreCase));

                if (achievementNotificationsTab != null)
                {
                    achievementNotificationsTab.DataContext = localSettings;

                    // Populate API-backed provider settings so shared notification controls can update them.
                    ExophaseEditSettings = _providerRegistry?.GetSettingsForEdit("Exophase") as Providers.Exophase.ExophaseSettings;
                    RetroAchievementsEditSettings = _providerRegistry?.GetSettingsForEdit("RetroAchievements") as Providers.RetroAchievements.RetroAchievementsSettings;
                    RefreshApiVerificationControls();

                    if (!ReferenceEquals(_notificationPreviewSettings, localSettings))
                    {
                        if (_notificationPreviewSettings != null)
                        {
                        }

                        _notificationPreviewSettings = localSettings;
                    }

                    RefreshAchievementNotificationControls(localSettings);
                    _achievementNotificationDebugSettingReady = true;
                }
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to initialize Achievement Notifications tab.");
            }
        }

        private void RefreshAchievementNotificationControls(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null)
                return;

            MigrateLegacyCustomSoundPath(localSettings);

            var soundOptions = BuildNotificationSoundOptions(localSettings);

            if (NotificationsBundledUnlockSoundComboBox != null)
            {
                _isRefreshingNotificationSoundSelection = true;
                try
                {
                    _notificationSoundOptions.Clear();
                    foreach (var option in soundOptions)
                    {
                        _notificationSoundOptions.Add(option);
                    }

                    NotificationsBundledUnlockSoundComboBox.ItemsSource = _notificationSoundOptions;

                    var selectedPath = localSettings.EffectiveBundledUnlockSoundPath;
                    var selectedOption = _notificationSoundOptions.FirstOrDefault(option =>
                        string.Equals(option.SoundPath, selectedPath, StringComparison.OrdinalIgnoreCase));

                    if (selectedOption == null)
                    {
                        selectedOption = _notificationSoundOptions.FirstOrDefault();
                    }

                    NotificationsBundledUnlockSoundComboBox.SelectedItem = selectedOption;
                    if (selectedOption != null)
                    {
                        localSettings.BundledUnlockSoundPath = selectedOption.SoundPath;
                    }

                    CollectionProgressSoundComboBox.ItemsSource = _notificationSoundOptions;
                    PrestigeProgressSoundComboBox.ItemsSource = _notificationSoundOptions;
                    if (string.IsNullOrWhiteSpace(localSettings.CollectionProgressNotifications.SoundPath))
                    {
                        localSettings.CollectionProgressNotifications.SoundPath = localSettings.UnlockSoundPath;
                    }
                    if (string.IsNullOrWhiteSpace(localSettings.PrestigeProgressNotifications.SoundPath))
                    {
                        localSettings.PrestigeProgressNotifications.SoundPath = localSettings.UnlockSoundPath;
                    }
                    CollectionProgressSoundComboBox.SelectedValue = localSettings.CollectionProgressNotifications.SoundPath;
                    PrestigeProgressSoundComboBox.SelectedValue = localSettings.PrestigeProgressNotifications.SoundPath;
                }
                finally
                {
                    _isRefreshingNotificationSoundSelection = false;
                }
            }

            UpdateNotificationSoundButtons();
            RefreshOverlayRuntimeControls(localSettings);

            if (NotificationsPollingIntervalSecondsTextBox != null)
            {
                NotificationsPollingIntervalSecondsTextBox.Text = localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
            }

            if (NotificationsUnlockSoundLeadMillisecondsTextBox != null)
            {
                NotificationsUnlockSoundLeadMillisecondsTextBox.Text = localSettings.UnlockSoundLeadMilliseconds.ToString();
            }


            RefreshNotificationCustomIconOptions(localSettings);
        }











        private void NotificationsUnlockSoundLeadMillisecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsUnlockSoundLeadFromTextBox(updateTextBox: false);
        }

        private void NotificationsUnlockSoundLeadMillisecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsUnlockSoundLeadFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsUnlockSoundLeadFromTextBox(bool updateTextBox)
        {
            if (NotificationsUnlockSoundLeadMillisecondsTextBox == null)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            if (int.TryParse(NotificationsUnlockSoundLeadMillisecondsTextBox.Text, out var value))
            {
                localSettings.UnlockSoundLeadMilliseconds = value;
            }

            if (updateTextBox)
            {
                NotificationsUnlockSoundLeadMillisecondsTextBox.Text = localSettings.UnlockSoundLeadMilliseconds.ToString();
            }
        }

        private void RefreshApiVerificationControls()
        {
            _isRefreshingApiVerificationControls = true;
            try
            {
                ApiVerificationActiveMonitoring =
                    ExophaseEditSettings?.EnableActiveMonitoring == true &&
                    RetroAchievementsEditSettings?.EnableActiveMonitoring == true;

                if (ApiVerificationPollingIntervalSecondsTextBox != null)
                {
                    var interval = ExophaseEditSettings?.MonitoringIntervalSeconds
                        ?? RetroAchievementsEditSettings?.MonitoringIntervalSeconds
                        ?? 300;
                    ApiVerificationPollingIntervalSecondsTextBox.Text = interval.ToString();
                }
            }
            finally
            {
                _isRefreshingApiVerificationControls = false;
            }
        }

        private void ApplyApiVerificationActiveMonitoring(bool enabled)
        {
            if (_isRefreshingApiVerificationControls)
            {
                return;
            }

            if (ExophaseEditSettings != null)
            {
                ExophaseEditSettings.EnableActiveMonitoring = enabled;
            }

            if (RetroAchievementsEditSettings != null)
            {
                RetroAchievementsEditSettings.EnableActiveMonitoring = enabled;
            }
        }



        private void NotificationsPollingIntervalSecondsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNotificationsPollingIntervalFromTextBox(updateTextBox: false);
        }

        private void NotificationsPollingIntervalSecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyNotificationsPollingIntervalFromTextBox(updateTextBox: true);
        }

        private void ApplyNotificationsPollingIntervalFromTextBox(bool updateTextBox)
        {
            if (NotificationsPollingIntervalSecondsTextBox == null)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            if (int.TryParse(NotificationsPollingIntervalSecondsTextBox.Text, out var interval))
            {
                localSettings.ActiveGameMonitoringIntervalSeconds = interval;
            }

            if (updateTextBox)
            {
                NotificationsPollingIntervalSecondsTextBox.Text = localSettings.ActiveGameMonitoringIntervalSeconds.ToString();
            }
        }

        private void NotificationsBundledUnlockSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingNotificationSoundSelection)
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null || NotificationsBundledUnlockSoundComboBox.SelectedItem == null)
                return;

            if (NotificationsBundledUnlockSoundComboBox.SelectedItem is NotificationSoundOption option)
            {
                localSettings.CustomUnlockSoundPath = string.Empty;
                localSettings.BundledUnlockSoundPath = option.SoundPath ?? string.Empty;
            }

            UpdateNotificationSoundButtons();
        }

        private void ScoreProgressSoundBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Wave files|*.wav|All files|*.*");
            if (string.IsNullOrWhiteSpace(path)) return;
            var local = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (local == null) return;
            var isPrestige = string.Equals((sender as FrameworkElement)?.Tag as string, "Prestige", StringComparison.OrdinalIgnoreCase);
            if (isPrestige) local.PrestigeProgressNotifications.SoundPath = path;
            else local.CollectionProgressNotifications.SoundPath = path;
            AddNotificationSoundEntries(local, new[] { path }, autoSelectFirstAdded: false);
            if (isPrestige) PrestigeProgressSoundComboBox.SelectedValue = path;
            else CollectionProgressSoundComboBox.SelectedValue = path;
        }

        private void ScoreProgressNotificationTest_Click(object sender, RoutedEventArgs e)
        {
            var local = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (local == null) return;
            var isPrestige = string.Equals((sender as FrameworkElement)?.Tag as string, "Prestige", StringComparison.OrdinalIgnoreCase);
            var config = isPrestige ? local.PrestigeProgressNotifications : local.CollectionProgressNotifications;
            var scoreName = isPrestige ? "Prestige" : "Collection";
            new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger)
                .SendScoreProgressNotification(scoreName, "tier", 12, "Gold2", config, local);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Tested {scoreName} score notification.";
        }

        private void NotificationsAddSoundFile_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Wave files|*.wav|All files|*.*");
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            AddNotificationSoundEntries(localSettings, new[] { selectedPath }, autoSelectFirstAdded: true);
        }








        private void NotificationsAddSoundFolder_Click(object sender, RoutedEventArgs e)
        {
            var selectedFolder = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedFolder))
                return;

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var folderEntries = Directory.EnumerateFiles(selectedFolder, "*.wav", SearchOption.TopDirectoryOnly)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (folderEntries.Count == 0)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "No .wav files found in selected folder.";
                return;
            }

            AddNotificationSoundEntries(localSettings, folderEntries, autoSelectFirstAdded: true);
            NotificationsUnlockSoundStatusTextBlock.Text = $"Added {folderEntries.Count} sound(s) from folder.";
        }

        private void NotificationsRemoveSound_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            if (!(NotificationsBundledUnlockSoundComboBox?.SelectedItem is NotificationSoundOption selected))
                return;

            if (selected.IsDefault)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "Steam default sound cannot be removed.";
                return;
            }

            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries()
                .Where(path => !string.Equals(path, selected.SoundPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);

            if (string.Equals(localSettings.BundledUnlockSoundPath, selected.SoundPath, StringComparison.OrdinalIgnoreCase))
            {
                localSettings.BundledUnlockSoundPath = DefaultSteamSoundPath;
            }

            RefreshAchievementNotificationControls(localSettings);
            NotificationsUnlockSoundStatusTextBlock.Text = "Removed selected sound.";
        }



                private void NotificationsTestUnlockSoundButton_Click(object sender, RoutedEventArgs e)
        {
            // The only notification renderer is the official SAN template, so the general test button
            // sends the Default tab's test notification.
            OfficialSanTest_Click(sender, e);
        }




        private void MigrateLegacyCustomSoundPath(Providers.Local.LocalSettings localSettings)
        {
            if (localSettings == null || string.IsNullOrWhiteSpace(localSettings.CustomUnlockSoundPath))
            {
                return;
            }

            var soundPath = localSettings.CustomUnlockSoundPath.Trim();
            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries().ToList();
            if (!updatedPaths.Any(path => string.Equals(path, soundPath, StringComparison.OrdinalIgnoreCase)))
            {
                updatedPaths.Add(soundPath);
                localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);
            }

            localSettings.BundledUnlockSoundPath = soundPath;
            localSettings.CustomUnlockSoundPath = string.Empty;
        }

        private List<NotificationSoundOption> BuildNotificationSoundOptions(Providers.Local.LocalSettings localSettings)
        {
            var options = new List<NotificationSoundOption>
            {
                new NotificationSoundOption
                {
                    DisplayName = "Steam (Default)",
                    SoundPath = DefaultSteamSoundPath,
                    IsDefault = true
                }
            };

            foreach (var soundPath in localSettings.GetExtraUnlockSoundPathEntries())
            {
                if (string.Equals(soundPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                options.Add(new NotificationSoundOption
                {
                    DisplayName = GetSoundDisplayName(soundPath),
                    SoundPath = soundPath,
                    IsDefault = false
                });
            }

            var selectedPath = localSettings.EffectiveBundledUnlockSoundPath;
            if (!string.IsNullOrWhiteSpace(selectedPath) &&
                !options.Any(option => string.Equals(option.SoundPath, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new NotificationSoundOption
                {
                    DisplayName = GetSoundDisplayName(selectedPath),
                    SoundPath = selectedPath,
                    IsDefault = string.Equals(selectedPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase)
                });
            }

            return options;
        }

        private static string GetSoundDisplayName(string soundPath)
        {
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return "Unnamed Sound";
            }

            if (string.Equals(soundPath, DefaultSteamSoundPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Steam (Default)";
            }

            var fileName = Path.GetFileNameWithoutExtension(soundPath);
            return string.IsNullOrWhiteSpace(fileName)
                ? soundPath
                : fileName;
        }

        private void AddNotificationSoundEntries(
            Providers.Local.LocalSettings localSettings,
            IEnumerable<string> soundPaths,
            bool autoSelectFirstAdded)
        {
            if (localSettings == null || soundPaths == null)
            {
                return;
            }

            var updatedPaths = localSettings.GetExtraUnlockSoundPathEntries().ToList();
            string firstAdded = null;
            foreach (var rawPath in soundPaths)
            {
                var soundPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(soundPath))
                {
                    continue;
                }

                if (!updatedPaths.Any(path => string.Equals(path, soundPath, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedPaths.Add(soundPath);
                    if (firstAdded == null)
                    {
                        firstAdded = soundPath;
                    }
                }
            }

            localSettings.SetExtraUnlockSoundPathEntries(updatedPaths);
            if (autoSelectFirstAdded && !string.IsNullOrWhiteSpace(firstAdded))
            {
                localSettings.BundledUnlockSoundPath = firstAdded;
            }

            RefreshAchievementNotificationControls(localSettings);
        }

        private void UpdateNotificationSoundButtons()
        {
            if (NotificationsRemoveSoundButton == null)
            {
                return;
            }

            var selected = NotificationsBundledUnlockSoundComboBox?.SelectedItem as NotificationSoundOption;
            NotificationsRemoveSoundButton.IsEnabled = selected != null && !selected.IsDefault;
        }










        private void RefreshOverlayRuntimeControls(Providers.Local.LocalSettings localSettings)
        {
            // Slider value and text block are now bound via TwoWay XAML binding.
            // This method remains for any future runtime-only overlay controls.
        }












        private static Models.Settings.OfficialSanSlot ResolveOfficialSanSlot(Providers.Local.LocalSettings settings, string tag)
        {
            var parts = (tag ?? string.Empty).Split('.', '|');
            var profile = settings?.OfficialSanProfiles?.Get(parts.Length > 0 ? parts[0] : null);
            if (profile == null)
            {
                return null;
            }

            return parts.Length > 1 && string.Equals(parts[1], "Alt", StringComparison.OrdinalIgnoreCase)
                ? profile.Alt
                : profile.Primary;
        }

        private void OfficialSanThemeBrowse_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            var slot = ResolveOfficialSanSlot(localSettings, (sender as FrameworkElement)?.Tag as string);
            if (slot == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Steam Achievement Notifier themes|*.san|All files|*.*");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                slot.ThemePath = selectedPath;
            }
        }

        private void OfficialSanThemeClear_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            var slot = ResolveOfficialSanSlot(localSettings, (sender as FrameworkElement)?.Tag as string);
            if (slot != null)
            {
                slot.ThemePath = string.Empty;
            }
        }

        private void OfficialSanSoundBrowse_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            var slot = ResolveOfficialSanSlot(localSettings, (sender as FrameworkElement)?.Tag as string);
            if (slot == null)
            {
                return;
            }

            var selectedPath = _plugin?.PlayniteApi?.Dialogs?.SelectFile("Sound files|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac|All files|*.*");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                slot.SoundPath = selectedPath;
                slot.SoundMode = Models.Settings.OfficialSanSoundMode.File;
            }
        }

        private void OfficialSanTest_Click(object sender, RoutedEventArgs e)
        {
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            var parts = (((sender as FrameworkElement)?.Tag as string) ?? "Common|Primary").Split('|');
            var profileKey = parts[0];
            var useAlt = parts.Length > 1 && string.Equals(parts[1], "Alt", StringComparison.OrdinalIgnoreCase);

            if (!localSettings.UseOfficialSanTemplate)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = "Turn on the official SAN template first.";
                return;
            }

            try
            {
                var previewGame = GetSelectedNotificationPreviewGame();
                var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;
                var previewAchievement = GetSelectedNotificationPreviewAchievement();
                var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
                var shown = publisher.ShowOfficialSanTest(
                    localSettings,
                    profileKey,
                    useAlt,
                    previewGameName,
                    previewAchievement?.DisplayName ?? "Test Achievement",
                    previewAchievement?.Description ?? "Test description for preview.",
                    previewAchievement?.UnlockedIconPath,
                    previewGame);
                NotificationsUnlockSoundStatusTextBlock.Text = shown
                    ? $"Test sent for {profileKey}{(useAlt ? " (second theme)" : string.Empty)}."
                    : $"No .san theme is set for {profileKey} (or the Default tab).";
            }
            catch (Exception ex)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = $"Failed to send test notification: {ex.Message}";
            }
        }






























        private void RefreshNotificationCustomIconOptions(Providers.Local.LocalSettings localSettings)
        {
            _notificationCustomIconOptions.Clear();
            if (localSettings == null)
            {
                return;
            }

            foreach (var path in localSettings.GetOverlayCustomIconLibraryPathEntries())
            {
                _notificationCustomIconOptions.Add(new NotificationCustomIconOption
                {
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    SourcePath = ResolveNotificationCustomIconSourcePath(path),
                    PreviewSource = TryCreateNotificationCustomIconPreview(path),
                    SlotUsage = ResolveNotificationCustomIconSlotUsage(localSettings, path)
                });
            }
        }

        private string ResolveNotificationCustomIconSourcePath(string managedPath)
        {
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                return string.Empty;
            }

            var sources = LoadNotificationCustomIconSourcePaths();
            return sources.TryGetValue(managedPath, out var sourcePath) && !string.IsNullOrWhiteSpace(sourcePath)
                ? sourcePath
                : managedPath;
        }

        private string GetNotificationCustomIconSourceMapPath()
        {
            var directory = _plugin?.GetPluginUserDataPath();
            return string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : Path.Combine(directory, "notification_custom_icons_sources.json");
        }

        private Dictionary<string, string> LoadNotificationCustomIconSourcePaths()
        {
            try
            {
                var path = GetNotificationCustomIconSourceMapPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }





        private static string ResolveNotificationCustomIconSlotUsage(Providers.Local.LocalSettings localSettings, string path)
        {
            var usages = new List<string>();
            if (string.Equals(localSettings?.OverlayCustomIconPath, path, StringComparison.OrdinalIgnoreCase))
            {
                usages.Add("Primary");
            }

            if (string.Equals(localSettings?.OverlayCustomSecondaryIconPath, path, StringComparison.OrdinalIgnoreCase))
            {
                usages.Add("Secondary");
            }

            return usages.Count == 0 ? string.Empty : string.Join(" + ", usages);
        }

        private static ImageSource TryCreateNotificationCustomIconPreview(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }






        private void StopSanViewPreview(bool closeOverlay)
        {
            if (closeOverlay)
            {
                NotificationPublisher.ClosePersistentSettingsPreview();
            }
        }





































































        // -----

        // Theme migration
        // -----

        private void LoadThemes()
        {
            // Ensure we're on the UI thread before accessing DependencyProperties
            if (Dispatcher.CheckAccess())
            {
                LoadThemesInternal();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(LoadThemesInternal));
            }
        }

        private void LoadThemesInternal()
        {
            try
            {
                AvailableThemes.Clear();
                RevertableThemes.Clear();

                var cache = _settingsViewModel?.Settings?.Persisted?.ThemeMigrationVersionCache;
                var themes = _themeDiscovery
                    .DiscoverDefaultThemes(cache)
                    .Where(theme => !IsExcludedTheme(theme))
                    .ToList();
                if (themes.Count == 0)
                {
                    ForkLog.Wrap(_logger).Info("No themes path found for theme migration.");
                    UpdateThemeMigrationState();
                    return;
                }

                // Show all discovered themes in both dropdowns so users can
                // re-run migration/revert on any theme without hunting in separate lists.
                foreach (var theme in themes)
                {
                    AvailableThemes.Add(theme);
                }

                foreach (var theme in themes)
                {
                    RevertableThemes.Add(theme);
                }

                if (!string.IsNullOrWhiteSpace(SelectedThemePath) &&
                    !AvailableThemes.Any(t => string.Equals(t.Path, SelectedThemePath, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedThemePath = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(SelectedThemePath))
                {
                    SelectedThemePath = AvailableThemes.FirstOrDefault()?.Path ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(SelectedRevertThemePath) &&
                    !RevertableThemes.Any(t => string.Equals(t.Path, SelectedRevertThemePath, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedRevertThemePath = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
                {
                    SelectedRevertThemePath = RevertableThemes.FirstOrDefault()?.Path ?? string.Empty;
                }

                UpdateThemeMigrationState();

                ForkLog.Wrap(_logger).Info($"Loaded {AvailableThemes.Count} themes to migrate, {RevertableThemes.Count} themes to revert.");
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to load themes for migration.");
            }
        }

        private void UpdateThemeMigrationState()
        {
            var hasThemes = AvailableThemes.Count > 0;
            var hasRevertable = RevertableThemes.Any(theme => theme?.HasBackup == true);

            HasThemesToMigrate = hasThemes;
            HasRevertableThemes = hasRevertable;
            ShowNoThemesMessage = !hasThemes;
            ShowNoRevertableThemesMessage = !hasRevertable;
            UpdateThemeMigrationModeButtonState();
            RefreshStartPageCompatibilityState();
        }

        private void RefreshStartPageCompatibilityState()
        {
            try
            {
                HasStartPageInstalled = _startPageCompatibility?.IsStartPageInstalled() == true;
                ShowStartPageNotInstalledMessage = !HasStartPageInstalled;
                HasStartPageBackup = _startPageCompatibility?.HasBackup() == true;
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "Failed to detect StartPage installation state.");
                HasStartPageInstalled = false;
                ShowStartPageNotInstalledMessage = true;
                HasStartPageBackup = false;
            }
        }

        private async void MigrateThemeLimited_Click(object sender, RoutedEventArgs e)
        {
            CommitThemeMigrationControls();
            _settingsViewModel.Settings.Persisted.ThemeMigrationMode = MigrationMode.Limited;
            await ExecuteThemeMigrationAsync(MigrationMode.Limited);
        }

        private async void MigrateThemeFull_Click(object sender, RoutedEventArgs e)
        {
            CommitThemeMigrationControls();
            _settingsViewModel.Settings.Persisted.ThemeMigrationMode = MigrationMode.Full;
            await ExecuteThemeMigrationAsync(MigrationMode.Full, BuildFullMigrationSelection());
        }

        private async void MigrateThemeCustom_Click(object sender, RoutedEventArgs e)
        {
            CommitThemeMigrationControls();
            _settingsViewModel.Settings.Persisted.ThemeMigrationMode = MigrationMode.Custom;
            await ExecuteThemeMigrationAsync(MigrationMode.Custom, BuildCustomMigrationSelection());
        }

        private void LegacyThemeMigrationThemeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            CommitThemeMigrationControls();
        }

        private void CommitThemeMigrationControls()
        {
            if (LegacyThemeMigrationThemeComboBox?.SelectedItem is
                ThemeDiscoveryService.ThemeInfo selectedTheme)
            {
                SelectedThemePath = selectedTheme.Path;
            }

            if (LegacyUseScrollableAchievementsCheckBox?.IsChecked is bool useScrollable)
            {
                UseScrollableAchievementsForThemeMigration = useScrollable;
            }

            if (LegacyHighlightLatestAchievementCheckBox?.IsChecked is bool highlightLatest)
            {
                HighlightLatestAchievementForThemeMigration = highlightLatest;
            }
        }

        private void AchievementNotificationDebugLogging_Checked(object sender, RoutedEventArgs e)
        {
            if (!_achievementNotificationDebugSettingReady)
            {
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
            {
                return;
            }

            try
            {
                Services.Logging.AchievementNotificationDebugLog.Initialize(_plugin.GetPluginUserDataPath());
                var recreate = false;
                if (Services.Logging.AchievementNotificationDebugLog.FileExists())
                {
                    var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                        "AchNotifDebug.log already exists.\n\nSelect Yes to delete it and create a new log, or No to continue adding to the existing log.",
                        "Achievement Notification Debug Log",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    recreate = result == MessageBoxResult.Yes;
                }

                Services.Logging.AchievementNotificationDebugLog.SetEnabled(true, recreate);
                Services.Logging.AchievementNotificationDebugLog.Info(
                    $"Debug logging enabled from settings; fileMode='{(recreate ? "recreated" : "append")}'.");
                Services.Logging.AchievementNotificationDebugLog.LogSettingsSnapshot(
                    localSettings,
                    _settingsViewModel.Settings?.Persisted,
                    "debug-enabled-from-settings",
                    force: true);
            }
            catch (Exception ex)
            {
                Services.Logging.AchievementNotificationDebugLog.SetEnabled(false);
                localSettings.EnableOverlayDebugLogging = false;
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to enable the Achievement Notification debug log.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    $"AchNotifDebug.log could not be created.\n\n{ex.Message}",
                    "Achievement Notification Debug Log",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void AchievementNotificationDebugLogging_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_achievementNotificationDebugSettingReady)
            {
                return;
            }

            Services.Logging.AchievementNotificationDebugLog.Info("Debug logging disabled from settings.");
            Services.Logging.AchievementNotificationDebugLog.SetEnabled(false);
        }

        private void ThemeMigrationCustomExpander_Expanded(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void ThemeMigrationCustomExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            UpdateThemeMigrationModeButtonState();
        }

        private void ThemeMigrationSetAllLegacy_Click(object sender, RoutedEventArgs e)
        {
            SetAllThemeMigrationCustomOptions(false);
        }

        private void ThemeMigrationSetAllModern_Click(object sender, RoutedEventArgs e)
        {
            SetAllThemeMigrationCustomOptions(true);
        }

        private async Task ExecuteThemeMigrationAsync(MigrationMode mode, CustomMigrationSelection customSelection = null)
        {
            var selectedThemePath = SelectedThemePath;
            if (string.IsNullOrWhiteSpace(selectedThemePath))
            {
                ForkLog.Wrap(_logger).Warn("Migrate clicked but no theme selected.");
                return;
            }

            var highlightLatest = customSelection?.HighlightLatestUnlockedAchievement;
            var useScrollable = customSelection?.ModernizeCompactAchievementLists;
            ForkLog.Wrap(_logger).Info(
                $"User requested {mode} theme migration for: {selectedThemePath}; " +
                $"useScrollableAchievements={useScrollable?.ToString() ?? "not-applicable"}; " +
                $"highlightLatestUnlockedAchievement={highlightLatest?.ToString() ?? "not-applicable"}");

            try
            {
                var selectedThemeHasBackup = _themeMigration.HasBackup(selectedThemePath);
                if (selectedThemeHasBackup)
                {
                    ForkLog.Wrap(_logger).Info($"Selected theme already has backup. Reverting before migration: {selectedThemePath}");

                    var revertResult = await _themeMigration.RevertThemeAsync(selectedThemePath);
                    if (!revertResult.Success)
                    {
                        ForkLog.Wrap(_logger).Warn($"Pre-migration revert failed: {revertResult.Message}");
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            revertResult.Message,
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                var result = await _themeMigration.MigrateThemeAsync(
                    selectedThemePath,
                    mode,
                    customSelection);

                if (result.Success)
                {
                    ForkLog.Wrap(_logger).Info($"Theme migration ({mode}) successful: {selectedThemePath}");

                    // Show restart notice whenever files were modified.
                    if (result.FilesProcessed > 0)
                    {
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            $"{result.Message}{Environment.NewLine}{Environment.NewLine}{L("LOCPlayAch_ThemeMigration_RestartRequired", "Please restart Playnite to apply the theme changes.")}",
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        // No changes were made, just show info message
                        _plugin.PlayniteApi.Dialogs.ShowMessage(
                            result.Message,
                            L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    ForkLog.Wrap(_logger).Warn($"Theme migration failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to execute theme migration.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Title", "Theme Migration"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void RevertTheme_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SelectedRevertThemePath))
            {
                ForkLog.Wrap(_logger).Warn("Revert clicked but no theme selected.");
                return;
            }

            if (!_themeMigration.HasBackup(SelectedRevertThemePath))
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_ThemeMigration_NoRevertableThemes", "No themes with backups to revert."),
                    L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ForkLog.Wrap(_logger).Info($"User requested theme revert for: {SelectedRevertThemePath}");

            try
            {
                var result = await _themeMigration.RevertThemeAsync(SelectedRevertThemePath);

                if (result.Success)
                {
                    ForkLog.Wrap(_logger).Info($"Theme revert successful: {SelectedRevertThemePath}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Reload themes to update the lists
                    LoadThemes();
                }
                else
                {
                    ForkLog.Wrap(_logger).Warn($"Theme revert failed: {result.Message}");
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to execute theme revert.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_Revert", "Revert Theme"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void ApplyStartPageCompatibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshStartPageCompatibilityState();
                if (!HasStartPageInstalled)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_NotInstalled", "StartPage is not installed in this Playnite environment."),
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Applying this migration is an explicit action. Persist its companion
                // options now because the migration's Restart button exits Playnite
                // without going through the settings dialog's normal Save command.
                _plugin.PersistSettingsForUi();

                var result = await _startPageCompatibility.ApplyAsync();
                if (result.Success)
                {
                    var restartText = L("LOCPlayAch_ThemeMigration_RestartRequired", "Please restart Playnite to apply the theme changes.");
                    var details = string.IsNullOrWhiteSpace(result.BackupPath)
                        ? result.Message
                        : $"{result.Message}{Environment.NewLine}{Environment.NewLine}Backup: {result.BackupPath}";

                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        $"{details}{Environment.NewLine}{Environment.NewLine}{restartText}",
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        result.Message,
                        L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                RefreshStartPageCompatibilityState();
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to apply StartPage compatibility migration from settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void RevertStartPageCompatibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var confirm = _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_RevertConfirm",
                        "Restore the achievement cache from the last backup? This will undo the compatibility migration."),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                var result = await _startPageCompatibility.RevertAsync();
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    result.Message,
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

                RefreshStartPageCompatibilityState();
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to revert StartPage compatibility migration from settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RestartPlaynite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.PersistSettingsForUi();
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    // Launch cmd.exe to wait for this process to exit, then relaunch Playnite.
                    // This avoids the "another instance is running" startup error.
                    var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

                    // If a StartPage DLL update is pending, apply it after Playnite has exited
                    // (the DLL is locked while loaded; taskkill releases it before the copy).
                    var hasPendingDll = _startPageCompatibility?.HasPendingDllUpdate() == true;
                    var pendingDll = hasPendingDll ? _startPageCompatibility.GetPendingDllPath() : null;
                    var installedDll = hasPendingDll ? _startPageCompatibility.GetInstalledDllPath() : null;

                    var dllChain = (hasPendingDll && !string.IsNullOrWhiteSpace(installedDll))
                        ? $" & copy /Y \"{pendingDll}\" \"{installedDll}\" >nul 2>&1 & del \"{pendingDll}\" >nul 2>&1"
                        : string.Empty;

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c taskkill /pid {pid} /f >nul 2>&1 & timeout /t 2 /nobreak >nul{dllChain} & start \"\" \"{exePath}\"",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                else
                {
                    System.Windows.Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to restart Playnite.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_ThemeMigration_StartPageCompatibility_Title", "StartPage Compatibility"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void InitializeThemeMigrationCustomOptions()
        {
            ThemeMigrationCustomOptions.Clear();

            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginButton",
                "LOCPlayAch_Settings_ButtonPreview",
                "Button"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginChart",
                "LOCPlayAch_Settings_BarChartPreview",
                "Bar Chart"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactList",
                "LOCPlayAch_Settings_CompactListPreview",
                "Compact List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactLocked",
                "LOCPlayAch_Settings_CompactLockedListPreview",
                "Compact Locked List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginCompactUnlocked",
                "LOCPlayAch_Settings_CompactUnlockedListPreview",
                "Compact Unlocked List",
                isModern: UseScrollableAchievementsForThemeMigration));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginList",
                "LOCPlayAch_Settings_AchievementDataGridPreview",
                "Achievement DataGrid"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginProgressBar",
                "LOCPlayAch_Settings_ProgressBarPreview",
                "Progress Bar"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginUserStats",
                "LOCPlayAch_Settings_StatsPreview",
                "Stats"));
            ThemeMigrationCustomOptions.Add(CreateThemeMigrationControlOption(
                "PluginViewItem",
                "LOCPlayAch_Settings_ViewItemPreview",
                "View Item"));
        }

        private ThemeMigrationElementOption CreateThemeMigrationControlOption(
            string key,
            string resourceKey,
            string fallback,
            bool isModern = true)
        {
            var persisted = _settingsViewModel.Settings.Persisted;
            var option = new ThemeMigrationElementOption(
                key,
                L(resourceKey, fallback),
                isBindingOption: false,
                isModern: persisted.ThemeMigrationControlOptions.TryGetValue(key, out var saved) ? saved : isModern);
            option.PropertyChanged += (sender, args) =>
            {
                var settings = _settingsViewModel.Settings.Persisted;
                var options = new Dictionary<string, bool>(settings.ThemeMigrationControlOptions);
                options[key] = option.IsModern;
                settings.ThemeMigrationControlOptions = options;
                settings.ThemeMigrationMode = MigrationMode.Custom;
            };
            return option;
        }

        private static void OnUseScrollableAchievementsForThemeMigrationChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
            {
                if (control._settingsViewModel != null)
                {
                    control._settingsViewModel.Settings.Persisted.ThemeMigrationUseScrollableAchievements = (bool)e.NewValue;
                }
                control.SetCompactAchievementMigrationOptions((bool)e.NewValue);
            }
        }

        private static void OnHighlightLatestAchievementForThemeMigrationChanged(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control && control._settingsViewModel != null)
            {
                control._settingsViewModel.Settings.Persisted.ThemeMigrationHighlightLatestAchievement = (bool)e.NewValue;
            }
        }

        private static void OnSelectedThemePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
            {
                control.UpdateThemeMigrationModeButtonState();
            }
        }

        private void UpdateThemeMigrationModeButtonState()
        {
            var isCustomExpanded = ThemeMigrationCustomExpander?.IsExpanded == true;
            var isFullscreenTheme = ThemeMigrationService.IsFullscreenThemePath(SelectedThemePath);

            if (isFullscreenTheme && isCustomExpanded)
            {
                ThemeMigrationCustomExpander.IsExpanded = false;
                isCustomExpanded = false;
            }

            if (ThemeMigrationPresetButtons != null)
            {
                ThemeMigrationPresetButtons.IsEnabled = HasThemesToMigrate && !isCustomExpanded;
            }

            if (ThemeMigrationFullButton != null)
            {
                ThemeMigrationFullButton.IsEnabled = HasThemesToMigrate && !isCustomExpanded && !isFullscreenTheme;
            }

            if (ThemeMigrationCustomContainer != null)
            {
                ThemeMigrationCustomContainer.IsEnabled = HasThemesToMigrate && !isFullscreenTheme;
            }
        }

        private CustomMigrationSelection BuildCustomMigrationSelection()
        {
            var modernControlNames = ThemeMigrationCustomOptions
                .Where(option => option.IsModern)
                .Select(option => option.Key)
                .ToList();

            return new CustomMigrationSelection(modernControlNames, modernizeBindings: true)
            {
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration,
                HighlightLatestUnlockedAchievement =
                    HighlightLatestAchievementForThemeMigration
            };
        }

        private CustomMigrationSelection BuildFullMigrationSelection()
        {
            return new CustomMigrationSelection(
                ControlMappings.LegacyToModernControlNames.Keys,
                modernizeBindings: true)
            {
                ModernizeCompactAchievementLists = UseScrollableAchievementsForThemeMigration,
                HighlightLatestUnlockedAchievement =
                    HighlightLatestAchievementForThemeMigration
            };
        }

        private void ThemeMigrationRowSetLegacy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = false;
            }
        }

        private void ThemeMigrationRowSetModern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ThemeMigrationElementOption option })
            {
                option.IsModern = true;
            }
        }

        private void SetAllThemeMigrationCustomOptions(bool isModern)
        {
            foreach (var option in ThemeMigrationCustomOptions)
            {
                option.IsModern = isModern;
            }
        }

        private void SetCompactAchievementMigrationOptions(bool isModern)
        {
            foreach (var option in ThemeMigrationCustomOptions)
            {
                if (ControlMappings.CompactAchievementListControlNames.Contains(option.Key))
                {
                    option.IsModern = isModern;
                }
            }
        }

        // -----------------------------
        // Cache actions
        // -----------------------------

        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            string message = null;
            var image = MessageBoxImage.Information;
            Exception operationError = null;
            var progressText = L(
                "LOCPlayAch_Settings_Cache_ProgressClearing",
                "Clearing cached achievement data...");

            RunMaintenanceProgress(
                progressText,
                isIndeterminate: true,
                operation: progress =>
                {
                    try
                    {
                        _plugin.RefreshRuntime.Cache.ClearCache();
                        message = L("LOCPlayAch_Status_Succeeded", "Success!");
                        image = MessageBoxImage.Information;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", operationError.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message ?? L("LOCPlayAch_Status_Succeeded", "Success!"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }

        private void ClearAllIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.All);

        private void ClearCompressedIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.CompressedOnly);

        private void ClearFullResolutionIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.FullResolutionOnly);

        private void ClearLockedIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.LockedOnly);

        private void ClearIconCache(IconCacheClearScope scope)
        {
            var fileLabel = ResourceProvider.GetString(GetIconCacheFileLabelResourceKey(scope));
            var scanningText = LF(
                "LOCPlayAch_Settings_IconCache_ProgressScanning",
                "Scanning cached {0} files...",
                fileLabel);
            var deletingTextFormat = L(
                "LOCPlayAch_Settings_IconCache_ProgressDeletingCount",
                "Deleting cached {0} files... ({1}/{2})");
            var deletedCount = 0;
            Exception operationError = null;

            RunMaintenanceProgress(
                scanningText,
                isIndeterminate: false,
                operation: progress =>
                {
                    try
                    {
                        UpdateMaintenanceProgress(progress, current: 0, max: 1);

                        IEnumerable<string> additionalPaths = null;
                        if (scope == IconCacheClearScope.LockedOnly)
                        {
                            additionalPaths = GetExplicitLockedIconCachePaths(progress);
                        }

                        _plugin.ImageService?.Clear();
                        deletedCount = _plugin.ImageService?.ClearDiskCache(
                            scope,
                            additionalPaths,
                            (processed, total) =>
                            {
                                var safeTotal = Math.Max(1, total);
                                var safeProcessed = total <= 0
                                    ? 1
                                    : Math.Max(0, Math.Min(total, processed));

                                var progressText = total <= 0
                                    ? LF(
                                        "LOCPlayAch_Settings_IconCache_ProgressNoFiles",
                                        "No cached {0} files were found.",
                                        fileLabel)
                                    : string.Format(
                                        deletingTextFormat,
                                        fileLabel,
                                        safeProcessed,
                                        total);

                                UpdateMaintenanceProgress(
                                    progress,
                                    text: progressText,
                                    current: safeProcessed,
                                    max: safeTotal);
                            }) ?? 0;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", operationError.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var message = L("LOCPlayAch_Status_Succeeded", "Success!");

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private string GetIconCacheFileLabelResourceKey(IconCacheClearScope scope)
        {
            switch (scope)
            {
                case IconCacheClearScope.CompressedOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_Compressed";
                case IconCacheClearScope.FullResolutionOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_FullResolution";
                case IconCacheClearScope.LockedOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_Locked";
                default:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_All";
            }
        }

        private void RunMaintenanceProgress(
            string initialText,
            bool isIndeterminate,
            Action<GlobalProgressActionArgs> operation)
        {
            var progressOptions = new GlobalProgressOptions(initialText)
            {
                Cancelable = false,
                IsIndeterminate = isIndeterminate
            };

            _plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                UpdateMaintenanceProgress(progress, text: initialText, isIndeterminate: isIndeterminate);
                await Task.Run(() => operation?.Invoke(progress)).ConfigureAwait(false);
            }, progressOptions);
        }

        private void UpdateMaintenanceProgress(
            GlobalProgressActionArgs progress,
            string text = null,
            int? current = null,
            int? max = null,
            bool? isIndeterminate = null)
        {
            if (progress == null)
            {
                return;
            }

            Action update = () =>
            {
                if (max.HasValue)
                {
                    progress.ProgressMaxValue = max.Value;
                }

                if (current.HasValue)
                {
                    progress.CurrentProgressValue = current.Value;
                }

                if (isIndeterminate.HasValue)
                {
                    progress.IsIndeterminate = isIndeterminate.Value;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    progress.Text = text;
                }
            };

            if (progress.MainDispatcher != null)
            {
                progress.MainDispatcher.InvokeIfNeeded(update);
            }
            else
            {
                update();
            }
        }

        private IEnumerable<string> GetExplicitLockedIconCachePaths(GlobalProgressActionArgs progress = null)
        {
            var dataService = _plugin?.AchievementDataService;
            var cachedGameIds = dataService?.GetCachedGameIds();
            if (cachedGameIds == null || cachedGameIds.Count == 0)
            {
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: L(
                            "LOCPlayAch_Settings_IconCache_ProgressNoLockedReferences",
                            "No cached locked icon references were found."),
                        current: 1,
                        max: 1);
                }

                return Array.Empty<string>();
            }

            var lockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (progress != null)
            {
                UpdateMaintenanceProgress(progress, current: 0, max: cachedGameIds.Count);
            }

            for (var i = 0; i < cachedGameIds.Count; i++)
            {
                var gameId = cachedGameIds[i];
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: LF(
                            "LOCPlayAch_Settings_IconCache_ProgressScanningLockedReferences",
                            "Scanning cached locked icon references... ({0}/{1})",
                            i + 1,
                            cachedGameIds.Count),
                        current: i + 1,
                        max: cachedGameIds.Count);
                }

                var gameData = dataService?.GetRawGameAchievementData(gameId);
                var achievements = gameData?.Achievements;
                if (achievements == null)
                {
                    continue;
                }

                foreach (var achievement in achievements)
                {
                    var lockedPath = achievement?.LockedIconPath;
                    if (!DiskImageService.IsLocalIconPath(lockedPath))
                    {
                        continue;
                    }

                    var unlockedPath = achievement?.UnlockedIconPath;
                    if (!string.IsNullOrWhiteSpace(unlockedPath) &&
                        string.Equals(lockedPath.Trim(), unlockedPath.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    lockedPaths.Add(lockedPath);
                }
            }

            return lockedPaths;
        }

        private void ResetFirstTimeSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ForkLog.Wrap(_logger).Info($"Resetting FirstTimeSetupCompleted. Current value before: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted = false;

                ForkLog.Wrap(_logger).Info($"Value after setting to false: {_settingsViewModel.Settings.Persisted.FirstTimeSetupCompleted}");

                _plugin.SavePluginSettings(_settingsViewModel.Settings);
                NotificationPublisher.ClosePersistentSettingsPreview();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to reset first-time setup.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ForkLog.Wrap(_logger).Info("Resetting Display tab settings to defaults.");

                _settingsViewModel.Settings.Persisted.ResetDisplaySettingsToDefaults();
                PercentRarityHelper.ApplyBadgeApplicationResources(
                    _settingsViewModel.Settings.Persisted.UseUniformRarityBadges);
                RefreshMockPreviews();
                _plugin.SavePluginSettings(_settingsViewModel.Settings);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExportDatabase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportBaseDir = _plugin.GetPluginUserDataPath();
                var exportDir = _plugin.RefreshRuntime.Cache.ExportDatabaseToCsv(exportBaseDir);

                ForkLog.Wrap(_logger).Info($"Database exported to: {exportDir}");

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded", "Success!") + "\n" + exportDir,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to export database.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dataPath = _plugin.GetPluginUserDataPath();

                if (!Directory.Exists(dataPath))
                {
                    Directory.CreateDirectory(dataPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dataPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to open extension data folder.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // -----------------------------
        // Compact list sort direction toggles
        // -----------------------------

        private void ToggleCompactListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactListSortDescending = !persisted.CompactListSortDescending;
            }
        }

        private void ToggleCompactUnlockedListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactUnlockedListSortDescending = !persisted.CompactUnlockedListSortDescending;
            }
        }

        private void ToggleCompactLockedListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactLockedListSortDescending = !persisted.CompactLockedListSortDescending;
            }
        }

        private void ToggleOverviewGameSummariesGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.OverviewGameSummariesGridSortDescending = !persisted.OverviewGameSummariesGridSortDescending;
            }
        }

        private void ToggleStartPageGameSummariesGridSortDescending(object sender, RoutedEventArgs e)
        {
            var settings = _settingsViewModel?.Settings?.Persisted?.StartPageGameSummariesGrid;
            if (settings != null)
            {
                settings.SortDescending = !settings.SortDescending;
            }
        }

        private void ToggleOverviewSelectedGameGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.OverviewSelectedGameGridSortDescending = !persisted.OverviewSelectedGameGridSortDescending;
            }
        }




        private void ToggleSingleGameGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.SingleGameGridSortDescending = !persisted.SingleGameGridSortDescending;
            }
        }

        private void ToggleStartPageRecentUnlocksGridSortDescending(object sender, RoutedEventArgs e)
        {
            var settings = _settingsViewModel?.Settings?.Persisted?.StartPageRecentUnlocksGrid;
            if (settings != null)
            {
                settings.SortDescending = !settings.SortDescending;
            }
        }

        private void ToggleAchievementDataGridSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted != null)
            {
                persisted.AchievementDataGridSortDescending = !persisted.AchievementDataGridSortDescending;
            }
        }

        private void StartPageActivityScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageActivityScopeContextMenu(sender as Button);
        }

        private void StartPageProgressScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageProgressScopeContextMenu(sender as Button);
        }

        private void OpenStartPageActivityScopeContextMenu(Button button)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameActivityScope.Played, Label = L("LOCPlayAch_Filter_Played", "Played") },
                new { Scope = GameActivityScope.Unplayed, Label = L("LOCPlayAch_Filter_Unplayed", "Unplayed") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageActivityScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settingsViewModel?.Settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageActivityScope = isChecked
                            ? settings.StartPageActivityScope | scope
                            : settings.StartPageActivityScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private void OpenStartPageProgressScopeContextMenu(Button button)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameProgressScope.Completed, Label = L("LOCPlayAch_Filter_Complete", "Complete") },
                new { Scope = GameProgressScope.InProgress, Label = L("LOCPlayAch_Filter_InProgress", "In Progress") },
                new { Scope = GameProgressScope.NoProgress, Label = L("LOCPlayAch_Filter_NoProgress", "No Progress") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageProgressScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settingsViewModel?.Settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageProgressScope = isChecked
                            ? settings.StartPageProgressScope | scope
                            : settings.StartPageProgressScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static ContextMenu PrepareStartPageScopeContextMenu(Button button)
        {
            var menu = button?.ContextMenu;
            if (menu == null)
            {
                return null;
            }

            menu.Items.Clear();
            return menu;
        }

        private static MenuItem CreateStartPageScopeMenuItem(
            Button button,
            string header,
            bool isChecked,
            Action<bool> setSelection)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            var itemStyle = button?.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => setSelection?.Invoke(item.IsChecked);
            return item;
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private void UpdateStartPageScopeTexts()
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            var activityScope = persisted?.StartPageActivityScope ??
                PersistedSettings.DefaultStartPageActivityScope;
            var progressScope = persisted?.StartPageProgressScope ??
                PersistedSettings.DefaultStartPageProgressScope;

            StartPageActivityScopeText = GetActivityScopeText(activityScope);
            StartPageProgressScopeText = GetProgressScopeText(progressScope);
        }

        private static string GetActivityScopeText(GameActivityScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageActivityScope(scope);
            if (scope == GameActivityScope.None)
            {
                return L("LOCPlayAch_Filter_ActivitySelectorPlaceholder", "Activity");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameActivityScope.Played))
            {
                labels.Add(L("LOCPlayAch_Filter_Played", "Played"));
            }

            if (scope.HasFlag(GameActivityScope.Unplayed))
            {
                labels.Add(L("LOCPlayAch_Filter_Unplayed", "Unplayed"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Filter_ActivitySelectorPlaceholder", "Activity");
        }

        private static string GetProgressScopeText(GameProgressScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageProgressScope(scope);
            if (scope == GameProgressScope.None)
            {
                return L("LOCPlayAch_Progress", "Progress");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameProgressScope.Completed))
            {
                labels.Add(L("LOCPlayAch_Filter_Complete", "Complete"));
            }

            if (scope.HasFlag(GameProgressScope.InProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_InProgress", "In Progress"));
            }

            if (scope.HasFlag(GameProgressScope.NoProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_NoProgress", "No Progress"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Progress", "Progress");
        }

        // -----------------------------
        // Tagging Methods
        // -----------------------------

        /// <summary>
        /// Commits pending tag name changes from text boxes to the source.
        /// </summary>
        private void CommitTagNameBindings()
        {
            var textBoxes = new TextBox[]
            {
                HasAchievementsTagTextBox,
                InProgressTagTextBox,
                CompletedTagTextBox,
                NoAchievementsTagTextBox,
                CustomizedTagTextBox,
                NotCustomizedTagTextBox,
                ExcludedTagTextBox,
                ExcludedFromSummariesTagTextBox
            };

            foreach (var textBox in textBoxes)
            {
                var binding = textBox?.GetBindingExpression(TextBox.TextProperty);
                if (binding != null)
                {
                    binding.UpdateSource();
                }
            }
        }

        private void ApplyAndSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Commit any pending text box changes
                CommitTagNameBindings();

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    ForkLog.Wrap(_logger)?.Warn("TagSyncService not available");
                    return;
                }

                // Sync all tags (handles orphan cleanup and re-tagging with new names)
                tagSyncService.SyncAllTags();
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to apply and sync tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RemoveAllTags_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Tagging_RemoveAllConfirm", "Remove all Playnite Achievements tags from all games?"),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    ForkLog.Wrap(_logger)?.Warn("TagSyncService not available");
                    return;
                }

                tagSyncService.RemoveAllTags();
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger).Error(ex, "Failed to remove tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", "Error: {0}", ex.Message),
                    L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------
        // Provider Navigation Overview
        // -----------------------------

        /// <summary>
        /// Builds provider navigation items for the overview from registered providers.
        /// Items are added in the natural discovery order.
        /// </summary>
        private void BuildLegacyProviderNavigationItems()
        {
            if (_providerNavigationBuilt)
            {
                return;
            }

            LegacyProviderNavigationItems.Clear();

            foreach (var providerKey in _providerRegistry.GetSettingsViewProviderKeys())
            {
                var settings = _providerRegistry.GetSettingsForEdit(providerKey);
                if (settings == null) continue;

                var view = _providerRegistry.CreateSettingsView(providerKey);
                var displayName = ProviderRegistry.GetLocalizedName(providerKey);
                var iconKey = $"ProviderIcon{providerKey}";
                var colorHex = "#FF4084D6";
                string redirectProviderKey = null;
                string subtitle = null;

                if (_providerRegistry.TryGetProviderVisuals(providerKey, out var providerIconKey, out var providerColorHex))
                {
                    if (!string.IsNullOrWhiteSpace(providerIconKey))
                    {
                        iconKey = providerIconKey;
                    }

                    if (!string.IsNullOrWhiteSpace(providerColorHex))
                    {
                        colorHex = providerColorHex;
                    }
                }

                if (view == null)
                {
                    if (!ProviderUiPolicies.TryGetSettingsRedirectProviderKey(providerKey, out redirectProviderKey))
                    {
                        continue;
                    }

                    subtitle = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_ProviderServicedByFormat"),
                        ProviderRegistry.GetLocalizedName(redirectProviderKey));
                }
                else
                {
                    view.Initialize(settings);
                }

                LegacyProviderNavigationItems.Add(new LegacyProviderNavigationItem(
                    providerKey,
                    displayName,
                    GetProviderSettingsGroupName(providerKey),
                    iconKey,
                    colorHex,
                    settings,
                    view,
                    redirectProviderKey,
                    subtitle));
            }

            ConfigureProviderNavigationView();

            if (SelectedLegacyProviderNavigationItem == null && LegacyProviderNavigationItems.Count > 0)
            {
                SelectedLegacyProviderNavigationItem = LegacyProviderNavigationItems[0];
            }

            _providerNavigationBuilt = true;
            ForkLog.Wrap(_logger)?.Info($"Built {LegacyProviderNavigationItems.Count} platform navigation items");
        }

        private static string GetProviderSettingsGroupName(string providerKey)
        {
            return ResourceProvider.GetString(ProviderUiPolicies.GetSettingsGroupResourceKey(providerKey));
        }

        private void ConfigureProviderNavigationView()
        {
            _providerNavigationView = CollectionViewSource.GetDefaultView(LegacyProviderNavigationItems);
            if (_providerNavigationView == null)
            {
                return;
            }

            _providerNavigationView.Filter = FilterLegacyProviderNavigationItem;
            _providerNavigationView.GroupDescriptions.Clear();
            _providerNavigationView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(LegacyProviderNavigationItem.GroupName)));
            _providerNavigationView.Refresh();
        }

        private bool FilterLegacyProviderNavigationItem(object item)
        {
            if (!(item is LegacyProviderNavigationItem providerItem))
            {
                return false;
            }

            var searchText = ProviderSearchText;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsSearchText(providerItem.DisplayName, searchText) ||
                ContainsSearchText(providerItem.ProviderKey, searchText) ||
                ContainsSearchText(providerItem.GroupName, searchText) ||
                ContainsSearchText(providerItem.Subtitle, searchText);
        }

        private static bool ContainsSearchText(string value, string searchText)
            => !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void OnProviderSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control)
            {
                control._providerNavigationView?.Refresh();
                control.SelectFirstVisibleProviderIfNeeded();
            }
        }

        private void SelectFirstVisibleProviderIfNeeded()
        {
            if (_providerNavigationView == null)
            {
                return;
            }

            if (SelectedLegacyProviderNavigationItem != null &&
                _providerNavigationView.Contains(SelectedLegacyProviderNavigationItem))
            {
                return;
            }

            SelectedLegacyProviderNavigationItem = _providerNavigationView
                .Cast<LegacyProviderNavigationItem>()
                .FirstOrDefault();
        }

        private static void OnSelectedLegacyProviderNavigationItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LegacyNotificationSettingsControl control && e.NewValue is LegacyProviderNavigationItem item)
            {
                control.OnSelectedLegacyProviderNavigationItemChangedInternal(item);
            }
        }

        private async void OnSelectedLegacyProviderNavigationItemChangedInternal(LegacyProviderNavigationItem item)
        {
            if (item == null) return;

            if (item.IsRedirect)
            {
                var redirectItem = LegacyProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, item.RedirectProviderKey, StringComparison.OrdinalIgnoreCase));

                if (redirectItem != null && !ReferenceEquals(item, redirectItem))
                {
                    ProviderSearchText = string.Empty;
                    SelectedLegacyProviderNavigationItem = redirectItem;
                    ForkLog.Wrap(_logger)?.Info($"Redirected {item.ProviderKey} settings navigation to {redirectItem.ProviderKey}");
                }
                else
                {
                    ForkLog.Wrap(_logger)?.Warn($"Provider settings redirect target not found for {item.ProviderKey}: {item.RedirectProviderKey}");
                }

                return;
            }

            if (item.SettingsView == null) return;

            // Refresh auth status if the view implements IAuthRefreshable
            if (item.SettingsView is IAuthRefreshable authRefreshable)
            {
                try
                {
                    await authRefreshable.RefreshAuthStatusAsync();
                    ForkLog.Wrap(_logger)?.Info($"Refreshed auth status for {item.ProviderKey}");
                }
                catch (Exception ex)
                {
                    ForkLog.Wrap(_logger)?.Error(ex, $"Failed to refresh auth status for {item.ProviderKey}");
                }
            }
        }

        private void NavigateToPendingProvider()
        {
            var targetKey = PendingNavigationProviderKey;
            PendingNavigationProviderKey = null;

            if (!string.IsNullOrWhiteSpace(targetKey))
            {
                var targetItem = LegacyProviderNavigationItems?.FirstOrDefault(x =>
                    string.Equals(x.ProviderKey, targetKey, StringComparison.OrdinalIgnoreCase));

                if (targetItem != null && ProvidersTab != null)
                {
                    SettingsTabControl.SelectedItem = ProvidersTab;
                    SelectedLegacyProviderNavigationItem = targetItem;
                    ForkLog.Wrap(_logger)?.Info($"Navigated to pending provider: {targetKey}");
                    return;
                }

                foreach (TabItem tab in SettingsTabControl.Items)
                {
                    if (string.Equals(tab.Tag as string, targetKey, StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsTabControl.SelectedItem = tab;
                        return;
                    }
                }
            }

            var targetTabName = PendingNavigationTabName;
            PendingNavigationTabName = null;
            if (string.IsNullOrWhiteSpace(targetTabName))
            {
                return;
            }

            foreach (TabItem tab in SettingsTabControl.Items)
            {
                if (string.Equals(tab.Name, targetTabName, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsTabControl.SelectedItem = tab;
                    return;
                }
            }
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is TabControl)) return;

            // Inspect the newly selected TabItem by name (uses x:Name from XAML)
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not TabItem selected) return;

            var name = selected.Name ?? string.Empty;
            var tag = selected.Tag as string ?? string.Empty;
            CurrentSelectedTabName = name;
            CurrentSelectedProviderKey = string.IsNullOrWhiteSpace(tag) ? null : tag;

            if (string.Equals(name, "ThemeMigrationTab", StringComparison.OrdinalIgnoreCase))
            {
                LoadThemes();
                ForkLog.Wrap(_logger)?.Info("Loaded themes for Theme Migration tab.");
            }
        }

        // Quick navigation button handlers from General tab
        private void JumpToDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (DisplayTab != null)
            {
                SettingsTabControl.SelectedItem = DisplayTab;
                DisplayTab.BringIntoView();
            }
        }

        private void JumpToProviders_Click(object sender, RoutedEventArgs e)
        {
            if (ProvidersTab != null)
            {
                SettingsTabControl.SelectedItem = ProvidersTab;
                ProvidersTab.BringIntoView();
            }
        }

        private void JumpToThemeMigration_Click(object sender, RoutedEventArgs e)
        {
            if (ThemeMigrationTab != null)
            {
                SettingsTabControl.SelectedItem = ThemeMigrationTab;
                ThemeMigrationTab.BringIntoView();
            }
        }

        private void ViewAchievementsHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.ViewAchievements, ViewAchievementsHotkeyCaptureButton);
        }

        private void ManageAchievementsHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.ManageAchievements, ManageAchievementsHotkeyCaptureButton);
        }

        private void OverviewHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyCapture(HotkeyCaptureTarget.Overview, OverviewHotkeyCaptureButton);
        }

        private void ResetViewAchievementsHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.ViewAchievementsHotkey = PersistedSettings.DefaultViewAchievementsHotkey;
            EndHotkeyCapture();
        }

        private void ResetManageAchievementsHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.ManageAchievementsHotkey = PersistedSettings.DefaultManageAchievementsHotkey;
            EndHotkeyCapture();
        }

        private void ResetOverviewHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.OverviewHotkey = PersistedSettings.DefaultOverviewHotkey;
            EndHotkeyCapture();
        }

        private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey.HasValue)
            {
                return;
            }

            var target = _capturingHotkey.Value;
            if ((target == HotkeyCaptureTarget.ViewAchievements &&
                 !ReferenceEquals(sender, ViewAchievementsHotkeyCaptureButton)) ||
                (target == HotkeyCaptureTarget.ManageAchievements &&
                 !ReferenceEquals(sender, ManageAchievementsHotkeyCaptureButton)) ||
                (target == HotkeyCaptureTarget.Overview &&
                 !ReferenceEquals(sender, OverviewHotkeyCaptureButton)))
            {
                return;
            }

            e.Handled = true;
            var key = GetEffectiveHotkeyCaptureKey(e);
            if (key == Key.Escape)
            {
                EndHotkeyCapture();
                return;
            }

            if (key == Key.Back || key == Key.Delete)
            {
                SetCapturedHotkey(target, string.Empty);
                EndHotkeyCapture();
                return;
            }

            if (!AchievementHotkeyGesture.TryCreate(key, Keyboard.Modifiers, out var gesture))
            {
                HotkeyCaptureStatusText = L(
                    "LOCPlayAch_Hotkeys_InvalidShortcut",
                    "Unsupported shortcut. Press a letter, digit, function key, or a modified shortcut.");
                return;
            }

            if (IsDuplicateHotkey(target, gesture))
            {
                HotkeyCaptureStatusText = L(
                    "LOCPlayAch_Hotkeys_DuplicateShortcut",
                    "That shortcut is already assigned.");
                return;
            }

            SetCapturedHotkey(target, gesture.ToString());
            EndHotkeyCapture();
        }

        private void StartHotkeyCapture(HotkeyCaptureTarget target, Button button)
        {
            _capturingHotkey = target;
            HotkeyCaptureStatusText = L("LOCPlayAch_Hotkeys_CapturePrompt", "Press a shortcut...");

            if (target == HotkeyCaptureTarget.ViewAchievements)
            {
                ViewAchievementsHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ManageAchievementsHotkey);
                OverviewHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.OverviewHotkey);
            }
            else if (target == HotkeyCaptureTarget.ManageAchievements)
            {
                ManageAchievementsHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ViewAchievementsHotkey);
                OverviewHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.OverviewHotkey);
            }
            else
            {
                OverviewHotkeyButtonText = L("LOCPlayAch_Hotkeys_CaptureButton", "Press keys...");
                ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ViewAchievementsHotkey);
                ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(
                    _settingsViewModel?.Settings?.Persisted?.ManageAchievementsHotkey);
            }

            button?.Focus();
            Keyboard.Focus(button);
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = null;
            HotkeyCaptureStatusText = string.Empty;
            UpdateHotkeyButtonTexts();
        }

        private void SetCapturedHotkey(HotkeyCaptureTarget target, string hotkey)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (target == HotkeyCaptureTarget.ViewAchievements)
            {
                persisted.ViewAchievementsHotkey = hotkey;
            }
            else if (target == HotkeyCaptureTarget.ManageAchievements)
            {
                persisted.ManageAchievementsHotkey = hotkey;
            }
            else
            {
                persisted.OverviewHotkey = hotkey;
            }
        }

        private bool IsDuplicateHotkey(HotkeyCaptureTarget target, AchievementHotkeyGesture gesture)
        {
            if (gesture == null || gesture.IsEmpty)
            {
                return false;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            if (persisted == null)
            {
                return false;
            }

            return IsMatchingHotkey(target, HotkeyCaptureTarget.ViewAchievements, persisted.ViewAchievementsHotkey, gesture) ||
                   IsMatchingHotkey(target, HotkeyCaptureTarget.ManageAchievements, persisted.ManageAchievementsHotkey, gesture) ||
                   IsMatchingHotkey(target, HotkeyCaptureTarget.Overview, persisted.OverviewHotkey, gesture);
        }

        private static bool IsMatchingHotkey(
            HotkeyCaptureTarget currentTarget,
            HotkeyCaptureTarget comparedTarget,
            string comparedText,
            AchievementHotkeyGesture gesture)
        {
            if (currentTarget == comparedTarget)
            {
                return false;
            }

            return AchievementHotkeyGesture.TryParse(comparedText, out var otherGesture) &&
                   otherGesture != null &&
                   !otherGesture.IsEmpty &&
                   gesture.Equals(otherGesture);
        }

        private void UpdateHotkeyButtonTexts()
        {
            if (_capturingHotkey.HasValue)
            {
                return;
            }

            var persisted = _settingsViewModel?.Settings?.Persisted;
            ViewAchievementsHotkeyButtonText = FormatHotkeyButtonText(persisted?.ViewAchievementsHotkey);
            ManageAchievementsHotkeyButtonText = FormatHotkeyButtonText(persisted?.ManageAchievementsHotkey);
            OverviewHotkeyButtonText = FormatHotkeyButtonText(persisted?.OverviewHotkey);
        }

        private string FormatHotkeyButtonText(string hotkey)
        {
            return string.IsNullOrWhiteSpace(hotkey)
                ? L("LOCPlayAch_Hotkeys_None", "None")
                : hotkey;
        }

        private static Key GetEffectiveHotkeyCaptureKey(KeyEventArgs e)
        {
            if (e == null)
            {
                return Key.None;
            }

            if (e.Key == Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key == Key.ImeProcessed)
            {
                return e.ImeProcessedKey;
            }

            return e.Key;
        }

        // -----------------------------
        // Settings property change handling for mock preview refresh
        // -----------------------------

        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var persisted = _settingsViewModel?.Settings?.Persisted;

            if (persisted != null &&
                e.PropertyName == nameof(Models.Settings.PersistedSettings.DefaultAchievementSortMode) &&
                persisted.DefaultAchievementSortMode == Models.Settings.CompactListSortMode.DisplayOrder &&
                persisted.DefaultAchievementSortDescending)
            {
                // RetroAchievements progression order should default to the API sequence (ascending DisplayOrder).
                persisted.DefaultAchievementSortDescending = false;
            }

            // Refresh mock previews when display-affecting settings change
            var refreshProperties = new[]
            {
                nameof(Models.Settings.PersistedSettings.ShowCompactListRarityBar),
                nameof(Models.Settings.PersistedSettings.ShowRarityGlow),
                nameof(Models.Settings.PersistedSettings.ShowHiddenIcon),
                nameof(Models.Settings.PersistedSettings.ShowHiddenTitle),
                nameof(Models.Settings.PersistedSettings.ShowHiddenDescription),
                nameof(Models.Settings.PersistedSettings.ShowHiddenSuffix),
                nameof(Models.Settings.PersistedSettings.ShowLockedIcon),
                nameof(Models.Settings.PersistedSettings.UseSeparateLockedIconsWhenAvailable),
                nameof(Models.Settings.PersistedSettings.UseUniformRarityBadges)
            };

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.UseUniformRarityBadges))
            {
                PercentRarityHelper.ApplyBadgeApplicationResources(
                    _settingsViewModel?.Settings?.Persisted?.UseUniformRarityBadges ?? false);
            }

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.StartPageActivityScope) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.StartPageProgressScope))
            {
                UpdateStartPageScopeTexts();
            }

            if (e.PropertyName == nameof(Models.Settings.PersistedSettings.ViewAchievementsHotkey) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.ManageAchievementsHotkey) ||
                e.PropertyName == nameof(Models.Settings.PersistedSettings.OverviewHotkey))
            {
                UpdateHotkeyButtonTexts();
            }

            if (refreshProperties.Contains(e.PropertyName))
            {
                RefreshMockPreviews();
            }
        }

        // -----------------------------
        // IDisposable implementation
        // -----------------------------

        public object DetachAchievementNotificationsContent()
        {
            // This compatibility control itself is never added to the visual tree, so its
            // Loaded handler will not run. Initialize the notification tab explicitly before
            // moving its content into the v3 settings shell.
            InitializeAchievementNotificationsTab();

            var content = AchievementNotificationsTab?.Content;
            if (content is FrameworkElement contentElement &&
                AchievementNotificationsTab?.DataContext != null)
            {
                // The original tab binds notification controls directly to LocalSettings.
                // Preserve that inherited context when the content is hosted by the v3 shell.
                contentElement.DataContext = AchievementNotificationsTab.DataContext;
                contentElement.Loaded -= NotificationSettingsContent_Loaded;
                contentElement.Loaded += NotificationSettingsContent_Loaded;
            }

            if (AchievementNotificationsTab != null)
            {
                AchievementNotificationsTab.Content = null;
            }

            return content;
        }

        public object DetachThemeMigrationContent()
        {
            if (!_themeMigrationLoaded)
            {
                RefreshThemeMigrationContent();
            }

            var content = ThemeMigrationTab?.Content;
            if (ThemeMigrationTab != null)
            {
                ThemeMigrationTab.Content = null;
            }

            if (content is FrameworkElement contentElement)
            {
                // This page moves into SettingsControl, outside this control's name scope.
                // Keep its ElementName bindings connected to the instance that owns both
                // the selected theme paths and the click handlers.
                var scope = new NameScope();
                scope.RegisterName(nameof(LegacyNotificationSettingsControlRoot), this);
                NameScope.SetNameScope(contentElement, scope);
            }

            return content;
        }

        public void RefreshThemeMigrationContent()
        {
            LoadThemes();
            RefreshStartPageCompatibilityState();
            _themeMigrationLoaded = true;
        }

        public void Dispose()
        {
            if (_settingsHostWindow != null)
            {
                _settingsHostWindow.Closed -= SettingsHostWindow_Closed;
                _settingsHostWindow = null;
            }
            NotificationPublisher.ClosePersistentSettingsPreview();
            if (_notificationPreviewSettings != null)
            {
                _notificationPreviewSettings = null;
            }

            if (_settingsViewModel?.Settings?.Persisted != null)
            {
                _settingsViewModel.Settings.Persisted.PropertyChanged -= OnSettingsPropertyChanged;
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string LF(string key, string fallbackFormat, params object[] args)
        {
            return string.Format(L(key, fallbackFormat), args);
        }

        private static bool IsExcludedTheme(ThemeDiscoveryService.ThemeInfo theme)
        {
            if (theme == null)
            {
                return true;
            }

            if (string.Equals(theme.DisplayName, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(theme.Name, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(theme.Name) &&
                (theme.Name.EndsWith("/Default", StringComparison.OrdinalIgnoreCase) ||
                 theme.Name.EndsWith("\\Default", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private Game GetSelectedNotificationPreviewGame()
        {
            return _plugin?.PlayniteApi?.MainView?.SelectedGames?.FirstOrDefault();
        }

        private AchievementDetail GetSelectedNotificationPreviewAchievement()
        {
            return null;
        }



    }

    /// <summary>
    /// Represents a provider item in the settings Providers overview navigation.
    /// </summary>
    public sealed class LegacyProviderNavigationItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly IProviderSettings _settings;

        public LegacyProviderNavigationItem(
            string providerKey,
            string displayName,
            string groupName,
            string providerIconKey,
            string providerColorHex,
            IProviderSettings settings,
            ProviderSettingsViewBase settingsView,
            string redirectProviderKey = null,
            string subtitle = null)
        {
            ProviderKey = providerKey;
            DisplayName = displayName;
            GroupName = groupName;
            ProviderIconKey = providerIconKey;
            ProviderColorHex = providerColorHex;
            SettingsView = settingsView;
            RedirectProviderKey = redirectProviderKey;
            Subtitle = subtitle;
            _settings = settings;

            if (_settings != null)
            {
                _settings.PropertyChanged += Settings_PropertyChanged;
            }
        }

        public string ProviderKey { get; }
        public string DisplayName { get; }
        public string GroupName { get; }
        public string ProviderIconKey { get; }
        public string ProviderColorHex { get; }
        public string RedirectProviderKey { get; }
        public string Subtitle { get; }
        public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
        public bool IsRedirect => !string.IsNullOrWhiteSpace(RedirectProviderKey);
        public bool IsEnabled => _settings?.IsEnabled ?? true;
        public ProviderSettingsViewBase SettingsView { get; }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(IProviderSettings.IsEnabled), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }

    public sealed class ThemeMigrationElementOption : INotifyPropertyChanged
    {
        private bool _isModern;

        public ThemeMigrationElementOption(string key, string displayName, bool isBindingOption, bool isModern)
        {
            Key = key;
            DisplayName = displayName;
            IsBindingOption = isBindingOption;
            _isModern = isModern;
        }

        public string Key { get; }

        public string DisplayName { get; }

        public bool IsBindingOption { get; }

        public bool IsModern
        {
            get => _isModern;
            set
            {
                if (_isModern != value)
                {
                    _isModern = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModern)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}




