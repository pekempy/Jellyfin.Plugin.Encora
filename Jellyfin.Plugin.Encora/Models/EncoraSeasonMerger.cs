using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Merges Seasons that share a name but are each backed by their own distinct real on-disk folder -
    /// the case <see cref="EncoraSeasonDuplicateCleaner"/> deliberately leaves alone. Confirmed by testing
    /// against a real library: Jellyfin's local scanner ties Season identity to the physical folder
    /// directly under a Series, one-to-one, regardless of any S##E## embedded in an episode's filename -
    /// publishing every recording in its own folder (bootleg-linker's per-recording-folder layout, no
    /// shared Tour/Season folder) means Jellyfin creates one Season per recording folder, not one per
    /// tour, EVERY time Jellyfin (re-)resolves that folder - not just once. Also confirmed by testing:
    /// this happens on any refresh, not only a full library scan (e.g. a plugin reload triggers Jellyfin
    /// to re-walk the library), so <see cref="EncoraSeasonPatcher"/> calls <see cref="MergeAsync"/> inline
    /// for its own Series/Season immediately after patching, rather than relying solely on a periodic
    /// batch pass to undo the re-fragmentation later. <see cref="RunAsync"/> remains useful as a one-off
    /// bulk cleanup (e.g. after a migration) and as a second pass after a full scan. Unlike the duplicate
    /// cleaner, Episodes are never deleted here - they're real, distinct recordings, just re-parented onto
    /// one shared Season.
    /// </summary>
    public static class EncoraSeasonMerger
    {
        /// <summary>
        /// Finds and merges same-named, different-folder Seasons across all TV libraries in Encora's scope.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task RunAsync(ILibraryManager libraryManager, ILogger logger, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(logger);

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return;
            }

            var seasons = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true
            }).OfType<Season>().ToList();

            var groups = seasons
                .Where(season => !string.IsNullOrWhiteSpace(season.Name))
                .GroupBy(season => (season.ParentId, Name: season.Name.Trim()));

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MergeAsync(libraryManager, logger, group.Key.ParentId, group.Key.Name, protectItemId: null, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Finds and merges every Season named <paramref name="seasonName"/> under Series
        /// <paramref name="seriesId"/> that's backed by its own distinct real folder. No-ops if there's
        /// nothing to merge (0 or 1 matching Season, or they all share the same folder/no folder - the
        /// scanner-race case <see cref="EncoraSeasonDuplicateCleaner"/> handles instead).
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="seriesId">The parent Series' Id.</param>
        /// <param name="seasonName">The Season name to merge duplicates of.</param>
        /// <param name="protectItemId">
        /// If set, and this Season would be chosen as a "loser" (deleted) in this merge, the merge is
        /// skipped entirely instead. Callers pass their own Season's Id here when invoking this inline
        /// from within that Season's own metadata resolution - deleting the very item the caller is about
        /// to return metadata for, mid-call, would leave Jellyfin persisting onto a row that no longer
        /// exists. Deferred merges still resolve on a later pass, once the actual keeper is processed.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task MergeAsync(ILibraryManager libraryManager, ILogger logger, Guid seriesId, string seasonName, Guid? protectItemId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(logger);

            if (string.IsNullOrWhiteSpace(seasonName) || Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return;
            }

            var members = libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = seriesId,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true
            }).OfType<Season>()
              .Where(season => string.Equals(season.Name?.Trim(), seasonName.Trim(), StringComparison.OrdinalIgnoreCase))
              .ToList();

            if (members.Count < 2)
            {
                return;
            }

            // Only the "each backed by its own distinct real folder" case - same-path/no-path duplicates
            // are the scanner-race case EncoraSeasonDuplicateCleaner already handles.
            var distinctRealPaths = members
                .Where(season => !string.IsNullOrWhiteSpace(season.Path))
                .Select(season => season.Path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctRealPaths.Count < 2)
            {
                return;
            }

            if (protectItemId.HasValue)
            {
                var keeperCandidate = members.OrderBy(season => season.PremiereDate ?? DateTime.MaxValue).ThenBy(season => season.Id).First();
                if (keeperCandidate.Id != protectItemId.Value && members.Any(season => season.Id == protectItemId.Value))
                {
                    return;
                }
            }

            var series = libraryManager.GetItemById(seriesId);
            var scopePath = members[0].Path ?? series?.Path;
            if (string.IsNullOrWhiteSpace(scopePath) || !EncoraLibraryScope.IsPathInScope(libraryManager, scopePath, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                return;
            }

            await MergeSeasonsAsync(libraryManager, logger, series?.Name ?? seriesId.ToString(), series, members, cancellationToken).ConfigureAwait(false);
        }

        private static async Task MergeSeasonsAsync(ILibraryManager libraryManager, ILogger logger, string seriesName, BaseItem? series, List<Season> members, CancellationToken cancellationToken)
        {
            var keeper = members.OrderBy(season => season.PremiereDate ?? DateTime.MaxValue).ThenBy(season => season.Id).First();
            var mergedEpisodeCount = 0;
            var mergedSeasonCount = 0;

            foreach (var loser in members.Where(season => season.Id != keeper.Id))
            {
                var episodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = loser.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                }).OfType<Episode>().ToList();

                foreach (var episode in episodes)
                {
                    episode.SetParent(keeper);
                    episode.SeasonId = keeper.Id;
                    episode.SeasonName = keeper.Name;
                    episode.ParentIndexNumber = keeper.IndexNumber;
                    await libraryManager.UpdateItemAsync(episode, keeper, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    mergedEpisodeCount++;
                }

                libraryManager.DeleteItem(loser, new DeleteOptions { DeleteFileLocation = false });
                mergedSeasonCount++;
            }

            if (mergedEpisodeCount > 0)
            {
                logger.LogWarning(
                    "[Encora] 🔗 Merged {SeasonCount} Season folder(s) named '{SeasonName}' under series '{SeriesName}' into one shared Season ({EpisodeCount} episode(s) moved, kept Id {KeeperId})",
                    mergedSeasonCount,
                    keeper.Name,
                    seriesName,
                    mergedEpisodeCount,
                    keeper.Id);

                var newIndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(libraryManager, keeper.SeriesId, keeper.Id, keeper.PremiereDate, keeper.Name);
                if (keeper.IndexNumber != newIndexNumber && series != null)
                {
                    keeper.IndexNumber = newIndexNumber;
                    await libraryManager.UpdateItemAsync(keeper, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
