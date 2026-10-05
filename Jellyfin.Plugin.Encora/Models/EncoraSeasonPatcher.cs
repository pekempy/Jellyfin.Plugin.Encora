using System;
using System.Globalization;
using System.IO;
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

            var newName = EncoraTitleFormatter.FormatTourTitle(seasonTitleFormat, recording);
            var newPremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null;

            if (string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            var series = libraryManager.GetItemById(episode.SeriesId) as Folder;
            if (series == null)
            {
                return;
            }

            var seasonMatchesTarget = string.Equals(season.Name?.Trim(), newName.Trim(), StringComparison.OrdinalIgnoreCase);

            // If the season already matches the target tour and has an on-disk folder, it's already managed.
            if (seasonMatchesTarget && !string.IsNullOrWhiteSpace(season.Path) && Directory.Exists(season.Path))
            {
                if (episode.ParentIndexNumber != season.IndexNumber)
                {
                    episode.ParentIndexNumber = season.IndexNumber;
                    await libraryManager.UpdateItemAsync(episode, season, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                EncoraEpisodeMetadataProvider.RecordPendingSeason(episodePath, season);
                return;
            }

            // Check if a season with the target tour name already exists under this series.
            if (!seasonMatchesTarget)
            {
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
                    episode.SetParent(existingTargetSeason);
                    episode.SeasonId = existingTargetSeason.Id;
                    episode.SeasonName = existingTargetSeason.Name;
                    episode.ParentIndexNumber = existingTargetSeason.IndexNumber;
                    await libraryManager.UpdateItemAsync(episode, existingTargetSeason, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    EncoraEpisodeMetadataProvider.RecordPendingSeason(episodePath, existingTargetSeason);
                    logger.LogInformation(
                        "[Encora] ✅ Re-parented episode from season '{OldSeason}' to existing season '{NewSeason}' for {Path}",
                        season.Name,
                        newName,
                        episodePath);
                    return;
                }

                // If the current season contains other episodes or is another folder, create a new season.
                var otherEpisodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                }).OfType<Episode>().Where(e => e.Id != episode.Id).ToList();

                var isForeignPhysicalFolder = !string.IsNullOrWhiteSpace(season.Path)
                    && Directory.Exists(season.Path)
                    && !episodePath.StartsWith(season.Path, StringComparison.OrdinalIgnoreCase);

                if (otherEpisodes.Count > 0 || isForeignPhysicalFolder)
                {
                    var newIndex = EncoraSeasonIndexResolver.ResolveIndexNumber(libraryManager, episode.SeriesId, Guid.Empty, newPremiereDate, newName);
                    var newSeason = new Season
                    {
                        Name = newName,
                        IndexNumber = newIndex,
                        PremiereDate = newPremiereDate,
                        SeriesId = series.Id,
                        SeriesName = series.Name,
                        SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                        Id = libraryManager.GetNewItemId(
                            series.Id + newIndex.ToString(CultureInfo.InvariantCulture) + newName,
                            typeof(Season))
                    };

                    if (recording.Metadata != null)
                    {
                        newSeason.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
                    }

                    series.AddChild(newSeason);

                    episode.SetParent(newSeason);
                    episode.SeasonId = newSeason.Id;
                    episode.SeasonName = newSeason.Name;
                    episode.ParentIndexNumber = newSeason.IndexNumber;
                    await libraryManager.UpdateItemAsync(episode, newSeason, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    EncoraEpisodeMetadataProvider.RecordPendingSeason(episodePath, newSeason);

                    logger.LogInformation(
                        "[Encora] ✅ Created new Season '{NewSeason}' (S{Index}) and re-parented episode from '{OldSeason}' for {Path}",
                        newName,
                        newIndex,
                        season.Name,
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

                    return;
                }
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
            }

            if (episode.ParentIndexNumber != season.IndexNumber)
            {
                episode.ParentIndexNumber = season.IndexNumber;
                await libraryManager.UpdateItemAsync(episode, season, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            EncoraEpisodeMetadataProvider.RecordPendingSeason(episodePath, season);
            await EncoraSeasonIndexResolver.SyncChildEpisodeIndexNumbersAsync(libraryManager, season, cancellationToken).ConfigureAwait(false);

            if (!season.HasImage(ImageType.Primary, 0) && !EncoraRecordingApplier.HasLocalPosterFile(season.Path))
            {
                var showPoster = series.GetImagePath(ImageType.Primary);
                if (string.IsNullOrWhiteSpace(showPoster) || !File.Exists(showPoster))
                {
                    if (!string.IsNullOrWhiteSpace(series.Path) && Directory.Exists(series.Path))
                    {
                        foreach (var name in new[] { "folder.jpg", "folder.png", "poster.jpg", "poster.png" })
                        {
                            var candidate = Path.Combine(series.Path, name);
                            if (File.Exists(candidate))
                            {
                                showPoster = candidate;
                                break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(showPoster) && File.Exists(showPoster))
                {
                    var assignedPoster = showPoster;
                    if (!string.IsNullOrWhiteSpace(season.Path) && Directory.Exists(season.Path))
                    {
                        var seasonPosterPath = Path.Combine(season.Path, "folder.jpg");
                        if (!File.Exists(seasonPosterPath) && !string.Equals(Path.GetFullPath(seasonPosterPath), Path.GetFullPath(showPoster), StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Copy(showPoster, seasonPosterPath, overwrite: false);
                                assignedPoster = seasonPosterPath;
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "[Encora] Could not copy show poster to season folder {SeasonPoster}", seasonPosterPath);
                            }
                        }
                        else if (File.Exists(seasonPosterPath))
                        {
                            assignedPoster = seasonPosterPath;
                        }
                    }

                    season.SetImagePath(ImageType.Primary, assignedPoster);
                    EncoraRecordingApplier.MarkPosterLocked(season);
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            await libraryManager.UpdateItemAsync(season, series!, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
