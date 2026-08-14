using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraMovieMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="mediaEncoder">The media encoder used for processing media files.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraMovieMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraMovieMetadataProvider> logger, IMediaEncoder mediaEncoder, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
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

            if (Plugin.Instance?.Configuration?.EnableMovieMatching != true)
            {
                _logger.LogInformation("[Encora] Movie matching is disabled in plugin settings, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.MovieLibraryIds))
            {
                _logger.LogInformation("[Encora] Path {Path} is not in a scoped Movie library, skipping metadata fetch", info.Path);
                return result;
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            var encoraId = EncoraIdExtractor.ExtractEncoraId(_logger, info.Path);
            _logger.LogInformation("[Encora] Extracted Encora ID: {EncoraId} from path: {Path}", encoraId, info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found in path: {Path}, falling back to NFO file...", info.Path);
                return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
            }

            var movieDir = Path.GetDirectoryName(info.Path);

            var options = BuildOptions();

            try
            {
                var recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);

                if (recording != null)
                {
                    _logger.LogInformation("[Encora] ✅ Successfully fetched metadata from Encora for ID {EncoraId}", encoraId);

                    var existingMovie = _libraryManager.FindByPath(info.Path, isFolder: false);
                    var posterPath = options.FetchPoster && !string.IsNullOrWhiteSpace(movieDir) && (existingMovie == null || !existingMovie.HasImage(ImageType.Primary))
                        ? Path.Combine(movieDir, "folder.jpg")
                        : null;
                    var headshots = await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterPath, cancellationToken).ConfigureAwait(false);

                    var titleFormat = Plugin.Instance?.Configuration?.MovieTitleFormat ?? "{show}";

                    var movie = new Movie
                    {
                        Name = FormatTitle(titleFormat, recording, info.Path),
                        OriginalTitle = recording.Show,
                        SortName = recording.Show,
                        HomePageUrl = $"https://encora.it/recordings/{encoraId}",
                    };

                    EncoraRecordingApplier.ApplyRecordingFields(movie, _libraryManager, info.Path, recording, encoraId, options, _logger);
                    EncoraRecordingApplier.ApplyNftRating(movie, recording.Nft, options.IncludeNftTag);

                    result.HasMetadata = true;
                    result.Item = movie;

                    if (recording.Cast != null)
                    {
                        EncoraCastMember.MapCastToResult(result, recording.Cast, headshots, recording.Master, options.AddMasterDirector);
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

            if (options.GenerateThumbnail)
            {
                await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, movieDir, info.Path, options.ThumbnailSeekMinPercent, options.ThumbnailSeekMaxPercent).ConfigureAwait(false);
            }

            return result;
        }

        /// <summary>
        /// Builds the apply-options for Movie libraries from the plugin's Movie-scoped configuration.
        /// </summary>
        /// <returns>The apply options.</returns>
        private static EncoraApplyOptions BuildOptions()
        {
            var config = Plugin.Instance?.Configuration;
            return new EncoraApplyOptions
            {
                DateReplaceChar = config?.MovieDateReplaceChar ?? "x",
                AddMasterDirector = config?.MovieAddMasterDirector ?? false,
                PreserveManualDescriptionEdits = config?.MoviePreserveManualDescriptionEdits ?? true,
                OverviewSource = config?.MovieOverviewSource ?? "description_notes",
                StudioSource = config?.MovieStudioSource ?? "venue",
                ProductionLocationSource = config?.MovieProductionLocationSource ?? "city",
                TaglineSource = config?.MovieTaglineSource ?? "tour",
                IncludeGenreTags = config?.MovieIncludeGenreTags ?? true,
                IncludeNftTag = config?.MovieIncludeNftTag ?? true,
                FetchPoster = config?.MovieFetchPoster ?? true,
                GenerateThumbnail = config?.MovieGenerateThumbnail ?? true,
                ThumbnailSeekMinPercent = config?.MovieThumbnailSeekMinPercent ?? 15,
                ThumbnailSeekMaxPercent = config?.MovieThumbnailSeekMaxPercent ?? 60,
            };
        }

        /// <summary>
        ///     Formats the title to the configured format using the recording data.
        /// </summary>
        /// <param name="format">The format string used to generate the title. It may contain placeholders like {show}, {date}, etc.</param>
        /// <param name="recording">The recording object containing data to populate the placeholders in the format string.</param>
        /// <param name="path">The file path of the recording, used to extract additional information if needed.</param>
        private string FormatTitle(string format, EncoraRecording recording, string path)
        {
            var dateReplaceChar = Plugin.Instance?.Configuration?.MovieDateReplaceChar ?? "x";
            var dateVariants = EncoraDateHelper.BuildDateVariants(recording.Date, dateReplaceChar);

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
                ["date"] = dateVariants.Long,
                ["date_iso"] = dateVariants.Iso,
                ["date_numeric"] = dateVariants.Numeric,
                ["date_usa"] = dateVariants.Usa,
                ["tour"] = recording.Tour,
                ["master"] = EncoraTitleFormatter.ResolveMaster(recording.Master),
                ["venue"] = recording.Metadata?.Venue,
                ["city"] = recording.Metadata?.City
            };

            return EncoraTitleFormatter.Format(format, variables);
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

            await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, movieDir, info.Path).ConfigureAwait(false);

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
