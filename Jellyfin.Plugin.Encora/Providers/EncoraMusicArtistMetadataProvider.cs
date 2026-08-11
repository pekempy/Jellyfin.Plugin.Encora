using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Artist-level metadata for Music libraries from the Encora API. An Artist represents a show
    /// (e.g. "Hadestown"); its metadata is bootstrapped from the first Encora-identifiable audio file found
    /// anywhere under the artist folder.
    /// </summary>
    public class EncoraMusicArtistMetadataProvider : IRemoteMetadataProvider<MusicArtist, ArtistInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraMusicArtistMetadataProvider> _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraMusicArtistMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraMusicArtistMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraMusicArtistMetadataProvider> logger, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraMusicArtistMetadataProvider initialized.");
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
        /// Gets search results for artists.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for an artist.
        /// </summary>
        /// <param name="info">The artist information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<MusicArtist>();

            if (Plugin.Instance?.Configuration?.EnableAudioMatching != true)
            {
                return result;
            }

            if (info == null || string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.AudioLibraryIds))
            {
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping artist metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = EncoraFolderScanner.FindFirstEncoraId(_logger, info.Path, EncoraFolderScanner.AudioExtensions);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found under artist folder: {Path}", info.Path);
                return result;
            }

            EncoraRecording? recording;
            try
            {
                recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error fetching artist metadata for {Path}", info.Path);
                return result;
            }

            if (recording == null || string.IsNullOrWhiteSpace(recording.Show))
            {
                _logger.LogInformation("[Encora] ❌ Failed to fetch artist metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                return result;
            }

            var artistTitleFormat = Plugin.Instance?.Configuration?.AudioArtistTitleFormat ?? "{show}";
            var artistName = EncoraTitleFormatter.FormatShowTitle(artistTitleFormat, recording);

            var artist = new MusicArtist
            {
                Name = artistName,
                OriginalTitle = recording.Show,
                SortName = artistName,
            };

            var preserveManualDescriptionEdits = Plugin.Instance?.Configuration?.AudioPreserveManualDescriptionEdits ?? true;
            var description = recording.Metadata?.ShowDescription;
            EncoraOverviewGuard.ApplyOverview(artist, _libraryManager, info.Path, isFolder: true, string.IsNullOrWhiteSpace(description) ? "No Notes" : description, preserveManualDescriptionEdits, _logger, info.Path);

            artist.SetProviderId("EncoraRecordingId", encoraId);
            if (recording.Metadata != null)
            {
                artist.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
            }

            if (Plugin.Instance?.Configuration?.AudioFetchPoster ?? true)
            {
                var posterPath = Path.Combine(info.Path, "folder.jpg");
                await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
            }

            result.HasMetadata = true;
            result.Item = artist;
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
