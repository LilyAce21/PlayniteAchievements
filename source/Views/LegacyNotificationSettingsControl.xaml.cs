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
        private readonly DispatcherTimer _notificationAutoPopupPreviewTimer;
        private readonly DispatcherTimer _notificationInlinePreviewLoopTimer;
        private readonly DispatcherTimer _sanViewPreviewLoopTimer;
        private readonly DispatcherTimer _sanViewPreviewCloseTimer;
        private int? _activeSanPreviewView;
        private Providers.Local.LocalSettings _notificationPreviewSettings;
        private bool _isRefreshingNotificationSoundSelection;
        private bool _achievementNotificationDebugSettingReady;
        private const string DefaultSteamSoundPath = @"Resources\Sounds\Steam.wav";

        private sealed class NotificationSoundOption
        {
            public string DisplayName { get; set; }

            public string SoundPath { get; set; }

            public bool IsDefault { get; set; }
        }

        private sealed class NotificationStyleOption
        {
            public string DisplayName { get; set; }

            public string StyleKey { get; set; }
        }

        private sealed class NotificationProviderOption
        {
            public string DisplayName { get; set; }

            public string ProviderKey { get; set; }
        }

        private sealed class NotificationPreviewGameOption
        {
            public string DisplayName { get; set; }

            public Game Game { get; set; }
        }

        private sealed class NotificationPreviewAchievementOption
        {
            public string DisplayName { get; set; }

            public AchievementDetail Achievement { get; set; }
        }

        private sealed class CustomStyleSlotOption
        {
            public string DisplayName { get; set; }

            public int SlotNumber { get; set; }
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

            _notificationAutoPopupPreviewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _notificationAutoPopupPreviewTimer.Tick += NotificationAutoPopupPreviewTimer_Tick;
            _notificationInlinePreviewLoopTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2600)
            };
            _sanViewPreviewLoopTimer = new DispatcherTimer();
            _sanViewPreviewLoopTimer.Tick += SanViewPreviewLoopTimer_Tick;
            _sanViewPreviewCloseTimer = new DispatcherTimer();
            _sanViewPreviewCloseTimer.Tick += SanViewPreviewCloseTimer_Tick;
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
            _notificationAutoPopupPreviewTimer?.Stop();
            _sanViewPreviewLoopTimer?.Stop();
            _sanViewPreviewCloseTimer?.Stop();
            _activeSanPreviewView = null;
            NotificationPublisher.ClosePersistentSettingsPreview();
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


            RefreshCustomStyleSlotControls(localSettings);
            RefreshNotificationCustomIconOptions(localSettings);
            RefreshSanCompatibilityControls(localSettings);
            UpdateCustomStyleInlinePreview(localSettings);
        }





        private void NotificationAutoPopupPreviewTimer_Tick(object sender, EventArgs e)
        {
            _notificationAutoPopupPreviewTimer.Stop();
            if (_activeSanPreviewView.HasValue)
            {
                ShowCustomPopupPreview(forceStatusMessage: true, forcedSanView: _activeSanPreviewView.Value);
                ScheduleSanViewPreviewLoop();
                return;
            }

            ShowCustomPopupPreview(forceStatusMessage: false);
        }

        private void SanViewPreviewLoopTimer_Tick(object sender, EventArgs e)
        {
            _sanViewPreviewLoopTimer.Stop();
            if (!_activeSanPreviewView.HasValue)
            {
                return;
            }

            ShowCustomPopupPreview(forceStatusMessage: true, forcedSanView: _activeSanPreviewView.Value);
            ScheduleSanViewPreviewLoop();
        }

        private void SanViewPreviewCloseTimer_Tick(object sender, EventArgs e)
        {
            _sanViewPreviewCloseTimer.Stop();
            if (_activeSanPreviewView.HasValue)
            {
                NotificationPublisher.ClosePersistentSettingsPreview();
            }
        }

        private void ScheduleSanViewPreviewLoop()
        {
            if (!_activeSanPreviewView.HasValue)
            {
                _sanViewPreviewLoopTimer.Stop();
                _sanViewPreviewCloseTimer.Stop();
                return;
            }

            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            var duration = _activeSanPreviewView.Value == 2
                ? localSettings?.OverlayCustomSanView2DurationMilliseconds ?? 5000
                : localSettings?.OverlayCustomSanView1DurationMilliseconds ?? 5000;
            _sanViewPreviewLoopTimer.Stop();
            _sanViewPreviewCloseTimer.Stop();
            _sanViewPreviewCloseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, duration));
            _sanViewPreviewCloseTimer.Start();
            _sanViewPreviewLoopTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, duration) + 2000);
            _sanViewPreviewLoopTimer.Start();
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
            var localSettings = _providerRegistry?.GetSettingsForEdit("Local") as Providers.Local.LocalSettings;
            if (localSettings == null)
                return;

            var soundPath = localSettings.UnlockSoundPath?.Trim();
            if (!string.IsNullOrWhiteSpace(soundPath))
            {
                soundPath = Services.NotificationPublisher.ResolveSoundPath(soundPath);
            }

            try
            {
                NotificationsTestUnlockSoundButton.IsEnabled = false;
                NotificationsUnlockSoundStatusTextBlock.Text = "Sending test notification...";

                var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;
                if (localSettings.UseUpstreamNotificationTemplate)
                {
                    PlayniteAchievementsPlugin.NotifyAchievementUnlocked(new Models.AchievementUnlockedEventArgs
                    {
                        PlayniteGameId = previewGame?.Id ?? Guid.Empty,
                        GameName = previewGameName,
                        ProviderKey = previewProviderKey,
                        DisplayName = previewAchievement?.DisplayName ?? "Test Achievement",
                        IconPath = previewAchievement?.UnlockedIconPath,
                        Description = previewAchievement?.Description ?? "Test description for preview.",
                        Points = previewAchievement?.Points ?? 25,
                        TrophyType = previewAchievement?.TrophyType ?? "Gold",
                        IsPreview = true
                    });
                    NotificationsUnlockSoundStatusTextBlock.Text = "Test upstream notification sent.";
                    NotificationsTestUnlockSoundButton.IsEnabled = true;
                    return;
                }
                var requiresCustomOverlay = IsSanTransitionStyle(localSettings.UnlockOverlayTransitionStyle) ||
                    !string.IsNullOrWhiteSpace(localSettings.OverlayCustomSanElementPresetId);
                if (requiresCustomOverlay && string.IsNullOrWhiteSpace(soundPath))
                {
                    soundPath = NotificationPublisher.ResolveSanDefaultSoundPath(localSettings);
                }

                var forcedDeliveryMode = requiresCustomOverlay
                    ? (LocalUnlockNotificationDeliveryMode?)LocalUnlockNotificationDeliveryMode.Overlay
                    : null;
                var publisher = new NotificationPublisher(_plugin?.PlayniteApi, _settingsViewModel?.Settings, _logger);
                publisher.ShowLocalAchievementUnlocked(
                    previewGameName,
                    new[] { previewAchievement?.DisplayName ?? "Test Achievement" },
                    soundPath,
                    unlockedAchievementIconPath: previewAchievement?.UnlockedIconPath,
                    game: previewGame,
                    achievementDescription: previewAchievement?.Description ?? "Test description for preview.",
                    achievementPoints: previewAchievement?.Points ?? 25,
                    achievementRarity: FormatPreviewAchievementRarity(previewAchievement, "52.4%"),
                    achievementTrophy: previewAchievement?.TrophyType ?? "Gold",
                    forcedStyle: NotificationPublisher.NotificationStyleCustom,
                    forcedDeliveryMode: forcedDeliveryMode,
                    overrideLocalSettings: localSettings,
                    notificationProviderKey: previewProviderKey,
                    isSettingsPreview: true);

                NotificationsUnlockSoundStatusTextBlock.Text = requiresCustomOverlay
                    ? $"Test custom overlay sent using transition '{localSettings.UnlockOverlayTransitionStyle}'."
                    : $"Test notification sent for {ProviderRegistry.GetLocalizedName(previewProviderKey)} using {localSettings.UnlockNotificationDeliveryMode}.";
                if (NotificationsTestUnlockSoundButton != null)
                    NotificationsTestUnlockSoundButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                NotificationsUnlockSoundStatusTextBlock.Text = $"Failed to send test notification: {ex.Message}";
                if (NotificationsTestUnlockSoundButton != null)
                    NotificationsTestUnlockSoundButton.IsEnabled = true;
            }
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





        private static string FormatPreviewAchievementRarity(AchievementDetail achievement, string fallback)
        {
            return achievement?.GlobalPercentUnlocked.HasValue == true
                ? achievement.GlobalPercentUnlocked.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : fallback;
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
                var previewGameName = string.IsNullOrWhiteSpace(previewGame?.Name) ? "Current Game" : previewGame.Name;
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
}
