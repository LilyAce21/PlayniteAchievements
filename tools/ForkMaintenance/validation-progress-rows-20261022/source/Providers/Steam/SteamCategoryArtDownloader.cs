using Playnite.SDK;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Downloads default category art for a (normalized category label -> Steam appId) plan to
    /// deterministic per-game cache paths. Category sources (Steam achievement sets,
    /// SteamHunters groups) build plans; this class only downloads. Best-effort: failures are
    /// logged and never fail a refresh, and existing files are skipped, so re-scans cost nothing.
    /// </summary>
    internal sealed class SteamCategoryArtDownloader
    {
        private readonly Func<DiskImageService> _diskImageServiceResolver;
        private readonly ILogger _logger;

        public SteamCategoryArtDownloader(Func<DiskImageService> diskImageServiceResolver, ILogger logger)
        {
            _diskImageServiceResolver = diskImageServiceResolver;
            _logger = logger;
        }

        public async Task DownloadAsync(
            Guid playniteGameId,
            IReadOnlyList<KeyValuePair<string, int>> plan,
            CancellationToken cancel)
        {
            var diskImageService = _diskImageServiceResolver?.Invoke();
            if (diskImageService == null || playniteGameId == Guid.Empty || plan == null || plan.Count == 0)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            foreach (var entry in plan)
            {
                cancel.ThrowIfCancellationRequested();
                var label = entry.Key;
                var entryAppId = entry.Value;
                try
                {
                    var artTarget = diskImageService.GetDefaultCategoryImagePath(
                        gameIdText, label);
                    // Wide banner art is preferred for visual consistency across categories:
                    // static header, then the appdetails content-hashed header (newer apps
                    // serve art only from hashed store_item_assets URLs that cannot be derived
                    // from the appId), then portrait library art as the last downloadable tier.
                    // decodeSize 0 stores the original bytes: no square crop, original aspect.
                    var artResult = await diskImageService.GetOrDownloadIconToPathAsync(
                        SteamImageUrls.Header(entryAppId), artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                    if (artResult == null)
                    {
                        var storeUrls = await SteamImageUrls
                            .GetStoreFallbackAsync(entryAppId, cancel, _logger)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(storeUrls?.CoverUrl))
                        {
                            artResult = await diskImageService.GetOrDownloadIconToPathAsync(
                                storeUrls.CoverUrl, artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                        }
                    }

                    if (artResult == null)
                    {
                        await diskImageService.GetOrDownloadIconToPathAsync(
                            SteamImageUrls.Cover(entryAppId), artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[SteamAch] Default category art download failed for appId={entryAppId}.");
                }
            }
        }
    }
}
