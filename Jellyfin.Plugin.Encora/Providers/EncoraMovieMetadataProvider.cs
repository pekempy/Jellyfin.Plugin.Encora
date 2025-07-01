using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides metadata for movies from the Encora API.
    /// </summary>
    public class EncoraMovieMetadataProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraMovieMetadataProvider> _logger;
        private readonly IMediaEncoder _mediaEncoder;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraMovieMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="mediaEncoder">The media encoder used for processing media files.</param>
        public EncoraMovieMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraMovieMetadataProvider> logger, IMediaEncoder mediaEncoder)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _mediaEncoder = mediaEncoder;
            _logger.LogInformation("[Encora] ✅ EncoraMovieMetadataProvider initialized.");
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
            _logger.LogInformation("[Encora] Runnin GetMetadata for movie info: {Path}", info?.Path ?? "null");
            var result = new MetadataResult<Movie>();

            if (info == null)
            {
                _logger.LogInformation("[Encora] ❌ Movie info is null, skipping metadata fetch.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(info.Path))
            {
                _logger.LogInformation("[Encora] ❌ No path provided for movie info, skipping metadata fetch. {InfoPath}", info.Path);
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = ExtractEncoraId(info.Path);
            _logger.LogInformation("[Encora] Extracted Encora ID: {EncoraId} from path: {Path}", encoraId, info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found in path: {Path}, falling back to NFO file...", info.Path);
                return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
            }

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");
            var movieDir = Path.GetDirectoryName(info.Path);

            try
            {
                var response = await client.GetAsync($"https://encora.it/api/recording/{encoraId}", cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Encora] Bad response from Encora API, Falling back to NFO metadata for {Path}", info.Path);
                    return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var recording = JsonSerializer.Deserialize<EncoraRecording>(json, JsonOptions);

                if (recording != null)
                {
                    _logger.LogInformation("[Encora] ✅ Successfully fetched metadata from Encora for ID {EncoraId}", encoraId);

                    var actorIds = recording.Cast?
                        .Select(c => c.Performer?.Id.ToString(CultureInfo.InvariantCulture))
                        .ToArray();
                    var actorIdsParam = actorIds != null && actorIds.Length > 0
                        ? string.Join(",", actorIds) : "1";

                    var posterPath = !string.IsNullOrWhiteSpace(movieDir)
                        ? System.IO.Path.Combine(movieDir, "folder.jpg")
                        : null;
                    var headshots = new Collection<StageMediaPerformer>();

                    try
                    {
                        var stageMediaApiKey = Plugin.Instance?.Configuration?.StageMediaAPIKey;
                        if (!string.IsNullOrWhiteSpace(stageMediaApiKey) && recording.Metadata?.ShowId > 0)
                        {
                            _logger.LogInformation("[Encora] Fetching StageMedia images for ShowId {ShowId} with ActorIds {ActorIds}", recording.Metadata.ShowId, actorIdsParam);
                            var stageMediaUrl = $"https://stagemedia.me/api/images?show_id={recording.Metadata.ShowId}&actor_ids={actorIdsParam}";
                            var stageMediaClient = _httpClientFactory.CreateClient();
                            stageMediaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stageMediaApiKey);
                            stageMediaClient.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

                            var stageMediaResponse = await stageMediaClient.GetAsync(stageMediaUrl, cancellationToken).ConfigureAwait(false);
                            stageMediaResponse.EnsureSuccessStatusCode();
                            var stageMediaJson = await stageMediaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            var images = JsonSerializer.Deserialize<StageMediaImages>(stageMediaJson);

                            // Download poster to media folder if doesnt exist.
                            if (images?.Posters != null && images.Posters.Count > 0 && (!string.IsNullOrWhiteSpace(posterPath) && !System.IO.File.Exists(posterPath)))
                            {
                                var posterUrl = images.Posters[0];
                                var posterResponse = await stageMediaClient.GetAsync(posterUrl, cancellationToken).ConfigureAwait(false);
                                posterResponse.EnsureSuccessStatusCode();
                                var posterBytes = await posterResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                await File.WriteAllBytesAsync(posterPath, posterBytes, cancellationToken).ConfigureAwait(false);
                            }

                            if (images?.Performers != null && images.Performers.Count > 0)
                            {
                                headshots = images.Performers;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Encora] Could not download and save StageMedia poster for ShowId {ShowId}", recording.Metadata?.ShowId);
                    }

                    var titleFormat = Plugin.Instance?.Configuration?.TitleFormat ?? "{show}";

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
                    var finalDescription = string.IsNullOrWhiteSpace(description) ? "Fetched from Encora.it" : description;

                    var movie = new Movie
                    {
                        Name = FormatTitle(titleFormat, recording, info.Path),
                        Overview = finalDescription,
                        PremiereDate = DateTime.TryParse(recording.Date?.FullDate, out var date) ? date : (DateTime?)null,
                        ProductionYear = DateTime.TryParse(recording.Date?.FullDate, out var yearDate) ? yearDate.Year : 0,
                        OriginalTitle = recording.Show,
                        SortName = recording.Show,
                        HomePageUrl = $"https://encora.it/recordings/{encoraId}",
                    };

                    // Set genres from metadata
                    if (recording.Metadata != null)
                    {
                        movie.SetProviderId("StageMediaShowId", recording.Metadata.ShowId.ToString(CultureInfo.InvariantCulture));
                        movie.SetProviderId("EncoraRecordingId", encoraId);

                        if (!string.IsNullOrWhiteSpace(recording.Metadata.RecordingType))
                        {
                            movie.AddGenre(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.RecordingType));
                        }

                        if (!string.IsNullOrWhiteSpace(recording.Metadata.AmountRecorded))
                        {
                            movie.AddGenre(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(recording.Metadata.AmountRecorded));
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

                    // If metadata.HasSubtitles, request and download them
                    if (recording.Metadata?.HasSubtitles == true && !string.IsNullOrWhiteSpace(encoraId) && !string.IsNullOrWhiteSpace(movieDir))
                    {
                        _logger.LogInformation("[Encora] Fetching subtitles for recording {EncoraId}", encoraId);
                        try
                        {
                            var subtitlesUrl = $"https://encora.it/api/recording/{encoraId}/subtitles";
                            var subtitlesResponse = await client.GetAsync(subtitlesUrl, cancellationToken).ConfigureAwait(false);
                            subtitlesResponse.EnsureSuccessStatusCode();
                            var subtitlesJson = await subtitlesResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            var subtitles = JsonSerializer.Deserialize<List<EncoraSubtitles>>(subtitlesJson);

                            if (subtitles != null && subtitles.Count > 0)
                            {
                                var mediaFileName = System.IO.Path.GetFileNameWithoutExtension(info.Path);
                                var subtitlePaths = new List<string>();

                                foreach (var sub in subtitles)
                                {
                                    if (string.IsNullOrWhiteSpace(sub.Url) || string.IsNullOrWhiteSpace(sub.FileType))
                                    {
                                        continue;
                                    }

                                    var lang = sub.Language?.Length >= 2
                                        ? sub.Language[..2].ToLowerInvariant()
                                        : "en";

                                    var ext = sub.FileType.ToLowerInvariant();
                                    var subFileName = $"{mediaFileName}.{lang}.{ext}";
                                    var subFilePath = Path.Combine(movieDir, subFileName);

                                    var subFileResponse = await client.GetAsync(sub.Url, cancellationToken).ConfigureAwait(false);
                                    subFileResponse.EnsureSuccessStatusCode();
                                    var subFileBytes = await subFileResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                    await File.WriteAllBytesAsync(subFilePath, subFileBytes, cancellationToken).ConfigureAwait(false);
                                    subtitlePaths.Add(subFilePath);
                                    movie.HasSubtitles = true;
                                }

                                movie.SubtitleFiles = subtitlePaths.ToArray();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Encora] Could not download subtitles for recording {EncoraId}", encoraId);
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

                    result.HasMetadata = true;
                    result.Item = movie;

                    var shouldAddMasterDirector = Plugin.Instance?.Configuration?.AddMasterDirector ?? false;

                    if (recording.Cast != null)
                    {
                        EncoraCastMember.MapCastToResult(result, recording.Cast, headshots, recording.Master, shouldAddMasterDirector);
                    }
                }
                else
                {
                    _logger.LogInformation("[Encora] ❌ Failed to fetch metadata from Encora for ID {EncoraId} - Falling back to NFO metadata for {Path}", encoraId, info.Path);
                    return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("[Encora] ❌ Error while fetching metadata from Encora for ID {EncoraId} - Falling back to NFO metadata for {Path}", encoraId, info.Path);
                _logger.LogError(ex, "[Encora] Error details: {Ex}", ex);
                return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
            }

            await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, movieDir, info).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Extracts the Encora ID from the given path.
        /// </summary>
        /// <param name="path">The file path.</param>
        /// <returns>The Encora ID if found; otherwise, null.</returns>
        private string? ExtractEncoraId(string path)
        {
            _logger.LogInformation("[Encora] Extracting Encora ID from path: {Path}", path);
            // Try to extract from path
            var match = Regex.Match(path, @"{e-(\d+)}", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                _logger.LogInformation("[Encora] Found Encora ID in path: {Path} - ID: {Id}", path, match.Groups[1].Value);
                return match.Groups[1].Value;
            }

            // Fallback: look for .encora-<id> file in the directory
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var files = Directory.GetFiles(directory, ".encora-*");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var fileMatch = Regex.Match(fileName, @"\.encora-(\d+)", RegexOptions.IgnoreCase);
                    if (fileMatch.Success)
                    {
                        _logger.LogInformation("[Encora] Found Encora ID in file: {FileName} - ID: {Id}", fileName, fileMatch.Groups[1].Value);
                        return fileMatch.Groups[1].Value;
                    }
                }

                // Fallback: check for .encora-id file
                var encoraIdFile = Path.Combine(directory, ".encora-id");
                if (File.Exists(encoraIdFile))
                {
                    var id = File.ReadAllText(encoraIdFile).Trim();
                    _logger.LogInformation("[Encora] Found Encora ID in .encora-id file: {Id}", id);
                    return string.IsNullOrWhiteSpace(id) ? null : id;
                }
            }

            _logger.LogInformation("[Encora] No Encora ID found in path: {Path}", path);
            return null;
        }

        /// <summary>
        ///     Formats the title to the configured format using the recording data.
        /// </summary>
        /// <param name="format">The format string used to generate the title. It may contain placeholders like {show}, {date}, etc.</param>
        /// <param name="recording">The recording object containing data to populate the placeholders in the format string.</param>
        /// <param name="path">The file path of the recording, used to extract additional information if needed.</param>
        private string FormatTitle(string format, EncoraRecording recording, string path)
        {
            var dateReplaceChar = Plugin.Instance?.Configuration?.DateReplaceChar ?? "x";
            var date = recording.Date;
            string? dateLong = null;
            string? dateIso = null;
            string? dateUsa = null;
            string? dateNumeric = null;

            if (date != null && !string.IsNullOrWhiteSpace(date.FullDate))
            {
                var parts = date.FullDate.Split('-');
                var year = parts.Length > 0 ? parts[0] : string.Empty;
                var month = (parts.Length > 1 && date.MonthKnown) ? parts[1] : new string(dateReplaceChar[0], 2);
                var day = (parts.Length > 2 && date.DayKnown) ? parts[2] : new string(dateReplaceChar[0], 2);

                // {date}: "December 31, 2024" or with replace char
                if (date.MonthKnown && date.DayKnown && int.TryParse(month, out var m) && int.TryParse(day, out var d) && int.TryParse(year, out var y))
                {
                    var dt = new DateTime(y, m, d);
                    dateLong = dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                }
                else if (date.MonthKnown && int.TryParse(month, out var m2) && int.TryParse(year, out var y2))
                {
                    var dt = new DateTime(y2, m2, 1);
                    dateLong = dt.ToString("MMMM", CultureInfo.InvariantCulture) + $" {day}, {year}";
                    if (!date.DayKnown)
                    {
                        dateLong = dt.ToString("MMMM", CultureInfo.InvariantCulture) + $" {new string(dateReplaceChar[0], 2)}, {year}";
                    }
                }
                else if (int.TryParse(year, out var y3))
                {
                    dateLong = $"{year}";
                    if (!date.MonthKnown)
                    {
                        dateLong = $"{year}";
                    }
                }
                else
                {
                    dateLong = $"{year}-{month}-{day}";
                }

                // {date_iso}: "2024-12-31"
                dateIso = $"{year}-{month}-{day}";

                // {date_usa}: "12-31-2024"
                dateUsa = $"{month}-{day}-{year}";

                // {date_numeric}: "31-12-2024"
                dateNumeric = $"{day}-{month}-{year}";

                // Append variant if present
                if (!string.IsNullOrWhiteSpace(date.DateVariant))
                {
                    dateLong += $" ({date.DateVariant})";
                    dateIso += $" ({date.DateVariant})";
                    dateUsa += $" ({date.DateVariant})";
                    dateNumeric += $" ({date.DateVariant})";
                }

                // Append (matinee) if time is "matinee"
                if (!string.IsNullOrWhiteSpace(date.Time) && date.Time.Equals("matinee", StringComparison.OrdinalIgnoreCase))
                {
                    dateLong += " (matinée)";
                    dateIso += " (matinée)";
                    dateUsa += " (matinée)";
                    dateNumeric += " (matinée)";
                }
            }

            // Append "Act X" from filename if present
            var match = Regex.Match(path ?? string.Empty, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
            var showWithAct = recording.Show;
            if (match.Success)
            {
                showWithAct = $"{showWithAct} Act {match.Groups[1].Value}";
            }

            var variables = new Dictionary<string, string?>
            {
                ["show"] = showWithAct,
                ["date"] = dateLong,
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

        /// <summary>
        /// Fetches data from the NFO file as a fallback.
        /// </summary>
        /// <param name="info">The movie info.</param>
        /// <param name="cancellationToken">A cancellation token for the await.</param>
        /// <returns>A task.</returns>
        private async Task<MetadataResult<Movie>> ParseNfoMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Encora] [NFO] Processing NFO metadata for {Path}", info.Path);
            var result = new MetadataResult<Movie>();

            var movieDir = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrWhiteSpace(movieDir))
            {
                return result;
            }

            await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, movieDir, info).ConfigureAwait(false);

            var nfoPath = Path.Combine(movieDir, "movie.nfo");
            if (!File.Exists(nfoPath))
            {
                var fileNameNoExt = Path.GetFileNameWithoutExtension(info.Path);
                nfoPath = Path.Combine(movieDir, fileNameNoExt + ".nfo");
                if (!File.Exists(nfoPath))
                {
                    _logger.LogInformation("[Encora] [NFO] No NFO file found at {NfoPath}", nfoPath);
                    return result;
                }
            }

            var nfoContent = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
            var doc = XDocument.Parse(nfoContent);
            var movieElem = doc.Root;
            if (movieElem == null || !movieElem.Name.LocalName.Equals("movie", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var movie = new Movie
            {
                Name = movieElem.Element("title")?.Value,
                Overview = movieElem.Element("plot")?.Value,
                OriginalTitle = movieElem.Element("originaltitle")?.Value,
                SortName = movieElem.Element("sorttitle")?.Value
            };

            if (DateTime.TryParse(movieElem.Element("premiered")?.Value, out var premiere))
            {
                movie.PremiereDate = premiere;
            }
            else if (DateTime.TryParse(movieElem.Element("releasedate")?.Value, out var release))
            {
                movie.PremiereDate = release;
            }

            if (int.TryParse(movieElem.Element("year")?.Value, out var year))
            {
                movie.ProductionYear = year;
            }

            var studio = movieElem.Element("studio")?.Value;
            if (!string.IsNullOrWhiteSpace(studio))
            {
                movie.AddStudio(studio);
            }

            foreach (var genreElem in movieElem.Elements("genre"))
            {
                if (!string.IsNullOrWhiteSpace(genreElem.Value))
                {
                    movie.AddGenre(genreElem.Value);
                }
            }

            foreach (var certElem in movieElem.Elements("certification"))
            {
                if (!string.IsNullOrWhiteSpace(certElem.Value))
                {
                    movie.OfficialRating = "NFT";
                }
            }

            var posterUrl = movieElem.Elements("thumb")
                .FirstOrDefault(e => (string?)e.Attribute("aspect") == "poster")?.Value;
            if (!string.IsNullOrWhiteSpace(posterUrl))
            {
                movie.AddImage(new ItemImageInfo { Path = posterUrl, Type = ImageType.Primary });
            }

            foreach (var actorElem in movieElem.Elements("actor"))
            {
                var name = actorElem.Element("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.AddPerson(new PersonInfo
                    {
                        Name = name,
                        Role = actorElem.Element("role")?.Value,
                        ImageUrl = actorElem.Element("thumb")?.Value,
                        Type = Data.Enums.PersonKind.Actor
                    });
                }
            }

            result.Item = movie;
            result.HasMetadata = true;
            _logger.LogInformation("[Encora] [NFO] ✅ Successfully processed NFO metadata for {Path}", info.Path);
            return result;
        }
    }
}
