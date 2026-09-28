using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Keeps a Season's Name/PremiereDate/IndexNumber up to date from an Episode's own successfully
    /// resolved Encora recording, for TV libraries published without a physical Season folder (S##E##
    /// embedded directly in each episode's filename instead - see bootleg-linker's per-recording-folder
    /// layout). Without a Season folder, <see cref="Jellyfin.Plugin.Encora.Providers.EncoraSeasonMetadataProvider"/>
    /// has no reliable way to scan for "the" recording defining a season's tour on its own - it would
    /// only ever see the Series' single bootstrap recording, wrongly applying the same tour to every
    /// Season of a multi-tour show. Each Episode already knows unambiguously which Season it belongs to
    /// (Jellyfin's own local resolver sets <c>Episode.SeasonId</c> from the S## parsed out of the
    /// filename, before any remote provider runs) and already has its own successfully-fetched Encora
    /// recording, so it reaches up and patches its parent Season directly instead.
    /// </summary>
    public static class EncoraSeasonPatcher
    {
        /// <summary>
        /// Patches the given episode's parent Season (Name/PremiereDate/StageMediaShowId/IndexNumber)
        /// from a freshly-fetched Encora recording, if it needs updating. No-ops if the episode isn't
        /// resolved locally yet, has no Season, the Season has a real physical folder (handled by
        /// <see cref="Jellyfin.Plugin.Encora.Providers.EncoraSeasonMetadataProvider"/> instead), or
        /// nothing actually changed.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="episodePath">The episode's on-disk path, used to resolve its already-created local item.</param>
        /// <param name="recording">The episode's successfully-fetched Encora recording.</param>
        /// <param name="seasonTitleFormat">The configured Season title format.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task PatchParentSeasonAsync(
            ILibraryManager libraryManager,
            ILogger logger,
            string episodePath,
            EncoraRecording recording,
            string seasonTitleFormat,
            CancellationToken cancellationToken)
        {
            if (libraryManager.FindByPath(episodePath, isFolder: false) is not Episode episode || episode.SeasonId == Guid.Empty)
            {
                return;
            }

            if (libraryManager.GetItemById(episode.SeasonId) is not Season season)
            {
                return;
            }

            // Seasons backed by a real physical folder are handled by EncoraSeasonMetadataProvider
            // scanning that folder directly - only pathless (flat) Seasons need patching from here.
            if (!string.IsNullOrWhiteSpace(season.Path))
            {
                return;
            }

            var newName = EncoraTitleFormatter.FormatTourTitle(seasonTitleFormat, recording);
            var newPremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null;

            var changed = false;
            if (!string.Equals(season.Name, newName, StringComparison.Ordinal))
            {
                season.Name = newName;
                changed = true;
            }

            if (season.PremiereDate != newPremiereDate)
            {
                season.PremiereDate = newPremiereDate;
                changed = true;
            }

            if (recording.Metadata != null && !season.ProviderIds.ContainsKey("StageMediaShowId"))
            {
                season.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            var newIndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(libraryManager, episode.SeriesId, season.Id, newPremiereDate, newName);
            if (season.IndexNumber != newIndexNumber)
            {
                season.IndexNumber = newIndexNumber;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var series = libraryManager.GetItemById(episode.SeriesId);
            if (series == null)
            {
                return;
            }

            await libraryManager.UpdateItemAsync(season, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[Encora] ✅ Patched pathless Season '{Name}' (S{Index}) for Series {SeriesId} from Episode {EpisodePath}",
                newName,
                newIndexNumber,
                episode.SeriesId,
                episodePath);
        }
    }
}
