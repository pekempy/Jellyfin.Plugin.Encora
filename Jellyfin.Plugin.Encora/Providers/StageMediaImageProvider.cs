using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides remote image support for Movies, Series and Seasons using the StageMedia API. StageMedia's
    /// image pool is keyed by show, not by a specific recording/tour/date, so Series and Season share the
    /// exact same searchable poster pool as their underlying Movies do.
    /// </summary>
    public class StageMediaImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StageMediaImageProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="StageMediaImageProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance.</param>
        public StageMediaImageProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<StageMediaImageProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "StageMedia";

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Movie || item is Series || item is Season;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            // GetImages backs Jellyfin's manual "Identify -> search for images" flow as well as its own
            // automatic per-library image-fetcher pipeline - there's no signal in this interface to tell
            // those two callers apart. Refusing candidates here for a locked poster or an item that
            // already has one would also refuse them for the manual flow, where "already has a poster" /
            // "poster is locked" is exactly why an admin opened search in the first place. Automated
            // silent overwrites are guarded separately and unconditionally in EncoraSeasonMetadataProvider
            // / EncoraSeriesMetadataProvider / EncoraMovieMetadataProvider's own direct StageMedia fetch,
            // which checks IsPosterLocked/HasImage before ever calling FetchStageMediaImagesAsync - that
            // guard is untouched. This method should always return every real candidate.

            _logger.LogInformation("[Encora] [StageMedia] GetImages called for {ItemType} '{ItemName}' ({ItemId})", item.GetType().Name, item.Name, item.Id);

            if (!item.ProviderIds.TryGetValue("StageMediaShowId", out var showId) || string.IsNullOrWhiteSpace(showId))
            {
                _logger.LogError("[Encora] [StageMedia] {ItemName} does not have a valid StageMedia Show ID.", item.Name);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var url = $"https://stagemedia.me/api/images?show_id={showId}&actor_ids=1";
            var client = _httpClientFactory.CreateClient();

            var stageMediaApiKey = Plugin.Instance?.Configuration?.StageMediaAPIKey;
            if (string.IsNullOrWhiteSpace(stageMediaApiKey))
            {
                _logger.LogError("[Encora] [StageMedia] StageMedia API key is missing from configuration.");
                return Enumerable.Empty<RemoteImageInfo>();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stageMediaApiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

            try
            {
                var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[Encora] [StageMedia] {Url} -> HTTP {StatusCode}", url, (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var images = JsonSerializer.Deserialize<StageMediaImages>(json);

                var remoteImages = new List<RemoteImageInfo>();

                if (images?.Posters != null)
                {
                    foreach (var posterUrl in images.Posters.Where(p => !string.IsNullOrWhiteSpace(p)))
                    {
                        remoteImages.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Url = posterUrl,
                            Type = ImageType.Primary
                        });
                    }
                }

                _logger.LogInformation("[Encora] [StageMedia] Returning {Count} candidate poster(s) for {ItemName}", remoteImages.Count, item.Name);
                return remoteImages;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "[Encora] [StageMedia] GetImages failed for {ItemName}", item.Name);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }
}
