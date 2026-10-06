using System;
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
    /// Provides remote image support for Movies and Series using the StageMedia API.
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
        public bool Supports(BaseItem item) => item is Movie || item is Series;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (item is Season)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var stack = Environment.StackTrace;
            var isManualSearch = stack.Contains("RemoteImageController", StringComparison.OrdinalIgnoreCase);

            // Jellyfin calls IRemoteImageProvider.GetImages directly for its own automatic image refresh/fetch
            // flows (configured in LibraryOptions ImageFetchers). If this returns candidate posters when an item
            // already has a primary image, has a local poster file (e.g. folder.jpg), or has its poster locked,
            // Jellyfin's automatic fetcher downloads the first candidate into metadata cache and silently overwrites
            // the user's custom poster! We must strictly guard against this for automated flows, while still allowing
            // the user to manually search for images in the "Edit Images" UI.
            if (!isManualSearch && (EncoraRecordingApplier.IsPosterLocked(item) ||
                item.HasImage(ImageType.Primary, 0) ||
                EncoraRecordingApplier.HasLocalPosterFile(item.Path)))
            {
                _logger.LogInformation("[Encora] [StageMedia] Skipping automated GetImages for {ItemType} '{ItemName}' - poster is locked, already exists, or local poster file is present", item.GetType().Name, item.Name);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            _logger.LogInformation("[Encora] [StageMedia] GetImages called for {ItemType} '{ItemName}' (ManualSearch={IsManual})", item.GetType().Name, item.Name, isManualSearch);

            _logger.LogInformation("[Encora] [StageMedia] GetImages called for {ItemType} '{ItemName}' ({ItemId})", item.GetType().Name, item.Name, item.Id);

            if (!item.ProviderIds.TryGetValue("StageMediaShowId", out var showId) || string.IsNullOrWhiteSpace(showId))
            {
                _logger.LogError("[Encora] [StageMedia] {ItemName} does not have a valid StageMedia Show ID.", item.Name);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            if (!StageMediaCircuitBreaker.IsAvailable(_logger))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var url = $"https://stagemedia.me/api/images?show_id={showId}&actor_ids=1";
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

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
                StageMediaCircuitBreaker.RecordResponse(_logger, response);
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
                StageMediaCircuitBreaker.RecordException(_logger, ex);
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
