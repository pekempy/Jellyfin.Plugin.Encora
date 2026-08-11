using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Track-level metadata for Music libraries from the Encora API. A Track represents one specific
    /// dated recording (or one Act of it, for multi-file recordings), matched by the same Encora ID convention
    /// as Movie/TV libraries.
    /// </summary>
    public class EncoraAudioTrackMetadataProvider : IRemoteMetadataProvider<Audio, SongInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraAudioTrackMetadataProvider> _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraAudioTrackMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraAudioTrackMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraAudioTrackMetadataProvider> logger, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraAudioTrackMetadataProvider initialized.");
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
        /// Gets search results for tracks.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for a track.
        /// </summary>
        /// <param name="info">The track information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Audio>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Audio>();

            if (info == null || string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            if (Plugin.Instance?.Configuration?.EnableAudioMatching != true)
            {
                _logger.LogInformation("[Encora] Audio matching is disabled in plugin settings, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.AudioLibraryIds))
            {
                _logger.LogInformation("[Encora] Path {Path} is not in a scoped Music library, skipping metadata fetch", info.Path);
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = EncoraIdExtractor.ExtractEncoraId(_logger, info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found in path: {Path}", info.Path);
                return result;
            }

            var options = EncoraAudioOptions.Build();

            try
            {
                var recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);

                if (recording == null)
                {
                    _logger.LogInformation("[Encora] ❌ Failed to fetch metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                    return result;
                }

                _logger.LogInformation("[Encora] ✅ Successfully fetched metadata from Encora for ID {EncoraId}", encoraId);

                var headshots = await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterDestinationPath: null, cancellationToken).ConfigureAwait(false);

                var albumTitleFormat = Plugin.Instance?.Configuration?.AudioAlbumTitleFormat ?? "{tour} - {date}";
                var albumName = EncoraAudioTitleFormatter.FormatAlbumTitle(albumTitleFormat, recording, options.DateReplaceChar);

                var artistTitleFormat = Plugin.Instance?.Configuration?.AudioArtistTitleFormat ?? "{show}";
                var artistName = EncoraTitleFormatter.FormatShowTitle(artistTitleFormat, recording);

                var trackTitleFormat = Plugin.Instance?.Configuration?.AudioTrackTitleFormat ?? "Act {act}";
                var match = Regex.Match(info.Path, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
                var encoraTrackName = match.Success ? EncoraAudioTitleFormatter.FormatTrackTitle(trackTitleFormat, match.Groups[1].Value) : albumName;
                var encoraTrackIndex = match.Success && int.TryParse(match.Groups[1].Value, out var actNumber) ? actNumber : 1;

                // SongInfo.Name/IndexNumber reflect whatever Jellyfin's local tag probe (e.g. ID3) already
                // found for this file, since that pass runs before remote providers. Keep it if present,
                // rather than overwriting a real embedded title/track-number with our own guess.
                var respectEmbeddedTags = Plugin.Instance?.Configuration?.AudioRespectEmbeddedTags ?? true;
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(info.Path);
                var hasEmbeddedTitle = respectEmbeddedTags && !string.IsNullOrWhiteSpace(info.Name) &&
                    !string.Equals(info.Name, fileNameWithoutExt, StringComparison.OrdinalIgnoreCase);
                var hasEmbeddedTrackNumber = respectEmbeddedTags && info.IndexNumber.HasValue;

                var track = new Audio
                {
                    Name = hasEmbeddedTitle ? info.Name : encoraTrackName,
                    IndexNumber = hasEmbeddedTrackNumber ? info.IndexNumber : encoraTrackIndex,
                    Artists = new[] { artistName },
                    AlbumArtists = new[] { artistName },
                    Album = albumName,
                };

                EncoraRecordingApplier.ApplyRecordingFields(track, _libraryManager, info.Path, recording, encoraId, options, _logger);
                EncoraRecordingApplier.ApplyNftRating(track, recording.Nft, options.IncludeNftTag);

                result.HasMetadata = true;
                result.Item = track;

                if (recording.Cast != null)
                {
                    EncoraCastMember.MapCastToResult(result, recording.Cast, headshots, recording.Master, shouldAddMasterDirector: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Encora] Error while fetching track metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
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
