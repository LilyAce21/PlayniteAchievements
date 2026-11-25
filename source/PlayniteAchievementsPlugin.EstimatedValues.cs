using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows;
using Playnite.SDK;
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

        private PersistedSettings EstimationPersisted => _settingsViewModel?.Settings?.Persisted;

        private static string PluginTitle => ResourceProvider.GetString("LOCPlayAch_Title_PluginName");

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
        internal void GenerateEstimatedValues(IReadOnlyList<Guid> gameIds, bool regenerate)
        {
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
            var scope = gameIds == null ? "every game" : gameIds.Count == 1 ? "this game" : gameIds.Count + " games";
            var prompt = regenerate
                ? "Regenerate estimated trophy grades and Gamerscore for " + scope + "? Existing generated values will be replaced. Native, metadata and custom values are never changed."
                : "Generate estimated trophy grades and Gamerscore for " + scope + "? Public game information (achievement names, descriptions and any native values) is sent to Google's Gemini service. Nothing about your account, unlocks or dates is sent. Native values are never changed.";
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
                var coordinator = new EstimationCoordinator(
                    new CacheEstimationHost(cache),
                    client,
                    () => EstimatedValuesConfig.ToRunOptions(EstimationPersisted));

                var ids = gameIds ?? new CacheEstimationHost(cache).GetGameIds();
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

            PlayniteApi?.Dialogs?.ShowMessage(DescribeSummary(summary), PluginTitle, MessageBoxButton.OK,
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
            if (summary.GeneratedWithFallback > 0)
            {
                text.Append(summary.GeneratedWithFallback).Append(summary.GeneratedWithFallback == 1 ? " game was" : " games were")
                    .Append(" estimated from list structure because Gemini's answer could not be used. ");
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
                    cleared > 0 ? "Deleted generated values for " + cleared + " achievements." : "There were no generated values to delete.",
                    PluginTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return ok;
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
