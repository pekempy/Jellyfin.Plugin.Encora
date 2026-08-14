using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// Searches Encora for shows by name, for Jellyfin's manual "Identify" flow.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(searchInfo?.Name))
            {
                return new List<RemoteSearchResult>();
            }

            List<EncoraShowSearchResult>? shows;
            try
            {
                shows = await EncoraShowClient.SearchShowsAsync(_httpClientFactory, _logger, apiKey, searchInfo.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error searching shows for {Query}", searchInfo.Name);
                return new List<RemoteSearchResult>();
            }

            if (shows == null)
            {
                return new List<RemoteSearchResult>();
            }

            return shows
                .Where(show => !string.IsNullOrWhiteSpace(show.Name))
                .Select(show => new RemoteSearchResult
                {
                    Name = show.Name,
                    ImageUrl = show.PosterUrl,
                    ProductionYear = show.Year,
                    SearchProviderName = Name,
                    ProviderIds = new Dictionary<string, string> { ["EncoraShowId"] = show.Id.ToString(CultureInfo.InvariantCulture) }
                })
                .ToList();
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

            if (info.ProviderIds.TryGetValue("EncoraShowId", out var manualShowId) && !string.IsNullOrWhiteSpace(manualShowId))
            {
                return await GetMetadataFromShowAsync(manualShowId, apiKey, info.Path, cancellationToken).ConfigureAwait(false);
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

            var existingSeries = _libraryManager.FindByPath(info.Path, isFolder: true);
            if ((Plugin.Instance?.Configuration?.TvFetchPoster ?? true) && (existingSeries == null || !existingSeries.HasImage(ImageType.Primary, 0)))
            {
                var posterPath = Path.Combine(info.Path, "folder.jpg");
                await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
            }

            result.HasMetadata = true;
            result.Item = series;
            return result;
        }

        /// <summary>
        /// Builds Series metadata directly from an Encora show, for a Series that's been manually
        /// identified/matched (via <see cref="GetSearchResults"/>) rather than bootstrapped from a
        /// recording found on disk. No specific recording is involved, so this works even for a series
        /// folder with no Encora-identifiable recordings under it yet.
        /// </summary>
        private async Task<MetadataResult<Series>> GetMetadataFromShowAsync(string showId, string apiKey, string path, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();

            EncoraShow? show;
            try
            {
                show = await EncoraShowClient.FetchShowAsync(_httpClientFactory, _logger, apiKey, showId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error fetching show {ShowId}", showId);
                return result;
            }

            if (show == null || string.IsNullOrWhiteSpace(show.Name))
            {
                _logger.LogInformation("[Encora] ❌ Failed to fetch show metadata from Encora for ShowId {ShowId}", showId);
                return result;
            }

            var seriesTitleFormat = Plugin.Instance?.Configuration?.TvSeriesTitleFormat ?? "{show}";
            var seriesName = EncoraTitleFormatter.Format(seriesTitleFormat, new Dictionary<string, string?>
            {
                ["show"] = show.Name,
                ["venue"] = null,
                ["city"] = null
            });

            var series = new Series
            {
                Name = seriesName,
                OriginalTitle = show.Name,
                SortName = seriesName,
            };

            var preserveManualDescriptionEdits = Plugin.Instance?.Configuration?.TvPreserveManualDescriptionEdits ?? true;
            EncoraOverviewGuard.ApplyOverview(series, _libraryManager, path, isFolder: true, string.IsNullOrWhiteSpace(show.Description) ? "No Notes" : show.Description, preserveManualDescriptionEdits, _logger, path);

            series.SetProviderId("EncoraShowId", showId);
            series.SetProviderId("StageMediaShowId", showId);

            var existingSeriesFromShow = _libraryManager.FindByPath(path, isFolder: true);
            if ((Plugin.Instance?.Configuration?.TvFetchPoster ?? true) && (existingSeriesFromShow == null || !existingSeriesFromShow.HasImage(ImageType.Primary, 0)))
            {
                var posterPath = Path.Combine(path, "folder.jpg");
                await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, show.Id, null, posterPath, cancellationToken).ConfigureAwait(false);
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
