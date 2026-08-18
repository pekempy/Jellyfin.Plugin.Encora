using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Album-level metadata for Music libraries from the Encora API. An Album represents one
    /// specific dated recording (tour + date), matched by the same Encora ID convention as Movie/TV libraries,
    /// via any Encora-identifiable audio file found under the album folder.
    /// </summary>
    public class EncoraMusicAlbumMetadataProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraMusicAlbumMetadataProvider> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraMusicAlbumMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        /// <param name="mediaEncoder">Used to detect embedded ID3 album art (via ffprobe) before downloading a poster.</param>
        public EncoraMusicAlbumMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraMusicAlbumMetadataProvider> logger, ILibraryManager libraryManager, IMediaEncoder mediaEncoder)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _logger.LogInformation("[Encora] ✅ EncoraMusicAlbumMetadataProvider initialized.");
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
        /// Gets search results for albums.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for an album.
        /// </summary>
        /// <param name="info">The album information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<MusicAlbum>();

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
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping album metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = EncoraFolderScanner.FindFirstEncoraId(_logger, info.Path, EncoraFolderScanner.AudioExtensions);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found under album folder: {Path}", info.Path);
                return result;
            }

            var options = EncoraAudioOptions.Build();

            try
            {
                var recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);

                if (recording == null)
                {
                    _logger.LogInformation("[Encora] ❌ Failed to fetch album metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                    return result;
                }

                var titleFormat = Plugin.Instance?.Configuration?.AudioAlbumTitleFormat ?? "{tour} - {date}";
                var albumName = EncoraAudioTitleFormatter.FormatAlbumTitle(titleFormat, recording, options.DateReplaceChar);

                var artistTitleFormat = Plugin.Instance?.Configuration?.AudioArtistTitleFormat ?? "{show}";
                var artistName = EncoraTitleFormatter.FormatShowTitle(artistTitleFormat, recording);

                var album = new MusicAlbum
                {
                    Name = albumName,
                    Artists = new[] { artistName },
                    AlbumArtists = new[] { artistName },
                };

                EncoraRecordingApplier.ApplyRecordingFields(album, _libraryManager, info.Path, recording, encoraId, options, _logger);
                EncoraRecordingApplier.ApplyNftRating(album, recording.Nft, options.IncludeNftTag);

                var existingAlbum = _libraryManager.FindByPath(info.Path, isFolder: true);
                var posterLocked = EncoraRecordingApplier.IsPosterLocked(existingAlbum);
                var hasExistingImage = existingAlbum != null && existingAlbum.HasImage(ImageType.Primary, 0);

                var respectEmbeddedTags = Plugin.Instance?.Configuration?.AudioRespectEmbeddedTags ?? true;
                if (posterLocked)
                {
                    _logger.LogInformation("[Encora] Skipping StageMedia poster download for {Path} - poster already locked", info.Path);
                }
                else if (options.FetchPoster && respectEmbeddedTags && await EncoraFolderScanner.FolderHasEmbeddedArtworkAsync(_mediaEncoder, info.Path).ConfigureAwait(false))
                {
                    _logger.LogInformation("[Encora] Skipping StageMedia poster download for {Path} - embedded album art found", info.Path);
                }
                else if (options.FetchPoster && !hasExistingImage)
                {
                    var posterPath = System.IO.Path.Combine(info.Path, "folder.jpg");
                    await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);
                    if (System.IO.File.Exists(posterPath))
                    {
                        EncoraRecordingApplier.MarkPosterLocked(album);
                    }
                }

                if (posterLocked || hasExistingImage)
                {
                    EncoraRecordingApplier.MarkPosterLocked(album);
                }

                result.HasMetadata = true;
                result.Item = album;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Encora] Error while fetching album metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                return result;
            }

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
