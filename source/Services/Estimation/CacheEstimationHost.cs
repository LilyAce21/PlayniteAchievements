using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>Connects the estimation coordinator to the plugin's achievement cache.</summary>
    internal sealed class CacheEstimationHost : IEstimationHost
    {
        private readonly CacheManager _cache;

        public CacheEstimationHost(CacheManager cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public IReadOnlyList<Guid> GetGameIds()
        {
            return _cache.GetCachedGameIds()
                .Select(key => Guid.TryParse(key, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }

        public EstimationSourceGame LoadSource(Guid playniteGameId)
        {
            return EstimationSourceFactory.From(_cache.LoadGameData(playniteGameId.ToString()));
        }

        public int WriteRows(Guid playniteGameId, string model, IReadOnlyList<EstimatedValueRow> rows)
        {
            return _cache.WriteEstimatedValues(playniteGameId, model, rows);
        }

        public long ClearAll() => _cache.ClearEstimatedValues();
    }
}
