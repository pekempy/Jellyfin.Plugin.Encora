using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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

            // Only patch seasons without a physical on-disk folder — seasons that have a real
            // folder are managed by EncoraSeasonMetadataProvider directly.
            if (!string.IsNullOrWhiteSpace(season.Path) && System.IO.Directory.Exists(season.Path))
            {
                return;
            }

            var newName = EncoraTitleFormatter.FormatTourTitle(seasonTitleFormat, recording);
            var newPremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null;

            if (string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            // If the current season already has the right name, only sync date/index if needed.
            var series = libraryManager.GetItemById(episode.SeriesId);
            if (series == null)
            {
                return;
            }

            // Check if a season with the target tour name already exists under this series.
            // If so, re-parent just this episode there rather than renaming the current season
            // (which may contain episodes from a different tour).
            var existingTargetSeason = libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = episode.SeriesId,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true,
            }).OfType<Season>()
              .FirstOrDefault(s => s.Id != season.Id &&
                                   string.Equals(s.Name?.Trim(), newName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existingTargetSeason != null)
            {
                // Re-parent this episode to the existing correctly-named season instead of
                // renaming the current season (which would break its other episodes).
                episode.SetParent(existingTargetSeason);
                episode.SeasonId = existingTargetSeason.Id;
                episode.SeasonName = existingTargetSeason.Name;
                episode.ParentIndexNumber = existingTargetSeason.IndexNumber;
                await libraryManager.UpdateItemAsync(episode, existingTargetSeason, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    "[Encora] ✅ Re-parented episode from season '{OldSeason}' to existing season '{NewSeason}' for {Path}",
                    season.Name,
                    newName,
                    episodePath);
                return;
            }

            // No existing season with the target name — patch (rename) the current season.
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
                season.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                changed = true;
            }

            var newIndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(libraryManager, episode.SeriesId, season.Id, newPremiereDate, newName);
            if (season.IndexNumber != newIndexNumber)
            {
                season.IndexNumber = newIndexNumber;
                changed = true;
                await EncoraSeasonIndexResolver.SyncChildEpisodeIndexNumbersAsync(libraryManager, season, cancellationToken).ConfigureAwait(false);
            }

            if (!changed)
            {
                return;
            }

            await libraryManager.UpdateItemAsync(season, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[Encora] ✅ Patched Season '{Name}' (S{Index}) for Series {SeriesId} from Episode {EpisodePath}",
                newName,
                newIndexNumber,
                episode.SeriesId,
                episodePath);

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(2500).ConfigureAwait(false);
                        await EncoraSeasonMerger.MergeAsync(libraryManager, logger, episode.SeriesId, newName, protectItemId: null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[Encora] Failed in post-delay Season merge from episode patcher for {Path}", episodePath);
                    }
                },
                CancellationToken.None);
        }
    }
}
