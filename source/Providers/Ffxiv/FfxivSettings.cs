using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Final Fantasy XIV provider settings. Achievement data is sourced from the
    /// FFXIV Collect API for the configured character.
    /// </summary>
    public class FfxivSettings : ProviderSettingsBase
    {
        private string _characterProfile;
        private long _resolvedCharacterId;

        /// <inheritdoc />
        public override string ProviderKey => "FFXIV";

        /// <summary>
        /// Lodestone or FFXIV Collect character profile URL, or a bare Lodestone id.
        /// </summary>
        public string CharacterProfile
        {
            get => _characterProfile;
            set => SetValue(ref _characterProfile, value);
        }

        /// <summary>
        /// Lodestone character id resolved by earlier versions from a name and world
        /// search. Used only while <see cref="CharacterProfile"/> is empty, so a
        /// character configured that way keeps refreshing. Zero when not set.
        /// </summary>
        public long ResolvedCharacterId
        {
            get => _resolvedCharacterId;
            set => SetValue(ref _resolvedCharacterId, value);
        }

        /// <summary>
        /// The configured Lodestone character id, or null when none is configured or
        /// <see cref="CharacterProfile"/> does not contain one.
        /// </summary>
        public long? GetCharacterId()
        {
            if (!string.IsNullOrWhiteSpace(CharacterProfile))
            {
                return FfxivParsing.ParseCharacterId(CharacterProfile);
            }

            return ResolvedCharacterId > 0 ? ResolvedCharacterId : (long?)null;
        }
    }
}
