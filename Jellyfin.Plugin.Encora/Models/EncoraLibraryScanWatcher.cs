using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Watches for Jellyfin's library scan task to finish and, when it does, runs Encora's duplicate-season
    /// cleanup as a "second pass" (see <see cref="EncoraSeasonDuplicateCleaner"/>).
    /// </summary>
    public class EncoraLibraryScanWatcher : IHostedService
    {
        private readonly ITaskManager _taskManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<EncoraLibraryScanWatcher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraLibraryScanWatcher"/> class.
        /// </summary>
        /// <param name="taskManager">Used to observe scheduled task completions.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger instance.</param>
        public EncoraLibraryScanWatcher(ITaskManager taskManager, ILibraryManager libraryManager, ILogger<EncoraLibraryScanWatcher> logger)
        {
            _taskManager = taskManager;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _taskManager.TaskCompleted += OnTaskCompleted;
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

            _logger.LogInformation("[Encora] 🔍 Library scan finished, running duplicate-season second pass");

            try
            {
                await EncoraSeasonDuplicateCleaner.RunAsync(_libraryManager, _logger, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Encora] Error running post-scan duplicate season cleanup");
            }
        }
    }
}
