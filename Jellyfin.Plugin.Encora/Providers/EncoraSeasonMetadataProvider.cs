using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    public class EncoraSeasonMetadataProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder, IMetadataProvider
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
                    _logger.LogInformation("[Encora] ❌ No Encora ID found under season folder: {Path}", info.Path);
                    return result;
                }
            }
            else
            {
                // Flat-structure shows have no on-disk season folder, so Jellyfin creates a path-less
                // "Season Unknown" to hold their episodes and there's nothing to scan. Fall back to the
                // Series' own bootstrap recording (set by EncoraSeriesMetadataProvider) - its presence
                // there already proves the series passed the scope/matching checks.
                if (!info.SeriesProviderIds.TryGetValue("EncoraRecordingId", out encoraId) || string.IsNullOrWhiteSpace(encoraId))
                {
                    _logger.LogInformation("[Encora] ❌ No season folder and no Encora series ID to fall back on for season {IndexNumber}", info.IndexNumber);
                    return result;
                }
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

            if ((Plugin.Instance?.Configuration?.TvFetchPoster ?? true) && !string.IsNullOrWhiteSpace(info.Path))
            {
                var existingSeason = _libraryManager.FindByPath(info.Path, isFolder: true);
                if (existingSeason == null || !existingSeason.HasImage(ImageType.Primary))
                {
                    var posterPath = Path.Combine(info.Path, "folder.jpg");
                    await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
                }
            }

            result.HasMetadata = true;
            result.Item = season;
            return result;
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
