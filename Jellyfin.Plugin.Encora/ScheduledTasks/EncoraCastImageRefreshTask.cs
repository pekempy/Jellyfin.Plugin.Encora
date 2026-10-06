using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.ScheduledTasks
{
    /// <summary>
    /// Periodically checks whether StageMedia headshots for cast members have changed since the
    /// overlay was last applied. When a headshot differs (by SHA-256), the cached Jellyfin person
    /// image is updated and the episode's thumb.png is recomposited from its clean backup.
    /// Only runs when <c>TvOverlayCastOnThumb</c> is enabled; only processes episodes that have
    /// a <c>thumb.overlay-state.json</c> sidecar (written by the overlay on first apply).
    /// </summary>
    public class EncoraCastImageRefreshTask : IScheduledTask
    {
        private static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(48);

        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraCastImageRefreshTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraCastImageRefreshTask"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="httpClientFactory">HTTP client factory for StageMedia requests.</param>
        /// <param name="logger">Logger.</param>
        public EncoraCastImageRefreshTask(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<EncoraCastImageRefreshTask> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Refresh Cast Headshots on Thumbnails";

        /// <inheritdoc />
        public string Key => "EncoraCastImageRefreshTask";

        /// <inheritdoc />
        public string Description => "Checks StageMedia every 48 hours for updated performer headshots and recomposites episode thumbnails when changes are detected.";

        /// <inheritdoc />
        public string Category => "Encora";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(48).Ticks,
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.TvOverlayCastOnThumb != true)
            {
                _logger.LogInformation("[Encora] [CastImageRefresh] TvOverlayCastOnThumb is disabled — skipping.");
                return;
            }

            var apiKey = config.EncoraAPIKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("[Encora] [CastImageRefresh] No API key configured — skipping.");
                return;
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                HasAnyProviderId = new Dictionary<string, string> { ["EncoraRecordingId"] = string.Empty },
                Recursive = true,
            }).OfType<Episode>().ToList();

            var candidates = episodes
                .Where(ep =>
                {
                    var dir = Path.GetDirectoryName(ep.Path);
                    return !string.IsNullOrWhiteSpace(dir)
                        && File.Exists(Path.Combine(dir, "thumb.original.png"))
                        && CastThumbOverlay.IsOverlayStale(dir, StalenessThreshold);
                })
                .ToList();

            _logger.LogInformation(
                "[Encora] [CastImageRefresh] {Total} overlay episodes; {Stale} need re-checking.",
                episodes.Count,
                candidates.Count);

            if (candidates.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var http = _httpClientFactory.CreateClient();
            int done = 0;

            foreach (var episode in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dir = Path.GetDirectoryName(episode.Path)!;
                var encoraId = episode.GetProviderId("EncoraRecordingId");
                if (!string.IsNullOrWhiteSpace(encoraId))
                {
                    await ProcessEpisodeAsync(episode, dir, encoraId, apiKey, http, cancellationToken)
                        .ConfigureAwait(false);
                }

                done++;
                progress.Report(100.0 * done / candidates.Count);
            }

            _logger.LogInformation("[Encora] [CastImageRefresh] Done — checked {Count} episodes.", done);
        }

        private async Task ProcessEpisodeAsync(
            Episode episode,
            string dir,
            string encoraId,
            string apiKey,
            HttpClient http,
            CancellationToken cancellationToken)
        {
            var recording = await EncoraRecordingApplier.FetchRecordingAsync(
                _httpClientFactory, _logger, apiKey, encoraId, cancellationToken).ConfigureAwait(false);

            if (recording == null)
            {
                return;
            }

            var stageMedia = await EncoraRecordingApplier.FetchStageMediaImagesAsync(
                _httpClientFactory, _logger, recording, posterDestinationPath: null, cancellationToken)
                .ConfigureAwait(false);

            if (stageMedia == null || stageMedia.Count == 0)
            {
                await CastThumbOverlay.SaveOverlayStateAsync(dir, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool anyChanged = false;

            foreach (var sm in stageMedia)
            {
                if (string.IsNullOrWhiteSpace(sm.Url))
                {
                    continue;
                }

                var castMember = recording.Cast?.FirstOrDefault(c => c.Performer?.Id == sm.Id);
                var name = castMember?.Performer?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var person = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Person },
                    Name = name,
                    Limit = 1,
                }).OfType<Person>().FirstOrDefault();

                var imageInfo = person?.GetImageInfo(ImageType.Primary, 0);
                if (imageInfo?.Path == null || !File.Exists(imageInfo.Path))
                {
                    continue;
                }

                var newBytes = await CastThumbOverlay.FetchIfChangedAsync(
                    http, imageInfo.Path, sm.Url, cancellationToken).ConfigureAwait(false);

                if (newBytes != null)
                {
                    try
                    {
                        await File.WriteAllBytesAsync(imageInfo.Path, newBytes, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation(
                            "[Encora] [CastImageRefresh] Updated headshot for {Name} on episode {Episode}",
                            name,
                            episode.Name);
                        anyChanged = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Encora] [CastImageRefresh] Could not update headshot for {Name}", name);
                    }
                }
            }

            if (anyChanged)
            {
                var sources = CastThumbOverlay.GetHeadshotSources(
                    recording.Cast, _libraryManager, stageMedia);

                if (sources.Count > 0)
                {
                    await CastThumbOverlay.OverlayAsync(
                        _logger, dir, sources, _httpClientFactory, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "[Encora] [CastImageRefresh] Recomposited thumbnail for {Episode}", episode.Name);
                }
            }
            else
            {
                await CastThumbOverlay.SaveOverlayStateAsync(dir, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
