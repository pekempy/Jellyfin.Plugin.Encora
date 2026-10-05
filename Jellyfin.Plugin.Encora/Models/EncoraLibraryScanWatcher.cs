using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Watches for Jellyfin's library scan task to finish and, when it does, runs Encora's duplicate-
    /// season cleanup (see <see cref="EncoraSeasonDuplicateCleaner"/>) followed by the same-tour Season
    /// merge (see <see cref="EncoraSeasonMerger"/>) as a "second pass". Also seeds the Encora collection
    /// cache on startup if it is empty or stale, so scans are served from cache immediately.
    /// </summary>
    public class EncoraLibraryScanWatcher : IHostedService
    {
        private readonly ITaskManager _taskManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EncoraLibraryScanWatcher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraLibraryScanWatcher"/> class.
        /// </summary>
        /// <param name="taskManager">Used to observe scheduled task completions.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="httpClientFactory">Used to seed the collection cache on startup.</param>
        /// <param name="logger">The logger instance.</param>
        public EncoraLibraryScanWatcher(ITaskManager taskManager, ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<EncoraLibraryScanWatcher> logger)
        {
            _taskManager = taskManager;
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _taskManager.TaskCompleted += OnTaskCompleted;

            // Seed the collection cache in the background if it's empty or stale,
            // without blocking Jellyfin's startup sequence.
            if (EncoraCollectionCache.IsStale())
            {
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            _logger.LogInformation("[Encora] 📥 Collection cache empty/stale on startup — seeding in background");
                            await EncoraCollectionCacheRefreshTask.RefreshAsync(_httpClientFactory, _logger).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Encora] Background collection cache seed failed — will retry on next scan or scheduled refresh");
                        }
                    },
                    CancellationToken.None);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
            return Task.CompletedTask;
        }

        private async void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
        {
            var task = e.Task.ScheduledTask;
            var isLibraryScan = string.Equals(task.Key, "RefreshLibrary", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(task.Category, "Library", StringComparison.OrdinalIgnoreCase)
                    && task.Name.Contains("Scan", StringComparison.OrdinalIgnoreCase));

            if (!isLibraryScan)
            {
                return;
            }

            _logger.LogInformation("[Encora] 🔍 Library scan finished, running season second passes");

            try
            {
                await EncoraSeasonDuplicateCleaner.RunAsync(_libraryManager, _logger, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error running post-scan duplicate season cleanup");
            }

            try
            {
                await EncoraSeasonMerger.RunAsync(_libraryManager, _logger, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error running post-scan season merge");
            }
        }
    }
}
