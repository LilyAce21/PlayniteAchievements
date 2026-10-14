using Playnite.SDK;
using PlayniteAchievements.Services.Psn;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Gives games a PSN Capstone automatically when the setting is on. Only games whose NPWR ID
    /// is remembered are considered, and any game that already has a capstone, auto or PSN or
    /// hand-set, is left alone. One game at a time with a pause between, and the whole pass stops
    /// the moment PlayStation rejects the token or asks for a pause.
    /// </summary>
    internal sealed class PsnCapstoneGenerator
    {
        private static readonly TimeSpan PauseBetweenGames = TimeSpan.FromSeconds(2);

        private readonly PsnCapstoneService _service;
        private readonly PsnCapstoneAuthoring _authoring;
        private readonly Func<bool> _isEnabled;
        private readonly ILogger _logger;

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // Games PlayStation could not answer for this session, so each is asked about once.
        private readonly HashSet<Guid> _unresolvedThisSession = new HashSet<Guid>();

        public PsnCapstoneGenerator(
            PsnCapstoneService service,
            PsnCapstoneAuthoring authoring,
            Func<bool> isEnabled,
            ILogger logger)
        {
            _service = service;
            _authoring = authoring;
            _isEnabled = isEnabled;
            _logger = logger;
        }

        public async Task GenerateAsync(
            IEnumerable<Guid> gameIds,
            CancellationToken cancel = default(CancellationToken),
            Action<int, int> progress = null)
        {
            if (_isEnabled?.Invoke() != true || _service == null || _authoring == null || !_service.HasNpsso)
            {
                return;
            }

            var candidates = (gameIds ?? Enumerable.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Where(id => _service.GetNpwrs(id).Count > 0)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var gameId = candidates[i];

                    if (!_unresolvedThisSession.Contains(gameId) &&
                        !OpenEditorRegistry.IsOpen(gameId) &&
                        !_authoring.HasAnyCapstone(gameId))
                    {
                        var stop = await GenerateOneAsync(gameId, cancel).ConfigureAwait(false);
                        progress?.Invoke(i + 1, candidates.Count);
                        if (stop)
                        {
                            return;
                        }

                        if (i < candidates.Count - 1)
                        {
                            await Task.Delay(PauseBetweenGames, cancel).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        progress?.Invoke(i + 1, candidates.Count);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <returns>True when the whole pass should stop.</returns>
        private async Task<bool> GenerateOneAsync(Guid gameId, CancellationToken cancel)
        {
            try
            {
                var result = await _service.FetchAsync(_service.GetNpwrs(gameId), cancel).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger?.Info($"PSN capstone skipped for gameId={gameId}: {result.Error}");
                    switch (result.Failure)
                    {
                        case PsnFailure.InvalidNpsso:
                        case PsnFailure.AuthRejected:
                        case PsnFailure.RateLimited:
                        case PsnFailure.Network:
                            return true;
                        default:
                            _unresolvedThisSession.Add(gameId);
                            return false;
                    }
                }

                await _authoring.AuthorAsync(gameId, result.Platinum, result.Icon, replaceExisting: false)
                    .ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Automatic PSN capstone generation failed for gameId={gameId}.");
                return false;
            }
        }
    }
}
