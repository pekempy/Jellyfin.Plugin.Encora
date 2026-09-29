using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Season-level metadata for TV libraries from the Encora API. A Season represents a tour
    /// (e.g. "Broadway", "West End"); its name is bootstrapped from the first Encora-identifiable recording
    /// found under the season folder.
    /// </summary>
    public class EncoraSeasonMetadataProvider : IRemoteMetadataProvider<Season, SeasonInfo>, ICustomMetadataProvider<Season>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraSeasonMetadataProvider> _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraSeasonMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="libraryManager">The library manager, used for per-library scoping.</param>
        public EncoraSeasonMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraSeasonMetadataProvider> logger, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraSeasonMetadataProvider initialized.");
        }

        /// <summary>
        /// Gets the name of the provider.
        /// </summary>
        public string Name => "Encora";

        /// <summary>
        /// Gets the order of the provider.
        /// </summary>
        public int Order => 100;

        /// <summary>
        /// Gets search results for seasons.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for a season.
        /// </summary>
        /// <param name="info">The season information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return result;
            }

            if (info == null)
            {
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping season metadata fetch for {Path}", info.Path);
                return result;
            }

            string? encoraId;
            if (!string.IsNullOrWhiteSpace(info.Path))
            {
                if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
                {
                    return result;
                }

                encoraId = EncoraFolderScanner.FindFirstEncoraId(_logger, info.Path);
                if (string.IsNullOrWhiteSpace(encoraId))
                {
                    return HandleNonEncoraSeason(info);
                }
            }
            else
            {
                // Flat-structure shows (no on-disk Season folder - S##E## embedded directly in each
                // episode's filename instead, e.g. bootleg-linker's per-recording-folder layout) have
                // nothing here to scan for "the" recording defining this specific Season's tour - unlike
                // the folder-based case, the Series' single bootstrap recording is not a safe substitute,
                // since a multi-tour show would wrongly get every Season named after the same one tour.
                // EncoraSeasonPatcher patches this Season directly once one of its own Episodes has been
                // resolved instead (it unambiguously knows its own Season via Episode.SeasonId).
                _logger.LogInformation(
                    "[Encora] No on-disk season folder for season {IndexNumber} - deferring to episode-side patching",
                    info.IndexNumber);
                return result;
            }

            EncoraRecording? recording;
            try
            {
                recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error fetching season metadata for {EncoraId}", encoraId);
                return result;
            }

            if (recording == null || string.IsNullOrWhiteSpace(recording.Tour))
            {
                _logger.LogInformation("[Encora] ❌ Failed to fetch season metadata from Encora for ID {EncoraId}", encoraId);
                return result;
            }

            var seasonTitleFormat = Plugin.Instance?.Configuration?.TvSeasonTitleFormat ?? "{tour}";

            var season = new Season
            {
                Name = EncoraTitleFormatter.FormatTourTitle(seasonTitleFormat, recording),
                ForcedSortName = EncoraDateHelper.BuildDateSortKey(recording.Date, null),
                PremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var seasonDate) ? seasonDate : (DateTime?)null,
            };

            if (recording.Metadata != null)
            {
                season.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(info.Path))
            {
                var existingSeason = _libraryManager.FindByPath(info.Path, isFolder: true) as Season;

                if (existingSeason != null)
                {
                    season.IndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(_libraryManager, existingSeason.SeriesId, existingSeason.Id, season.PremiereDate, season.Name);

                    // Confirmed by extensive live testing: Jellyfin ties Season identity to the physical
                    // recording folder one-to-one, so a multi-recording tour ends up as several same-named
                    // Seasons here (one per folder) on every refresh, not just the first time - merge them
                    // back together immediately rather than only via a separate batch pass. This was
                    // reverted once already tonight on suspicion it caused metadata loss; the real cause
                    // was an unbounded ffmpeg/Trickplay hang (now fixed - see ThumbGenerator's bounded
                    // timeout and Trickplay being disabled for this library) that had nothing to do with
                    // this merge. protectItemId defers the merge instead of deleting this Season's own
                    // row mid-call if it would itself be the "loser".
                    await EncoraSeasonMerger.MergeAsync(_libraryManager, _logger, existingSeason.SeriesId, season.Name, existingSeason.Id, cancellationToken).ConfigureAwait(false);
                }

                var posterLocked = EncoraRecordingApplier.IsPosterLocked(existingSeason);
                var hasExistingImage = (existingSeason != null && existingSeason.HasImage(ImageType.Primary, 0)) || EncoraRecordingApplier.HasLocalPosterFile(info.Path);

                if ((Plugin.Instance?.Configuration?.TvFetchPoster ?? true) && !posterLocked && !hasExistingImage)
                {
                    var posterPath = Path.Combine(info.Path, "folder.jpg");
                    await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
                    if (File.Exists(posterPath))
                    {
                        EncoraRecordingApplier.MarkPosterLocked(season);
                    }
                }

                if (posterLocked || hasExistingImage)
                {
                    EncoraRecordingApplier.MarkPosterLocked(season);
                }
            }

            result.HasMetadata = true;
            result.Item = season;
            return result;
        }

        /// <summary>
        /// Handles a season folder with no resolvable Encora ID: uses an <see cref="EncoraTourMarker"/>
        /// if one has already been assigned (manually, or via a previous run of this method), otherwise
        /// registers the folder as needing a manual tour assignment via the config page's
        /// "Non-Encora Recordings Needing a Tour" section and leaves the Season untouched.
        /// </summary>
        /// <param name="info">The season information.</param>
        /// <returns>The metadata result: a minimal Season if a tour override was found, otherwise empty.</returns>
        private MetadataResult<Season> HandleNonEncoraSeason(SeasonInfo info)
        {
            var result = new MetadataResult<Season>();

            var tour = EncoraTourMarker.ReadTour(_logger, info.Path);
            if (string.IsNullOrWhiteSpace(tour) && !string.IsNullOrWhiteSpace(info.Path) && Directory.Exists(info.Path))
            {
                try
                {
                    var nfoFiles = Directory.EnumerateFiles(info.Path, "*.nfo", SearchOption.AllDirectories);
                    foreach (var nfoFile in nfoFiles)
                    {
                        var nfoContent = File.ReadAllText(nfoFile);
                        var sanitizedXml = Regex.Replace(nfoContent, @"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)", "&amp;");
                        var doc = XDocument.Parse(sanitizedXml);
                        var tagline = doc.Root?.Element("tagline")?.Value;
                        if (!string.IsNullOrWhiteSpace(tagline))
                        {
                            tour = tagline.Trim();
                            break;
                        }

                        var title = doc.Root?.Element("title")?.Value;
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var tourMatch = Regex.Match(title, @"\(([^-\)]+?)(?:\s*-\s*[^)]*)?\)");
                            if (tourMatch.Success)
                            {
                                tour = tourMatch.Groups[1].Value.Trim();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Encora] [NFO] Error reading NFO tour candidate from {Path}", info.Path);
                }
            }

            if (!string.IsNullOrWhiteSpace(tour))
            {
                var season = new Season { Name = tour };

                var existingSeason = _libraryManager.FindByPath(info.Path, isFolder: true) as Season;
                if (existingSeason != null)
                {
                    season.IndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(_libraryManager, existingSeason.SeriesId, existingSeason.Id, season.PremiereDate, season.Name);

                    if (!string.Equals(existingSeason.Name, tour, StringComparison.Ordinal))
                    {
                        existingSeason.Name = tour;
                        existingSeason.IndexNumber = season.IndexNumber;
                        var series = _libraryManager.GetItemById(existingSeason.SeriesId);
                        if (series != null)
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(2000).ConfigureAwait(false);
                                        await _libraryManager.UpdateItemAsync(existingSeason, series, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                                        await EncoraSeasonMerger.MergeAsync(_libraryManager, _logger, existingSeason.SeriesId, tour, protectItemId: existingSeason.Id, CancellationToken.None).ConfigureAwait(false);
                                        _logger.LogInformation("[Encora] ✅ Updated and merged Season '{Tour}' in LibraryManager for {Path}", tour, info.Path);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "[Encora] Failed to update Season in LibraryManager for {Path}", info.Path);
                                    }
                                },
                                CancellationToken.None);
                        }
                    }
                }

                _logger.LogInformation("[Encora] ✅ Using tour override '{Tour}' for non-Encora season folder: {Path}", tour, info.Path);
                result.HasMetadata = true;
                result.Item = season;
                return result;
            }

            _logger.LogInformation("[Encora] ❌ No Encora ID and no tour override found under season folder: {Path}", info.Path);

            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                var seriesName = (_libraryManager.FindByPath(info.Path, isFolder: true) as Season) is { } season2
                    ? _libraryManager.GetItemById(season2.SeriesId)?.Name ?? "Unknown Show"
                    : "Unknown Show";
                var recordingLabel = Path.GetFileName(info.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (EncoraPendingTourResolver.RegisterPending(config, info.Path, seriesName, recordingLabel))
                {
                    _logger.LogWarning(
                        "[Encora] 🏷️ Non-Encora recording needs a tour assignment - configure it under Dashboard → Plugins → Encora → Videos - TV Library: {Path}",
                        info.Path);
                    Plugin.Instance!.SaveConfiguration(config);
                }
            }

            return result;
        }

        /// <summary>
        /// Applies metadata directly to the Season item before it is saved by MetadataService.
        /// </summary>
        /// <param name="item">The season item being refreshed.</param>
        /// <param name="options">The metadata refresh options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task returning the item update type.</returns>
        public async Task<ItemUpdateType> FetchAsync(Season item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return ItemUpdateType.None;
            }

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return ItemUpdateType.None;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, item.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                return ItemUpdateType.None;
            }

            string? tour = null;
            DateTime? premiereDate = null;
            var encoraId = EncoraFolderScanner.FindFirstEncoraId(_logger, item.Path);
            if (!string.IsNullOrWhiteSpace(encoraId))
            {
                var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    try
                    {
                        var recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);
                        if (recording != null && !string.IsNullOrWhiteSpace(recording.Tour))
                        {
                            var seasonTitleFormat = Plugin.Instance?.Configuration?.TvSeasonTitleFormat ?? "{tour}";
                            tour = EncoraTitleFormatter.FormatTourTitle(seasonTitleFormat, recording);
                            if (DateTime.TryParse(recording.Date?.FullDate, out var date))
                            {
                                premiereDate = date;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Encora] [CustomProvider] Error fetching season recording for ID {EncoraId}", encoraId);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(tour))
            {
                tour = EncoraTourMarker.ReadTour(_logger, item.Path);
            }

            if (string.IsNullOrWhiteSpace(tour) && string.IsNullOrWhiteSpace(encoraId))
            {
                var nfoResult = HandleNonEncoraSeason(new SeasonInfo { Path = item.Path });
                if (nfoResult.HasMetadata && nfoResult.Item != null)
                {
                    tour = nfoResult.Item.Name;
                    premiereDate = nfoResult.Item.PremiereDate;
                }
            }

            var updated = false;
            if (premiereDate.HasValue && item.PremiereDate != premiereDate)
            {
                item.PremiereDate = premiereDate;
                updated = true;
            }

            if (!string.IsNullOrWhiteSpace(tour) && !string.Equals(item.Name, tour, StringComparison.Ordinal))
            {
                _logger.LogInformation("[Encora] [CustomProvider] Overriding Season Name from '{OldName}' to '{NewName}' for {Path}", item.Name, tour, item.Path);
                item.Name = tour;
                item.IndexNumber = EncoraSeasonIndexResolver.ResolveIndexNumber(_libraryManager, item.SeriesId, item.Id, item.PremiereDate, item.Name);
                updated = true;

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await Task.Delay(2000).ConfigureAwait(false);
                            await EncoraSeasonMerger.MergeAsync(_libraryManager, _logger, item.SeriesId, tour, protectItemId: item.Id, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Encora] Failed in post-delay Season merge for {Path}", item.Path);
                        }
                    },
                    CancellationToken.None);
            }

            return updated ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
        }

        /// <summary>
        /// Gets the image response for a given URL.
        /// </summary>
        /// <param name="url">The image URL.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the HTTP response message.</returns>
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }
}
