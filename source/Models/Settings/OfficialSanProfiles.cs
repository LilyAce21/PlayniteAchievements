using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>Where a notification's sound comes from in the official SAN template.</summary>
    public enum OfficialSanSoundMode
    {
        Silent = 0,
        /// <summary>The first sound file bundled inside the .san theme, when it has one.</summary>
        Theme = 1,
        /// <summary>A sound file the user picked.</summary>
        File = 2
    }

    /// <summary>
    /// One complete notification setup in the official SAN template: the .san theme and its own
    /// position, sound, volume and display time. Every rarity profile has a primary slot and an
    /// optional second slot.
    /// </summary>
    public sealed class OfficialSanSlot : ObservableObject
    {
        private string _themePath = string.Empty;
        private bool _useThemePosition = true;
        private Providers.Local.LocalUnlockOverlayPosition _position = Providers.Local.LocalUnlockOverlayPosition.BottomCenter;
        private OfficialSanSoundMode _soundMode = OfficialSanSoundMode.Silent;
        private string _soundPath = string.Empty;
        private int _soundVolume = 100;
        private int _durationSeconds;
        private bool _liveBlurEnabled;
        private int _liveBlurStrength = 50;
        private System.Collections.Generic.Dictionary<string, string> _styleOverrides = new System.Collections.Generic.Dictionary<string, string>();

        public string ThemePath { get => _themePath; set => SetValue(ref _themePath, value ?? string.Empty); }

        /// <summary>True keeps the screen position the theme was authored with.</summary>
        public bool UseThemePosition { get => _useThemePosition; set => SetValue(ref _useThemePosition, value); }

        public Providers.Local.LocalUnlockOverlayPosition Position { get => _position; set => SetValue(ref _position, value); }

        public OfficialSanSoundMode SoundMode { get => _soundMode; set => SetValue(ref _soundMode, value); }

        public string SoundPath { get => _soundPath; set => SetValue(ref _soundPath, value ?? string.Empty); }

        /// <summary>Percent of full volume, 0-100.</summary>
        public int SoundVolume
        {
            get => _soundVolume;
            set => SetValue(ref _soundVolume, System.Math.Max(0, System.Math.Min(100, value)));
        }

        /// <summary>Seconds the notification stays up; 0 keeps the theme's own display time.</summary>
        public int DurationSeconds
        {
            get => _durationSeconds;
            set => SetValue(ref _durationSeconds, System.Math.Max(0, System.Math.Min(60, value)));
        }

        /// <summary>
        /// Blurs the live game picture behind the notification card (the same capture the native
        /// toast uses), instead of relying on the theme's own blur, which has nothing to blur in a
        /// transparent window.
        /// </summary>
        public bool LiveBlurEnabled { get => _liveBlurEnabled; set => SetValue(ref _liveBlurEnabled, value); }

        /// <summary>How far the live blur spreads, 0-100 (shared mapping with the toast blur).</summary>
        public int LiveBlurStrength
        {
            get => _liveBlurStrength;
            set => SetValue(ref _liveBlurStrength, System.Math.Max(0, System.Math.Min(100, value)));
        }

        /// <summary>
        /// Style settings this slot overrides over the .san theme's own values, keyed by SAN
        /// <c>customisation</c> key (see <see cref="SanStyleCatalog"/>). Only keys that are present
        /// are applied; everything else keeps the theme's value. Values are stored as text: numbers
        /// in invariant culture, booleans as true/false, colours in SAN's #RRGGBB or #RRGGBBAA.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> StyleOverrides
        {
            get => _styleOverrides;
            set => SetValue(ref _styleOverrides, value ?? new System.Collections.Generic.Dictionary<string, string>());
        }
    }

    /// <summary>
    /// The notification setup for one kind of unlock. When the second slot is enabled, each unlock
    /// has a 50/50 chance of using it instead of the primary slot.
    /// </summary>
    public sealed class OfficialSanProfile : ObservableObject
    {
        private OfficialSanSlot _primary = new OfficialSanSlot();
        private OfficialSanSlot _alt = new OfficialSanSlot();
        private bool _altEnabled;

        public OfficialSanSlot Primary { get => _primary; set => SetValue(ref _primary, value ?? new OfficialSanSlot()); }

        public OfficialSanSlot Alt { get => _alt; set => SetValue(ref _alt, value ?? new OfficialSanSlot()); }

        public bool AltEnabled { get => _altEnabled; set => SetValue(ref _altEnabled, value); }
    }

    /// <summary>
    /// Every profile of the official SAN template. A profile whose primary theme is empty falls
    /// back to the Default profile, so one theme can cover everything.
    /// </summary>
    public sealed class OfficialSanProfiles : ObservableObject
    {
        private OfficialSanProfile _default = new OfficialSanProfile();
        private OfficialSanProfile _common = new OfficialSanProfile();
        private OfficialSanProfile _uncommon = new OfficialSanProfile();
        private OfficialSanProfile _rare = new OfficialSanProfile();
        private OfficialSanProfile _ultraRare = new OfficialSanProfile();
        private OfficialSanProfile _capstone = new OfficialSanProfile();
        private OfficialSanProfile _completion = new OfficialSanProfile();

        public OfficialSanProfile Default { get => _default; set => SetValue(ref _default, value ?? new OfficialSanProfile()); }
        public OfficialSanProfile Common { get => _common; set => SetValue(ref _common, value ?? new OfficialSanProfile()); }
        public OfficialSanProfile Uncommon { get => _uncommon; set => SetValue(ref _uncommon, value ?? new OfficialSanProfile()); }
        public OfficialSanProfile Rare { get => _rare; set => SetValue(ref _rare, value ?? new OfficialSanProfile()); }
        public OfficialSanProfile UltraRare { get => _ultraRare; set => SetValue(ref _ultraRare, value ?? new OfficialSanProfile()); }

        /// <summary>Capstone achievements (the game's final / completion-grade achievement).</summary>
        public OfficialSanProfile Capstone { get => _capstone; set => SetValue(ref _capstone, value ?? new OfficialSanProfile()); }

        /// <summary>The separate 100%-completion notification.</summary>
        public OfficialSanProfile Completion { get => _completion; set => SetValue(ref _completion, value ?? new OfficialSanProfile()); }

        public OfficialSanProfile Get(string key)
        {
            switch ((key ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "common": return Common;
                case "uncommon": return Uncommon;
                case "rare": return Rare;
                case "ultrarare": return UltraRare;
                case "capstone": return Capstone;
                case "completion": return Completion;
                default: return Default;
            }
        }
    }
}
