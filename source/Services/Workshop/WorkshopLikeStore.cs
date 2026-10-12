using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Workshop
{
    /// <summary>
    /// The like state every Workshop surface reads: how many likes each item has, and which ones
    /// this install has liked. One instance is shared by Browse and the Library page, so a press
    /// on either is seen by both. A press applies locally first and asks the service after, and a
    /// refused call puts the local state back.
    /// </summary>
    public sealed class WorkshopLikeStore
    {
        private readonly WorkshopLikesClient _client;
        private readonly WorkshopIdentityStore _identity;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, long> _counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private Task<bool> _load;

        public WorkshopLikeStore(WorkshopLikesClient client, WorkshopIdentityStore identity, ILogger logger = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _logger = logger;
        }

        /// <summary>
        /// Raised with an item's id after its count or its liked state changed, on the thread that
        /// changed it. Lists subscribe once and raise the affected row's properties.
        /// </summary>
        public event EventHandler<string> LikesChanged;

        /// <summary>
        /// True once a listing has been read. Until then no count is known to be true, so the
        /// surfaces hide the like affordance entirely rather than show a zero.
        /// </summary>
        public bool IsAvailable { get; private set; }

        /// <summary>
        /// Reads the listing once per session. Later callers await the same fetch. False when the
        /// service could not be reached, which leaves <see cref="IsAvailable"/> false.
        /// </summary>
        public Task<bool> EnsureLoadedAsync(CancellationToken cancel)
        {
            lock (_sync)
            {
                if (_load == null)
                {
                    _load = LoadAsync(cancel);
                }

                return _load;
            }
        }

        private async Task<bool> LoadAsync(CancellationToken cancel)
        {
            var voter = _identity.TryGetSubmitterHash();
            var snapshot = await _client.FetchAsync(voter, cancel).ConfigureAwait(false);
            if (snapshot == null)
            {
                lock (_sync)
                {
                    _load = null;
                }

                return false;
            }

            lock (_sync)
            {
                _counts.Clear();
                foreach (var pair in snapshot.Counts)
                {
                    _counts[pair.Key] = pair.Value;
                }
            }

            // The service is the record of what this key has liked; a key copied from another
            // install brings its likes with it this way.
            if (voter != null)
            {
                _identity.SetLikedItemIds(snapshot.Mine);
            }

            IsAvailable = true;
            RaiseChanged(null);
            return true;
        }

        public long CountFor(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return 0;
            }

            lock (_sync)
            {
                return _counts.TryGetValue(itemId, out var count) ? count : 0;
            }
        }

        public bool IsLiked(string itemId) => _identity.IsLiked(itemId);

        /// <summary>
        /// Turns this install's like for an item on or off. The change shows at once and is sent
        /// to the service; a failure undoes it and leaves the count as the service last gave it.
        /// </summary>
        public async Task ToggleAsync(string itemId, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            var liked = !_identity.IsLiked(itemId);
            Apply(itemId, liked, liked ? 1 : -1);

            // Liking is the first thing that needs an identity on an install that has never
            // shared, so this is where the submitter key is created.
            var voter = _identity.GetSubmitterHash();
            var count = await _client.SetAsync(itemId, voter, liked, cancel).ConfigureAwait(false);
            if (count == null)
            {
                Apply(itemId, !liked, liked ? -1 : 1);
                return;
            }

            SetCount(itemId, count.Value);
            RaiseChanged(itemId);
        }

        /// <summary>
        /// Likes the items this install owns that it has not liked yet, so an author's own work
        /// carries their like without them pressing anything.
        /// </summary>
        public async Task LikeOwnItemsAsync(IEnumerable<WorkshopItem> items, CancellationToken cancel)
        {
            if (!IsAvailable || items == null)
            {
                return;
            }

            var voter = _identity.TryGetSubmitterHash();
            if (voter == null)
            {
                return;
            }

            var mine = items
                .Where(item => WorkshopIdentityStore.IsOwnedBy(item, voter) && !_identity.IsLiked(item.Id))
                .Select(item => item.Id)
                .ToList();

            foreach (var id in mine)
            {
                cancel.ThrowIfCancellationRequested();
                Apply(id, true, 1);
                var count = await _client.SetAsync(id, voter, true, cancel).ConfigureAwait(false);
                if (count == null)
                {
                    Apply(id, false, -1);
                    continue;
                }

                SetCount(id, count.Value);
                RaiseChanged(id);
            }
        }

        private void Apply(string itemId, bool liked, long delta)
        {
            _identity.SetLiked(itemId, liked);
            lock (_sync)
            {
                _counts.TryGetValue(itemId, out var count);
                _counts[itemId] = Math.Max(0, count + delta);
            }

            RaiseChanged(itemId);
        }

        private void SetCount(string itemId, long count)
        {
            lock (_sync)
            {
                _counts[itemId] = Math.Max(0, count);
            }
        }

        private void RaiseChanged(string itemId)
        {
            try
            {
                LikesChanged?.Invoke(this, itemId);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "A Workshop likes change handler failed.");
            }
        }
    }
}
