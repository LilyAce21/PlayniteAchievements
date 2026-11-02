using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Local
{
    public enum LocalSteamSchemaPreference
    {
        PreferSteam = 0,
        PreferSteamHunters = 1,
        PreferSteamCommunity = 2,
        PreferCompletionist = 3
    }

    public enum LocalImportedGameLibraryTarget
    {
        None = 0,
        Steam = 1,
        CustomSource = 2
    }

    public enum LocalExistingGameImportBehavior
    {
        OverwriteExisting = 0,
        SkipExisting = 1
    }

    public enum LocalIconRateLimitRetryMode
    {
        None = 0,
        FixedRounds = 1,
        Infinite = 2
    }

    public enum LocalUnlockScreenshotCaptureMode
    {
        FullDesktop = 0,
        ActiveWindow = 1
    }

    public enum LocalUnlockScreenshotImageFormat
    {
        Png = 0,
        Jpeg = 1
    }

    public enum LocalUnlockNotificationDeliveryMode
    {
        WindowsToast = 0,
        Overlay = 1,
        Hybrid = 2
    }

    public sealed class ScoreProgressNotificationSettings
    {
        public bool NotifyLevelUp { get; set; }
        public bool NotifyTierUp { get; set; }
        public int CustomStyleSlotIndex { get; set; }
        public string SoundPath { get; set; } = string.Empty;
        public int SoundLeadMilliseconds { get; set; }
    }

    public enum LocalUnlockOverlayPosition
    {
        TopRight = 0,
        TopLeft = 1,
        BottomRight = 2,
        BottomLeft = 3,
        TopCenter = 4,
        BottomCenter = 5
    }

    /// <summary>How the official SAN template decides whether to show the rarity percentage.</summary>
    public enum OfficialSanPercentMode
    {
        Theme = 0,
        Always = 1,
        Never = 2
    }

    public enum LocalOverlayCoverPosition
    {
        None = 0,
        Left = 1,
        Right = 2
    }

    public enum LocalUnlockOverlayTransitionStyle
    {
        Fade = 0,
        SlideFromRight = 1,
        SlideFromLeft = 2,
        SlideFromTop = 3,
        SlideFromBottom = 4,
        Circle = 5,
        SanDefault = 6,
        SanXqjan = 7,
        SanDeck = 8,
        SanEpic = 9,
        SanXboxModern = 10,
        SanXboxClassic = 11,
        SanPsModern = 12,
        SanPsClassic = 13,
        SanPsRetro = 14,
        SanSquare = 15,
        SanAero = 16
    }

    public enum LocalOverlayIconSource
    {
        None = -1,
        AchievementIcon = 0,
        TrophyIcon = 1,
        PentagonIcon = 2,
        ProviderIcon = 3,
        CustomIcon = 4
    }

    public enum LocalCustomOverlayLayoutStyle
    {
        Standard = 0,
        XboxModern = 1,
        XboxClassic = 2
    }

    public enum LocalCustomOverlayAnimationStyle
    {
        Standard = 0,
        SanXqjan = 1,
        SanExpandCard = 2,
        SanSlideCard = 3,
        SanEpic = 4,
        SanAero = 5,
        SanDefault = 6
    }

    public enum LocalSanProviderLogoSource
    {
        ExtensionIcon = 0,
        SanLogo = 1
    }

    public enum LocalSanElementPosition
    {
        Left = 0,
        Right = 1
    }

    public enum LocalSanTextViewTarget
    {
        View1 = 0,
        View2 = 1,
        Both = 2
    }

    public enum LocalOverlayTextFormattingMode
    {
        Auto = 0,
        Ideal = 1,
        Display = 2
    }

    public sealed class LocalMetadataSourceOption
    {
        public LocalMetadataSourceOption(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string Id { get; }

        public string DisplayName { get; }
    }

    public sealed class LocalSteamAppCacheUserOption
    {
        public LocalSteamAppCacheUserOption(string userId, string displayName)
        {
            UserId = userId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string UserId { get; }

        public string DisplayName { get; }
    }

    public sealed class LocalCustomOverlayStyleSlot
    {
        public string Name { get; set; } = string.Empty;
        public bool AutoResizeToContent { get; set; }
        public bool WrapAllText { get; set; }
        public bool ShowLine1 { get; set; } = true;
        public bool ShowBorder { get; set; } = true;
        public double BorderWidth { get; set; } = 1.5;
        public bool ShowGameName { get; set; }
        public bool ShowMeta { get; set; }
        public double IconSize { get; set; } = 58;
        public double SecondaryIconSize { get; set; } = 58;
        public double IconCornerRadius { get; set; } = 10;
        public double SecondaryIconCornerRadius { get; set; } = 10;
        public bool ShowIconBackground { get; set; } = true;
        public bool ShowSecondaryIconBackground { get; set; } = true;
        public string IconBackgroundColor { get; set; } = "#24FFFFFF";
        public string SecondaryIconBackgroundColor { get; set; } = "#18FFFFFF";
        public bool ShowCoverBackground { get; set; } = true;
        public string CoverBackgroundColor { get; set; } = "#1CFFFFFF";
        public double Width { get; set; } = 460;
        public double Height { get; set; } = 128;
        public double CornerRadius { get; set; } = 18;
        public double TitleFontSize { get; set; } = 17;
        public double DetailFontSize { get; set; } = 13;
        public double MetaFontSize { get; set; } = 11;
        public double Line4FontSize { get; set; } = 11;
        public double Line5FontSize { get; set; } = 11;
        public double Line6FontSize { get; set; } = 11;
        public string Line1FontFamily { get; set; } = "Default";
        public string Line2FontFamily { get; set; } = "Default";
        public string Line3FontFamily { get; set; } = "Default";
        public string Line4FontFamily { get; set; } = "Default";
        public string Line5FontFamily { get; set; } = "Default";
        public string Line6FontFamily { get; set; } = "Default";
        public LocalOverlayTextFormattingMode TextFormattingMode { get; set; } = LocalOverlayTextFormattingMode.Auto;
        public string BackgroundColor { get; set; } = "#1E2430";
        public string BorderColor { get; set; } = "#6FA3D8";
        public string AccentColor { get; set; } = "#A7E0FF";
        public string TitleColor { get; set; } = "#FFFFFF";
        public string DetailColor { get; set; } = "#E7EEF7";
        public string MetaColor { get; set; } = "#BCD0E5";
        public string BackgroundImagePath { get; set; } = string.Empty;
        public string TitleTemplate { get; set; } = "Achievement unlocked";
        public string GameNameTemplate { get; set; } = "<gameName>";
        public string AchievementTemplate { get; set; } = "Unlocked: <achievementName>";
        public string MetaTemplate { get; set; } = "<provider> / Custom";
        public string Line4Template { get; set; } = string.Empty;
        public string Line5Template { get; set; } = string.Empty;
        public string Line6Template { get; set; } = string.Empty;
        public bool ShowLine4 { get; set; }
        public bool ShowLine5 { get; set; }
        public ObservableCollection<NotificationProgressBarDefinition> ProgressBars { get; set; } = new ObservableCollection<NotificationProgressBarDefinition>();
        public bool ShowProgressBar { get; set; } = false;
        public double ProgressBarWidth { get; set; } = 0;
        public double ProgressBarHeight { get; set; } = 6;
        public double ProgressBarCornerRadius { get; set; } = 3;
        public double ProgressBarSpacing { get; set; } = 6;
        public string ProgressBarFillColor { get; set; } = "#A7E0FF";
        public string ProgressBarTrackColor { get; set; } = "#30FFFFFF";
        public bool ProgressBarShowLabel { get; set; } = false;
        public string ProgressBarLabelColor { get; set; } = "#FFFFFF";
        public double ProgressBarLabelFontSize { get; set; } = 11;
        public double ProgressBarOffsetX { get; set; } = 0;
        public double ProgressBarOffsetY { get; set; } = 0;
        public bool ShowLine6 { get; set; }
        public string Line4Color { get; set; } = "#BCD0E5";
        public string Line5Color { get; set; } = "#BCD0E5";
        public string Line6Color { get; set; } = "#BCD0E5";
        public string Line1OutlineColor { get; set; } = "transparent";
        public string Line2OutlineColor { get; set; } = "transparent";
        public string Line3OutlineColor { get; set; } = "transparent";
        public string Line4OutlineColor { get; set; } = "transparent";
        public string Line5OutlineColor { get; set; } = "transparent";
        public string Line6OutlineColor { get; set; } = "transparent";
        public string Line1ShadowColor { get; set; } = "transparent";
        public string Line2ShadowColor { get; set; } = "transparent";
        public string Line3ShadowColor { get; set; } = "transparent";
        public string Line4ShadowColor { get; set; } = "transparent";
        public string Line5ShadowColor { get; set; } = "transparent";
        public string Line6ShadowColor { get; set; } = "transparent";
        public double OutlineSize { get; set; } = 0.6;
        public double ShadowSize { get; set; } = 2;
        public double Line1OutlineSize { get; set; } = 0.6;
        public double Line2OutlineSize { get; set; } = 0.6;
        public double Line3OutlineSize { get; set; } = 0.6;
        public double Line4OutlineSize { get; set; } = 0.6;
        public double Line5OutlineSize { get; set; } = 0.6;
        public double Line6OutlineSize { get; set; } = 0.6;
        public double Line1ShadowSize { get; set; } = 2;
        public double Line2ShadowSize { get; set; } = 2;
        public double Line3ShadowSize { get; set; } = 2;
        public double Line4ShadowSize { get; set; } = 2;
        public double Line5ShadowSize { get; set; } = 2;
        public double Line6ShadowSize { get; set; } = 2;
        public bool Line1OutlineEnabled { get; set; }
        public bool Line2OutlineEnabled { get; set; }
        public bool Line3OutlineEnabled { get; set; }
        public bool Line4OutlineEnabled { get; set; }
        public bool Line5OutlineEnabled { get; set; }
        public bool Line6OutlineEnabled { get; set; }
        public bool Line1ShadowEnabled { get; set; }
        public bool Line2ShadowEnabled { get; set; }
        public bool Line3ShadowEnabled { get; set; }
        public bool Line4ShadowEnabled { get; set; }
        public bool Line5ShadowEnabled { get; set; }
        public bool Line6ShadowEnabled { get; set; }
        public double LineSpacing { get; set; } = 3;
        public double Line1Spacing { get; set; } = 3;
        public double Line2Spacing { get; set; } = 3;
        public double Line3Spacing { get; set; } = 3;
        public double Line4Spacing { get; set; } = 3;
        public double Line5Spacing { get; set; } = 3;
        public double Line6Spacing { get; set; } = 3;
        public LocalSanTextViewTarget Line1View { get; set; } = LocalSanTextViewTarget.View1;
        public LocalSanTextViewTarget Line2View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line3View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line4View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line5View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line6View { get; set; } = LocalSanTextViewTarget.View2;
        public int Line1Order { get; set; } = 1;
        public int Line2Order { get; set; } = 1;
        public int Line3Order { get; set; } = 2;
        public int Line4Order { get; set; } = 3;
        public int Line5Order { get; set; } = 4;
        public int Line6Order { get; set; } = 5;
        public LocalCustomOverlayLayoutStyle LayoutStyle { get; set; } = LocalCustomOverlayLayoutStyle.Standard;
        public LocalCustomOverlayAnimationStyle AnimationStyle { get; set; } = LocalCustomOverlayAnimationStyle.Standard;
        public LocalOverlayIconSource SecondaryIconSource { get; set; } = LocalOverlayIconSource.None;
        public bool ShowSecondaryIcon { get; set; }
        public string SanElementPresetId { get; set; } = string.Empty;
        public LocalSanElementPosition SanElementPosition { get; set; } = LocalSanElementPosition.Left;
        public LocalSanProviderLogoSource SanProviderLogoSource { get; set; } = LocalSanProviderLogoSource.ExtensionIcon;
        public LocalUnlockOverlayTransitionStyle TransitionStyle { get; set; } = LocalUnlockOverlayTransitionStyle.Fade;
        public string SanPresetId { get; set; } = string.Empty;
        public string SanPresetHtml { get; set; } = string.Empty;
        public string SanPresetCss { get; set; } = string.Empty;
        public string SanBaseCss { get; set; } = string.Empty;
        public string SanBaseAnimCss { get; set; } = string.Empty;
        public string SanAssetRootPath { get; set; } = string.Empty;
        public string SanThemeJson { get; set; } = string.Empty;
        public string SanThemeDirectory { get; set; } = string.Empty;
        public string ManualElementCss { get; set; } = string.Empty;
        public string ElementAnchorsJson { get; set; } = string.Empty;
        public int SanView1DurationMilliseconds { get; set; } = 5000;
        public int SanView2DurationMilliseconds { get; set; } = 5000;
        public bool TitleBold { get; set; } = true;
        public bool TitleItalic { get; set; }
        public bool TitleUnderline { get; set; }
        public bool TitleStrikethrough { get; set; }
        public bool DetailBold { get; set; }
        public bool DetailItalic { get; set; }
        public bool DetailUnderline { get; set; }
        public bool DetailStrikethrough { get; set; }
        public bool MetaBold { get; set; } = true;
        public bool MetaItalic { get; set; }
        public bool MetaUnderline { get; set; }
        public bool MetaStrikethrough { get; set; }
        public bool Line4Bold { get; set; }
        public bool Line4Italic { get; set; }
        public bool Line4Underline { get; set; }
        public bool Line4Strikethrough { get; set; }
        public bool Line5Bold { get; set; }
        public bool Line5Italic { get; set; }
        public bool Line5Underline { get; set; }
        public bool Line5Strikethrough { get; set; }
        public bool Line6Bold { get; set; }
        public bool Line6Italic { get; set; }
        public bool Line6Underline { get; set; }
        public bool Line6Strikethrough { get; set; }
        public LocalOverlayIconSource IconSource { get; set; } = LocalOverlayIconSource.AchievementIcon;
        public string CustomIconPath { get; set; } = string.Empty;
        public string SecondaryCustomIconPath { get; set; } = string.Empty;
        public bool ShowIconRarityGlow { get; set; }
        public bool ShowSecondaryIconRarityGlow { get; set; }
        public bool EnableGameCoverInOverlay { get; set; }
        public LocalOverlayCoverPosition GameCoverPosition { get; set; } = LocalOverlayCoverPosition.Right;
        public double GameCoverWidth { get; set; } = 80;
        public bool CenterGameCoverVertically { get; set; }
        public bool EnableGameBannerAsBackground { get; set; }
        public double GameBannerOpacity { get; set; } = 0.3;
        public int GameBannerBlurRadius { get; set; } = 8;
        public string CustomCoverImagePath { get; set; } = string.Empty;
        public string CustomBannerImagePath { get; set; } = string.Empty;
    }

    public class LocalSettings : ProviderSettingsBase
    {
        // Runtime-only copy prepared for one notification, never part of a saved style.
        [Newtonsoft.Json.JsonIgnore]
        internal bool AchievementProgressPrepared { get; set; }

        public const int MinActiveGameMonitoringIntervalSeconds = 1;
        public const int MaxActiveGameMonitoringIntervalSeconds = 60;
        public const int MinMissingAchievementFilePollingIntervalSeconds = 1;
        public const int MaxMissingAchievementFilePollingIntervalSeconds = 300;
        public const double MinCustomOverlayHeight = 45;
        public const int ProviderIconMaxPixelSize = 64;
        public const string DefaultBundledUnlockSoundPath = @"Resources\Sounds\Steam.wav";
        public const string DefaultScreenshotFilenameTemplate = "<dateTime>_<gameName>_<achievementName>";
        public const string DefaultRecordingFilenameTemplate = "<dateTime>_<gameName>_<achievementName>";
        public const string SteamAppCacheUserNone = "<none>";

        private Dictionary<Guid, int> _steamAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, int> _lumaPlayAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, string> _lumaPlayIniPathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _epicSchemaPathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _epicSavePathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _epicProductIdOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _localFolderOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _customSchemaPathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, bool> _customSchemaEnabledOverrides = new Dictionary<Guid, bool>();
        private Dictionary<Guid, string> _steamAppCacheUserOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, bool> _refreshOnGameCloseOverrides = new Dictionary<Guid, bool>();
        private string _steamUserdataPath = string.Empty;
        private bool _useSteamHuntersForCategories = true;
        private bool _enableActiveGameMonitoring;
        private bool _refreshAchievementsOnGameClose;
        private int _activeGameMonitoringIntervalSeconds = 5;
        private int _missingAchievementFilePollingIntervalSeconds = 5;
        private bool _enableUnlockScreenshots;
        private string _screenshotSaveFolder = string.Empty;
        private string _screenshotFilenameTemplate = DefaultScreenshotFilenameTemplate;
        private bool _screenshotClean;
        private bool _screenshotWithNotification = true;
        private bool _screenshotFramed;
        private string _screenshotSuffixClean = "clean";
        private string _screenshotSuffixWithNotification = "notification";
        private string _screenshotSuffixFramed = "framed";
        private SanScreenshotView _sanScreenshotView = SanScreenshotView.Automatic;
        private RaritySelection _screenshotCleanRarities = RaritySelection.All;
        private bool _screenshotCleanAlwaysCaptureCompletion = true;
        private RaritySelection _screenshotWithNotificationRarities = RaritySelection.All;
        private bool _screenshotWithNotificationAlwaysCaptureCompletion = true;
        private RaritySelection _screenshotFramedRarities = RaritySelection.All;
        private bool _screenshotFramedAlwaysCaptureCompletion = true;
        private bool _enableUnlockRecordings;
        private string _recordingSaveFolder = string.Empty;
        private string _recordingFilenameTemplate = DefaultRecordingFilenameTemplate;
        private int _recordingClipSeconds = 15;
        private int _recordingFps = 30;
        private RecordingResolution _recordingResolution = RecordingResolution.Native;
        private bool _recordingIncludeAudio;
        private RecordingAudioSource _recordingAudioSource = RecordingAudioSource.FullSystem;
        private bool _recordingIncludeMicrophone;
        private RaritySelection _recordingRarities = RaritySelection.All;
        private bool _recordingAlwaysCaptureCompletion = true;
        private LocalUnlockNotificationDeliveryMode _unlockNotificationDeliveryMode = LocalUnlockNotificationDeliveryMode.Hybrid;
        private bool _showOverlayOnActiveGameMonitor = false;
        private bool _enableOverlayDebugLogging;
        private int _unlockOverlayFadeInMilliseconds = 180;
        private int _unlockOverlayFadeOutMilliseconds = 280;
        private LocalUnlockOverlayTransitionStyle _unlockOverlayTransitionStyle = LocalUnlockOverlayTransitionStyle.Fade;
        private int _unlockSoundLeadMilliseconds;
        private string _overlayCustomTitleTemplate = "Achievement unlocked";
        private string _overlayCustomGameNameTemplate = "<gameName>";
        private string _overlayCustomAchievementTemplate = "Unlocked: <achievementName>";
        private string _overlayCustomMetaTemplate = "<provider> / Custom";
        private string _overlayCustomLine4Template = string.Empty;
        private string _overlayCustomLine5Template = string.Empty;
        private string _overlayCustomLine6Template = string.Empty;
        private string _overlayCustomSanElementPresetId = string.Empty;
        private int _overlayCustomSanView1DurationMilliseconds = 5000;
        private int _overlayCustomSanView2DurationMilliseconds = 5000;
        private string _overlayCustomIconPath = string.Empty;
        private string _overlayCustomSecondaryIconPath = string.Empty;
        private string _overlayCustomIconLibraryPaths = string.Empty;
        private int _selectedCustomStyleSlot = 1;
        private List<LocalCustomOverlayStyleSlot> _customOverlayStyleSlots = CreateDefaultCustomOverlayStyleSlots();
        private bool _enableInAppUnlockNotifications = true;
        private bool _enableWindowsToastNotifications = true;
        private string _bundledUnlockSoundPath = string.Empty;
        private string _customUnlockSoundPath = string.Empty;
        private string _extraUnlockSoundPaths = string.Empty;
        private ScoreProgressNotificationSettings _collectionProgressNotifications = new ScoreProgressNotificationSettings();
        private ScoreProgressNotificationSettings _prestigeProgressNotifications = new ScoreProgressNotificationSettings();
        private LocalSteamSchemaPreference _steamSchemaPreference = LocalSteamSchemaPreference.PreferSteam;
        private LocalImportedGameLibraryTarget _importedGameLibraryTarget = LocalImportedGameLibraryTarget.None;
        private string _importedGameCustomSourceName = string.Empty;
        private string _importedGameMetadataSourceId = string.Empty;
        private string _successStoryImportTargetSourceName = string.Empty;
        private string _successStoryImportPath = string.Empty;
        private string _successStoryImportCategoryKeys = string.Empty;
        private string _successStoryImportExcludedCategoryKeys = string.Empty;
        private string _successStoryCategoryScanSnapshotJson = string.Empty;
        private bool _successStoryOverwriteExistingAchievements;
        private LocalExistingGameImportBehavior _existingGameImportBehavior = LocalExistingGameImportBehavior.OverwriteExisting;
        private bool _includeFoldersWithoutAchievementFilesOnImport;
        private LocalIconRateLimitRetryMode _iconRateLimitRetryMode = LocalIconRateLimitRetryMode.FixedRounds;
        private int _iconRateLimitRetryRounds = 2;
        private string _steamAppCacheUserId = string.Empty;
        private string _customProviderIconPath = string.Empty;
        private string _borrowedProviderIconKey = string.Empty;
        private bool _warnOnAmbiguousLocalFolder = true;
        private bool _enableGameCoverInOverlay = false;
        private LocalOverlayCoverPosition _gameCoverPosition = LocalOverlayCoverPosition.Right;
        private double _gameCoverWidth = 80;
        private bool _centerGameCoverVertically;
        private bool _enableGameBannerAsBackground = false;
        private double _gameBannerOpacity = 0.3;
        private int _gameBannerBlurRadius = 8;

        public override string ProviderKey => "Local";

        public string ExtraLocalPaths { get; set; } = string.Empty;

        public string ExcludedLocalPaths { get; set; } = string.Empty;

        private bool _useUpstreamNotificationTemplate;

        public bool UseUpstreamNotificationTemplate
        {
            get => _useUpstreamNotificationTemplate;
            set
            {
                if (SetValue(ref _useUpstreamNotificationTemplate, value) && value && _useOfficialSanTemplate)
                {
                    UseOfficialSanTemplate = false;
                }
            }
        }

        // ---- Official Steam Achievement Notifier (.san) template -------------------------------
        // A third notification template next to the Santodan-fork template and the upstream one.
        // It renders an imported .san theme exactly as Steam Achievement Notifier does, filled with
        // Playnite Achievements' own data (rarity tiers, custom trophy icons, hidden state ...).
        // Every option below belongs to this template only; it does not read the upstream or the
        // Santodan template settings.
        private bool _useOfficialSanTemplate;
        private Models.Settings.OfficialSanProfiles _officialSanProfiles = new Models.Settings.OfficialSanProfiles();
        private bool _officialSanShowOnActiveGameMonitor;
        private int _officialSanScalePercent = 100;
        private OfficialSanPercentMode _officialSanPercentMode = OfficialSanPercentMode.Theme;
        private bool _officialSanUseCustomRarityIcons = true;
        private bool _officialSanShowHiddenAsLocked = true;
        private bool _officialSanApplyUpstreamElements;

        public bool UseOfficialSanTemplate
        {
            get => _useOfficialSanTemplate;
            set
            {
                if (SetValue(ref _useOfficialSanTemplate, value) && value && _useUpstreamNotificationTemplate)
                {
                    UseUpstreamNotificationTemplate = false;
                }
            }
        }

        /// <summary>Per-rarity, capstone and completion notification setups (theme, position, sound, volume, time, second theme).</summary>
        public Models.Settings.OfficialSanProfiles OfficialSanProfiles
        {
            get => _officialSanProfiles;
            set => SetValue(ref _officialSanProfiles, value ?? new Models.Settings.OfficialSanProfiles());
        }

        public bool OfficialSanShowOnActiveGameMonitor { get => _officialSanShowOnActiveGameMonitor; set => SetValue(ref _officialSanShowOnActiveGameMonitor, value); }

        /// <summary>Extra scale on top of the theme's own scale (100 = as authored).</summary>
        public int OfficialSanScalePercent
        {
            get => _officialSanScalePercent;
            set => SetValue(ref _officialSanScalePercent, Math.Max(25, Math.Min(400, value)));
        }

        public OfficialSanPercentMode OfficialSanPercentMode { get => _officialSanPercentMode; set => SetValue(ref _officialSanPercentMode, value); }

        /// <summary>Feeds the Display-settings custom rarity / trophy / capstone Platinum images into the theme.</summary>
        public bool OfficialSanUseCustomRarityIcons { get => _officialSanUseCustomRarityIcons; set => SetValue(ref _officialSanUseCustomRarityIcons, value); }

        /// <summary>Marks hidden achievements so themes with a hidden-icon element show it.</summary>
        public bool OfficialSanShowHiddenAsLocked { get => _officialSanShowHiddenAsLocked; set => SetValue(ref _officialSanShowHiddenAsLocked, value); }



        /// <summary>True when capture (screenshots, recordings) should ignore the Memories settings.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool UpstreamCaptureOverridesMemories =>
            true;





        public bool OfficialSanApplyUpstreamElements { get => _officialSanApplyUpstreamElements; set => SetValue(ref _officialSanApplyUpstreamElements, value); }

        public bool EnableActiveGameMonitoring
        {
            get => _enableActiveGameMonitoring;
            set => SetValue(ref _enableActiveGameMonitoring, value);
        }


        public int ActiveGameMonitoringIntervalSeconds
        {
            get => _activeGameMonitoringIntervalSeconds;
            set => SetValue(
                ref _activeGameMonitoringIntervalSeconds,
                Math.Max(MinActiveGameMonitoringIntervalSeconds, Math.Min(MaxActiveGameMonitoringIntervalSeconds, value)));
        }

        public int MissingAchievementFilePollingIntervalSeconds
        {
            get => _missingAchievementFilePollingIntervalSeconds;
            set => SetValue(
                ref _missingAchievementFilePollingIntervalSeconds,
                Math.Max(
                    MinMissingAchievementFilePollingIntervalSeconds,
                    Math.Min(MaxMissingAchievementFilePollingIntervalSeconds, value)));
        }

        public bool EnableUnlockScreenshots
        {
            get => _enableUnlockScreenshots;
            set => SetValue(ref _enableUnlockScreenshots, value);
        }

        public string ScreenshotSaveFolder
        {
            get => _screenshotSaveFolder;
            set
            {
                if (SetValue(ref _screenshotSaveFolder, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(EffectiveScreenshotSaveFolder));
                }
            }
        }

        [JsonIgnore]
        public string EffectiveScreenshotSaveFolder => GetEffectiveScreenshotSaveFolder();

        public string ScreenshotFilenameTemplate
        {
            get => _screenshotFilenameTemplate;
            set => SetValue(
                ref _screenshotFilenameTemplate,
                string.IsNullOrWhiteSpace(value) ? DefaultScreenshotFilenameTemplate : value);
        }






        public bool ScreenshotClean { get => _screenshotClean; set => SetValue(ref _screenshotClean, value); }
        public bool ScreenshotWithNotification { get => _screenshotWithNotification; set => SetValue(ref _screenshotWithNotification, value); }
        public bool ScreenshotFramed { get => _screenshotFramed; set => SetValue(ref _screenshotFramed, value); }
        public string ScreenshotSuffixClean { get => _screenshotSuffixClean; set => SetValue(ref _screenshotSuffixClean, value ?? string.Empty); }
        public string ScreenshotSuffixWithNotification { get => _screenshotSuffixWithNotification; set => SetValue(ref _screenshotSuffixWithNotification, value ?? string.Empty); }
        public string ScreenshotSuffixFramed { get => _screenshotSuffixFramed; set => SetValue(ref _screenshotSuffixFramed, value ?? string.Empty); }
        public SanScreenshotView SanScreenshotView { get => _sanScreenshotView; set => SetValue(ref _sanScreenshotView, value); }
        public RaritySelection ScreenshotCleanRarities { get => _screenshotCleanRarities; set => SetValue(ref _screenshotCleanRarities, value); }
        public bool ScreenshotCleanAlwaysCaptureCompletion { get => _screenshotCleanAlwaysCaptureCompletion; set => SetValue(ref _screenshotCleanAlwaysCaptureCompletion, value); }
        public RaritySelection ScreenshotWithNotificationRarities { get => _screenshotWithNotificationRarities; set => SetValue(ref _screenshotWithNotificationRarities, value); }
        public bool ScreenshotWithNotificationAlwaysCaptureCompletion { get => _screenshotWithNotificationAlwaysCaptureCompletion; set => SetValue(ref _screenshotWithNotificationAlwaysCaptureCompletion, value); }
        public RaritySelection ScreenshotFramedRarities { get => _screenshotFramedRarities; set => SetValue(ref _screenshotFramedRarities, value); }
        public bool ScreenshotFramedAlwaysCaptureCompletion { get => _screenshotFramedAlwaysCaptureCompletion; set => SetValue(ref _screenshotFramedAlwaysCaptureCompletion, value); }


        public LocalUnlockNotificationDeliveryMode UnlockNotificationDeliveryMode
        {
            get => _unlockNotificationDeliveryMode;
            set => SetValue(ref _unlockNotificationDeliveryMode, value);
        }


        public bool EnableUnlockRecordings
        {
            get => _enableUnlockRecordings;
            set => SetValue(ref _enableUnlockRecordings, value);
        }

        public string RecordingSaveFolder
        {
            get => _recordingSaveFolder;
            set => SetValue(ref _recordingSaveFolder, value ?? string.Empty);
        }

        public string RecordingFilenameTemplate
        {
            get => _recordingFilenameTemplate;
            set => SetValue(
                ref _recordingFilenameTemplate,
                string.IsNullOrWhiteSpace(value) ? DefaultRecordingFilenameTemplate : value);
        }

        public int RecordingClipSeconds
        {
            get => _recordingClipSeconds;
            set => SetValue(ref _recordingClipSeconds, Math.Min(60, Math.Max(5, value)));
        }

        public int RecordingFps
        {
            get => _recordingFps;
            set => SetValue(ref _recordingFps, Math.Min(60, Math.Max(10, value)));
        }

        public RecordingResolution RecordingResolution
        {
            get => _recordingResolution;
            set => SetValue(ref _recordingResolution, value);
        }

        public bool RecordingIncludeAudio
        {
            get => _recordingIncludeAudio;
            set => SetValue(ref _recordingIncludeAudio, value);
        }

        public RecordingAudioSource RecordingAudioSource
        {
            get => _recordingAudioSource;
            set => SetValue(ref _recordingAudioSource, value);
        }

        public bool RecordingIncludeMicrophone
        {
            get => _recordingIncludeMicrophone;
            set => SetValue(ref _recordingIncludeMicrophone, value);
        }

        public RaritySelection RecordingRarities
        {
            get => _recordingRarities;
            set
            {
                if (SetValue(ref _recordingRarities, value))
                {
                    OnPropertyChanged(nameof(RecordingCaptureCommon));
                    OnPropertyChanged(nameof(RecordingCaptureUncommon));
                    OnPropertyChanged(nameof(RecordingCaptureRare));
                    OnPropertyChanged(nameof(RecordingCaptureUltraRare));
                }
            }
        }

        public bool RecordingAlwaysCaptureCompletion
        {
            get => _recordingAlwaysCaptureCompletion;
            set => SetValue(ref _recordingAlwaysCaptureCompletion, value);
        }

        [JsonIgnore]
        public bool RecordingCaptureCommon { get => RecordingRarities.Contains(RarityTier.Common); set => SetRecordingRarity(RaritySelection.Common, value); }
        [JsonIgnore]
        public bool RecordingCaptureUncommon { get => RecordingRarities.Contains(RarityTier.Uncommon); set => SetRecordingRarity(RaritySelection.Uncommon, value); }
        [JsonIgnore]
        public bool RecordingCaptureRare { get => RecordingRarities.Contains(RarityTier.Rare); set => SetRecordingRarity(RaritySelection.Rare, value); }
        [JsonIgnore]
        public bool RecordingCaptureUltraRare { get => RecordingRarities.Contains(RarityTier.UltraRare); set => SetRecordingRarity(RaritySelection.UltraRare, value); }


        private void SetRecordingRarity(RaritySelection rarity, bool enabled)
        {
            RecordingRarities = enabled ? RecordingRarities | rarity : RecordingRarities & ~rarity;
        }

        public bool ShowOverlayOnActiveGameMonitor
        {
            get => _showOverlayOnActiveGameMonitor;
            set => SetValue(ref _showOverlayOnActiveGameMonitor, value);
        }

        public bool EnableOverlayDebugLogging
        {
            get => _enableOverlayDebugLogging;
            set => SetValue(ref _enableOverlayDebugLogging, value);
        }


        public int UnlockOverlayFadeInMilliseconds
        {
            get => _unlockOverlayFadeInMilliseconds;
            set => SetValue(ref _unlockOverlayFadeInMilliseconds, Math.Max(0, Math.Min(4000, value)));
        }

        public int UnlockOverlayFadeOutMilliseconds
        {
            get => _unlockOverlayFadeOutMilliseconds;
            set => SetValue(ref _unlockOverlayFadeOutMilliseconds, Math.Max(0, Math.Min(4000, value)));
        }

        public LocalUnlockOverlayTransitionStyle UnlockOverlayTransitionStyle
        {
            get => _unlockOverlayTransitionStyle;
            set
            {
                if (SetValue(ref _unlockOverlayTransitionStyle, value))
                {
                    OnPropertyChanged(nameof(HasSanScreenshotTiming));
                }
            }
        }


        public int UnlockSoundLeadMilliseconds
        {
            get => _unlockSoundLeadMilliseconds;
            set => SetValue(ref _unlockSoundLeadMilliseconds, value);
        }











































        public string OverlayCustomTitleTemplate
        {
            get => _overlayCustomTitleTemplate;
            set => SetValue(ref _overlayCustomTitleTemplate, value ?? string.Empty);
        }

        public string OverlayCustomGameNameTemplate
        {
            get => _overlayCustomGameNameTemplate;
            set => SetValue(ref _overlayCustomGameNameTemplate, value ?? string.Empty);
        }

        public string OverlayCustomAchievementTemplate
        {
            get => _overlayCustomAchievementTemplate;
            set => SetValue(ref _overlayCustomAchievementTemplate, value ?? string.Empty);
        }

        public string OverlayCustomMetaTemplate
        {
            get => _overlayCustomMetaTemplate;
            set => SetValue(ref _overlayCustomMetaTemplate, value ?? string.Empty);
        }

        public string OverlayCustomLine4Template
        {
            get => _overlayCustomLine4Template;
            set => SetValue(ref _overlayCustomLine4Template, value ?? string.Empty);
        }

        public string OverlayCustomLine5Template
        {
            get => _overlayCustomLine5Template;
            set => SetValue(ref _overlayCustomLine5Template, value ?? string.Empty);
        }

        public string OverlayCustomLine6Template
        {
            get => _overlayCustomLine6Template;
            set => SetValue(ref _overlayCustomLine6Template, value ?? string.Empty);
        }



        public static ObservableCollection<NotificationProgressBarDefinition> CloneProgressBars(IEnumerable<NotificationProgressBarDefinition> bars)
        {
            var copy=new ObservableCollection<NotificationProgressBarDefinition>((bars ?? Enumerable.Empty<NotificationProgressBarDefinition>()).Where(bar=>bar!=null).Select(bar=>bar.Clone()));
            NotificationBarLayout.Migrate(copy);
            return copy;
        }
        [JsonIgnore]
        internal Dictionary<NotificationBarSource, NotificationBarCounter> NotificationBarCounters { get; set; }













        [JsonIgnore]
        public bool OverlayCustomPreviewShowProgress { get => _previewShowProgress; set => SetValue(ref _previewShowProgress, value); }
        private bool _previewShowProgress = true;

        [JsonIgnore]
        internal int? AchievementProgressCurrent { get; set; }
        [JsonIgnore]
        internal int? AchievementProgressMaximum { get; set; }
























































        public string OverlayCustomSanElementPresetId
        {
            get => _overlayCustomSanElementPresetId;
            set
            {
                if (SetValue(ref _overlayCustomSanElementPresetId, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasSanScreenshotTiming));
                }
            }
        }

        [JsonIgnore]
        public bool HasSanScreenshotTiming =>
            UnlockOverlayTransitionStyle.ToString().StartsWith("San", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(OverlayCustomSanElementPresetId);













        public int OverlayCustomSanView1DurationMilliseconds
        {
            get => _overlayCustomSanView1DurationMilliseconds;
            set => SetValue(ref _overlayCustomSanView1DurationMilliseconds, Math.Max(500, Math.Min(30000, value)));
        }

        public int OverlayCustomSanView2DurationMilliseconds
        {
            get => _overlayCustomSanView2DurationMilliseconds;
            set => SetValue(ref _overlayCustomSanView2DurationMilliseconds, Math.Max(500, Math.Min(30000, value)));
        }


























        public string OverlayCustomIconPath
        {
            get => _overlayCustomIconPath;
            set => SetValue(ref _overlayCustomIconPath, value ?? string.Empty);
        }

        public string OverlayCustomSecondaryIconPath
        {
            get => _overlayCustomSecondaryIconPath;
            set => SetValue(ref _overlayCustomSecondaryIconPath, value ?? string.Empty);
        }

        public string OverlayCustomIconLibraryPaths
        {
            get => _overlayCustomIconLibraryPaths;
            set => SetValue(ref _overlayCustomIconLibraryPaths, value ?? string.Empty);
        }

        public IEnumerable<string> GetOverlayCustomIconLibraryPathEntries()
        {
            return SplitCustomIconLibraryPaths(OverlayCustomIconLibraryPaths);
        }






        public int SelectedCustomStyleSlot
        {
            get => _selectedCustomStyleSlot;
            set => SetValue(ref _selectedCustomStyleSlot, Math.Max(1, value));
        }

        public List<LocalCustomOverlayStyleSlot> CustomOverlayStyleSlots
        {
            get => _customOverlayStyleSlots;
            set => SetValue(ref _customOverlayStyleSlots, NormalizeCustomOverlayStyleSlots(value));
        }

        public bool EnableWindowsToastNotifications
        {
            get => _enableWindowsToastNotifications;
            set => SetValue(ref _enableWindowsToastNotifications, value);
        }

        public bool EnableInAppUnlockNotifications
        {
            get => _enableInAppUnlockNotifications;
            set => SetValue(ref _enableInAppUnlockNotifications, value);
        }

        public string ExtraUnlockSoundPaths
        {
            get => _extraUnlockSoundPaths;
            set => SetValue(ref _extraUnlockSoundPaths, value ?? string.Empty);
        }









        public ScoreProgressNotificationSettings CollectionProgressNotifications
        {
            get => _collectionProgressNotifications ?? (_collectionProgressNotifications = new ScoreProgressNotificationSettings());
            set => SetValue(ref _collectionProgressNotifications, value ?? new ScoreProgressNotificationSettings());
        }

        public ScoreProgressNotificationSettings PrestigeProgressNotifications
        {
            get => _prestigeProgressNotifications ?? (_prestigeProgressNotifications = new ScoreProgressNotificationSettings());
            set => SetValue(ref _prestigeProgressNotifications, value ?? new ScoreProgressNotificationSettings());
        }



        public string BundledUnlockSoundPath
        {
            get => _bundledUnlockSoundPath;
            set
            {
                if (SetValue(ref _bundledUnlockSoundPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(UnlockSoundPath));
                }
            }
        }

        public string CustomUnlockSoundPath
        {
            get => _customUnlockSoundPath;
            set
            {
                if (SetValue(ref _customUnlockSoundPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(UnlockSoundPath));
                }
            }
        }

        [JsonIgnore]
        public string UnlockSoundPath
        {
            get => !string.IsNullOrWhiteSpace(CustomUnlockSoundPath)
                ? CustomUnlockSoundPath
                : GetEffectiveBundledUnlockSoundPath();
        }

        [JsonIgnore]
        public string EffectiveBundledUnlockSoundPath => GetEffectiveBundledUnlockSoundPath();


        public string SteamUserdataPath
        {
            get => _steamUserdataPath;
            set => SetValue(ref _steamUserdataPath, value ?? string.Empty);
        }

        public bool UseSteamHuntersForCategories
        {
            get => _useSteamHuntersForCategories;
            set => SetValue(ref _useSteamHuntersForCategories, value);
        }

        public Dictionary<Guid, string> SteamAppCacheUserOverrides
        {
            get => _steamAppCacheUserOverrides;
            set => SetValue(ref _steamAppCacheUserOverrides, value ?? new Dictionary<Guid, string>());
        }

        public bool RefreshAchievementsOnGameClose
        {
            get => _refreshAchievementsOnGameClose;
            set => SetValue(ref _refreshAchievementsOnGameClose, value);
        }

        public Dictionary<Guid, bool> RefreshOnGameCloseOverrides
        {
            get => _refreshOnGameCloseOverrides;
            set => SetValue(ref _refreshOnGameCloseOverrides, value ?? new Dictionary<Guid, bool>());
        }

        public LocalSteamSchemaPreference SteamSchemaPreference
        {
            get => _steamSchemaPreference;
            set => SetValue(ref _steamSchemaPreference, value);
        }

        public LocalImportedGameLibraryTarget ImportedGameLibraryTarget
        {
            get => _importedGameLibraryTarget;
            set => SetValue(ref _importedGameLibraryTarget, value);
        }

        public string ImportedGameCustomSourceName
        {
            get => _importedGameCustomSourceName;
            set => SetValue(ref _importedGameCustomSourceName, value ?? string.Empty);
        }

        public string ImportedGameMetadataSourceId
        {
            get => _importedGameMetadataSourceId;
            set => SetValue(ref _importedGameMetadataSourceId, value ?? string.Empty);
        }

        public string SuccessStoryImportCategoryKeys
        {
            get => _successStoryImportCategoryKeys;
            set => SetValue(ref _successStoryImportCategoryKeys, value ?? string.Empty);
        }

        public string SuccessStoryImportExcludedCategoryKeys
        {
            get => _successStoryImportExcludedCategoryKeys;
            set => SetValue(ref _successStoryImportExcludedCategoryKeys, value ?? string.Empty);
        }

        public string SuccessStoryImportTargetSourceName
        {
            get => _successStoryImportTargetSourceName;
            set => SetValue(ref _successStoryImportTargetSourceName, value ?? string.Empty);
        }

        public string SuccessStoryImportPath
        {
            get => _successStoryImportPath;
            set => SetValue(ref _successStoryImportPath, value ?? string.Empty);
        }

        public string SuccessStoryCategoryScanSnapshotJson
        {
            get => _successStoryCategoryScanSnapshotJson;
            set => SetValue(ref _successStoryCategoryScanSnapshotJson, value ?? string.Empty);
        }

        public bool SuccessStoryOverwriteExistingAchievements
        {
            get => _successStoryOverwriteExistingAchievements;
            set => SetValue(ref _successStoryOverwriteExistingAchievements, value);
        }

        public LocalExistingGameImportBehavior ExistingGameImportBehavior
        {
            get => _existingGameImportBehavior;
            set => SetValue(ref _existingGameImportBehavior, value);
        }

        public bool IncludeFoldersWithoutAchievementFilesOnImport
        {
            get => _includeFoldersWithoutAchievementFilesOnImport;
            set => SetValue(ref _includeFoldersWithoutAchievementFilesOnImport, value);
        }

        public LocalIconRateLimitRetryMode IconRateLimitRetryMode
        {
            get => _iconRateLimitRetryMode;
            set => SetValue(ref _iconRateLimitRetryMode, value);
        }

        public int IconRateLimitRetryRounds
        {
            get => _iconRateLimitRetryRounds;
            set => SetValue(ref _iconRateLimitRetryRounds, Math.Max(1, Math.Min(20, value)));
        }

        public string SteamAppCacheUserId
        {
            get => _steamAppCacheUserId;
            set => SetValue(ref _steamAppCacheUserId, value ?? string.Empty);
        }

        public string CustomProviderIconPath
        {
            get => _customProviderIconPath;
            set => SetValue(ref _customProviderIconPath, value ?? string.Empty);
        }

        /// <summary>
        /// When set to a non-empty provider key (e.g. "Steam"), the Local provider will borrow that
        /// platform's vector icon and colour instead of the built-in Local icon.
        /// A custom image file (<see cref="CustomProviderIconPath"/>) takes precedence over this.
        /// </summary>
        public string BorrowedProviderIconKey
        {
            get => _borrowedProviderIconKey;
            set => SetValue(ref _borrowedProviderIconKey, value ?? string.Empty);
        }

        /// <summary>
        /// When true, shows a notification when multiple local folders are detected for the same
        /// game so the user can set a folder override to resolve the ambiguity.
        /// </summary>
        public bool WarnOnAmbiguousLocalFolder
        {
            get => _warnOnAmbiguousLocalFolder;
            set => SetValue(ref _warnOnAmbiguousLocalFolder, value);
        }

        public bool EnableGameCoverInOverlay
        {
            get => _enableGameCoverInOverlay;
            set => SetValue(ref _enableGameCoverInOverlay, value);
        }

        public LocalOverlayCoverPosition GameCoverPosition
        {
            get => _gameCoverPosition;
            set => SetValue(ref _gameCoverPosition, value);
        }

        public double GameCoverWidth
        {
            get => _gameCoverWidth;
            set => SetValue(ref _gameCoverWidth, Math.Max(40, Math.Min(200, value)));
        }

        public bool CenterGameCoverVertically
        {
            get => _centerGameCoverVertically;
            set => SetValue(ref _centerGameCoverVertically, value);
        }

        public bool EnableGameBannerAsBackground
        {
            get => _enableGameBannerAsBackground;
            set => SetValue(ref _enableGameBannerAsBackground, value);
        }

        public double GameBannerOpacity
        {
            get => _gameBannerOpacity;
            set => SetValue(ref _gameBannerOpacity, Math.Max(0.1, Math.Min(1.0, value)));
        }

        public int GameBannerBlurRadius
        {
            get => _gameBannerBlurRadius;
            set => SetValue(ref _gameBannerBlurRadius, Math.Max(0, Math.Min(50, value)));
        }

        public Dictionary<Guid, int> SteamAppIdOverrides
        {
            get => _steamAppIdOverrides;
            set => SetValue(ref _steamAppIdOverrides, value ?? new Dictionary<Guid, int>());
        }

        public Dictionary<Guid, int> LumaPlayAppIdOverrides
        {
            get => _lumaPlayAppIdOverrides;
            set => SetValue(ref _lumaPlayAppIdOverrides, value ?? new Dictionary<Guid, int>());
        }

        public Dictionary<Guid, string> LumaPlayIniPathOverrides
        {
            get => _lumaPlayIniPathOverrides;
            set => SetValue(ref _lumaPlayIniPathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> EpicSchemaPathOverrides
        {
            get => _epicSchemaPathOverrides;
            set => SetValue(ref _epicSchemaPathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> EpicSavePathOverrides
        {
            get => _epicSavePathOverrides;
            set => SetValue(ref _epicSavePathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> EpicProductIdOverrides
        {
            get => _epicProductIdOverrides;
            set => SetValue(ref _epicProductIdOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> LocalFolderOverrides
        {
            get => _localFolderOverrides;
            set => SetValue(ref _localFolderOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> CustomSchemaPathOverrides
        {
            get => _customSchemaPathOverrides;
            set => SetValue(ref _customSchemaPathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, bool> CustomSchemaEnabledOverrides
        {
            get => _customSchemaEnabledOverrides;
            set => SetValue(ref _customSchemaEnabledOverrides, value ?? new Dictionary<Guid, bool>());
        }

        public LocalSettings()
        {
            IsEnabled = true;
        }

        public IReadOnlyList<string> GetExtraLocalPathEntries()
        {
            return SplitExtraLocalPaths(ExtraLocalPaths).ToList();
        }

        public IReadOnlyList<string> GetExcludedLocalPathEntries()
        {
            return SplitExtraLocalPaths(ExcludedLocalPaths).ToList();
        }

        public IReadOnlyList<string> GetExtraUnlockSoundPathEntries()
        {
            return SplitUnlockSoundPaths(ExtraUnlockSoundPaths).ToList();
        }

        public IReadOnlyList<string> GetSuccessStoryImportCategoryKeyEntries()
        {
            return SplitListSetting(SuccessStoryImportCategoryKeys).ToList();
        }

        public IReadOnlyList<string> GetSuccessStoryImportExcludedCategoryKeyEntries()
        {
            return SplitListSetting(SuccessStoryImportExcludedCategoryKeys).ToList();
        }

        public void SetExtraLocalPathEntries(IEnumerable<string> paths)
        {
            ExtraLocalPaths = JoinExtraLocalPaths(paths);
        }

        public void SetExcludedLocalPathEntries(IEnumerable<string> paths)
        {
            ExcludedLocalPaths = JoinExtraLocalPaths(paths);
        }

        public void SetExtraUnlockSoundPathEntries(IEnumerable<string> paths)
        {
            ExtraUnlockSoundPaths = JoinUnlockSoundPaths(paths);
        }

        public void SetSuccessStoryImportCategoryKeyEntries(IEnumerable<string> keys)
        {
            SuccessStoryImportCategoryKeys = JoinListSetting(keys);
        }

        public void SetSuccessStoryImportExcludedCategoryKeyEntries(IEnumerable<string> keys)
        {
            SuccessStoryImportExcludedCategoryKeys = JoinListSetting(keys);
        }

        public static IEnumerable<string> SplitExtraLocalPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeExtraLocalPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinExtraLocalPaths(IEnumerable<string> paths)
        {
            return string.Join(";", NormalizeExtraLocalPaths(paths));
        }

        public static IEnumerable<string> SplitUnlockSoundPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeUnlockSoundPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinUnlockSoundPaths(IEnumerable<string> paths)
        {
            return string.Join(";", NormalizeUnlockSoundPaths(paths));
        }

        public static IEnumerable<string> SplitCustomIconLibraryPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeCustomIconLibraryPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }


        public static IEnumerable<string> SplitListSetting(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeListSetting(rawValue.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinListSetting(IEnumerable<string> values)
        {
            return string.Join(";", NormalizeListSetting(values));
        }

        private static IEnumerable<string> NormalizeExtraLocalPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in paths)
            {
                var normalizedPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedPath) || !seen.Add(normalizedPath))
                {
                    continue;
                }

                yield return normalizedPath;
            }
        }

        private static IEnumerable<string> NormalizeCustomIconLibraryPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var normalized = (path ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static IEnumerable<string> NormalizeUnlockSoundPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in paths)
            {
                var normalizedPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedPath) || !seen.Add(normalizedPath))
                {
                    continue;
                }

                yield return normalizedPath;
            }
        }

        private static IEnumerable<string> NormalizeListSetting(IEnumerable<string> values)
        {
            if (values == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawValue in values)
            {
                var normalizedValue = rawValue?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedValue) || !seen.Add(normalizedValue))
                {
                    continue;
                }

                yield return normalizedValue;
            }
        }

        private string GetEffectiveBundledUnlockSoundPath()
        {
            return string.IsNullOrWhiteSpace(BundledUnlockSoundPath)
                ? DefaultBundledUnlockSoundPath
                : BundledUnlockSoundPath;
        }

        private string GetEffectiveScreenshotSaveFolder()
        {
            if (!string.IsNullOrWhiteSpace(ScreenshotSaveFolder))
            {
                return ScreenshotSaveFolder;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "PlayniteAchievements",
                "UnlockScreenshots");
        }

        private static string NormalizeColorSetting(string value, string fallback)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }

        private static string NormalizeFontFamilySetting(string value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "Default" : normalized;
        }

        private static List<LocalCustomOverlayStyleSlot> CreateDefaultCustomOverlayStyleSlots()
        {
            return new List<LocalCustomOverlayStyleSlot>(1)
            {
                new LocalCustomOverlayStyleSlot { Name = "Slot 1", IconSize = 58, TitleTemplate = "Achievement unlocked", GameNameTemplate = "<gameName>", AchievementTemplate = "Unlocked: <achievementName>", MetaTemplate = "<provider> / Custom" }
            };
        }

        private static List<LocalCustomOverlayStyleSlot> NormalizeCustomOverlayStyleSlots(IEnumerable<LocalCustomOverlayStyleSlot> slots)
        {
            if (slots == null)
                return CreateDefaultCustomOverlayStyleSlots();

            var source = slots.ToList();
            if (source.Count == 0)
                return CreateDefaultCustomOverlayStyleSlots();

            var normalized = new List<LocalCustomOverlayStyleSlot>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var slot = source[index] ?? new LocalCustomOverlayStyleSlot();
                normalized.Add(new LocalCustomOverlayStyleSlot
                {
                    Name = string.IsNullOrWhiteSpace(slot.Name) ? $"Slot {index + 1}" : slot.Name.Trim(),
                    AutoResizeToContent = slot.AutoResizeToContent,
                    WrapAllText = slot.WrapAllText,
                    ShowLine1 = slot.ShowLine1,
                    ShowBorder = slot.ShowBorder,
                    BorderWidth = Math.Max(0.5, Math.Min(12, slot.BorderWidth <= 0 ? 1.5 : slot.BorderWidth)),
                    ShowGameName = slot.ShowGameName,
                    ShowMeta = slot.ShowMeta,
                    IconSize = Math.Max(24, Math.Min(220, slot.IconSize <= 0 ? 58 : slot.IconSize)),
                    SecondaryIconSize = Math.Max(24, Math.Min(220, slot.SecondaryIconSize <= 0 ? (slot.IconSize <= 0 ? 58 : slot.IconSize) : slot.SecondaryIconSize)),
                    IconCornerRadius = Math.Max(0, Math.Min(110, slot.IconCornerRadius)),
                    SecondaryIconCornerRadius = Math.Max(0, Math.Min(110, slot.SecondaryIconCornerRadius)),
                    ShowIconBackground = slot.ShowIconBackground,
                    ShowSecondaryIconBackground = slot.ShowSecondaryIconBackground,
                    IconBackgroundColor = NormalizeColorSetting(slot.IconBackgroundColor, "#24FFFFFF"),
                    SecondaryIconBackgroundColor = NormalizeColorSetting(slot.SecondaryIconBackgroundColor, "#18FFFFFF"),
                    ShowCoverBackground = slot.ShowCoverBackground,
                    CoverBackgroundColor = NormalizeColorSetting(slot.CoverBackgroundColor, "#1CFFFFFF"),
                    Width = Math.Max(280, Math.Min(900, slot.Width)),
                    Height = Math.Max(MinCustomOverlayHeight, Math.Min(320, slot.Height)),
                    CornerRadius = Math.Max(0, Math.Min(180, slot.CornerRadius)),
                    TitleFontSize = Math.Max(8, Math.Min(56, slot.TitleFontSize)),
                    DetailFontSize = Math.Max(8, Math.Min(56, slot.DetailFontSize)),
                    MetaFontSize = Math.Max(8, Math.Min(56, slot.MetaFontSize)),
                    Line4FontSize = Math.Max(8, Math.Min(56, slot.Line4FontSize <= 0 ? slot.MetaFontSize : slot.Line4FontSize)),
                    Line5FontSize = Math.Max(8, Math.Min(56, slot.Line5FontSize <= 0 ? slot.MetaFontSize : slot.Line5FontSize)),
                    Line6FontSize = Math.Max(8, Math.Min(56, slot.Line6FontSize <= 0 ? slot.MetaFontSize : slot.Line6FontSize)),
                    Line1FontFamily = NormalizeFontFamilySetting(slot.Line1FontFamily),
                    Line2FontFamily = NormalizeFontFamilySetting(slot.Line2FontFamily),
                    Line3FontFamily = NormalizeFontFamilySetting(slot.Line3FontFamily),
                    Line4FontFamily = NormalizeFontFamilySetting(slot.Line4FontFamily),
                    Line5FontFamily = NormalizeFontFamilySetting(slot.Line5FontFamily),
                    Line6FontFamily = NormalizeFontFamilySetting(slot.Line6FontFamily),
                    TextFormattingMode = slot.TextFormattingMode,
                    BackgroundColor = NormalizeColorSetting(slot.BackgroundColor, "#1E2430"),
                    BorderColor = NormalizeColorSetting(slot.BorderColor, "#6FA3D8"),
                    AccentColor = NormalizeColorSetting(slot.AccentColor, "#A7E0FF"),
                    TitleColor = NormalizeColorSetting(slot.TitleColor, "#FFFFFF"),
                    DetailColor = NormalizeColorSetting(slot.DetailColor, "#E7EEF7"),
                    MetaColor = NormalizeColorSetting(slot.MetaColor, "#BCD0E5"),
                    BackgroundImagePath = slot.BackgroundImagePath ?? string.Empty,
                    TitleTemplate = slot.TitleTemplate ?? "Achievement unlocked",
                    GameNameTemplate = slot.GameNameTemplate ?? "<gameName>",
                    AchievementTemplate = slot.AchievementTemplate ?? "Unlocked: <achievementName>",
                    MetaTemplate = slot.MetaTemplate ?? "<provider> / Custom",
                    Line4Template = slot.Line4Template ?? string.Empty,
                    Line5Template = slot.Line5Template ?? string.Empty,
                    Line6Template = slot.Line6Template ?? string.Empty,
                    ShowLine4 = slot.ShowLine4,
                    ShowLine5 = slot.ShowLine5,
                    ShowLine6 = slot.ShowLine6,
                    ProgressBars = CloneProgressBars(slot.ProgressBars),
                    ShowProgressBar = slot.ShowProgressBar,
                    ProgressBarWidth = slot.ProgressBarWidth,
                    ProgressBarHeight = slot.ProgressBarHeight,
                    ProgressBarCornerRadius = slot.ProgressBarCornerRadius,
                    ProgressBarSpacing = slot.ProgressBarSpacing,
                    ProgressBarFillColor = slot.ProgressBarFillColor,
                    ProgressBarTrackColor = slot.ProgressBarTrackColor,
                    ProgressBarShowLabel = slot.ProgressBarShowLabel,
                    ProgressBarLabelColor = slot.ProgressBarLabelColor,
                    ProgressBarLabelFontSize = slot.ProgressBarLabelFontSize,
                    ProgressBarOffsetX = slot.ProgressBarOffsetX,
                    ProgressBarOffsetY = slot.ProgressBarOffsetY,
                    Line4Color = NormalizeColorSetting(slot.Line4Color, "#BCD0E5"),
                    Line5Color = NormalizeColorSetting(slot.Line5Color, "#BCD0E5"),
                    Line6Color = NormalizeColorSetting(slot.Line6Color, "#BCD0E5"),
                    Line1OutlineColor = NormalizeColorSetting(slot.Line1OutlineColor, "transparent"),
                    Line2OutlineColor = NormalizeColorSetting(slot.Line2OutlineColor, "transparent"),
                    Line3OutlineColor = NormalizeColorSetting(slot.Line3OutlineColor, "transparent"),
                    Line4OutlineColor = NormalizeColorSetting(slot.Line4OutlineColor, "transparent"),
                    Line5OutlineColor = NormalizeColorSetting(slot.Line5OutlineColor, "transparent"),
                    Line6OutlineColor = NormalizeColorSetting(slot.Line6OutlineColor, "transparent"),
                    Line1ShadowColor = NormalizeColorSetting(slot.Line1ShadowColor, "transparent"),
                    Line2ShadowColor = NormalizeColorSetting(slot.Line2ShadowColor, "transparent"),
                    Line3ShadowColor = NormalizeColorSetting(slot.Line3ShadowColor, "transparent"),
                    Line4ShadowColor = NormalizeColorSetting(slot.Line4ShadowColor, "transparent"),
                    Line5ShadowColor = NormalizeColorSetting(slot.Line5ShadowColor, "transparent"),
                    Line6ShadowColor = NormalizeColorSetting(slot.Line6ShadowColor, "transparent"),
                    OutlineSize = Math.Max(0, Math.Min(8, slot.OutlineSize <= 0 ? 0.6 : slot.OutlineSize)),
                    ShadowSize = Math.Max(0, Math.Min(24, slot.ShadowSize <= 0 ? 2 : slot.ShadowSize)),
                    Line1OutlineSize = Math.Max(0, Math.Min(8, slot.Line1OutlineSize <= 0 ? slot.OutlineSize : slot.Line1OutlineSize)),
                    Line2OutlineSize = Math.Max(0, Math.Min(8, slot.Line2OutlineSize <= 0 ? slot.OutlineSize : slot.Line2OutlineSize)),
                    Line3OutlineSize = Math.Max(0, Math.Min(8, slot.Line3OutlineSize <= 0 ? slot.OutlineSize : slot.Line3OutlineSize)),
                    Line4OutlineSize = Math.Max(0, Math.Min(8, slot.Line4OutlineSize <= 0 ? slot.OutlineSize : slot.Line4OutlineSize)),
                    Line5OutlineSize = Math.Max(0, Math.Min(8, slot.Line5OutlineSize <= 0 ? slot.OutlineSize : slot.Line5OutlineSize)),
                    Line6OutlineSize = Math.Max(0, Math.Min(8, slot.Line6OutlineSize <= 0 ? slot.OutlineSize : slot.Line6OutlineSize)),
                    Line1ShadowSize = Math.Max(0, Math.Min(24, slot.Line1ShadowSize <= 0 ? slot.ShadowSize : slot.Line1ShadowSize)),
                    Line2ShadowSize = Math.Max(0, Math.Min(24, slot.Line2ShadowSize <= 0 ? slot.ShadowSize : slot.Line2ShadowSize)),
                    Line3ShadowSize = Math.Max(0, Math.Min(24, slot.Line3ShadowSize <= 0 ? slot.ShadowSize : slot.Line3ShadowSize)),
                    Line4ShadowSize = Math.Max(0, Math.Min(24, slot.Line4ShadowSize <= 0 ? slot.ShadowSize : slot.Line4ShadowSize)),
                    Line5ShadowSize = Math.Max(0, Math.Min(24, slot.Line5ShadowSize <= 0 ? slot.ShadowSize : slot.Line5ShadowSize)),
                    Line6ShadowSize = Math.Max(0, Math.Min(24, slot.Line6ShadowSize <= 0 ? slot.ShadowSize : slot.Line6ShadowSize)),
                    Line1OutlineEnabled = slot.Line1OutlineEnabled,
                    Line2OutlineEnabled = slot.Line2OutlineEnabled,
                    Line3OutlineEnabled = slot.Line3OutlineEnabled,
                    Line4OutlineEnabled = slot.Line4OutlineEnabled,
                    Line5OutlineEnabled = slot.Line5OutlineEnabled,
                    Line6OutlineEnabled = slot.Line6OutlineEnabled,
                    Line1ShadowEnabled = slot.Line1ShadowEnabled,
                    Line2ShadowEnabled = slot.Line2ShadowEnabled,
                    Line3ShadowEnabled = slot.Line3ShadowEnabled,
                    Line4ShadowEnabled = slot.Line4ShadowEnabled,
                    Line5ShadowEnabled = slot.Line5ShadowEnabled,
                    Line6ShadowEnabled = slot.Line6ShadowEnabled,
                    LineSpacing = Math.Max(0, Math.Min(24, slot.LineSpacing)),
                    Line1Spacing = Math.Max(0, Math.Min(48, slot.Line1Spacing)),
                    Line2Spacing = Math.Max(0, Math.Min(48, slot.Line2Spacing)),
                    Line3Spacing = Math.Max(0, Math.Min(48, slot.Line3Spacing)),
                    Line4Spacing = Math.Max(0, Math.Min(48, slot.Line4Spacing)),
                    Line5Spacing = Math.Max(0, Math.Min(48, slot.Line5Spacing)),
                    Line6Spacing = Math.Max(0, Math.Min(48, slot.Line6Spacing)),
                    Line1View = slot.Line1View,
                    Line2View = slot.Line2View,
                    Line3View = slot.Line3View,
                    Line4View = slot.Line4View,
                    Line5View = slot.Line5View,
                    Line6View = slot.Line6View,
                    Line1Order = Math.Max(1, Math.Min(6, slot.Line1Order <= 0 ? 1 : slot.Line1Order)),
                    Line2Order = Math.Max(1, Math.Min(6, slot.Line2Order <= 0 ? 1 : slot.Line2Order)),
                    Line3Order = Math.Max(1, Math.Min(6, slot.Line3Order <= 0 ? 2 : slot.Line3Order)),
                    Line4Order = Math.Max(1, Math.Min(6, slot.Line4Order <= 0 ? 3 : slot.Line4Order)),
                    Line5Order = Math.Max(1, Math.Min(6, slot.Line5Order <= 0 ? 4 : slot.Line5Order)),
                    Line6Order = Math.Max(1, Math.Min(6, slot.Line6Order <= 0 ? 5 : slot.Line6Order)),
                    LayoutStyle = slot.LayoutStyle,
                    AnimationStyle = slot.AnimationStyle,
                    SecondaryIconSource = slot.SecondaryIconSource,
                    ShowSecondaryIcon = slot.ShowSecondaryIcon,
                    SanElementPresetId = slot.SanElementPresetId ?? string.Empty,
                    SanElementPosition = slot.SanElementPosition,
                    SanProviderLogoSource = slot.SanProviderLogoSource,
                    TransitionStyle = slot.TransitionStyle == LocalUnlockOverlayTransitionStyle.Fade && !string.IsNullOrWhiteSpace(slot.SanPresetId)
                        ? InferSanTransitionStyle(slot.SanPresetId)
                        : slot.TransitionStyle,
                    SanPresetId = slot.SanPresetId ?? string.Empty,
                    SanPresetHtml = slot.SanPresetHtml ?? string.Empty,
                    SanPresetCss = slot.SanPresetCss ?? string.Empty,
                    SanBaseCss = slot.SanBaseCss ?? string.Empty,
                    SanBaseAnimCss = slot.SanBaseAnimCss ?? string.Empty,
                    SanAssetRootPath = slot.SanAssetRootPath ?? string.Empty,
                    SanThemeJson = slot.SanThemeJson ?? string.Empty,
                    SanThemeDirectory = slot.SanThemeDirectory ?? string.Empty,
                    ManualElementCss = slot.ManualElementCss ?? string.Empty,
                    ElementAnchorsJson = slot.ElementAnchorsJson ?? string.Empty,
                    SanView1DurationMilliseconds = Math.Max(500, Math.Min(30000, slot.SanView1DurationMilliseconds <= 0 ? 5000 : slot.SanView1DurationMilliseconds)),
                    SanView2DurationMilliseconds = Math.Max(500, Math.Min(30000, slot.SanView2DurationMilliseconds <= 0 ? 5000 : slot.SanView2DurationMilliseconds)),
                    TitleBold = slot.TitleBold,
                    TitleItalic = slot.TitleItalic,
                    TitleUnderline = slot.TitleUnderline,
                    TitleStrikethrough = slot.TitleStrikethrough,
                    DetailBold = slot.DetailBold,
                    DetailItalic = slot.DetailItalic,
                    DetailUnderline = slot.DetailUnderline,
                    DetailStrikethrough = slot.DetailStrikethrough,
                    MetaBold = slot.MetaBold,
                    MetaItalic = slot.MetaItalic,
                    MetaUnderline = slot.MetaUnderline,
                    MetaStrikethrough = slot.MetaStrikethrough,
                    Line4Bold = slot.Line4Bold,
                    Line4Italic = slot.Line4Italic,
                    Line4Underline = slot.Line4Underline,
                    Line4Strikethrough = slot.Line4Strikethrough,
                    Line5Bold = slot.Line5Bold,
                    Line5Italic = slot.Line5Italic,
                    Line5Underline = slot.Line5Underline,
                    Line5Strikethrough = slot.Line5Strikethrough,
                    Line6Bold = slot.Line6Bold,
                    Line6Italic = slot.Line6Italic,
                    Line6Underline = slot.Line6Underline,
                    Line6Strikethrough = slot.Line6Strikethrough,
                    IconSource = slot.IconSource,
                    ShowIconRarityGlow = slot.ShowIconRarityGlow,
                    ShowSecondaryIconRarityGlow = slot.ShowSecondaryIconRarityGlow,
                    EnableGameCoverInOverlay = slot.EnableGameCoverInOverlay,
                    GameCoverPosition = slot.GameCoverPosition,
                    GameCoverWidth = Math.Max(40, Math.Min(260, slot.GameCoverWidth <= 0 ? 80 : slot.GameCoverWidth)),
                    CenterGameCoverVertically = slot.CenterGameCoverVertically,
                    EnableGameBannerAsBackground = slot.EnableGameBannerAsBackground,
                    GameBannerOpacity = Math.Max(0, Math.Min(1, slot.GameBannerOpacity)),
                    GameBannerBlurRadius = Math.Max(0, Math.Min(30, slot.GameBannerBlurRadius)),
                    CustomCoverImagePath = slot.CustomCoverImagePath ?? string.Empty,
                    CustomBannerImagePath = slot.CustomBannerImagePath ?? string.Empty
                });
            }

            return normalized;
        }

        private static LocalUnlockOverlayTransitionStyle InferSanTransitionStyle(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "default": return LocalUnlockOverlayTransitionStyle.SanDefault;
                case "xqjan": return LocalUnlockOverlayTransitionStyle.SanXqjan;
                case "steamdeck": return LocalUnlockOverlayTransitionStyle.SanDeck;
                case "epicgames": return LocalUnlockOverlayTransitionStyle.SanEpic;
                case "xboxone": return LocalUnlockOverlayTransitionStyle.SanXboxModern;
                case "xbox360": return LocalUnlockOverlayTransitionStyle.SanXboxClassic;
                case "ps5": return LocalUnlockOverlayTransitionStyle.SanPsModern;
                case "ps4": return LocalUnlockOverlayTransitionStyle.SanPsClassic;
                case "ps3": return LocalUnlockOverlayTransitionStyle.SanPsRetro;
                case "windows": return LocalUnlockOverlayTransitionStyle.SanSquare;
                case "gfwl": return LocalUnlockOverlayTransitionStyle.SanAero;
                default: return LocalUnlockOverlayTransitionStyle.Fade;
            }
        }
    }
}
