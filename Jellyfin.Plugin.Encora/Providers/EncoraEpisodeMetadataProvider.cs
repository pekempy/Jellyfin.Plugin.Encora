using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Episode-level metadata for TV libraries from the Encora API. An Episode represents one
    /// specific dated recording, matched by the same Encora ID convention as Movie libraries.
    /// </summary>
    public class EncoraEpisodeMetadataProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraEpisodeMetadataProvider> _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraEpisodeMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="mediaEncoder">The media encoder used for processing media files.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraEpisodeMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraEpisodeMetadataProvider> logger, IMediaEncoder mediaEncoder, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraEpisodeMetadataProvider initialized.");
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
        /// Gets search results for episodes.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for an episode.
        /// </summary>
        /// <param name="info">The episode information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();

            if (info == null || string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                _logger.LogInformation("[Encora] TV matching is disabled in plugin settings, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                _logger.LogInformation("[Encora] Path {Path} is not in a scoped TV library, skipping metadata fetch", info.Path);
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

            var episodeDir = Path.GetDirectoryName(info.Path);
            var options = BuildOptions();

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

                var titleFormat = Plugin.Instance?.Configuration?.TvEpisodeTitleFormat ?? "{date}";

                var episode = new Episode
                {
                    Name = FormatEpisodeTitle(titleFormat, recording, info.Path),
                    IndexNumber = EncoraDateHelper.ComputeDateIndexNumber(recording.Date, info.Path),
                    ForcedSortName = EncoraDateHelper.BuildDateSortKey(recording.Date, info.Path),
                };

                EncoraRecordingApplier.ApplyRecordingFields(episode, _libraryManager, info.Path, recording, encoraId, options, _logger);
                EncoraRecordingApplier.ApplyNftRating(episode, recording.Nft, options.IncludeNftTag);

                if (options.DownloadSubtitles && recording.Metadata?.HasSubtitles == true && !string.IsNullOrWhiteSpace(episodeDir))
                {
                    _logger.LogInformation("[Encora] Fetching subtitles for recording {EncoraId}", encoraId);
                    await EncoraRecordingApplier.ApplyRecordingSubtitlesAsync(_httpClientFactory, _logger, episode, encoraId, info.Path, episodeDir, cancellationToken).ConfigureAwait(false);
                }

                result.HasMetadata = true;
                result.Item = episode;

                if (recording.Cast != null)
                {
                    EncoraCastMember.MapCastToResult(result, recording.Cast, headshots, recording.Master, options.AddMasterDirector);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Encora] Error while fetching episode metadata from Encora for ID {EncoraId} for {Path}", encoraId, info.Path);
                return result;
            }

            if (options.GenerateThumbnail)
            {
                await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, episodeDir, info.Path, options.ThumbnailSeekMinPercent, options.ThumbnailSeekMaxPercent).ConfigureAwait(false);
            }

            return result;
        }

        /// <summary>
        /// Builds the apply-options for TV libraries from the plugin's TV-scoped configuration.
        /// </summary>
        /// <returns>The apply options.</returns>
        private static EncoraApplyOptions BuildOptions()
        {
            var config = Plugin.Instance?.Configuration;
            return new EncoraApplyOptions
            {
                DateReplaceChar = config?.TvDateReplaceChar ?? "x",
                AddMasterDirector = config?.TvAddMasterDirector ?? false,
                PreserveManualDescriptionEdits = config?.TvPreserveManualDescriptionEdits ?? true,
                OverviewSource = config?.TvOverviewSource ?? "description_notes",
                StudioSource = config?.TvStudioSource ?? "venue",
                ProductionLocationSource = config?.TvProductionLocationSource ?? "city",
                TaglineSource = config?.TvTaglineSource ?? "tour",
                IncludeGenreTags = config?.TvIncludeGenreTags ?? true,
                IncludeNftTag = config?.TvIncludeNftTag ?? true,
                DownloadSubtitles = config?.TvDownloadSubtitles ?? true,
                FetchPoster = config?.TvFetchPoster ?? true,
                GenerateThumbnail = config?.TvGenerateThumbnail ?? true,
                ThumbnailSeekMinPercent = config?.TvThumbnailSeekMinPercent ?? 15,
                ThumbnailSeekMaxPercent = config?.TvThumbnailSeekMaxPercent ?? 60,
            };
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

        /// <summary>
        /// Formats the episode title from the configured format using the recording data. Unlike Movie
        /// titles, there's no {show}/{tour} placeholder here - those are implicit from the Series/Season -
        /// and any "Act N" suffix is appended to the date variants themselves instead.
        /// </summary>
        /// <param name="format">The format string, e.g. "{date}".</param>
        /// <param name="recording">The recording data.</param>
        /// <param name="path">The episode file path, used to detect an "Act N" suffix.</param>
        /// <returns>The formatted episode title.</returns>
        private string FormatEpisodeTitle(string format, EncoraRecording recording, string path)
        {
            var dateReplaceChar = Plugin.Instance?.Configuration?.TvDateReplaceChar ?? "x";
            var match = Regex.Match(path ?? string.Empty, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
            var actSuffix = match.Success ? match.Groups[1].Value : null;
            var dateVariants = EncoraDateHelper.BuildDateVariants(recording.Date, dateReplaceChar, actSuffix);

            var variables = new Dictionary<string, string?>
            {
                ["date"] = dateVariants.Long,
                ["date_iso"] = dateVariants.Iso,
                ["date_numeric"] = dateVariants.Numeric,
                ["date_usa"] = dateVariants.Usa,
                ["master"] = EncoraTitleFormatter.ResolveMaster(recording.Master),
                ["venue"] = recording.Metadata?.Venue,
                ["city"] = recording.Metadata?.City
            };

            return EncoraTitleFormatter.Format(format, variables);
        }
    }
}
