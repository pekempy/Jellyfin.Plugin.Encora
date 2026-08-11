using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.ScheduledTasks
{
    /// <summary>
    /// Periodically re-refreshes items already matched by Encora, so subtitle/cast/NFT-status changes on
    /// Encora's side get picked up without the admin having to manually "Replace all metadata".
    /// </summary>
    public class EncoraRefreshTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<EncoraRefreshTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraRefreshTask"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="providerManager">The provider manager.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The logger.</param>
        public EncoraRefreshTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<EncoraRefreshTask> logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Refresh Encora Metadata";

        /// <inheritdoc />
        public string Key => "EncoraRefreshTask";

        /// <inheritdoc />
        public string Description => "Re-fetches metadata from Encora for items already matched, picking up subtitle, cast, and NFT-status changes.";

        /// <inheritdoc />
        public string Category => "Encora";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => (Plugin.Instance?.Configuration?.AutoRefreshIntervalHours ?? 0) > 0;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var hours = Plugin.Instance?.Configuration?.AutoRefreshIntervalHours ?? 0;
            if (hours <= 0)
            {
                yield break;
            }

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            };
        }

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
                HasAnyProviderId = new Dictionary<string, string> { ["EncoraRecordingId"] = string.Empty },
                Recursive = true
            };

            var items = _libraryManager.GetItemList(query);
            _logger.LogInformation("[Encora] Auto-refresh: found {Count} previously-matched items to refresh", items.Count);

            var directoryService = new DirectoryService(_fileSystem);
            var total = items.Count;

            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var options = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false,
                    IsAutomated = true,
                };

                _providerManager.QueueRefresh(items[i].Id, options, RefreshPriority.Low);

                progress.Report(100.0 * (i + 1) / total);
            }

            return Task.CompletedTask;
        }
    }
}
