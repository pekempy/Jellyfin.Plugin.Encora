using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Computes a stable Season <c>IndexNumber</c> from the chronological order of sibling Seasons'
    /// bootstrap recording dates, rather than leaving it unset. Leaving it unset does not mean "no
    /// number" - Jellyfin's own local scanner already assigns an IndexNumber to every Season by parsing
    /// digits out of the on-disk folder name (e.g. "Season 33 - ..."), and those digits are frequently an
    /// arbitrary per-tour identifier, not a display order - so an on-disk folder tree ends up showing
    /// Seasons numbered 1, 2, 3, 33, 49 instead of a clean chronological 1, 2, 3, 4, 5.
    /// </summary>
    public static class EncoraSeasonIndexResolver
    {
        /// <summary>
        /// Resolves the 1-based display IndexNumber for a Season among its siblings under the same
        /// Series, ranked by earliest recording date (falling back to name for undated Seasons).
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="seriesId">The parent Series' Id.</param>
        /// <param name="currentPath">The current Season's on-disk path.</param>
        /// <param name="currentPremiereDate">The current Season's freshly-resolved PremiereDate.</param>
        /// <param name="currentName">The current Season's freshly-resolved Name.</param>
        /// <returns>The 1-based rank to use as IndexNumber.</returns>
        public static int ResolveIndexNumber(ILibraryManager libraryManager, Guid seriesId, string currentPath, DateTime? currentPremiereDate, string? currentName)
        {
            var siblingDates = libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = seriesId,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true
            }).OfType<Season>()
              .Where(season => !string.Equals(season.Path, currentPath, StringComparison.OrdinalIgnoreCase))
              .Select(season => (Date: season.PremiereDate, Name: season.Name ?? string.Empty))
              .Append((Date: currentPremiereDate, Name: currentName ?? string.Empty))
              .OrderBy(x => x.Date ?? DateTime.MaxValue)
              .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();

            var rank = siblingDates.FindIndex(x => x.Date == currentPremiereDate && string.Equals(x.Name, currentName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            return rank >= 0 ? rank + 1 : siblingDates.Count;
        }
    }
}
