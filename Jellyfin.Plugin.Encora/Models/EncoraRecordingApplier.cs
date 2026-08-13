using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Applies fields from a single fetched <see cref="EncoraRecording"/> onto a metadata item.
    /// Shared between Movie and TV Episode matching, since both represent one specific dated recording.
    /// </summary>
    public static class EncoraRecordingApplier
    {
        /// <summary>
        /// Poster used when StageMedia has no poster for a show, so items don't end up with no artwork at all.
        /// </summary>
        public const string FallbackPosterUrl = "https://i.ibb.co/vxtyjRwn/image-psd-3.png";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly ConcurrentDictionary<string, Lazy<Task<EncoraRecording?>>> RecordingCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> RecordingCacheTimestamps = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RecordingCacheTtl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Builds the Overview text for a recording (show description + master/general notes).
        /// </summary>
        /// <param name="recording">The recording.</param>
        /// <returns>The built description, or a fallback string if nothing was available.</returns>
        public static string BuildDescription(EncoraRecording recording)
        {
            var description = recording.Metadata?.ShowDescription;

            if (!string.IsNullOrEmpty(recording.MasterNotes))
            {
                description += $"\n\nMaster Notes: \n{recording.MasterNotes}";
            }

            if (!string.IsNullOrEmpty(recording.Notes))
            {
                description += $"\n\nGeneral Notes: \n{recording.Notes}";
            }

            description = description?.TrimStart('\n').Trim();
            return string.IsNullOrWhiteSpace(description) ? "No Notes" : description;
        }

        /// <summary>
        /// Builds the Overview text for a recording per the configured <c>OverviewSource</c> ("description_notes",
        /// "description", "notes", or "none"). Returns null for "none" (Overview should be left untouched).
        /// </summary>
        /// <param name="recording">The recording.</param>
        /// <param name="source">The configured Overview source.</param>
        /// <returns>The built description, or null if the Overview should not be written.</returns>
        public static string? BuildOverviewText(EncoraRecording recording, string source)
        {
            switch (source)
            {
                case "none":
                    return null;
                case "description":
                    var description = recording.Metadata?.ShowDescription?.Trim();
                    return string.IsNullOrWhiteSpace(description) ? "No Notes" : description;
                case "notes":
                    var notes = string.Join(
                        "\n\n",
                        new[]
                        {
                            !string.IsNullOrEmpty(recording.MasterNotes) ? $"Master Notes: \n{recording.MasterNotes}" : null,
                            !string.IsNullOrEmpty(recording.Notes) ? $"General Notes: \n{recording.Notes}" : null
                        }.Where(s => s != null));
                    return string.IsNullOrWhiteSpace(notes) ? "No Notes" : notes;
                case "description_notes":
                default:
                    return BuildDescription(recording);
            }
        }

        /// <summary>
        /// Resolves a single-value field mapping (Tagline/Studio/Production Location) against a recording,
        /// per the configured source key ("tour", "venue", "city", "master", "recording_type", or "none"/unrecognized).
        /// </summary>
        /// <param name="recording">The recording.</param>
        /// <param name="source">The configured source key.</param>
        /// <returns>The resolved value, or null if unmapped/unavailable.</returns>
        public static string? ResolveFieldSource(EncoraRecording recording, string source)
        {
            return source switch
            {
                "tour" => recording.Tour,
                "venue" => recording.Metadata?.Venue,
                "city" => recording.Metadata?.City,
                "master" => recording.Master,
                "recording_type" => recording.Metadata?.RecordingType,
                _ => null
            };
        }

        /// <summary>
        /// Applies the recording-level fields (Overview, PremiereDate, ProductionYear, Tagline,
        /// ProductionLocations, Genres, Studios, ProviderIds) that are true of one specific dated
        /// performance - used by both Movie and Episode. Each optional piece is gated by
        /// <paramref name="options"/> so Movie and TV libraries can be configured independently.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="item">The item to populate.</param>
        /// <param name="libraryManager">Used by the Overview guard to look up the currently-saved item.</param>
        /// <param name="path">The item's file path (used for the Overview guard).</param>
        /// <param name="recording">The fetched recording.</param>
        /// <param name="encoraId">The Encora recording ID.</param>
        /// <param name="options">The per-library-type toggles controlling which fields get applied.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        public static void ApplyRecordingFields<T>(T item, ILibraryManager libraryManager, string path, EncoraRecording recording, string encoraId, EncoraApplyOptions options, ILogger logger)
            where T : BaseItem
        {
            var overviewText = BuildOverviewText(recording, options.OverviewSource);
            if (overviewText != null)
            {
                EncoraOverviewGuard.ApplyOverview(item, libraryManager, path, isFolder: false, overviewText, options.PreserveManualDescriptionEdits, logger, path);
            }

            item.PremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null;
            item.ProductionYear = DateTime.TryParse(recording.Date?.FullDate, out var yearDate) ? yearDate.Year : 0;
            item.SetProviderId("EncoraRecordingId", encoraId);

            var taglineValue = ResolveFieldSource(recording, options.TaglineSource);
            if (!string.IsNullOrWhiteSpace(taglineValue))
            {
                item.Tagline = taglineValue;
            }

            if (recording.Metadata != null)
            {
                item.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));

                var locationValue = ResolveFieldSource(recording, options.ProductionLocationSource);
                if (!string.IsNullOrWhiteSpace(locationValue))
                {
                    item.ProductionLocations = new[] { locationValue };
                }

                if (options.IncludeGenreTags)
                {
                    if (!string.IsNullOrWhiteSpace(recording.Metadata.RecordingType))
                    {
                        item.AddGenre(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.RecordingType));
                    }

                    if (!string.IsNullOrWhiteSpace(recording.Metadata.AmountRecorded))
                    {
                        item.AddGenre(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.AmountRecorded));
                    }

                    if (recording.Metadata.BootCampRecommended)
                    {
                        item.AddGenre("Boot Camp");
                    }

                    if (recording.Metadata.HasSubtitles)
                    {
                        item.AddGenre("Subtitled");
                    }

                    if (recording.Metadata.IsConcert)
                    {
                        item.AddGenre("Concert");
                    }
                }

                var studioValue = ResolveFieldSource(recording, options.StudioSource);
                if (!string.IsNullOrWhiteSpace(studioValue))
                {
                    item.AddStudio(studioValue);
                }
            }
        }

        /// <summary>
        /// Sets OfficialRating and the "NFT" tag based on the recording's NFT status, if enabled.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="item">The item to populate.</param>
        /// <param name="nft">The recording's NFT info, if any.</param>
        /// <param name="includeNftTag">Whether NFT tagging is enabled.</param>
        public static void ApplyNftRating<T>(T item, EncoraNft? nft, bool includeNftTag)
            where T : BaseItem
        {
            if (nft == null || !includeNftTag)
            {
                return;
            }

            if (nft.NftForever)
            {
                item.OfficialRating = "NFT Forever";
                item.Tags = new[] { "NFT" };
            }
            else if (!string.IsNullOrWhiteSpace(nft.NftDate) &&
                     DateTime.TryParse(nft.NftDate, out var nftDateValue) &&
                     nftDateValue > DateTime.UtcNow)
            {
                item.OfficialRating = "NFT";
                item.Tags = new[] { "NFT" };
            }
            else
            {
                item.OfficialRating = string.Empty;
            }
        }

        /// <summary>
        /// Fetches StageMedia poster/headshot images for a recording's show, optionally downloading the
        /// poster to <paramref name="posterDestinationPath"/> if it doesn't already exist.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the StageMedia HTTP client.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="recording">The recording, used for ShowId and cast actor IDs.</param>
        /// <param name="posterDestinationPath">Where to write the poster, or null to skip poster download.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The performer headshots returned by StageMedia, if any.</returns>
        public static Task<Collection<StageMediaPerformer>> FetchStageMediaImagesAsync(IHttpClientFactory httpClientFactory, ILogger logger, EncoraRecording recording, string? posterDestinationPath, CancellationToken cancellationToken)
        {
            if (recording.Metadata?.ShowId is not > 0)
            {
                return Task.FromResult(new Collection<StageMediaPerformer>());
            }

            var actorIds = recording.Cast?.Select(c => c.Performer?.Id.ToString(CultureInfo.InvariantCulture));
            return FetchStageMediaImagesAsync(httpClientFactory, logger, recording.Metadata.ShowId, actorIds, posterDestinationPath, cancellationToken);
        }

        /// <summary>
        /// Fetches StageMedia poster/headshot images for a show, optionally downloading the poster to
        /// <paramref name="posterDestinationPath"/> if it doesn't already exist. Unlike the
        /// <see cref="EncoraRecording"/> overload, this doesn't require any specific recording to be
        /// known - used when a Series has been manually identified/matched directly to an Encora show.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the StageMedia HTTP client.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="showId">The StageMedia/Encora show ID.</param>
        /// <param name="actorIds">Cast actor IDs to request headshots for, or null/empty to fetch just the poster pool.</param>
        /// <param name="posterDestinationPath">Where to write the poster, or null to skip poster download.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The performer headshots returned by StageMedia, if any.</returns>
        public static async Task<Collection<StageMediaPerformer>> FetchStageMediaImagesAsync(IHttpClientFactory httpClientFactory, ILogger logger, int showId, IEnumerable<string?>? actorIds, string? posterDestinationPath, CancellationToken cancellationToken)
        {
            var headshots = new Collection<StageMediaPerformer>();
            var stageMediaApiKey = Plugin.Instance?.Configuration?.StageMediaAPIKey;

            if (string.IsNullOrWhiteSpace(stageMediaApiKey))
            {
                return headshots;
            }

            var actorIdsList = actorIds?.ToArray();
            var actorIdsParam = actorIdsList != null && actorIdsList.Length > 0
                ? string.Join(",", actorIdsList) : "1";

            try
            {
                logger.LogInformation("[Encora] Fetching StageMedia images for ShowId {ShowId} with ActorIds {ActorIds}", showId, actorIdsParam);
                var stageMediaUrl = $"https://stagemedia.me/api/images?show_id={showId}&actor_ids={actorIdsParam}";
                var stageMediaClient = httpClientFactory.CreateClient();
                stageMediaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stageMediaApiKey);
                stageMediaClient.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

                var stageMediaResponse = await stageMediaClient.GetAsync(stageMediaUrl, cancellationToken).ConfigureAwait(false);
                stageMediaResponse.EnsureSuccessStatusCode();
                var stageMediaJson = await stageMediaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var images = JsonSerializer.Deserialize<StageMediaImages>(stageMediaJson);

                if (!string.IsNullOrWhiteSpace(posterDestinationPath) && !File.Exists(posterDestinationPath))
                {
                    string posterUrl;
                    HttpClient posterClient;

                    if (images?.Posters != null && images.Posters.Count > 0)
                    {
                        posterUrl = images.Posters[0];
                        posterClient = stageMediaClient;
                    }
                    else
                    {
                        posterUrl = FallbackPosterUrl;
                        posterClient = httpClientFactory.CreateClient();
                    }

                    var posterResponse = await posterClient.GetAsync(posterUrl, cancellationToken).ConfigureAwait(false);
                    posterResponse.EnsureSuccessStatusCode();
                    var posterBytes = await posterResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(posterDestinationPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }

                if (images?.Performers != null && images.Performers.Count > 0)
                {
                    headshots = images.Performers;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Encora] Could not download and save StageMedia poster for ShowId {ShowId}", showId);
            }

            return headshots;
        }

        /// <summary>
        /// Fetches the list of subtitles Encora has available for a recording. Used by
        /// <see cref="Providers.EncoraSubtitleProvider"/> so subtitles are exposed through Jellyfin's
        /// standard subtitle search/download flow rather than downloaded automatically during a metadata
        /// refresh.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the Encora HTTP client.</param>
        /// <param name="logger">Logger for diagnostics, used by the rate limiter.</param>
        /// <param name="encoraId">The Encora recording ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The subtitles Encora has for the recording, or null if none were returned.</returns>
        public static async Task<List<EncoraSubtitles>?> FetchSubtitlesAsync(IHttpClientFactory httpClientFactory, ILogger logger, string encoraId, CancellationToken cancellationToken)
        {
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Plugin.Instance?.Configuration?.EncoraAPIKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

            var subtitlesUrl = $"https://encora.it/api/recording/{encoraId}/subtitles";
            await EncoraRateLimiter.WaitAsync(logger, cancellationToken).ConfigureAwait(false);
            var subtitlesResponse = await client.GetAsync(subtitlesUrl, cancellationToken).ConfigureAwait(false);
            EncoraRateLimiter.UpdateFromResponse(subtitlesResponse);
            subtitlesResponse.EnsureSuccessStatusCode();
            var subtitlesJson = await subtitlesResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<EncoraSubtitles>>(subtitlesJson);
        }

        /// <summary>
        /// Fetches and deserializes a recording from the Encora API. Results are cached (and concurrent
        /// requests for the same ID are coalesced into a single HTTP call) for a short time, since Movie,
        /// Series, Season, Episode, Artist, Album and every Track under it can all resolve to the identical
        /// Encora ID (e.g. "ID in folder name" convention) and would otherwise each fire their own redundant
        /// request - which, at Music-library track counts, meaningfully increases the odds that some subset
        /// hits a transient failure or rate limit while siblings succeed, leaving those items to silently
        /// fall back to inconsistent local/embedded metadata (e.g. a mismatched embedded ID3 artist tag).
        /// </summary>
        /// <param name="httpClientFactory">Used to create the Encora HTTP client.</param>
        /// <param name="logger">Logger for diagnostics, used by the rate limiter.</param>
        /// <param name="apiKey">The Encora API key.</param>
        /// <param name="encoraId">The recording ID to fetch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The deserialized recording, or null if the request failed or returned nothing.</returns>
        public static async Task<EncoraRecording?> FetchRecordingAsync(IHttpClientFactory httpClientFactory, ILogger logger, string apiKey, string encoraId, CancellationToken cancellationToken)
        {
            if (RecordingCacheTimestamps.TryGetValue(encoraId, out var cachedAt) && DateTime.UtcNow - cachedAt >= RecordingCacheTtl)
            {
                RecordingCache.TryRemove(encoraId, out _);
                RecordingCacheTimestamps.TryRemove(encoraId, out _);
            }

            var lazy = RecordingCache.GetOrAdd(
                encoraId,
                _ => new Lazy<Task<EncoraRecording?>>(() => FetchRecordingUncachedAsync(httpClientFactory, logger, apiKey, encoraId, cancellationToken)));

            var recording = await lazy.Value.ConfigureAwait(false);

            if (recording == null)
            {
                // Don't let a failed fetch "poison" the cache for the rest of the TTL - let the next caller retry.
                RecordingCache.TryRemove(encoraId, out _);
                RecordingCacheTimestamps.TryRemove(encoraId, out _);
            }
            else
            {
                RecordingCacheTimestamps[encoraId] = DateTime.UtcNow;
            }

            return recording;
        }

        private static async Task<EncoraRecording?> FetchRecordingUncachedAsync(IHttpClientFactory httpClientFactory, ILogger logger, string apiKey, string encoraId, CancellationToken cancellationToken)
        {
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

            await EncoraRateLimiter.WaitAsync(logger, cancellationToken).ConfigureAwait(false);
            var response = await client.GetAsync($"https://encora.it/api/recording/{encoraId}", cancellationToken).ConfigureAwait(false);
            EncoraRateLimiter.UpdateFromResponse(response);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    logger.LogWarning("[Encora] Rate limited (429) fetching recording {EncoraId}", encoraId);
                }

                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EncoraRecording>(json, JsonOptions);
        }
    }
}
