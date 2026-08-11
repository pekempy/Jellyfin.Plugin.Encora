using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Exposes Encora-hosted subtitles through Jellyfin's standard subtitle search/download flow (the
    /// library's Subtitles tab, the "Download missing subtitles" scheduled task), instead of only
    /// downloading them as a side effect of an Encora metadata refresh.
    /// </summary>
    public class EncoraSubtitleProvider : ISubtitleProvider
    {
        private const char FieldSeparator = '\u001f';

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<EncoraSubtitleProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraSubtitleProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Used to create the Encora HTTP client.</param>
        /// <param name="libraryManager">Used to check whether a media path falls within a scoped library.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        public EncoraSubtitleProvider(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<EncoraSubtitleProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Encora";

        /// <inheritdoc />
        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(request.MediaPath) || string.IsNullOrWhiteSpace(config.EncoraAPIKey))
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var matchingEnabled = request.ContentType == VideoContentType.Episode ? config.EnableTvMatching : config.EnableMovieMatching;
            if (!matchingEnabled)
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var allowedLibraryIds = request.ContentType == VideoContentType.Episode ? config.TvLibraryIds : config.MovieLibraryIds;
            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, request.MediaPath, allowedLibraryIds))
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var encoraId = EncoraIdExtractor.ExtractEncoraId(_logger, request.MediaPath);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            List<EncoraSubtitles>? subtitles;
            try
            {
                subtitles = await EncoraRecordingApplier.FetchSubtitlesAsync(_httpClientFactory, _logger, encoraId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Could not search subtitles for recording {EncoraId}", encoraId);
                return Array.Empty<RemoteSubtitleInfo>();
            }

            if (subtitles == null || subtitles.Count == 0)
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            return subtitles
                .Where(sub => !string.IsNullOrWhiteSpace(sub.Url) && !string.IsNullOrWhiteSpace(sub.FileType))
                .Select(sub =>
                {
                    var url = sub.Url!;
                    var format = sub.FileType!.ToLowerInvariant();
                    var language = NormalizeLanguage(sub.Language);

                    return new RemoteSubtitleInfo
                    {
                        Id = EncodeId(url, format, language),
                        ProviderName = Name,
                        Name = string.IsNullOrWhiteSpace(sub.Author) ? "Encora" : $"Encora ({sub.Author})",
                        Author = sub.Author,
                        Format = format,
                        ThreeLetterISOLanguageName = language,
                    };
                })
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            var (url, format, language) = DecodeId(id);

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Plugin.Instance?.Configuration?.EncoraAPIKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinAgent/0.1");

            await EncoraRateLimiter.WaitAsync(_logger, cancellationToken).ConfigureAwait(false);
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            EncoraRateLimiter.UpdateFromResponse(response);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return new SubtitleResponse
            {
                Format = format,
                Language = language,
                Stream = new MemoryStream(bytes),
            };
        }

        private static string NormalizeLanguage(string? language)
        {
            return language?.Length >= 2 ? language[..2].ToLowerInvariant() : "en";
        }

        private static string EncodeId(string url, string format, string language)
        {
            var raw = string.Join(FieldSeparator, url, format, language);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        private static (string Url, string Format, string Language) DecodeId(string id)
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(id));
            var parts = raw.Split(FieldSeparator);
            return (parts[0], parts[1], parts[2]);
        }
    }
}
