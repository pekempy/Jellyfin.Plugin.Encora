using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Cleans up duplicate Season records that Jellyfin's own scanner occasionally leaves behind for the
    /// same tour - observed when concurrent episode processing during a library scan races and resolves
    /// the same on-disk season folder to two separate Season rows (one left with a stale, partial episode
    /// count). Acts as a "second pass": for every Series, Seasons sharing the same name are collapsed down
    /// to the one with the most episodes. Only database records are touched - files on disk are never
    /// deleted, so a later scan will cleanly re-attach anything real.
    /// </summary>
    public static class EncoraSeasonDuplicateCleaner
    {
        /// <summary>
        /// Finds and removes duplicate same-named Seasons across all TV libraries in Encora's scope.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static Task RunAsync(ILibraryManager libraryManager, ILogger logger, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(logger);

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return Task.CompletedTask;
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

                var duplicates = group.ToList();
                if (duplicates.Count < 2)
                {
                    continue;
                }

                var series = libraryManager.GetItemById(group.Key.ParentId);
                var scopePath = duplicates[0].Path ?? series?.Path;
                if (string.IsNullOrWhiteSpace(scopePath) || !EncoraLibraryScope.IsPathInScope(libraryManager, scopePath, tvLibraryIds))
                {
                    continue;
                }

                RemoveDuplicateSeasons(libraryManager, logger, series?.Name ?? group.Key.ParentId.ToString("N", CultureInfo.InvariantCulture), duplicates);
            }

            return Task.CompletedTask;
        }

        private static void RemoveDuplicateSeasons(ILibraryManager libraryManager, ILogger logger, string seriesName, List<Season> duplicates)
        {
            var withCounts = duplicates
                .Select(season => (Season: season, EpisodeCount: CountEpisodes(libraryManager, season)))
                .OrderByDescending(x => x.EpisodeCount)
                .ThenBy(x => x.Season.Id)
                .ToList();

            var keeper = withCounts[0];

            foreach (var loser in withCounts.Skip(1))
            {
                foreach (var episode in libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = loser.Season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                }))
                {
                    libraryManager.DeleteItem(episode, new DeleteOptions { DeleteFileLocation = false });
                }

                libraryManager.DeleteItem(loser.Season, new DeleteOptions { DeleteFileLocation = false });

                logger.LogWarning(
                    "[Encora] 🧩 Removed duplicate Season '{SeasonName}' ({EpisodeCount} episodes) under series '{SeriesName}' - kept the Season with {KeeperEpisodeCount} episodes instead (Id {KeeperId})",
                    loser.Season.Name,
                    loser.EpisodeCount,
                    seriesName,
                    keeper.EpisodeCount,
                    keeper.Season.Id.ToString("N", CultureInfo.InvariantCulture));
            }
        }

        private static int CountEpisodes(ILibraryManager libraryManager, Season season)
        {
            return libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = season.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            }).Count;
        }
    }
}
