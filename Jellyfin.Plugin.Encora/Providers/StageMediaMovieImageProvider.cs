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
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides remote image support for movies using the StageMedia API.
    /// </summary>
    public class StageMediaMovieImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StageMediaMovieImageProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="StageMediaMovieImageProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance.</param>
        public StageMediaMovieImageProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<StageMediaMovieImageProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "StageMedia";

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Movie;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (item is not Movie movie)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            if (!movie.ProviderIds.TryGetValue("StageMediaShowId", out var showId) || string.IsNullOrWhiteSpace(showId))
            {
                _logger.LogError("[Encora] [StageMedia] Movie {MovieName} does not have a valid StageMedia Show ID.", movie.Name);
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinPlugin/1.0");

            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
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

            _logger.LogDebug("[Encora] [StageMedia] Posters: {Posters}", string.Join(", ", remoteImages.Select(i => i.Url)));

            return remoteImages;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }
}
