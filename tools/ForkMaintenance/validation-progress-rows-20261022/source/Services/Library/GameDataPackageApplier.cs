using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Workshop;

namespace PlayniteAchievements.Services.Library
{
    /// <summary>
    /// Applies Workshop game data onto games through the Workshop installer, which records each
    /// game's new version, baseline and package copy: an update merges a newer version in keeping
    /// the edits made since the last apply, and a reset applies the kept package copy again as
    /// published. Shared by Manage Achievements and the Library page.
    /// </summary>
    /// <summary>One game a game data package goes onto, and how it meets that game's data.</summary>
    public struct GameDataTarget
    {
        public GameDataTarget(Guid gameId, WorkshopGameDataInstallMode mode)
        {
            GameId = gameId;
            Mode = mode;
        }

        public Guid GameId { get; }

        public WorkshopGameDataInstallMode Mode { get; }
    }

    public sealed class GameDataPackageApplier
    {
        private readonly WorkshopClient _client;
        private readonly WorkshopInstaller _installer;
        private readonly GameDataLinkService _links;

        public GameDataPackageApplier(WorkshopClient client, WorkshopInstaller installer, GameDataLinkService links)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
            _links = links ?? throw new ArgumentNullException(nameof(links));
        }

        /// <summary>
        /// Downloads <paramref name="item"/> once and merges it into each game, keeping the edits
        /// made there since its last apply. The results are combined.
        /// </summary>
        public Task<WorkshopInstallResult> UpdateAsync(WorkshopItem item, IReadOnlyList<Guid> gameIds, CancellationToken cancel)
        {
            var targets = new List<GameDataTarget>();
            foreach (var gameId in gameIds ?? Array.Empty<Guid>())
            {
                targets.Add(new GameDataTarget(gameId, WorkshopGameDataInstallMode.KeepEditsSinceInstall));
            }

            return ApplyManyAsync(item, targets, cancel);
        }

        /// <summary>Downloads <paramref name="item"/> once and installs it onto each target. The results are combined.</summary>
        public async Task<WorkshopInstallResult> ApplyManyAsync(WorkshopItem item, IReadOnlyList<GameDataTarget> targets, CancellationToken cancel)
        {
            if (item == null || targets == null || targets.Count == 0)
            {
                return new WorkshopInstallResult();
            }

            var scratch = NewScratch();
            try
            {
                var downloaded = Path.Combine(scratch, "download", PackageFileOf(item));
                Directory.CreateDirectory(Path.GetDirectoryName(downloaded));
                await _client.DownloadPackageAsync(item, downloaded, null, cancel);
                return await InstallManyAsync(item, downloaded, targets, cancel);
            }
            finally
            {
                PortablePackage.TryDeleteDirectory(scratch);
            }
        }

        /// <summary>
        /// Installs the package at <paramref name="packagePath"/> onto each target, each from its
        /// own copy, so the file stays. The results are combined.
        /// </summary>
        public async Task<WorkshopInstallResult> InstallManyAsync(
            WorkshopItem item,
            string packagePath,
            IReadOnlyList<GameDataTarget> targets,
            CancellationToken cancel)
        {
            var combined = new WorkshopInstallResult();
            foreach (var target in targets ?? Array.Empty<GameDataTarget>())
            {
                var result = await InstallAsync(target.GameId, item, packagePath, target.Mode, cancel);
                combined.Warnings.AddRange(result.Warnings);
                combined.KeptEdits += result.KeptEdits;
                combined.UpdatedTargets++;
                combined.GameId = target.GameId;
            }

            return combined;
        }

        /// <summary>
        /// Applies the game's record again as published: from its kept package copy, or, for a
        /// record without one (made before copies were kept), the Workshop's current version,
        /// which is the only one the index offers.
        /// </summary>
        /// <param name="name">The item's display name, for the record the reset writes.</param>
        public async Task<WorkshopInstallResult> ResetAsync(Guid gameId, LibraryLink link, string name, CancellationToken cancel)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            var kept = _links.PackagePathOf(link);
            if (kept != null)
            {
                var item = new WorkshopItem
                {
                    Id = GameDataLinkService.WorkshopItemIdOf(link),
                    Kind = WorkshopItemKind.GameCustomData,
                    Name = name,
                    Version = link.AppliedVersion
                };
                return await InstallAsync(gameId, item, kept, WorkshopGameDataInstallMode.Replace, cancel);
            }

            var index = _client.LastIndex ?? await _client.FetchIndexAsync(cancel);
            var latest = GameDataLinkService.IndexItemOf(index, link)
                         ?? throw new InvalidOperationException("The Workshop no longer lists this item.");
            return await ApplyAsync(gameId, latest, WorkshopGameDataInstallMode.Replace, cancel);
        }

        /// <summary>Downloads <paramref name="item"/> and installs it onto the game in <paramref name="mode"/>.</summary>
        public async Task<WorkshopInstallResult> ApplyAsync(Guid gameId, WorkshopItem item, WorkshopGameDataInstallMode mode, CancellationToken cancel)
        {
            var scratch = NewScratch();
            try
            {
                var downloaded = Path.Combine(scratch, "download", PackageFileOf(item));
                Directory.CreateDirectory(Path.GetDirectoryName(downloaded));
                await _client.DownloadPackageAsync(item, downloaded, null, cancel);
                return await InstallAsync(gameId, item, downloaded, mode, cancel);
            }
            finally
            {
                PortablePackage.TryDeleteDirectory(scratch);
            }
        }

        /// <summary>Installs from a copy of <paramref name="source"/>, since an install consumes its package.</summary>
        private async Task<WorkshopInstallResult> InstallAsync(Guid gameId, WorkshopItem item, string source, WorkshopGameDataInstallMode mode, CancellationToken cancel)
        {
            var scratch = NewScratch();
            try
            {
                Directory.CreateDirectory(scratch);
                var package = Path.Combine(scratch, Path.GetFileName(source));
                await Task.Run(() => File.Copy(source, package, overwrite: true), cancel);
                return await _installer.InstallAsync(
                    new WorkshopInstallRequest
                    {
                        Item = item,
                        PackagePath = package,
                        TargetGameId = gameId,
                        GameDataMode = mode
                    },
                    cancel);
            }
            finally
            {
                PortablePackage.TryDeleteDirectory(scratch);
            }
        }

        private static string PackageFileOf(WorkshopItem item)
        {
            return item?.Package?.File ?? "package" + GameCustomDataStore.PortableFileExtension;
        }

        private static string NewScratch()
        {
            return Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "GameDataApply", Guid.NewGuid().ToString("N"));
        }
    }
}
