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
    /// Nudges an Episode's date-encoded <c>IndexNumber</c> (see <see cref="EncoraDateHelper.ComputeDateIndexNumber"/>)
    /// off a sibling's if they'd otherwise collide. Encora's own <c>date_variant</c> field (the source of the
    /// encoded index's variant digit) only disambiguates recordings within one uploader's own submissions for
    /// a date - it is not a globally unique value, so two genuinely different recordings by two different
    /// uploaders of the same undated/partially-dated performance can both land on <c>date_variant="1"</c>
    /// independently and compute the identical IndexNumber. Two Episodes sharing one IndexNumber under the
    /// same Season is a real display/sort bug in Jellyfin clients, not just a cosmetic oddity.
    /// </summary>
    public static class EncoraEpisodeIndexResolver
    {
        /// <summary>
        /// Returns <paramref name="candidateIndexNumber"/> unchanged if no sibling Episode under
        /// <paramref name="seasonId"/> already holds it, otherwise the smallest value greater than it that's
        /// free. Incrementing (rather than re-deriving a wholly different number) keeps the result immediately
        /// adjacent to the original date-encoded value, preserving the "reads as the date" property for
        /// everything except the rare colliding pair.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="seasonId">The parent Season's Id, or <see cref="Guid.Empty"/> if not yet known.</param>
        /// <param name="currentEpisodeId">The current Episode's own Id, excluded from its own sibling check.</param>
        /// <param name="candidateIndexNumber">The freshly-computed candidate IndexNumber.</param>
        /// <returns>A collision-free IndexNumber for this Season.</returns>
        public static int ResolveCollision(ILibraryManager libraryManager, Guid seasonId, Guid currentEpisodeId, int candidateIndexNumber)
        {
            if (seasonId == Guid.Empty)
            {
                return candidateIndexNumber;
            }

            var siblingIndexes = new HashSet<int>(
                libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = seasonId,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                }).OfType<Episode>()
                  .Where(episode => episode.Id != currentEpisodeId && episode.IndexNumber.HasValue)
                  .Select(episode => episode.IndexNumber!.Value));

            var resolved = candidateIndexNumber;
            while (siblingIndexes.Contains(resolved))
            {
                resolved++;
            }

            return resolved;
        }
    }
}
