using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// count). Acts as a "second pass": for every Series, Seasons sharing the same name AND the same
    /// on-disk Path (or no Path at all - an orphaned scan artifact) are collapsed down to the one with
    /// the most episodes. Seasons sharing a name but backed by two genuinely different real folders (e.g.
    /// two non-Encora recordings manually assigned the same tour via <see cref="EncoraTourMarker"/>) are
    /// never touched. Also collapses same-path/different-name duplicates that arise when Jellyfin's local
    /// NfoParser creates a generic "Season N" entry from a stale season.nfo while the Encora plugin has
    /// already created a correctly-named Season for the same folder (the gap between the name-grouped pass
    /// above and the folder-merged pass in <see cref="EncoraSeasonMerger"/>). Only database records are
    /// touched - files on disk are never deleted, so a later scan will cleanly re-attach anything real.
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

                var duplicates = group.ToList();
                if (duplicates.Count < 2)
                {
                    continue;
                }

                // More than one *distinct* real on-disk Path sharing this Name means these are
                // legitimately different folders (e.g. two non-Encora recordings manually assigned the
                // same tour) - not a scanner race for the same folder. Leave those alone entirely; only
                // rows with no Path at all (orphaned scan artifacts) or all sharing the one real Path are
                // safe to collapse.
                var distinctRealPaths = duplicates
                    .Where(season => !string.IsNullOrWhiteSpace(season.Path))
                    .Select(season => season.Path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (distinctRealPaths.Count > 1)
                {
                    continue;
                }

                var series = group.Key.ParentId != Guid.Empty ? libraryManager.GetItemById(group.Key.ParentId) : null;
                var scopePath = duplicates[0].Path ?? series?.Path;
                if (string.IsNullOrWhiteSpace(scopePath) || !EncoraLibraryScope.IsPathInScope(libraryManager, scopePath, tvLibraryIds))
                {
                    continue;
                }

                await RemoveDuplicateSeasonsAsync(libraryManager, logger, series?.Name ?? group.Key.ParentId.ToString("N", CultureInfo.InvariantCulture), duplicates, cancellationToken).ConfigureAwait(false);
            }

            // Seasons can also end up orphaned outside any name-matching group - e.g. a Season that was
            // the "keeper" of an earlier merge, then lost its own episode(s) to a later merge pass once a
            // rescan re-fragmented and re-resolved a new keeper under the same tour name. With no Path and
            // no episodes left, a pathless Season's own display Name is whatever Jellyfin's local scanner
            // last happened to fall back to (e.g. "Series 5") - it will never match any other Season by
            // name, so it's invisible to the grouped passes above. Safe to remove outright: nothing to
            // lose (0 episodes, no on-disk folder).
            foreach (var orphan in seasons.Where(season => string.IsNullOrWhiteSpace(season.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (CountEpisodes(libraryManager, orphan) > 0)
                {
                    continue;
                }

                var series = orphan.ParentId != Guid.Empty ? libraryManager.GetItemById(orphan.ParentId) : null;
                if (series != null && !string.IsNullOrWhiteSpace(series.Path) && !EncoraLibraryScope.IsPathInScope(libraryManager, series.Path, tvLibraryIds))
                {
                    continue;
                }

                libraryManager.DeleteItem(orphan, new DeleteOptions { DeleteFileLocation = false });
                logger.LogWarning(
                    "[Encora] 🧹 Removed empty orphaned Season '{SeasonName}' under series '{SeriesName}' (no episodes, no on-disk folder)",
                    orphan.Name,
                    series?.Name ?? "Unknown");
            }

            // Same-path / different-name duplicates: Jellyfin's NfoParser can create a generic "Season N"
            // entry from a stale season.nfo <seasonnumber> while the Encora plugin has already created a
            // correctly-named Season for the exact same folder. These are in different name-groups so the
            // pass above misses them; the Merger also misses them because it only acts when there are 2+
            // distinct real paths. Collapse to the Season with the more descriptive (non-generic) name, or
            // the one with more episodes as a tiebreaker.
            var pathGroups = seasons
                .Where(season => !string.IsNullOrWhiteSpace(season.Path))
                .GroupBy(season => (season.ParentId, Path: season.Path.Trim().ToLowerInvariant()))
                .Where(g => g.Count() > 1);

            foreach (var pathGroup in pathGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var members = pathGroup.ToList();

                var series = pathGroup.Key.ParentId != Guid.Empty ? libraryManager.GetItemById(pathGroup.Key.ParentId) : null;
                var scopePath = members[0].Path;
                if (string.IsNullOrWhiteSpace(series?.Path) || string.IsNullOrWhiteSpace(scopePath)
                    || !EncoraLibraryScope.IsPathInScope(libraryManager, scopePath, tvLibraryIds))
                {
                    continue;
                }

                // Keep the one with a descriptive (non-generic) tour name; fall back to episode count.
                var keeper = members
                    .OrderBy(s => IsGenericSeasonName(s.Name) ? 1 : 0)
                    .ThenByDescending(s => CountEpisodes(libraryManager, s))
                    .ThenBy(s => s.Id)
                    .First();

                foreach (var loser in members.Where(s => s.Id != keeper.Id))
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
                        episode.ParentId = keeper.Id;
                        episode.SeasonId = keeper.Id;
                        episode.SeasonName = keeper.Name;
                        episode.ParentIndexNumber = keeper.IndexNumber;
                        await libraryManager.UpdateItemAsync(episode, keeper, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrWhiteSpace(loser.Path) || !Directory.Exists(loser.Path))
                    {
                        libraryManager.DeleteItem(loser, new DeleteOptions { DeleteFileLocation = false });
                    }

                    logger.LogWarning(
                        "[Encora] 🧩 Removed same-path duplicate Season '{LoserName}' (generic={IsGeneric}) under series '{SeriesName}' — kept '{KeeperName}' (path: {Path})",
                        loser.Name,
                        IsGenericSeasonName(loser.Name),
                        series.Name,
                        keeper.Name,
                        scopePath);
                }
            }

            return;
        }

        private static async Task RemoveDuplicateSeasonsAsync(ILibraryManager libraryManager, ILogger logger, string seriesName, List<Season> duplicates, CancellationToken cancellationToken)
        {
            var withCounts = duplicates
                .Select(season => (Season: season, EpisodeCount: CountEpisodes(libraryManager, season)))
                .OrderByDescending(x => x.EpisodeCount)
                .ThenBy(x => x.Season.Id)
                .ToList();

            var keeper = withCounts[0].Season;

            foreach (var loser in withCounts.Skip(1))
            {
                var loserEpisodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = loser.Season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                }).OfType<Episode>().ToList();

                foreach (var episode in loserEpisodes)
                {
                    episode.SetParent(keeper);
                    episode.ParentId = keeper.Id;
                    episode.SeasonId = keeper.Id;
                    episode.SeasonName = keeper.Name;
                    episode.ParentIndexNumber = keeper.IndexNumber;
                    await libraryManager.UpdateItemAsync(episode, keeper, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(loser.Season.Path) || !Directory.Exists(loser.Season.Path))
                {
                    libraryManager.DeleteItem(loser.Season, new DeleteOptions { DeleteFileLocation = false });
                }

                logger.LogWarning(
                    "[Encora] 🧩 Merged duplicate Season '{SeasonName}' ({EpisodeCount} episodes) under series '{SeriesName}' into Season with Id {KeeperId}",
                    loser.Season.Name,
                    loser.EpisodeCount,
                    seriesName,
                    keeper.Id.ToString("N", CultureInfo.InvariantCulture));
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

        private static bool IsGenericSeasonName(string? name)
        {
            return string.IsNullOrWhiteSpace(name)
                || Regex.IsMatch(name.Trim(), @"^Season\s+\d+$", RegexOptions.IgnoreCase);
        }
    }
}
