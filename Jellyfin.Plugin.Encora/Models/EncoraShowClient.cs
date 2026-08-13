using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Searches for and fetches shows (independent of any single recording) from the Encora API, used to
    /// power Jellyfin's manual "Identify" flow for Series.
    /// </summary>
    public static class EncoraShowClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Searches Encora for shows matching <paramref name="query"/>.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the Encora HTTP client.</param>
        /// <param name="logger">Logger for diagnostics, used by the rate limiter.</param>
        /// <param name="apiKey">The Encora API key.</param>
        /// <param name="query">The show name to search for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The matching shows, or null if the request failed.</returns>
        public static async Task<List<EncoraShowSearchResult>?> SearchShowsAsync(IHttpClientFactory httpClientFactory, ILogger logger, string apiKey, string query, CancellationToken cancellationToken)
        {
            var client = CreateClient(httpClientFactory, apiKey);
            var url = $"https://encora.it/api/shows/search?q={Uri.EscapeDataString(query)}";

            await EncoraRateLimiter.WaitAsync(logger, cancellationToken).ConfigureAwait(false);
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            EncoraRateLimiter.UpdateFromResponse(response);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    logger.LogWarning("[Encora] Rate limited (429) searching shows for {Query}", query);
                }

                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<EncoraShowSearchResult>>(json, JsonOptions);
        }

        /// <summary>
        /// Fetches a single show by its Encora show ID.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the Encora HTTP client.</param>
        /// <param name="logger">Logger for diagnostics, used by the rate limiter.</param>
        /// <param name="apiKey">The Encora API key.</param>
        /// <param name="showId">The Encora show ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The show, or null if the request failed or returned nothing.</returns>
        public static async Task<EncoraShow?> FetchShowAsync(IHttpClientFactory httpClientFactory, ILogger logger, string apiKey, string showId, CancellationToken cancellationToken)
        {
            var client = CreateClient(httpClientFactory, apiKey);

            await EncoraRateLimiter.WaitAsync(logger, cancellationToken).ConfigureAwait(false);
            var response = await client.GetAsync($"https://encora.it/api/shows/{showId}", cancellationToken).ConfigureAwait(false);
            EncoraRateLimiter.UpdateFromResponse(response);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    logger.LogWarning("[Encora] Rate limited (429) fetching show {ShowId}", showId);
                }

                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EncoraShow>(json, JsonOptions);
        }

        private static HttpClient CreateClient(IHttpClientFactory httpClientFactory, string apiKey)
        {
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");
            return client;
        }
    }
}
