using System.Collections.Generic;
using Jellyfin.Plugin.Encora.Providers;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Formats Artist/Album/Track titles for Music libraries from a fetched recording.
    /// </summary>
    public static class EncoraAudioTitleFormatter
    {
        /// <summary>
        /// Formats the Album title from the configured format. There's no {show} placeholder here -
        /// that's the Artist - so only date and tour/master variables are available.
        /// </summary>
        /// <param name="format">The format string, e.g. "{tour} - {date}".</param>
        /// <param name="recording">The recording data.</param>
        /// <param name="dateReplaceChar">Character used in place of an unknown day/month.</param>
        /// <returns>The formatted album title.</returns>
        public static string FormatAlbumTitle(string format, EncoraRecording recording, string dateReplaceChar)
        {
            var dateVariants = EncoraDateHelper.BuildDateVariants(recording.Date, dateReplaceChar);

            var variables = new Dictionary<string, string?>
            {
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
        /// Formats the Track title from the configured format when an Act number was detected in the
        /// file path. Callers should fall back to the Album title when no Act is present.
        /// </summary>
        /// <param name="format">The format string, e.g. "Act {act}".</param>
        /// <param name="actNumber">The detected act number.</param>
        /// <returns>The formatted track title.</returns>
        public static string FormatTrackTitle(string format, string actNumber)
        {
            var variables = new Dictionary<string, string?>
            {
                ["act"] = actNumber
            };

            return EncoraTitleFormatter.Format(format, variables);
        }
    }
}
