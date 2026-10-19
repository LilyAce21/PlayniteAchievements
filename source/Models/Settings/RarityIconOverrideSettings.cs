using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// User-supplied image files (absolute paths, .png and other WPF-readable formats) that replace
    /// the plugin-drawn rarity and trophy icons app-wide. A null/blank path keeps the built-in icon.
    /// </summary>
    public sealed class RarityIconOverrideSettings : ObservableObject
    {
        private string _common;
        private string _uncommon;
        private string _rare;
        private string _ultraRare;
        private string _completed;
        private string _trophyBronze;
        private string _trophySilver;
        private string _trophyGold;
        private string _trophyPlatinum;

        public string Common { get => _common; set => SetValue(ref _common, Norm(value)); }
        public string Uncommon { get => _uncommon; set => SetValue(ref _uncommon, Norm(value)); }
        public string Rare { get => _rare; set => SetValue(ref _rare, Norm(value)); }
        public string UltraRare { get => _ultraRare; set => SetValue(ref _ultraRare, Norm(value)); }
        public string Completed { get => _completed; set => SetValue(ref _completed, Norm(value)); }
        public string TrophyBronze { get => _trophyBronze; set => SetValue(ref _trophyBronze, Norm(value)); }
        public string TrophySilver { get => _trophySilver; set => SetValue(ref _trophySilver, Norm(value)); }
        public string TrophyGold { get => _trophyGold; set => SetValue(ref _trophyGold, Norm(value)); }
        public string TrophyPlatinum { get => _trophyPlatinum; set => SetValue(ref _trophyPlatinum, Norm(value)); }

        public static RarityIconOverrideSettings CreateDefault() => new RarityIconOverrideSettings();

        public RarityIconOverrideSettings Clone()
        {
            return new RarityIconOverrideSettings
            {
                Common = Common,
                Uncommon = Uncommon,
                Rare = Rare,
                UltraRare = UltraRare,
                Completed = Completed,
                TrophyBronze = TrophyBronze,
                TrophySilver = TrophySilver,
                TrophyGold = TrophyGold,
                TrophyPlatinum = TrophyPlatinum
            };
        }

        private static string Norm(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
