using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Encora.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.ScheduledTasks
{
    /// <summary>
    /// Runs the duplicate-season cleanup (<see cref="EncoraSeasonDuplicateCleaner"/>) and same-tour Season
    /// merge (<see cref="EncoraSeasonMerger"/>) on demand, without waiting for a full "Scan Media Library"
    /// run. Both passes only read/write already-populated Season/Episode data already in Jellyfin's
    /// database - no Encora API calls, no file probing - so this is normally fast even on a large library,
    /// and useful to run right after a targeted refresh rather than a whole-server scan.
    /// </summary>
    public class EncoraSeasonMergeTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<EncoraSeasonMergeTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoraSeasonMergeTask"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        public EncoraSeasonMergeTask(ILibraryManager libraryManager, ILogger<EncoraSeasonMergeTask> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Merge Duplicate/Same-Tour Seasons";

        /// <inheritdoc />
        public string Key => "EncoraSeasonMergeTask";

        /// <inheritdoc />
        public string Description => "Cleans up scanner-race duplicate Seasons and merges Seasons that share a tour but live in separate recording folders, without needing a full library scan.";

        /// <inheritdoc />
        public string Category => "Encora";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield break;
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Encora] 🔍 On-demand season merge starting");

            await EncoraSeasonDuplicateCleaner.RunAsync(_libraryManager, _logger, cancellationToken).ConfigureAwait(false);
            progress.Report(50.0);

            await EncoraSeasonMerger.RunAsync(_libraryManager, _logger, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0);

            _logger.LogInformation("[Encora] ✅ On-demand season merge finished");
        }
    }
}
