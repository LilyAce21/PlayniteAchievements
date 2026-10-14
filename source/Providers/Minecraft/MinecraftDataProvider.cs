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
    internal sealed class MinecraftDataProvider : DataProviderBase<MinecraftSettings>, IDataProvider, IProviderOverride, IInGameProgressSource
    {
        internal const string Key = "Minecraft";

        // A Minecraft folder, launcher instance or single world folder for this game.
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Minecraft",
            ValidateOverrideFolder);

        private readonly MinecraftScanner _scanner;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;

        public MinecraftDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
            : this(logger, settings, playniteApi, string.Empty)
        {
        }

        public MinecraftDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;
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
        /// Always true: there is no sign-in, and a game's folders (override, launch action, install
        /// folder) can live anywhere, such as a portable launcher, so no global folder check decides
        /// availability. <see cref="IsCapable"/> filters the games.
        /// </summary>
        public bool IsAuthenticated => true;

        public bool IsCapable(Game game)
        {
            var source = MinecraftGameResolver.Resolve(game, ProviderSettings, _playniteApi);
            return source != null && source.HasWorlds;
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
        /// Watches the latest.log of every game folder the game covers for the integrated server's
        /// advancement announcements, so an unlock shows as soon as it happens rather than at the
        /// next world save. Folders without rows yet (a new world or instance) are watched too: their
        /// titles resolve through the instance's definitions, and the unknown row makes the monitor
        /// refresh at once. Unannounced unlocks still arrive through the refresh once the world saves.
        /// </summary>
        InGameProgressRegistration IInGameProgressSource.TryRegister(Game game, GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var source = MinecraftGameResolver.Resolve(game, ProviderSettings, _playniteApi);
            if (source == null || source.Candidates.Count == 0)
            {
                return null;
            }

            var state = new MinecraftLogState
            {
                PlayerNames = MinecraftAccounts.ResolveNames(ProviderSettings?.PlayerUuid, source.Candidates.Select(candidate => candidate.GameFolder))
            };
            foreach (var candidate in source.Candidates)
            {
                var prefix = candidate.Key + "|";
                var rows = cachedSchema.Achievements
                    .Where(achievement => achievement?.ApiName != null &&
                                          achievement.ApiName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(achievement => (achievement.ApiName, achievement.DisplayName));
                var instance = candidate;
                state.Targets.Add(MinecraftLogWatcher.CreateTarget(
                    candidate.GameFolder,
                    candidate.Key,
                    rows,
                    () => MinecraftScanner.BuildTitleIndex(
                        new MinecraftInstance
                        {
                            Key = instance.Key,
                            Name = instance.Name,
                            GameFolder = instance.GameFolder,
                            Worlds = source.SingleWorld != null
                                ? new List<string> { source.SingleWorld }
                                : MinecraftPaths.EnumerateWorlds(instance.GameFolder)
                        },
                        _settings?.Persisted?.GlobalLanguage,
                        _logger),
                    source.SingleWorld));
            }

            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = state.Targets.Select(target => target.LogPath).ToList(),
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                State = state
            };
        }

        Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>();
            foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context?.Game?.Id ?? Guid.Empty;
                if (!(context?.Registration?.State is MinecraftLogState state))
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "no_state"));
                    continue;
                }

                var observations = MinecraftLogWatcher.ReadUnlocks(state, DateTime.Now, context.SessionStartUtc)
                    .Select(unlock => new AchievementProgressObservation
                    {
                        ApiName = unlock.ApiName,
                        Unlocked = true,
                        UnlockTimeUtc = unlock.UnlockTimeUtc
                    })
                    .ToList();
                results.Add(InGameProgressQueryResult.Succeeded(gameId, observations, isDelta: true));
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
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
            return MinecraftPaths.IsWorldFolder(expanded) || MinecraftPaths.ExpandGameFolders(expanded).Count > 0
                ? ProviderOverrideValidation.Valid(trimmed)
                : ProviderOverrideValidation.Invalid("LOCPlayAch_InvalidPath");
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new MinecraftSettingsView(_playniteApi);
    }
}
