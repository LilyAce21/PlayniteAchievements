using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>What the coordinator needs from the plugin. Kept small so it can be tested without it.</summary>
    public interface IEstimationHost
    {
        /// <summary>The Playnite ids of games that have cached achievement data.</summary>
        IReadOnlyList<Guid> GetGameIds();

        /// <summary>The game as the planner sees it, or null when it has no achievement data.</summary>
        EstimationSourceGame LoadSource(Guid playniteGameId);

        /// <summary>Stores generated rows (the store skips any slot with native data). Returns how many values were stored.</summary>
        int WriteRows(Guid playniteGameId, string model, IReadOnlyList<EstimatedValueRow> rows);

        /// <summary>Deletes every generated value. Native data is not touched. Returns how many rows were deleted.</summary>
        long ClearAll();
    }

    public sealed class EstimationGameSummary
    {
        public Guid PlayniteGameId { get; set; }
        public string Title { get; set; }
        public EstimationRunStatus Status { get; set; }
        public int ValuesStored { get; set; }
        public string Message { get; set; }
        public LlmFailureKind FailureKind { get; set; }
    }

    public sealed class EstimationLibrarySummary
    {
        public int Games { get; set; }
        public int Generated { get; set; }
        public int GeneratedWithFallback { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int ValuesStored { get; set; }

        /// <summary>True when the run stopped early because trying more games would only fail the same way.</summary>
        public bool StoppedEarly { get; set; }

        /// <summary>Plain-language reason for stopping early, or null.</summary>
        public string StopReason { get; set; }

        public List<EstimationGameSummary> Results { get; } = new List<EstimationGameSummary>();
    }

    /// <summary>
    /// Runs estimation for one game or a whole library, one game at a time (the client paces requests for
    /// the free tier). A run stops early on a problem every remaining game would share: a missing or
    /// rejected key, an unavailable model, or a quota that is used up.
    /// </summary>
    public sealed class EstimationCoordinator
    {
        private readonly IEstimationHost _host;
        private readonly IEstimationLlmClient _client;
        private readonly Func<EstimationRunOptions> _options;
        private CalibrationProfile _calibration;
        private bool _calibrationDone;

        public EstimationCoordinator(IEstimationHost host, IEstimationLlmClient client, Func<EstimationRunOptions> options)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<EstimationGameSummary> RunGameAsync(Guid playniteGameId, bool regenerate, CancellationToken cancellationToken)
        {
            var summary = new EstimationGameSummary { PlayniteGameId = playniteGameId };

            var source = _host.LoadSource(playniteGameId);
            if (source == null || source.Achievements == null || source.Achievements.Count == 0)
            {
                summary.Status = EstimationRunStatus.Skipped;
                summary.Message = "No achievement data for this game.";
                return summary;
            }

            summary.Title = source.Title;
            var options = _options();
            options.Regenerate = regenerate;
            if (regenerate) ResearchNotesCache.Clear(playniteGameId); // regenerating means researching afresh
            if (options.Calibration == null && options.UseCalibration) options.Calibration = EnsureCalibration();

            var run = await EstimationRunner.RunAsync(source, _client, options, cancellationToken).ConfigureAwait(false);
            summary.Status = run.Status;
            summary.Message = run.Message;
            summary.FailureKind = run.FailureKind;

            if ((run.Status == EstimationRunStatus.Generated || run.Status == EstimationRunStatus.GeneratedWithFallback) && run.Rows.Count > 0)
            {
                summary.ValuesStored = _host.WriteRows(playniteGameId, options.Model, run.Rows);
            }

            return summary;
        }

        public Task<EstimationLibrarySummary> RunLibraryAsync(
            bool regenerate,
            IProgress<EstimationGameSummary> progress,
            CancellationToken cancellationToken)
        {
            return RunGamesAsync(_host.GetGameIds(), regenerate, progress, cancellationToken);
        }

        /// <summary>Runs the given games in order, with the same stop-early policy as a library run.</summary>
        public async Task<EstimationLibrarySummary> RunGamesAsync(
            IReadOnlyList<Guid> gameIds,
            bool regenerate,
            IProgress<EstimationGameSummary> progress,
            CancellationToken cancellationToken)
        {
            var library = new EstimationLibrarySummary();
            var ids = gameIds ?? new List<Guid>();
            library.Games = ids.Count;

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var summary = await RunGameAsync(id, regenerate, cancellationToken).ConfigureAwait(false);
                library.Results.Add(summary);
                progress?.Report(summary);

                library.ValuesStored += summary.ValuesStored;
                switch (summary.Status)
                {
                    case EstimationRunStatus.Generated: library.Generated++; break;
                    case EstimationRunStatus.GeneratedWithFallback: library.GeneratedWithFallback++; break;
                    case EstimationRunStatus.Skipped: library.Skipped++; break;
                    case EstimationRunStatus.Failed: library.Failed++; break;
                }

                // A failure from the model itself (key, model, quota, network) would repeat for every
                // remaining game, so stop and let the user fix it and run again; finished games are kept.
                // A refusal of one game's content is that game's problem alone.
                if (summary.Status == EstimationRunStatus.Failed && summary.FailureKind != LlmFailureKind.Content)
                {
                    library.StoppedEarly = true;
                    library.StopReason = summary.Message;
                    break;
                }
            }

            return library;
        }

        /// <summary>
        /// Summarises the user's native lists once per coordinator (so once per run). Any problem reading the
        /// library just means no calibration: estimates still work without it.
        /// </summary>
        public CalibrationProfile EnsureCalibration()
        {
            if (_calibrationDone) return _calibration;
            _calibrationDone = true;

            try
            {
                var profile = BuildCalibration(_host);
                _calibration = profile.IsEmpty ? null : profile;
            }
            catch (Exception)
            {
                _calibration = null;
            }

            return _calibration;
        }

        /// <summary>Summarises every native list the host can load. May be empty; throws if the library cannot be read.</summary>
        public static CalibrationProfile BuildCalibration(IEstimationHost host)
        {
            var sources = (host.GetGameIds() ?? new List<Guid>())
                .Select(id => host.LoadSource(id))
                .Where(source => source != null);
            return CalibrationProfile.FromSources(sources);
        }

        /// <summary>The roll-back for the whole feature. Returns how many generated rows were deleted.</summary>
        public long ClearAll()
        {
            ResearchNotesCache.ClearAll();
            return _host.ClearAll();
        }
    }
}
