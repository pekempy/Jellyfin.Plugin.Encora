using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.ScheduledTasks
{
    /// <summary>
    /// Refreshes the local Encora collection cache by fetching the user's full collection from
    /// <c>/api/collection</c>. The cache is used by <see cref="EncoraRecordingApplier"/> to resolve
    /// recording metadata without making per-item API calls during library scans, dramatically
    /// reducing rate-limit pressure.
    /// </summary>
    public class EncoraCollectionCacheRefreshTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraCollectionCacheRefreshTask> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraCollectionCacheRefreshTask"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        public EncoraCollectionCacheRefreshTask(IHttpClientFactory httpClientFactory, ILogger<EncoraCollectionCacheRefreshTask> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Refresh Encora Collection Cache";

        /// <inheritdoc />
        public string Key => "EncoraCollectionCacheRefresh";

        /// <inheritdoc />
        public string Description => "Fetches the full Encora collection into a local cache so library scans need zero per-item API calls for already-collected recordings.";

        /// <inheritdoc />
        public string Category => "Encora";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => (Plugin.Instance?.Configuration?.CollectionCacheRefreshIntervalHours ?? 0) > 0;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var hours = Plugin.Instance?.Configuration?.CollectionCacheRefreshIntervalHours ?? 24;
            if (hours <= 0)
            {
                yield break;
            }

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks,
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await RefreshAsync(_httpClientFactory, _logger, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Shared implementation — callable from other tasks (e.g. <see cref="EncoraRefreshTask"/>)
        /// so the cache is always warm before a metadata re-refresh runs.
        /// </summary>
        /// <param name="httpClientFactory">Factory used to create the HTTP client.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="progress">Optional progress reporter (0-100).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task RefreshAsync(
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("[Encora] Collection cache refresh skipped: no API key configured");
                return;
            }

            logger.LogInformation("[Encora] 📥 Starting Encora collection cache refresh");

            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

            var all = new List<EncoraRecording>();
            var page = 1;
            int? totalPages = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await EncoraRateLimiter.WaitAsync(logger, cancellationToken).ConfigureAwait(false);

                var url = $"https://encora.it/api/collection?per_page=100&page={page}";
                HttpResponseMessage response;
                try
                {
                    response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] Collection cache refresh: HTTP error on page {Page}", page);
                    break;
                }

                EncoraRateLimiter.UpdateFromResponse(response);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("[Encora] Collection cache refresh: non-success {Status} on page {Page}", response.StatusCode, page);
                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                CollectionPage? pageData;
                try
                {
                    pageData = JsonSerializer.Deserialize<CollectionPage>(json, _jsonOptions);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] Collection cache refresh: failed to deserialise page {Page}", page);
                    break;
                }

                if (pageData?.Data == null || pageData.Data.Count == 0)
                {
                    break;
                }

                foreach (var item in pageData.Data)
                {
                    if (item.Recording != null)
                    {
                        all.Add(item.Recording);
                    }
                }

                // Work out total pages from the meta on the first page
                if (totalPages == null && pageData.Meta != null)
                {
                    totalPages = pageData.Meta.LastPage;
                }

                var pct = totalPages.HasValue && totalPages.Value > 0
                    ? 100.0 * page / totalPages.Value
                    : 50.0;
                progress?.Report(Math.Min(pct, 99.0));

                logger.LogInformation(
                    "[Encora] Collection cache: fetched page {Page}/{Total} ({Count} recordings so far)",
                    page,
                    totalPages?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                    all.Count);

                if (pageData.NextPageUrl == null)
                {
                    break;
                }

                page++;
            }

            if (all.Count > 0)
            {
                EncoraCollectionCache.Populate(all);
                logger.LogInformation("[Encora] ✅ Collection cache refreshed: {Count} recordings cached", all.Count);
            }
            else
            {
                logger.LogWarning("[Encora] Collection cache refresh completed but returned 0 recordings — cache not updated");
            }

            progress?.Report(100.0);
        }

        // Minimal deserialisation wrappers for the /api/collection response
        private sealed class CollectionPage
        {
            [JsonPropertyName("data")]
            public List<CollectionItem>? Data { get; set; }

            [JsonPropertyName("next_page_url")]
            public string? NextPageUrl { get; set; }

            [JsonPropertyName("meta")]
            public CollectionMeta? Meta { get; set; }
        }

        private sealed class CollectionItem
        {
            [JsonPropertyName("recording")]
            public EncoraRecording? Recording { get; set; }
        }

        private sealed class CollectionMeta
        {
            [JsonPropertyName("last_page")]
            public int LastPage { get; set; }
        }
    }
}
