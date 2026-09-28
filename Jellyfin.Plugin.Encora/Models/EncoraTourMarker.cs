using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Reads/writes the <c>.encora-tour</c> marker file used to manually assign a tour/Season name to a
    /// non-Encora TV recording (one with no resolvable Encora ID under its season folder) - the TV
    /// equivalent of Movies' NFO fallback, since there's no Encora ID to look a tour name up from here.
    /// One marker file lives directly in the season folder and applies to every recording under it.
    /// </summary>
    public static class EncoraTourMarker
    {
        private const string MarkerFileName = ".encora-tour";

        /// <summary>
        /// Reads the tour name from a <c>.encora-tour</c> marker file in <paramref name="folderPath"/>, if present.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="folderPath">The season folder to look in.</param>
        /// <returns>The trimmed tour name, or null if no marker file exists or it's empty.</returns>
        public static string? ReadTour(ILogger logger, string folderPath)
        {
            var markerPath = Path.Combine(folderPath, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                return null;
            }

            try
            {
                var tour = File.ReadAllText(markerPath).Trim();
                if (string.IsNullOrWhiteSpace(tour))
                {
                    return null;
                }

                logger.LogInformation("[Encora] Found tour override '{Tour}' in {MarkerPath}", tour, markerPath);
                return tour;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "[Encora] Could not read tour marker file {MarkerPath}", markerPath);
                return null;
            }
        }

        /// <summary>
        /// Writes a <c>.encora-tour</c> marker file into <paramref name="folderPath"/>, so future scans
        /// pick up the tour name without needing the admin to assign it again via the config page.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="folderPath">The season folder to write into.</param>
        /// <param name="tour">The tour name to write.</param>
        public static void WriteTour(ILogger logger, string folderPath, string tour)
        {
            var markerPath = Path.Combine(folderPath, MarkerFileName);
            try
            {
                File.WriteAllText(markerPath, tour.Trim());
                logger.LogInformation("[Encora] Wrote tour override '{Tour}' to {MarkerPath}", tour, markerPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "[Encora] Could not write tour marker file {MarkerPath}", markerPath);
            }
        }
    }
}
