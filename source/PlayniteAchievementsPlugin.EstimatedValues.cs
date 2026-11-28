using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Estimation;

namespace PlayniteAchievements
{
    /// <summary>
    /// Actions for generated (estimated) trophy grades and Gamerscore: generate, regenerate and delete.
    /// Estimated values never replace native data; deleting them cannot touch anything native.
    /// </summary>
    public partial class PlayniteAchievementsPlugin
    {
        private static readonly HttpClient EstimationHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        private bool _estimationRunning;
        private SearchUsageTracker _searchUsage;
        private bool _researchCacheConfigured;

        /// <summary>
        /// Where research findings and the search credit count are saved: the plugin's own data folder. Findings are
        /// public facts about games; nothing about the player and no key is stored there.
        /// </summary>
        private void ConfigureResearchStorage()
        {
            if (_researchCacheConfigured) return;
            _researchCacheConfigured = true;

            var folder = Path.Combine(GetPluginUserDataPath(), "EstimatedValuesResearch");
            ResearchNotesCache.Configure(Path.Combine(folder, "notes"));
            _searchUsage = new SearchUsageTracker(Path.Combine(folder, "search-usage.json"));
        }

        private PersistedSettings EstimationPersisted => _settingsViewModel?.Settings?.Persisted;

        private static string PluginTitle => ResourceProvider.GetString("LOCPlayAch_Title_PluginName");

        /// <summary>
        /// Game menu entries for generating estimated values for the selected games. Only offered while the
        /// feature is switched on, so the menu stays uncluttered for everyone else.
        /// </summary>
        private IEnumerable<GameMenuItem> EstimatedValuesMenuItems(IReadOnlyList<Guid> gameIds)
        {
            if (EstimationPersisted?.EstimatedValuesEnabled != true || gameIds == null || gameIds.Count == 0)
            {
                yield break;
            }

            yield return new GameMenuItem
            {
                Description = "Generate estimated trophies and Gamerscore",
                MenuSection = PluginGameMenuSection,
                Action = a => GenerateEstimatedValues(gameIds, false)
            };

            yield return new GameMenuItem
            {
                Description = "Regenerate estimated trophies and Gamerscore",
                MenuSection = PluginGameMenuSection,
                Action = a => GenerateEstimatedValues(gameIds, true)
            };

            yield return new GameMenuItem
            {
                Description = "Retry missed research and regenerate estimated trophies and Gamerscore",
                MenuSection = PluginGameMenuSection,
                Action = a => GenerateEstimatedValues(gameIds, true, retryMissedResearch: true)
            };

            yield return new GameMenuItem
            {
                Description = "Delete saved research for estimated trophies and Gamerscore",
                MenuSection = PluginGameMenuSection,
                Action = a => ClearSavedResearchForGames(gameIds)
            };
        }

        /// <summary>Why a run cannot start, or null when it can.</summary>
        internal string GetEstimatedValuesProblem()
        {
            var persisted = EstimationPersisted;
            if (persisted == null) return "Settings are not available yet.";
            if (!persisted.EstimatedValuesEnabled) return "Turn on \"Generate estimated trophy grades and Gamerscore\" first.";
            if (!persisted.EstimatedValuesGenerateTrophies && !persisted.EstimatedValuesGenerateGamerscore) return "Turn on at least one of trophy grades or Gamerscore.";
            if (!EstimatedValuesConfig.HasApiKey(persisted)) return "Add a Gemini API key first. A free key is available from Google AI Studio.";
            if (!(_cacheManager is CacheManager)) return "The achievement cache is not ready yet.";
            return null;
        }

        /// <summary>
        /// Generates estimated values for every game with cached achievements (or just the given games),
        /// with a cancelable progress window and a summary at the end. Earlier results are kept unless
        /// <paramref name="regenerate"/> is true.
        /// </summary>
        internal void GenerateEstimatedValues(IReadOnlyList<Guid> gameIds, bool regenerate, bool retryMissedResearch = false)
        {
            if (retryMissedResearch) regenerate = true;
            if (_estimationRunning)
            {
                PlayniteApi?.Dialogs?.ShowMessage("Generating estimated values is already running.", PluginTitle);
                return;
            }

            var problem = GetEstimatedValuesProblem();
            if (problem != null)
            {
                PlayniteApi?.Dialogs?.ShowMessage(problem, PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var persisted = EstimationPersisted;
            var cache = _cacheManager as CacheManager;
            if (cache == null) return;
            ConfigureResearchStorage();
            var webSearch = EstimatedValuesConfig.UsesWebSearchResearch(persisted);
            var scope = gameIds == null ? "every game" : gameIds.Count == 1 ? "this game" : gameIds.Count + " games";
            var prompt = retryMissedResearch
                ? "Search again for the achievements of " + scope + " that research found nothing for, then regenerate their estimated trophy grades and Gamerscore? " +
                  "Research already found is kept and reused. Each new search costs a search credit (" + (_searchUsage?.UsedThisMonth ?? 0) + " of " +
                  persisted.EstimatedValuesSearchCreditsPerMonth + " used this month, up to " + persisted.EstimatedValuesSearchesPerGame + " searches per game). " +
                  "Native, metadata and custom values are never changed."
                : regenerate
                ? "Regenerate estimated trophy grades and Gamerscore for " + scope + "? Existing generated values will be replaced" +
                  (persisted.EstimatedValuesResearchEnabled ? ". Research that was already saved is reused, so this spends no extra search credits" : string.Empty) +
                  ". Native, metadata and custom values are never changed."
                : "Generate estimated trophy grades and Gamerscore for " + scope + "? Public game information (achievement names, descriptions and any native values) is sent to Google's Gemini service" +
                  (webSearch
                      ? ". The game title and achievement names are also searched for on the web with Tavily to find guides, using search credits (" +
                        (_searchUsage?.UsedThisMonth ?? 0) + " of " + persisted.EstimatedValuesSearchCreditsPerMonth + " used this month)"
                      : persisted.EstimatedValuesResearchEnabled ? ", which also searches the web for the game and each achievement. This takes longer and uses more requests" : string.Empty) +
                  ". Nothing about your account, unlocks or dates is sent. Native values are never changed.";
            if (!ConfirmMaintenance(prompt)) return;

            EstimationLibrarySummary summary = null;
            Exception failure = null;
            _estimationRunning = true;
            try
            {
                cache.PurgeSupersededEstimates();

                var client = new GeminiClient(
                    EstimationHttp,
                    EstimatedValuesConfig.ToClientOptions(() => EstimationPersisted),
                    message => _logger?.Info(message));
                // Plugin-driven web search: the plugin searches, finds each achievement in the guide pages, and gives
                // Gemini what it found. It needs no Gemini search access, so it works on a free key.
                IEvidenceSource evidenceSource = null;
                if (webSearch)
                {
                    var search = new TavilySearchClient(
                        EstimationHttp,
                        () => EstimatedValuesConfig.GetTavilyKey(EstimationPersisted),
                        message => _logger?.Info(message));
                    evidenceSource = new EvidenceGatherer(
                        search, _searchUsage, EstimatedValuesConfig.ToEvidenceOptions(persisted), message => _logger?.Info(message));
                }

                var coordinator = new EstimationCoordinator(
                    new CacheEstimationHost(cache),
                    client,
                    () =>
                    {
                        var runOptions = EstimatedValuesConfig.ToRunOptions(EstimationPersisted);
                        runOptions.EvidenceSource = evidenceSource;
                        return runOptions;
                    });

                var ids = gameIds ?? new CacheEstimationHost(cache).GetGameIds();
                if (retryMissedResearch)
                {
                    var retried = ResearchNotesCache.ClearMisses(ids);
                    _logger?.Info("[EstimatedValues] Will search again for missed achievements in " + retried + " game(s); found research is kept.");
                }

                _logger?.Info("[EstimatedValues] Starting: " + ids.Count + " game(s), regenerate=" + regenerate +
                              ", model=" + (persisted.EstimatedValuesGeminiModel ?? string.Empty).Trim() +
                              ", trophies=" + persisted.EstimatedValuesGenerateTrophies +
                              ", gamerscore=" + persisted.EstimatedValuesGenerateGamerscore +
                              ", research=" + persisted.EstimatedValuesResearchEnabled +
                              (persisted.EstimatedValuesResearchEnabled ? " via " + persisted.EstimatedValuesResearchProvider : string.Empty) +
                              (persisted.EstimatedValuesResearchEnabled && !webSearch && !string.IsNullOrWhiteSpace(persisted.EstimatedValuesResearchModel)
                                  ? " (model " + persisted.EstimatedValuesResearchModel.Trim() + ")" : string.Empty) +
                              ", preset=" + persisted.EstimatedValuesBudgetPreset + ".");
                var options = new GlobalProgressOptions("Generating estimated trophy grades and Gamerscore…", true)
                {
                    Cancelable = true,
                    IsIndeterminate = false
                };

                PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
                {
                    progress.ProgressMaxValue = Math.Max(1, ids.Count);
                    progress.CurrentProgressValue = 0;
                    var done = 0;
                    var reporter = new SyncProgress<EstimationGameSummary>(game =>
                    {
                        done++;
                        // Counts and status only: never the request, the response or the key.
                        _logger?.Info("[EstimatedValues] " + (string.IsNullOrWhiteSpace(game.Title) ? game.PlayniteGameId.ToString() : game.Title) +
                                      ": " + game.Status + ", " + game.ValuesStored + " value(s) stored" +
                                      (string.IsNullOrWhiteSpace(game.Message) ? string.Empty : " (" + game.Message + ")"));
                        var evidenceLine = game.DescribeEvidence(12, 10);
                        if (evidenceLine != null) _logger?.Info("[EstimatedValues] " + (string.IsNullOrWhiteSpace(game.Title) ? game.PlayniteGameId.ToString() : game.Title) + ": " + evidenceLine);
                        progress.CurrentProgressValue = done;
                        progress.Text = "Generating estimated values (" + done + " of " + ids.Count + ")" +
                                        (string.IsNullOrWhiteSpace(game.Title) ? string.Empty : ": " + game.Title);
                    });

                    try
                    {
                        summary = coordinator
                            .RunGamesAsync(ids.ToList(), regenerate, reporter, progress.CancelToken)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // Games finished before cancelling are kept.
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }, options);
            }
            finally
            {
                _estimationRunning = false;
            }

            if (failure != null)
            {
                _logger?.Error(failure, "Generating estimated values failed.");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), failure.Message),
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _logger?.Info("[EstimatedValues] Finished: " + (summary == null
                ? "cancelled."
                : summary.Generated + " generated, " + summary.GeneratedWithFallback + " from structure fallback, " + summary.WithoutResearch + " without research, " + summary.Skipped +
                  " skipped, " + summary.Failed + " failed, " + summary.ValuesStored + " value(s) stored" +
                  (summary.StoppedEarly ? "; stopped early: " + summary.StopReason : string.Empty) + "."));

            var creditNote = EstimatedValuesConfig.UsesWebSearchResearch(EstimationPersisted) && _searchUsage != null
                ? " Search credits used this month: " + _searchUsage.UsedThisMonth + " of " + EstimationPersisted.EstimatedValuesSearchCreditsPerMonth + "."
                : string.Empty;
            PlayniteApi?.Dialogs?.ShowMessage(DescribeSummary(summary) + creditNote, PluginTitle, MessageBoxButton.OK,
                summary != null && summary.StoppedEarly ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static string DescribeSummary(EstimationLibrarySummary summary)
        {
            if (summary == null) return "Cancelled. Games finished before cancelling were kept.";

            var text = new StringBuilder();
            var generated = summary.Generated + summary.GeneratedWithFallback;
            text.Append("Generated estimated values for ").Append(generated).Append(generated == 1 ? " game" : " games")
                .Append(" (").Append(summary.ValuesStored).Append(" values). ");
            if (summary.Skipped > 0) text.Append(summary.Skipped).Append(" had nothing to generate or were kept from an earlier run. ");
            if (summary.WithoutResearch > 0)
            {
                text.Append(summary.WithoutResearch).Append(summary.WithoutResearch == 1 ? " game was" : " games were")
                    .Append(" scored without web research: ").Append(summary.ResearchSkippedReason).Append(" ");
            }

            if (summary.GeneratedWithFallback > 0)
            {
                text.Append(summary.GeneratedWithFallback).Append(summary.GeneratedWithFallback == 1 ? " game was" : " games were")
                    .Append(" estimated from list structure because Gemini's answer could not be used. ");
            }

            var withEvidence = summary.Results.Where(r => r.EvidenceTotal > 0).ToList();
            if (withEvidence.Count > 0)
            {
                text.AppendLine().AppendLine();
                foreach (var r in withEvidence.Take(5))
                {
                    text.Append(string.IsNullOrWhiteSpace(r.Title) ? "Game" : r.Title).Append(": ").AppendLine(r.DescribeEvidence(0, 5, includeNames: false));
                }

                if (withEvidence.Count > 5)
                {
                    text.Append("... and ").Append(withEvidence.Count - 5).AppendLine(" more games (see the log for each).");
                }
            }

            if (summary.StoppedEarly)
            {
                text.Append("Stopped early: ").Append(summary.StopReason).Append(" Games finished so far were kept; run again after fixing this.");
            }

            return text.ToString().Trim();
        }

        /// <summary>
        /// Looks at the user's own native PlayStation-style lists and suggests the trophy budget they imply
        /// (the median total points of lists with 5 to 45 trophies). Nothing changes unless the user agrees.
        /// </summary>
        internal void FitTrophyBudgetFromLibrary()
        {
            var persisted = EstimationPersisted;
            var cache = _cacheManager as CacheManager;
            if (persisted == null || cache == null)
            {
                PlayniteApi?.Dialogs?.ShowMessage("The achievement cache is not ready yet.", PluginTitle);
                return;
            }

            CalibrationProfile profile = null;
            Exception failure = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = "Reading your native trophy lists…";
                try
                {
                    profile = EstimationCoordinator.BuildCalibration(new CacheEstimationHost(cache));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, new GlobalProgressOptions("Reading your native trophy lists…", false) { IsIndeterminate = true });

            if (failure != null)
            {
                _logger?.Error(failure, "Fitting the trophy budget failed.");
                PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), failure.Message), PluginTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var suggested = profile?.SuggestedTrophyBudget;
            var counted = profile?.Lists.Count(l => l.Size >= 5 && l.Size <= 45) ?? 0;
            if (!suggested.HasValue)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Not enough native trophy lists to fit a budget: found " + counted + " complete lists with 5 to 45 trophies, and at least 5 are needed. " +
                    "Only complete lists from PlayStation sources (PSN, RPCS3, ShadPS4, Vita3K) count. The budget was left at " + persisted.EstimatedValuesTrophyBudget + ".",
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = PlayniteApi.Dialogs.ShowMessage(
                "Your " + counted + " complete native trophy lists with 5 to 45 trophies have a median of " + suggested.Value +
                " points. Set the trophy budget to " + suggested.Value + " (currently " + persisted.EstimatedValuesTrophyBudget + ")?",
                PluginTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                persisted.EstimatedValuesTrophyBudget = suggested.Value;
            }
        }

        /// <summary>
        /// Deletes every generated trophy grade and Gamerscore after confirmation. Native, metadata and
        /// custom data live in other tables and are not touched, so this is the roll-back for the feature.
        /// </summary>
        internal bool ClearEstimatedValues()
        {
            if (!ConfirmMaintenance(
                    "Delete all generated (estimated) trophy grades and Gamerscore for every game? " +
                    "Native, metadata and custom values are not touched. Totals update to match."))
            {
                return false;
            }

            long cleared = 0;
            var ok = RunMaintenance(() => cleared = (_cacheManager as CacheManager)?.ClearEstimatedValues() ?? 0, "clear estimated values");
            if (ok)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    (cleared > 0 ? "Deleted generated values for " + cleared + " achievements." : "There were no generated values to delete.") +
                    " Saved web research was kept, so generating again costs no search credits. Use \"Delete saved research\" to remove it too.",
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return ok;
        }

        /// <summary>
        /// Deletes the saved web research (the guide findings kept per game) after confirmation. Generated trophy grades and
        /// Gamerscore are not touched. The next generation researches again and spends search credits; the credit count for
        /// the month is not reset, because the credits were really spent.
        /// </summary>
        internal void ClearSavedResearch()
        {
            ConfigureResearchStorage();
            var games = ResearchNotesCache.SavedGameCount();
            if (games == 0)
            {
                PlayniteApi?.Dialogs?.ShowMessage("There is no saved research to delete.", PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ConfirmMaintenance(
                    "Delete the saved web research for " + games + " game" + (games == 1 ? string.Empty : "s") + "? " +
                    "Generated trophy grades and Gamerscore are not touched, but generating or regenerating again will research " +
                    "from scratch and spend search credits."))
            {
                return;
            }

            var ok = RunMaintenance(() => ResearchNotesCache.ClearAll(), "clear saved research");
            if (ok)
            {
                _logger?.Info("[EstimatedValues] Deleted saved research for " + games + " game(s).");
                PlayniteApi?.Dialogs?.ShowMessage("Deleted saved research for " + games + " game" + (games == 1 ? string.Empty : "s") + ".",
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Deletes the saved web research of just the given games after confirmation. Their generated grades and Gamerscore
        /// are not touched; the next generate or regenerate for these games researches again and spends search credits.
        /// </summary>
        internal void ClearSavedResearchForGames(IReadOnlyList<Guid> gameIds)
        {
            if (gameIds == null || gameIds.Count == 0) return;

            ConfigureResearchStorage();
            var withResearch = gameIds.Distinct().Count(ResearchNotesCache.HasSaved);
            var scope = gameIds.Count == 1 ? "this game" : gameIds.Count + " selected games";
            if (withResearch == 0)
            {
                PlayniteApi?.Dialogs?.ShowMessage("There is no saved research for " + scope + ".", PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ConfirmMaintenance(
                    "Delete the saved web research for " + scope + (gameIds.Count > 1 ? " (" + withResearch + " of them have any)" : string.Empty) + "? " +
                    "Generated trophy grades and Gamerscore are not touched, but generating or regenerating " +
                    (gameIds.Count == 1 ? "this game" : "these games") + " again will research from scratch and spend search credits."))
            {
                return;
            }

            var cleared = 0;
            var ok = RunMaintenance(() => { cleared = ResearchNotesCache.ClearGames(gameIds); }, "clear saved research for selected games");
            if (ok)
            {
                _logger?.Info("[EstimatedValues] Deleted saved research for " + cleared + " selected game(s).");
                PlayniteApi?.Dialogs?.ShowMessage("Deleted saved research for " + cleared + " game" + (cleared == 1 ? string.Empty : "s") + ".",
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>Reports progress on the calling thread, in order, without queueing.</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            public SyncProgress(Action<T> action) { _action = action; }
            public void Report(T value) => _action(value);
        }
    }
}
