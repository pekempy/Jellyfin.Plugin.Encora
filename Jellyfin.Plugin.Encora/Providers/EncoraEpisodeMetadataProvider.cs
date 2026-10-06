using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Provides Episode-level metadata for TV libraries from the Encora API. An Episode represents one
    /// specific dated recording, matched by the same Encora ID convention as Movie libraries.
    /// </summary>
    public class EncoraEpisodeMetadataProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, ICustomMetadataProvider<Episode>, IHasOrder, IMetadataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraEpisodeMetadataProvider> _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILibraryManager _libraryManager;
        private static readonly ConcurrentDictionary<string, Episode> _pendingEpisodeUpdates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraEpisodeMetadataProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger instance used for logging.</param>
        /// <param name="mediaEncoder">The media encoder used for processing media files.</param>
        /// <param name="libraryManager">The library manager, used to detect manually-edited descriptions.</param>
        public EncoraEpisodeMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<EncoraEpisodeMetadataProvider> logger, IMediaEncoder mediaEncoder, ILibraryManager libraryManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
            _logger.LogInformation("[Encora] ✅ EncoraEpisodeMetadataProvider initialized.");
        }

        /// <summary>
        /// Gets the name of the provider.
        /// </summary>
        public string Name => "Encora";

        /// <summary>
        /// Gets the order of the provider.
        /// </summary>
        public int Order => 100;

        internal static void RecordPendingSeason(string episodePath, Season targetSeason)
        {
            if (_pendingEpisodeUpdates.TryGetValue(episodePath, out var pending))
            {
                pending.SeasonId = targetSeason.Id;
                pending.SeasonName = targetSeason.Name;
                pending.ParentIndexNumber = targetSeason.IndexNumber;
                pending.SetParent(targetSeason);
            }
        }

        /// <summary>
        /// Gets search results for episodes.
        /// </summary>
        /// <param name="searchInfo">The search information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the search results.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        /// <summary>
        /// Gets metadata for an episode.
        /// </summary>
        /// <param name="info">The episode information.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the metadata result.</returns>
        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();

            if (info == null || string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                _logger.LogInformation("[Encora] TV matching is disabled in plugin settings, skipping metadata fetch for {Path}", info.Path);
                return result;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, info.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                _logger.LogInformation("[Encora] Path {Path} is not in a scoped TV library, skipping metadata fetch", info.Path);
                return result;
            }

            var encoraId = EncoraIdExtractor.ExtractEncoraId(_logger, info.Path);
            if (string.IsNullOrWhiteSpace(encoraId))
            {
                _logger.LogInformation("[Encora] ❌ No Encora ID found in path: {Path}, checking for NFO metadata...", info.Path);
                return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
            }

            var apiKey = Plugin.Instance?.Configuration?.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] ❌ No API key configured, falling back to NFO metadata for {Path}", info.Path);
                return await ParseNfoMetadata(info, cancellationToken).ConfigureAwait(false);
            }

            var episodeDir = Path.GetDirectoryName(info.Path);
            var options = BuildOptions();
            EncoraRecording? capturedRecording = null;
            System.Collections.ObjectModel.Collection<StageMediaPerformer>? capturedHeadshots = null;

            try
            {
                var recording = await EncoraRecordingApplier.FetchRecordingAsync(_httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);

                if (recording == null)
                {
                    _logger.LogWarning("[Encora] ❌ Failed to fetch metadata from Encora for ID {EncoraId} for {Path} — Encora is the source of truth; skipping rather than falling back to NFO", encoraId, info.Path);
                    return result;
                }

                _logger.LogInformation("[Encora] ✅ Successfully fetched metadata from Encora for ID {EncoraId}", encoraId);

                var headshots = await EncoraRecordingApplier.FetchStageMediaImagesAsync(_httpClientFactory, _logger, recording, posterDestinationPath: null, cancellationToken).ConfigureAwait(false);

                var titleFormat = Plugin.Instance?.Configuration?.TvEpisodeTitleFormat ?? "{date}";

                var episode = new Episode
                {
                    Name = FormatEpisodeTitle(titleFormat, recording, info.Path),
                    IndexNumber = EncoraDateHelper.ComputeDateIndexNumber(recording.Date, info.Path),
                    ForcedSortName = EncoraDateHelper.BuildDateSortKey(recording.Date, info.Path),
                };

                EncoraRecordingApplier.ApplyRecordingFields(episode, _libraryManager, info.Path, recording, encoraId, options, _logger);
                EncoraRecordingApplier.ApplyNftRating(episode, recording.Nft, options.IncludeNftTag);

                _pendingEpisodeUpdates[info.Path] = episode;
                result.HasMetadata = true;
                result.Item = episode;

                if (recording.Cast != null)
                {
                    EncoraCastMember.MapCastToResult(result, recording.Cast, headshots, recording.Master, options.AddMasterDirector);
                }

                capturedRecording = recording;
                capturedHeadshots = headshots;
                SpawnPostDelayEpisodeUpdate(info.Path, episode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error fetching from Encora for ID {EncoraId} for {Path} — Encora is the source of truth; skipping rather than falling back to NFO", encoraId, info.Path);
                return result;
            }

            if (options.GenerateThumbnail)
            {
                await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, episodeDir, info.Path, options.ThumbnailSeekMinPercent, options.ThumbnailSeekMaxPercent).ConfigureAwait(false);

                if (options.OverlayCastOnThumb && !string.IsNullOrWhiteSpace(episodeDir) && capturedRecording != null)
                {
                    var sources = CastThumbOverlay.GetHeadshotSources(
                        capturedRecording.Cast, _libraryManager, capturedHeadshots);
                    if (sources.Count > 0)
                    {
                        await CastThumbOverlay.OverlayAsync(
                            _logger, episodeDir!, sources, _httpClientFactory, cancellationToken).ConfigureAwait(false);

                        // Tell Jellyfin the thumb file changed so it regenerates its resize cache
                        var thumbPath = System.IO.Path.Combine(episodeDir!, "thumb.png");
                        if (System.IO.File.Exists(thumbPath)
                            && _libraryManager.FindByPath(info.Path, isFolder: false) is Episode existingEp)
                        {
                            existingEp.SetImagePath(MediaBrowser.Model.Entities.ImageType.Thumb, 0, thumbPath);
                            await _libraryManager.UpdateItemAsync(existingEp, existingEp.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            if (options.SetRandomEpisodeBackdrop)
            {
                TrySetSeriesBackdropFromEpisodeThumb(info.Path, episodeDir);
            }

            return result;
        }

        /// <summary>
        /// Builds the apply-options for TV libraries from the plugin's TV-scoped configuration.
        /// </summary>
        /// <returns>The apply options.</returns>
        private static EncoraApplyOptions BuildOptions()
        {
            var config = Plugin.Instance?.Configuration;
            return new EncoraApplyOptions
            {
                DateReplaceChar = config?.TvDateReplaceChar ?? "x",
                AddMasterDirector = config?.TvAddMasterDirector ?? false,
                PreserveManualDescriptionEdits = config?.TvPreserveManualDescriptionEdits ?? true,
                OverviewSource = config?.TvOverviewSource ?? "description_notes",
                StudioSource = config?.TvStudioSource ?? "venue",
                ProductionLocationSource = config?.TvProductionLocationSource ?? "city",
                TaglineSource = config?.TvTaglineSource ?? "tour",
                IncludeGenreTags = config?.TvIncludeGenreTags ?? true,
                IncludeNftTag = config?.TvIncludeNftTag ?? true,
                FetchPoster = config?.TvFetchPoster ?? true,
                GenerateThumbnail = config?.TvGenerateThumbnail ?? true,
                SetRandomEpisodeBackdrop = config?.TvSetRandomEpisodeBackdrop ?? true,
                ThumbnailSeekMinPercent = config?.TvThumbnailSeekMinPercent ?? 15,
                ThumbnailSeekMaxPercent = config?.TvThumbnailSeekMaxPercent ?? 60,
                OverlayCastOnThumb = config?.TvOverlayCastOnThumb ?? false,
            };
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
        /// Formats the episode title from the configured format using the recording data. Unlike Movie
        /// titles, there's no {show}/{tour} placeholder here - those are implicit from the Series/Season -
        /// and any "Act N" suffix is appended to the date variants themselves instead.
        /// </summary>
        /// <param name="format">The format string, e.g. "{date}".</param>
        /// <param name="recording">The recording data.</param>
        /// <param name="path">The episode file path, used to detect an "Act N" suffix.</param>
        /// <returns>The formatted episode title.</returns>
        private string FormatEpisodeTitle(string format, EncoraRecording recording, string path)
        {
            var dateReplaceChar = Plugin.Instance?.Configuration?.TvDateReplaceChar ?? "x";
            var match = Regex.Match(path ?? string.Empty, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
            var actSuffix = match.Success ? match.Groups[1].Value : null;
            var dateVariants = EncoraDateHelper.BuildDateVariants(recording.Date, dateReplaceChar, actSuffix);

            var variables = new Dictionary<string, string?>
            {
                ["date"] = dateVariants.Long,
                ["date_iso"] = dateVariants.Iso,
                ["date_numeric"] = dateVariants.Numeric,
                ["date_usa"] = dateVariants.Usa,
                ["master"] = EncoraTitleFormatter.ResolveMaster(recording.Master),
                ["venue"] = recording.Metadata?.Venue,
                ["city"] = recording.Metadata?.City
            };

            return EncoraTitleFormatter.Format(format, variables);
        }

        /// <summary>
        /// Fetches data from an NFO file (and folder/file naming) as a fallback for non-Encora or unindexed items.
        /// </summary>
        /// <param name="info">The episode info.</param>
        /// <param name="cancellationToken">A cancellation token for the await.</param>
        /// <returns>A metadata result.</returns>
        private async Task<MetadataResult<Episode>> ParseNfoMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Encora] [NFO] Processing episode NFO metadata for {Path}", info.Path);
            var result = new MetadataResult<Episode>();

            var episodeDir = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrWhiteSpace(episodeDir))
            {
                return result;
            }

            var options = BuildOptions();

            if (options.GenerateThumbnail)
            {
                await ThumbGenerator.GenerateThumbPng(_logger, _mediaEncoder, episodeDir, info.Path, options.ThumbnailSeekMinPercent, options.ThumbnailSeekMaxPercent).ConfigureAwait(false);
            }

            if (options.SetRandomEpisodeBackdrop)
            {
                TrySetSeriesBackdropFromEpisodeThumb(info.Path, episodeDir);
            }

            string? nfoPath = Path.ChangeExtension(info.Path, ".nfo");
            if (!File.Exists(nfoPath))
            {
                var fileNameNoExt = Path.GetFileNameWithoutExtension(info.Path);
                nfoPath = Path.Combine(episodeDir, fileNameNoExt + ".nfo");
            }

            if (!File.Exists(nfoPath))
            {
                nfoPath = Path.Combine(episodeDir, "movie.nfo");
            }

            if (!File.Exists(nfoPath))
            {
                try
                {
                    nfoPath = Directory.EnumerateFiles(episodeDir, "*.nfo").FirstOrDefault();
                }
                catch
                {
                    nfoPath = null;
                }
            }

            if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
            {
                try
                {
                    var fileInfo = new FileInfo(info.Path);
                    if (fileInfo.LinkTarget != null)
                    {
                        var targetPath = fileInfo.ResolveLinkTarget(true)?.FullName;
                        if (!string.IsNullOrWhiteSpace(targetPath))
                        {
                            var sourceDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir))
                            {
                                nfoPath = Path.ChangeExtension(targetPath, ".nfo");
                                if (!File.Exists(nfoPath))
                                {
                                    nfoPath = Path.Combine(sourceDir, "movie.nfo");
                                }

                                if (!File.Exists(nfoPath))
                                {
                                    nfoPath = Directory.EnumerateFiles(sourceDir, "*.nfo").FirstOrDefault();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Encora] [NFO] Could not inspect symlink source for NFO at {Path}", info.Path);
                }
            }

            XElement? root = null;
            if (!string.IsNullOrWhiteSpace(nfoPath) && File.Exists(nfoPath))
            {
                try
                {
                    var nfoContent = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
                    var sanitizedXml = Regex.Replace(nfoContent, @"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)", "&amp;");
                    var doc = XDocument.Parse(sanitizedXml);
                    root = doc.Root;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Encora] [NFO] Failed to parse NFO XML at {NfoPath}", nfoPath);
                }
            }
            else
            {
                _logger.LogInformation("[Encora] [NFO] No NFO file found in {EpisodeDir}, using path-based metadata", episodeDir);
            }

            EncoraDate? encoraDate = null;

            // Try extracting date from path (e.g. "[2025-12-xx] Hadestown ~ The Riddle {ne}" or "[2021-06-04]")
            // Supports [YYYY-MM-DD], [YYYY-MM-xx], [YYYY_MM_DD], [YYYY-xx-xx], optional variant (1)
            var dateMatch = Regex.Match(info.Path, @"\[([0-9]{4})[-_]([0-9xX]{2})[-_]([0-9xX]{2})(?:\s*\(([^)]+)\))?\]");
            if (dateMatch.Success)
            {
                var year = dateMatch.Groups[1].Value;
                var monthStr = dateMatch.Groups[2].Value;
                var dayStr = dateMatch.Groups[3].Value;
                var variantStr = dateMatch.Groups[4].Success ? dateMatch.Groups[4].Value : null;

                var monthKnown = !monthStr.Equals("xx", StringComparison.OrdinalIgnoreCase);
                var dayKnown = !dayStr.Equals("xx", StringComparison.OrdinalIgnoreCase);

                encoraDate = new EncoraDate
                {
                    FullDate = $"{year}-{(monthKnown ? monthStr : "00")}-{(dayKnown ? dayStr : "00")}",
                    MonthKnown = monthKnown,
                    DayKnown = dayKnown,
                    DateVariant = variantStr
                };
            }

            // Check if NFO title contains an explicit date (e.g. "Hadestown (Broadway - 2025-12-06)")
            if (root != null)
            {
                var titleVal = root.Element("title")?.Value;
                if (!string.IsNullOrWhiteSpace(titleVal))
                {
                    var nfoTitleDateMatch = Regex.Match(titleVal, @"(?:\b|[-_])([0-9]{4})[-_]([0-9]{2})[-_]([0-9]{2})(?:\b|[-_\)])");
                    if (nfoTitleDateMatch.Success)
                    {
                        var nfoYear = nfoTitleDateMatch.Groups[1].Value;
                        var nfoMonth = nfoTitleDateMatch.Groups[2].Value;
                        var nfoDay = nfoTitleDateMatch.Groups[3].Value;

                        if (encoraDate == null || !encoraDate.DayKnown || !encoraDate.MonthKnown || encoraDate.FullDate?.StartsWith(nfoYear, StringComparison.Ordinal) == true)
                        {
                            encoraDate ??= new EncoraDate();
                            encoraDate.FullDate = $"{nfoYear}-{nfoMonth}-{nfoDay}";
                            encoraDate.MonthKnown = true;
                            encoraDate.DayKnown = true;
                        }
                    }
                }

                if (encoraDate == null)
                {
                    var premiered = root.Element("premiered")?.Value ?? root.Element("releasedate")?.Value;
                    if (!string.IsNullOrWhiteSpace(premiered))
                    {
                        var parts = premiered.Split('-');
                        if (parts.Length > 0 && int.TryParse(parts[0], out _))
                        {
                            var mKnown = parts.Length > 1 && !parts[1].Equals("00", StringComparison.Ordinal) && !parts[1].Equals("xx", StringComparison.OrdinalIgnoreCase);
                            var dKnown = parts.Length > 2 && !parts[2].Equals("00", StringComparison.Ordinal) && !parts[2].Equals("xx", StringComparison.OrdinalIgnoreCase);
                            encoraDate = new EncoraDate
                            {
                                FullDate = $"{parts[0]}-{(mKnown ? parts[1] : "00")}-{(dKnown ? parts[2] : "00")}",
                                MonthKnown = mKnown,
                                DayKnown = dKnown
                            };
                        }
                    }
                    else if (int.TryParse(root.Element("year")?.Value, out var y))
                    {
                        encoraDate = new EncoraDate
                        {
                            FullDate = $"{y}-00-00",
                            MonthKnown = false,
                            DayKnown = false
                        };
                    }
                }
            }

            if (encoraDate != null)
            {
                if (Regex.IsMatch(info.Path, @"\((?:m|matinee|matin[eé]e)\)", RegexOptions.IgnoreCase))
                {
                    encoraDate.Time = "matinee";
                }
                else if (Regex.IsMatch(info.Path, @"\((?:e|evening)\)", RegexOptions.IgnoreCase))
                {
                    encoraDate.Time = "evening";
                }
            }

            string? master = null;
            if (root != null)
            {
                master = root.Element("director")?.Value;
            }

            if (string.IsNullOrWhiteSpace(master))
            {
                var masterMatch = Regex.Match(info.Path, @"~\s*([^{}\(\)\[\]]+?)(?:\s*[\{\(\[]|$)");
                if (masterMatch.Success)
                {
                    master = masterMatch.Groups[1].Value.Trim();
                }
            }

            string? tour = null;
            if (root != null)
            {
                tour = root.Element("tagline")?.Value;
                if (string.IsNullOrWhiteSpace(tour))
                {
                    var titleVal = root.Element("title")?.Value;
                    if (!string.IsNullOrWhiteSpace(titleVal))
                    {
                        var tourMatch = Regex.Match(titleVal, @"\(([^-\)]+?)(?:\s*-\s*[^)]*)?\)");
                        if (tourMatch.Success)
                        {
                            tour = tourMatch.Groups[1].Value.Trim();
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(tour))
            {
                tour = EncoraTourMarker.ReadTour(_logger, episodeDir);
            }

            var parentDir = Path.GetDirectoryName(episodeDir);
            if (string.IsNullOrWhiteSpace(tour) && !string.IsNullOrWhiteSpace(parentDir))
            {
                tour = EncoraTourMarker.ReadTour(_logger, parentDir);
            }

            string? show = null;
            if (_libraryManager.FindByPath(info.Path, isFolder: false) is Episode existingEp && existingEp.SeriesId != Guid.Empty)
            {
                show = _libraryManager.GetItemById(existingEp.SeriesId)?.Name;
            }

            if (string.IsNullOrWhiteSpace(show) && root != null)
            {
                var titleVal = root.Element("title")?.Value;
                if (!string.IsNullOrWhiteSpace(titleVal))
                {
                    var parenIdx = titleVal.IndexOf('(', StringComparison.Ordinal);
                    show = parenIdx > 0 ? titleVal.Substring(0, parenIdx).Trim() : titleVal.Trim();
                }
            }

            var plot = root?.Element("plot")?.Value;
            var venue = root?.Element("studio")?.Value;

            var recording = new EncoraRecording
            {
                Date = encoraDate,
                Master = master,
                Show = show,
                Tour = tour,
                Notes = plot,
                Metadata = new EncoraMetadata
                {
                    Venue = venue,
                    ShowDescription = plot
                }
            };

            var titleFormat = Plugin.Instance?.Configuration?.TvEpisodeTitleFormat ?? "{date}";
            var episodeTitle = FormatEpisodeTitle(titleFormat, recording, info.Path);
            if (string.IsNullOrWhiteSpace(episodeTitle) || episodeTitle.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                episodeTitle = root?.Element("title")?.Value ?? Path.GetFileNameWithoutExtension(info.Path);
            }

            var episode = new Episode
            {
                Name = episodeTitle,
                IndexNumber = EncoraDateHelper.ComputeDateIndexNumber(encoraDate, info.Path),
                ForcedSortName = EncoraDateHelper.BuildDateSortKey(encoraDate, info.Path),
            };

            if (DateTime.TryParse(encoraDate?.FullDate, out var dt))
            {
                episode.PremiereDate = dt;
                episode.ProductionYear = dt.Year;
            }
            else if (root != null && DateTime.TryParse(root.Element("premiered")?.Value, out var pDt))
            {
                episode.PremiereDate = pDt;
                episode.ProductionYear = pDt.Year;
            }
            else if (root != null && int.TryParse(root.Element("year")?.Value, out var y))
            {
                episode.ProductionYear = y;
            }

            EncoraOverviewGuard.ApplyOverview(
                episode,
                _libraryManager,
                info.Path,
                isFolder: false,
                !string.IsNullOrWhiteSpace(plot) ? plot : "No Notes",
                options.PreserveManualDescriptionEdits,
                _logger,
                info.Path);

            if (!string.IsNullOrWhiteSpace(venue))
            {
                episode.AddStudio(venue);
            }

            if (root != null)
            {
                foreach (var genreElem in root.Elements("genre"))
                {
                    if (!string.IsNullOrWhiteSpace(genreElem.Value))
                    {
                        episode.AddGenre(genreElem.Value);
                    }
                }

                foreach (var certElem in root.Elements("certification"))
                {
                    if (!string.IsNullOrWhiteSpace(certElem.Value))
                    {
                        episode.OfficialRating = "NFT";
                    }
                }
            }

            if (Regex.IsMatch(info.Path, @"{nftf?}", RegexOptions.IgnoreCase))
            {
                episode.OfficialRating = "NFT";
            }

            if (!string.IsNullOrWhiteSpace(master) && options.TaglineSource == "master")
            {
                episode.Tagline = master;
            }

            if (root != null)
            {
                foreach (var actorElem in root.Elements("actor"))
                {
                    var name = actorElem.Element("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.AddPerson(new PersonInfo
                        {
                            Name = name,
                            Role = actorElem.Element("role")?.Value,
                            ImageUrl = actorElem.Element("thumb")?.Value,
                            Type = PersonKind.Actor
                        });
                    }
                }
            }

            if (options.AddMasterDirector && !string.IsNullOrWhiteSpace(master))
            {
                result.AddPerson(new PersonInfo
                {
                    Name = master,
                    Type = PersonKind.Director
                });
            }

            _pendingEpisodeUpdates[info.Path] = episode;

            if (!string.IsNullOrWhiteSpace(tour))
            {
                var seasonTitleFormat = Plugin.Instance?.Configuration?.TvSeasonTitleFormat ?? "{tour}";
                await EncoraSeasonPatcher.PatchParentSeasonAsync(_libraryManager, _logger, info.Path, recording, seasonTitleFormat, cancellationToken).ConfigureAwait(false);
            }

            SpawnPostDelayEpisodeUpdate(info.Path, episode);

            result.Item = episode;
            result.HasMetadata = true;
            _logger.LogInformation("[Encora] [NFO] ✅ Successfully processed NFO metadata for episode {Path}: Name='{Name}', IndexNumber={IndexNumber}", info.Path, episode.Name, episode.IndexNumber);
            return result;
        }

        private void SpawnPostDelayEpisodeUpdate(string path, Episode episode)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        if (_libraryManager.FindByPath(path, isFolder: false) is Episode existingEpisode)
                        {
                            var changed = false;
                            if (!string.Equals(existingEpisode.Name, episode.Name, StringComparison.Ordinal))
                            {
                                existingEpisode.Name = episode.Name;
                                changed = true;
                            }

                            if (existingEpisode.IndexNumber != episode.IndexNumber)
                            {
                                existingEpisode.IndexNumber = episode.IndexNumber;
                                changed = true;
                            }

                            if (!string.Equals(existingEpisode.ForcedSortName, episode.ForcedSortName, StringComparison.Ordinal))
                            {
                                existingEpisode.ForcedSortName = episode.ForcedSortName;
                                changed = true;
                            }

                            if (episode.PremiereDate.HasValue && existingEpisode.PremiereDate != episode.PremiereDate)
                            {
                                existingEpisode.PremiereDate = episode.PremiereDate;
                                changed = true;
                            }

                            if (episode.ProductionYear.HasValue && episode.ProductionYear > 0 && existingEpisode.ProductionYear != episode.ProductionYear)
                            {
                                existingEpisode.ProductionYear = episode.ProductionYear;
                                changed = true;
                            }

                            if (episode.ParentIndexNumber.HasValue && existingEpisode.ParentIndexNumber != episode.ParentIndexNumber)
                            {
                                existingEpisode.ParentIndexNumber = episode.ParentIndexNumber;
                                changed = true;
                            }

                            if (episode.SeasonId != Guid.Empty && existingEpisode.SeasonId != episode.SeasonId)
                            {
                                existingEpisode.SeasonId = episode.SeasonId;
                                existingEpisode.SeasonName = episode.SeasonName;
                                if (episode.SeasonId != Guid.Empty && _libraryManager.GetItemById(episode.SeasonId) is Season targetSeason)
                                {
                                    existingEpisode.SetParent(targetSeason);
                                }

                                changed = true;
                            }

                            if (changed)
                            {
                                var parent = existingEpisode.SeasonId != Guid.Empty
                                    ? _libraryManager.GetItemById(existingEpisode.SeasonId)
                                    : (existingEpisode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(existingEpisode.SeriesId) : null);

                                if (parent != null)
                                {
                                    await _libraryManager.UpdateItemAsync(existingEpisode, parent, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                                    _logger.LogInformation("[Encora] ✅ Post-delay confirmed Episode in LibraryManager: Name='{Name}', S{Season}E{Index}", existingEpisode.Name, existingEpisode.ParentIndexNumber, existingEpisode.IndexNumber);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Encora] Error in post-delay Episode update for {Path}", path);
                    }
                },
                CancellationToken.None);
        }

        /// <summary>
        /// Applies metadata directly to the Episode item before it is saved by MetadataService.
        /// </summary>
        /// <param name="item">The episode item being refreshed.</param>
        /// <param name="options">The metadata refresh options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task returning the item update type.</returns>
        public async Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return ItemUpdateType.None;
            }

            if (Plugin.Instance?.Configuration?.EnableTvMatching != true)
            {
                return ItemUpdateType.None;
            }

            if (!EncoraLibraryScope.IsPathInScope(_libraryManager, item.Path, Plugin.Instance?.Configuration?.TvLibraryIds))
            {
                return ItemUpdateType.None;
            }

            var updateType = ItemUpdateType.None;

            if (_pendingEpisodeUpdates.TryRemove(item.Path, out var pending))
            {
                if (!string.IsNullOrWhiteSpace(pending.Name) && !string.Equals(item.Name, pending.Name, StringComparison.Ordinal))
                {
                    _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode Name from '{OldName}' to '{NewName}' for {Path}", item.Name, pending.Name, item.Path);
                    item.Name = pending.Name;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (pending.IndexNumber.HasValue && item.IndexNumber != pending.IndexNumber)
                {
                    _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode IndexNumber from {OldIndex} to {NewIndex} for {Path}", item.IndexNumber, pending.IndexNumber, item.Path);
                    item.IndexNumber = pending.IndexNumber;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (pending.ParentIndexNumber.HasValue && item.ParentIndexNumber != pending.ParentIndexNumber)
                {
                    _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode ParentIndexNumber from {OldIndex} to {NewIndex} for {Path}", item.ParentIndexNumber, pending.ParentIndexNumber, item.Path);
                    item.ParentIndexNumber = pending.ParentIndexNumber;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (pending.SeasonId != Guid.Empty && item.SeasonId != pending.SeasonId)
                {
                    _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode SeasonId from {OldSeasonId} to {NewSeasonId} ({SeasonName}) for {Path}", item.SeasonId, pending.SeasonId, pending.SeasonName, item.Path);
                    item.SeasonId = pending.SeasonId;
                    item.SeasonName = pending.SeasonName;
                    if (_libraryManager.GetItemById(pending.SeasonId) is Season targetSeason)
                    {
                        item.SetParent(targetSeason);
                    }

                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (!string.IsNullOrWhiteSpace(pending.ForcedSortName) && !string.Equals(item.ForcedSortName, pending.ForcedSortName, StringComparison.Ordinal))
                {
                    item.ForcedSortName = pending.ForcedSortName;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (pending.PremiereDate.HasValue && item.PremiereDate != pending.PremiereDate)
                {
                    item.PremiereDate = pending.PremiereDate;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (pending.ProductionYear.HasValue && pending.ProductionYear > 0 && item.ProductionYear != pending.ProductionYear)
                {
                    item.ProductionYear = pending.ProductionYear;
                    updateType |= ItemUpdateType.MetadataEdit;
                }

                if (item.IndexNumber.HasValue)
                {
                    var deduped = EncoraEpisodeIndexResolver.ResolveCollision(_libraryManager, item.SeasonId, item.Id, item.IndexNumber.Value);
                    if (deduped != item.IndexNumber.Value)
                    {
                        _logger.LogInformation("[Encora] [CustomProvider] Nudging Episode IndexNumber {OldIndex} -> {NewIndex} to avoid a sibling collision for {Path}", item.IndexNumber, deduped, item.Path);
                        item.IndexNumber = deduped;
                        updateType |= ItemUpdateType.MetadataEdit;
                    }
                }

                return updateType;
            }

            // Fallback: if GetMetadata wasn't called for this item in this pass, check if it's a non-encora item
            var encoraId = EncoraIdExtractor.ExtractEncoraId(_logger, item.Path);
            if (string.IsNullOrWhiteSpace(encoraId) && (item.Path.Contains("{ne", StringComparison.OrdinalIgnoreCase) || item.Path.Contains("!non-encora", StringComparison.OrdinalIgnoreCase)))
            {
                var nfoResult = await ParseNfoMetadata(new EpisodeInfo { Path = item.Path }, cancellationToken).ConfigureAwait(false);
                if (nfoResult.HasMetadata && nfoResult.Item != null)
                {
                    if (!string.IsNullOrWhiteSpace(nfoResult.Item.Name) && !string.Equals(item.Name, nfoResult.Item.Name, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode Name from '{OldName}' to '{NewName}' for {Path}", item.Name, nfoResult.Item.Name, item.Path);
                        item.Name = nfoResult.Item.Name;
                        updateType |= ItemUpdateType.MetadataEdit;
                    }

                    if (nfoResult.Item.IndexNumber.HasValue && item.IndexNumber != nfoResult.Item.IndexNumber)
                    {
                        _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode IndexNumber from {OldIndex} to {NewIndex} for {Path}", item.IndexNumber, nfoResult.Item.IndexNumber, item.Path);
                        item.IndexNumber = nfoResult.Item.IndexNumber;
                        updateType |= ItemUpdateType.MetadataEdit;
                    }

                    if (nfoResult.Item.ParentIndexNumber.HasValue && item.ParentIndexNumber != nfoResult.Item.ParentIndexNumber)
                    {
                        _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode ParentIndexNumber from {OldIndex} to {NewIndex} for {Path}", item.ParentIndexNumber, nfoResult.Item.ParentIndexNumber, item.Path);
                        item.ParentIndexNumber = nfoResult.Item.ParentIndexNumber;
                        updateType |= ItemUpdateType.MetadataEdit;
                    }

                    if (nfoResult.Item.SeasonId != Guid.Empty && item.SeasonId != nfoResult.Item.SeasonId)
                    {
                        _logger.LogInformation("[Encora] [CustomProvider] Overriding Episode SeasonId from {OldSeasonId} to {NewSeasonId} ({SeasonName}) for {Path}", item.SeasonId, nfoResult.Item.SeasonId, nfoResult.Item.SeasonName, item.Path);
                        item.SeasonId = nfoResult.Item.SeasonId;
                        item.SeasonName = nfoResult.Item.SeasonName;
                        if (_libraryManager.GetItemById(nfoResult.Item.SeasonId) is Season targetSeason)
                        {
                            item.SetParent(targetSeason);
                        }

                        updateType |= ItemUpdateType.MetadataEdit;
                    }

                    if (!string.IsNullOrWhiteSpace(nfoResult.Item.ForcedSortName) && !string.Equals(item.ForcedSortName, nfoResult.Item.ForcedSortName, StringComparison.Ordinal))
                    {
                        item.ForcedSortName = nfoResult.Item.ForcedSortName;
                        updateType |= ItemUpdateType.MetadataEdit;
                    }
                }
            }

            if (item.IndexNumber.HasValue)
            {
                var deduped = EncoraEpisodeIndexResolver.ResolveCollision(_libraryManager, item.SeasonId, item.Id, item.IndexNumber.Value);
                if (deduped != item.IndexNumber.Value)
                {
                    _logger.LogInformation("[Encora] [CustomProvider] Nudging Episode IndexNumber {OldIndex} -> {NewIndex} to avoid a sibling collision for {Path}", item.IndexNumber, deduped, item.Path);
                    item.IndexNumber = deduped;
                    updateType |= ItemUpdateType.MetadataEdit;
                }
            }

            return updateType;
        }

        /// <summary>
        /// If the episode has a thumb.png and its parent series does not yet have a backdrop,
        /// copies thumb.png to the series folder as backdrop.png.
        /// </summary>
        /// <param name="episodePath">The episode file path.</param>
        /// <param name="episodeDir">The episode directory containing thumb.png.</param>
        private void TrySetSeriesBackdropFromEpisodeThumb(string? episodePath, string? episodeDir)
        {
            if (string.IsNullOrWhiteSpace(episodePath) || string.IsNullOrWhiteSpace(episodeDir))
            {
                return;
            }

            try
            {
                var thumbPath = Path.Combine(episodeDir, "thumb.png");
                if (!File.Exists(thumbPath))
                {
                    return;
                }

                string? seriesPath = null;
                if (_libraryManager.FindByPath(episodePath, isFolder: false) is Episode episode && episode.SeriesId != Guid.Empty)
                {
                    var series = _libraryManager.GetItemById(episode.SeriesId) as Series;
                    seriesPath = series?.Path;
                }

                if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
                {
                    var dir = new DirectoryInfo(episodeDir);
                    while (dir?.Parent != null)
                    {
                        if (EncoraRecordingApplier.HasLocalPosterFile(dir.FullName))
                        {
                            seriesPath = dir.FullName;
                            break;
                        }

                        dir = dir.Parent;
                    }
                }

                if (!string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath))
                {
                    if (!EncoraRecordingApplier.HasLocalBackdropFile(seriesPath))
                    {
                        var targetBackdrop = Path.Combine(seriesPath, "backdrop.png");
                        File.Copy(thumbPath, targetBackdrop, overwrite: false);
                        _logger.LogInformation("[Encora] ✅ Set series backdrop for {SeriesPath} from episode thumb: {ThumbPath}", seriesPath, thumbPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Encora] Could not set series backdrop from episode thumb for {Path}", episodePath);
            }
        }
    }
}
