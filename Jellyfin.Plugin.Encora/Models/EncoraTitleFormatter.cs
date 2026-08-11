using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Encora.Providers;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Substitutes {variable} placeholders in a format string. Shared by every title formatter
    /// (Movie, Episode, Series, Season, Artist, Album, Track) so the substitution logic lives in one place.
    /// </summary>
    public static class EncoraTitleFormatter
    {
        /// <summary>
        /// Replaces every <c>{key}</c> placeholder in <paramref name="format"/> with its value from
        /// <paramref name="variables"/> (empty string if the value is null), then trims the result.
        /// </summary>
        /// <param name="format">The format string, e.g. "{show} - {date}".</param>
        /// <param name="variables">The variable name/value pairs available for substitution.</param>
        /// <returns>The formatted string.</returns>
        public static string Format(string format, IReadOnlyDictionary<string, string?> variables)
        {
            foreach (var kvp in variables)
            {
                format = format.Replace("{" + kvp.Key + "}", kvp.Value ?? string.Empty, StringComparison.Ordinal);
            }

            return format.Trim();
        }

        /// <summary>
        /// Resolves the <c>{master}</c> variable's value. Encora leaves this blank when the recorder is
        /// genuinely unknown, but titles built from <c>{master}</c> should still read "Unknown" (matching
        /// the "~ Unknown" convention already used in most bootleg filenames) instead of dropping the
        /// segment silently.
        /// </summary>
        /// <param name="master">The recording's master/recorder name, as returned by Encora.</param>
        /// <returns>The master name, or "Unknown" if it wasn't set.</returns>
        public static string ResolveMaster(string? master)
        {
            return string.IsNullOrWhiteSpace(master) ? "Unknown" : master;
        }

        /// <summary>
        /// Formats a show-level title (used by TV Series and Music Artist, which both bootstrap from
        /// one recording of possibly many). Variables: {show}, {venue}, {city}.
        /// </summary>
        /// <param name="format">The format string, e.g. "{show}".</param>
        /// <param name="recording">The bootstrap recording.</param>
        /// <returns>The formatted title.</returns>
        public static string FormatShowTitle(string format, EncoraRecording recording)
        {
            return Format(format, new Dictionary<string, string?>
            {
                ["show"] = recording.Show,
                ["venue"] = recording.Metadata?.Venue,
                ["city"] = recording.Metadata?.City
            });
        }

        /// <summary>
        /// Formats a tour-level title (used by TV Season, which bootstraps from one recording of the tour).
        /// Variables: {tour}, {venue}, {city}.
        /// </summary>
        /// <param name="format">The format string, e.g. "{tour}".</param>
        /// <param name="recording">The bootstrap recording.</param>
        /// <returns>The formatted title.</returns>
        public static string FormatTourTitle(string format, EncoraRecording recording)
        {
            return Format(format, new Dictionary<string, string?>
            {
                ["tour"] = recording.Tour,
                ["venue"] = recording.Metadata?.Venue,
                ["city"] = recording.Metadata?.City
            });
        }
    }
}
