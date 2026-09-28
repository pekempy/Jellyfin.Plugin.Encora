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
    /// tour. <see cref="EncoraSeasonPatcher"/> already renames each of those to the real tour, so once
    /// several end up sharing that same tour name under the same Series, they're genuine duplicates that
    /// belong together - this re-parents every Episode from the extra Seasons onto one keeper and removes
    /// the now-empty Season rows. Unlike the duplicate cleaner, Episodes are never deleted here - they're
    /// real, distinct recordings, just moved to the shared Season.
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

            var tvLibraryIds = Plugin.Instance?.Configuration?.TvLibraryIds;

            var seasons = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true
            }).OfType<Season>().ToList();

            var groups = seasons
                .Where(season => !string.IsNullOrWhiteSpace(season.Name))
                .GroupBy(season => (season.ParentId, Name: season.Name.Trim().ToUpperInvariant()));

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var members = group.ToList();
                if (members.Count < 2)
                {
                    continue;
                }

                // Only the "each backed by its own distinct real folder" case - same-path/no-path
                // duplicates are the scanner-race case EncoraSeasonDuplicateCleaner already handles.
                var distinctRealPaths = members
                    .Where(season => !string.IsNullOrWhiteSpace(season.Path))
                    .Select(season => season.Path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (distinctRealPaths.Count < 2)
                {
                    continue;
                }

                var series = libraryManager.GetItemById(group.Key.ParentId);
                var scopePath = members[0].Path ?? series?.Path;
                if (string.IsNullOrWhiteSpace(scopePath) || !EncoraLibraryScope.IsPathInScope(libraryManager, scopePath, tvLibraryIds))
                {
                    continue;
                }

                await MergeSeasonsAsync(libraryManager, logger, series?.Name ?? group.Key.ParentId.ToString(), series, members, cancellationToken).ConfigureAwait(false);
            }
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
