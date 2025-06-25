using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides metadata for movies from the Encora API.
    /// </summary>
    public class EncoraMovieMetadataProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraMovieMetadataProvider> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraMovieMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        public EncoraMovieMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraMovieMetadataProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
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
        /// Gets search results for movies.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for a movie.
        /// </summary>
        /// <param name="info">The movie information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();

            if (string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            var encoraId = ExtractEncoraId(info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return result;
            }

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try
            {
                var response = await client.GetAsync($"https://encora.it/api/recording/{encoraId}", cancellationToken).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var recording = JsonSerializer.Deserialize<EncoraRecording>(json, JsonOptions);

                _logger.LogInformation("[Encora] Response: {Response}", json);

                if (recording != null)
                {
                    _logger.LogInformation("[Encora] ✅ Successfully fetched metadata from Encora for ID {EncoraId}", encoraId);
                    _logger.LogInformation("[Encora] Show: {Show}, Tour: {Tour}, Date: {Date}, Master: {Master}", recording.Show, recording.Tour, recording.Date?.FullDate, recording.Master);

                    var titleFormat = Plugin.Instance?.Configuration?.TitleFormat ?? "{show}";

                    var movie = new Movie
                    {
                        Name = FormatTitle(titleFormat, recording),
                        Overview = recording.Metadata?.ShowDescription ?? recording.Notes ?? "Fetched from Encora.it",
                        PremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null,
                        ProductionYear = DateTime.TryParse(recording.Date?.FullDate, out var yearDate) ? yearDate.Year : 0,
                        OriginalTitle = recording.Show,
                        SortName = recording.Show
                    };

                    // StageMedia API request
                    var showId = recording.Metadata?.ShowId;
                    var actorIds = recording.Cast?
                        .Where(c => c.Performer?.Id != null)
                        .Select(c => c.Performer!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ToArray() ?? Array.Empty<string>();
                    var actorIdsParam = string.Join(",", actorIds);
                    var stageMediaApiUrl = $"https://stagemedia.me/api/images?show_id={showId}&actor_ids={actorIdsParam}";

                    _logger.LogInformation("[Encora] StageMedia request: {StageMediaApiUrl}", stageMediaApiUrl);

                    StageMediaImages? images = null;
                    try
                    {
                        var stageMediaApiKey = Plugin.Instance?.Configuration?.StageMediaAPIKey;
                        var mediaDbClient = _httpClientFactory.CreateClient();
                        mediaDbClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stageMediaApiKey);
                        mediaDbClient.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinPlugin/1.0");

                        var mediaDbResponse = await mediaDbClient.GetAsync(stageMediaApiUrl, cancellationToken).ConfigureAwait(false);

                        _logger.LogInformation("[Encora] StageMedia response status: {StatusCode}", mediaDbResponse.StatusCode);
                        _logger.LogInformation("[Encora] StageMedia response Json: {Json}", await mediaDbResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

                        mediaDbResponse.EnsureSuccessStatusCode();
                        var mediaDbJson = await mediaDbResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        images = JsonSerializer.Deserialize<StageMediaImages>(mediaDbJson, JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Encora] Failed to fetch images from StageMedia API.");
                    }

                    // Prepare headshot dictionary for cast mapping
                    Dictionary<string, string>? headshots = null;
                    if (images?.Performers != null)
                    {
                        headshots = images.Performers
                            .Where(p => !string.IsNullOrWhiteSpace(p.Url))
                            .ToDictionary(
                                p => p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                p => p.Url!);
                    }

                    // Set genres from metadata
                    if (recording.Metadata != null)
                    {
                        if (!string.IsNullOrWhiteSpace(recording.Metadata.RecordingType))
                        {
                            movie.AddGenre(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.RecordingType));
                        }

                        if (!string.IsNullOrWhiteSpace(recording.Metadata.AmountRecorded))
                        {
                            movie.AddGenre(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.AmountRecorded));
                        }

                        if (recording.Metadata.BootCampRecommended)
                        {
                            movie.AddGenre("Boot Camp");
                        }

                        if (recording.Metadata.HasSubtitles)
                        {
                            movie.AddGenre("Subtitled");
                        }

                        if (recording.Metadata.IsConcert)
                        {
                            movie.AddGenre("Concert");
                        }
                    }

                    // Set custom rating based on NFT status
                    if (recording.Nft != null)
                    {
                        if (recording.Nft.NftForever)
                        {
                            movie.OfficialRating = "NFT Forever";
                        }
                        else if (!string.IsNullOrWhiteSpace(recording.Nft.NftDate) &&
                                 DateTime.TryParse(recording.Nft.NftDate, out var nftDateValue) &&
                                 nftDateValue > DateTime.UtcNow)
                        {
                            movie.OfficialRating = "NFT";
                        }
                        else
                        {
                            movie.OfficialRating = string.Empty;
                        }
                    }

                    // Add 'Venue' from Encora to the items studio
                    if (!string.IsNullOrWhiteSpace(recording.Metadata?.Venue))
                    {
                        movie.AddStudio(recording.Metadata.Venue);
                    }

                    // Set posters from StageMedia API response
                    if (images?.Posters != null)
                    {
                        foreach (var posterUrl in images.Posters.Where(p => !string.IsNullOrWhiteSpace(p)))
                        {
                            result.RemoteImages.Add((posterUrl, ImageType.Primary));
                        }
                    }

                    result.HasMetadata = true;
                    result.Item = movie;

                    if (recording.Cast != null)
                    {
                        EncoraCastMember.MapCastToResult(result, recording.Cast, headshots);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Encora] ❌ Failed to fetch metadata from Encora for ID {EncoraId}", encoraId);
            }

            return result;
        }

        /// <summary>
        /// Extracts the Encora ID from the given path.
        /// </summary>
        /// <param name="path">The file path.</param>
        /// <returns>The Encora ID if found; otherwise, null.</returns>
        private string? ExtractEncoraId(string path)
        {
            var match = Regex.Match(path, @"{e-(\d+)}", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        ///     Formats the title to the configured format using the recording data.
        /// </summary>
        /// <param name="format">The format string used to generate the title. It may contain placeholders like {show}, {date}, etc.</param>
        /// <param name="recording">The recording object containing data to populate the placeholders in the format string.</param>
        private string FormatTitle(string format, EncoraRecording recording)
        {
            // Prepare date variables
            string? dateIso = recording.Date?.FullDate;
            string? dateNumeric = dateIso?.Replace("-", string.Empty, StringComparison.Ordinal);
            string? dateUsa = null;
            if (DateTime.TryParse(dateIso, out var dt))
            {
                dateUsa = dt.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
            }

            var variables = new Dictionary<string, string?>
            {
                ["show"] = recording.Show,
                ["date"] = dateIso,
                ["date_iso"] = dateIso,
                ["date_numeric"] = dateNumeric,
                ["date_usa"] = dateUsa,
                ["tour"] = recording.Tour,
                ["master"] = recording.Master
            };

            foreach (var kvp in variables)
            {
                format = format.Replace("{" + kvp.Key + "}", kvp.Value ?? string.Empty, StringComparison.Ordinal);
            }

            return format.Trim();
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
