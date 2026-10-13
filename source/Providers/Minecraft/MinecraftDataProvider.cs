using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Java Minecraft advancements read from local world saves. Covers every advancement the game
    /// displays, from vanilla, mods or datapacks, with progress merged across worlds.
    /// </summary>
    internal sealed class MinecraftDataProvider : DataProviderBase<MinecraftSettings>, IDataProvider, IProviderOverride
    {
        internal const string Key = "Minecraft";

        // A Minecraft folder, launcher instance or single world folder for this game.
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Minecraft",
            ValidateOverrideFolder);

        private readonly MinecraftScanner _scanner;
        private readonly IPlayniteAPI _playniteApi;

        public MinecraftDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
            : this(logger, settings, playniteApi, string.Empty)
        {
        }

        public MinecraftDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi;
            _scanner = new MinecraftScanner(logger, settings, ProviderSettings, _playniteApi, pluginUserDataPath);
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_Minecraft");
                return string.IsNullOrWhiteSpace(value) ? "Minecraft" : value;
            }
        }

        public string ProviderKey => Key;

        public string ProviderIconKey => "ProviderIconMinecraft";

        public string ProviderColorHex => "#866043";

        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        /// <summary>
        /// True once a configured game folder or a default launcher folder exists. Instances only
        /// reachable through a game's own folder are still found during refresh.
        /// </summary>
        public bool IsAuthenticated =>
            ProviderPathList.Normalize(ProviderSettings?.GameFolders).Any(Directory.Exists) ||
            MinecraftPaths.DefaultLauncherDataFolders().Any(Directory.Exists);

        public bool IsCapable(Game game)
        {
            var source = MinecraftGameResolver.Resolve(game, ProviderSettings, _playniteApi);
            return source != null && source.Worlds.Count > 0;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        /// <summary>
        /// Accepts a world folder, a .minecraft folder or a launcher instance. Values with Playnite
        /// variables are accepted as typed and resolved per game.
        /// </summary>
        internal static ProviderOverrideValidation ValidateOverrideFolder(string raw)
        {
            var trimmed = (raw ?? string.Empty).Trim().Trim('"').Trim();
            if (trimmed.Length == 0)
            {
                return ProviderOverrideValidation.Invalid(ProviderOverrideValidators.RequiredValueErrorKey);
            }

            if (trimmed.IndexOf('{') >= 0)
            {
                return ProviderOverrideValidation.Valid(trimmed);
            }

            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            return MinecraftPaths.IsWorldFolder(expanded) || MinecraftPaths.ResolveGameFolder(expanded) != null
                ? ProviderOverrideValidation.Valid(trimmed)
                : ProviderOverrideValidation.Invalid("LOCPlayAch_InvalidPath");
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new MinecraftSettingsView(_playniteApi);
    }
}
