using System.Collections.Generic;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Java Minecraft provider settings.
    /// </summary>
    public class MinecraftSettings : ProviderSettingsBase
    {
        private List<string> _gameFolders = new List<string>();
        private string _playerUuid;

        /// <inheritdoc />
        public override string ProviderKey => "Minecraft";

        /// <summary>
        /// Minecraft game folders (a .minecraft folder or a launcher instance) used for games matched
        /// by name that have no instance of their own. Empty means %AppData%\.minecraft.
        /// </summary>
        public List<string> GameFolders
        {
            get => _gameFolders;
            set => SetValue(ref _gameFolders, ProviderPathList.Normalize(value));
        }

        /// <summary>
        /// The player whose advancement files are read, with or without dashes. Empty means every
        /// account found in the launchers' account files.
        /// </summary>
        public string PlayerUuid
        {
            get => _playerUuid;
            set => SetValue(ref _playerUuid, value);
        }
    }
}
