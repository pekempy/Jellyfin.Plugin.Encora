using System;
using System.Linq;
using Jellyfin.Plugin.Encora.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Manages the config-page-driven "which tour does this non-Encora recording belong to" workflow:
    /// registers newly-discovered unresolved season folders, and turns admin-filled answers into
    /// <see cref="EncoraTourMarker"/> files once the config page is saved.
    /// </summary>
    public static class EncoraPendingTourResolver
    {
        /// <summary>
        /// Registers a season folder as needing a manual tour assignment, if it isn't already tracked.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="path">The season folder path.</param>
        /// <param name="seriesName">The Series (show) name, for display.</param>
        /// <param name="recordingLabel">A label identifying the specific recording, for display.</param>
        /// <returns><c>true</c> if a new entry was added (configuration should be saved).</returns>
        public static bool RegisterPending(PluginConfiguration config, string path, string seriesName, string recordingLabel)
        {
            ArgumentNullException.ThrowIfNull(config);

            if (config.PendingTourAssignments.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (path.Contains("{e-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            config.PendingTourAssignments.Add(new EncoraPendingTourAssignment
            {
                Path = path,
                SeriesName = seriesName,
                RecordingLabel = recordingLabel
            });
            return true;
        }

        /// <summary>
        /// Turns every filled-in <see cref="EncoraPendingTourAssignment.AssignedTour"/> into an on-disk
        /// <see cref="EncoraTourMarker"/> file, then removes it from the pending list. Called after the
        /// admin saves the config page.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <returns><c>true</c> if any assignment was resolved (configuration should be saved again).</returns>
        public static bool ResolveAssignments(PluginConfiguration config, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(logger);

            var invalid = config.PendingTourAssignments
                .Where(p => p.Path != null && p.Path.Contains("{e-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var bad in invalid)
            {
                config.PendingTourAssignments.Remove(bad);
            }

            var resolved = config.PendingTourAssignments
                .Where(p => !string.IsNullOrWhiteSpace(p.AssignedTour))
                .ToList();

            foreach (var assignment in resolved)
            {
                EncoraTourMarker.WriteTour(logger, assignment.Path, assignment.AssignedTour!);
                config.PendingTourAssignments.Remove(assignment);
            }

            return invalid.Count > 0 || resolved.Count > 0;
        }
    }
}
