using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Series-level metadata for TV libraries from the Encora API. A Series represents a show
    /// (e.g. "Hadestown"); its metadata is bootstrapped from the first Encora-identifiable recording found
    /// anywhere under the series folder.
    /// </summary>
    public class EncoraSeriesMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraSeriesMetadataProvider> _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraSeriesMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraSeriesMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraSeriesMetadataProvider> logger, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraSeriesMetadataProvider initialized.");
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
        /// Gets search results for series.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for a series.
        /// </summary>
        /// <param name="info">The series information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return result;
            }

            if (info == null || string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping series metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = EncoraFolderScanner.FindFirstEncoraId(_logger, info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found under series folder: {Path}", info.Path);
                return result;
            }

            EncoraRecording? recording;
            try
            {
                recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error fetching series metadata for {Path}", info.Path);
                return result;
            }

            if (recording == null || string.IsNullOrWhiteSpace(recording.Show))
            {
                _logger.LogInformation("[Encora] ❌ Failed to fetch series metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                return result;
            }

            var seriesTitleFormat = Plugin.Instance?.Configuration?.TvSeriesTitleFormat ?? "{show}";
            var seriesName = EncoraTitleFormatter.FormatShowTitle(seriesTitleFormat, recording);

            var series = new Series
            {
                Name = seriesName,
                OriginalTitle = recording.Show,
                SortName = seriesName,
            };

            var preserveManualDescriptionEdits = Plugin.Instance?.Configuration?.TvPreserveManualDescriptionEdits ?? true;
            var description = recording.Metadata?.ShowDescription;
            EncoraOverviewGuard.ApplyOverview(series, _libraryManager, info.Path, isFolder: true, string.IsNullOrWhiteSpace(description) ? "No Notes" : description, preserveManualDescriptionEdits, _logger, info.Path);

            series.SetProviderId("EncoraRecordingId", encoraId);
            if (recording.Metadata != null)
            {
                series.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
            }

            if (Plugin.Instance?.Configuration?.TvFetchPoster ?? true)
            {
                var posterPath = Path.Combine(info.Path, "folder.jpg");
                await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
            }

            result.HasMetadata = true;
            result.Item = series;
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
