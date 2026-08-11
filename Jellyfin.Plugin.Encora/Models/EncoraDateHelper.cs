using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Builds date-variant strings and ordering numbers from an <see cref="EncoraDate"/>, shared by
    /// Movie title formatting and TV Season/Episode metadata.
    /// </summary>
    public static class EncoraDateHelper
    {
        /// <summary>
        /// Builds the four date-variant strings for a recording date, applying the date-variant/matinee/Act suffixes.
        /// </summary>
        /// <param name="date">The recording date.</param>
        /// <param name="dateReplaceChar">Character used in place of an unknown day/month.</param>
        /// <param name="actSuffix">If set, appends " (Act N)" to every variant (used for TV episode titles, which have no {show} to attach the Act suffix to).</param>
        /// <returns>The computed date variants.</returns>
        public static EncoraDateVariants BuildDateVariants(EncoraDate? date, string dateReplaceChar, string? actSuffix = null)
        {
            string? dateLong = null;
            string? dateIso = null;
            string? dateUsa = null;
            string? dateNumeric = null;

            if (date != null && !string.IsNullOrWhiteSpace(date.FullDate))
            {
                var parts = date.FullDate.Split('-');
                var year = parts.Length > 0 ? parts[0] : string.Empty;
                var month = (parts.Length > 1 && date.MonthKnown) ? parts[1] : new string(dateReplaceChar[0], 2);
                var day = (parts.Length > 2 && date.DayKnown) ? parts[2] : new string(dateReplaceChar[0], 2);

                // {date}: "December 31, 2024" or with replace char
                if (date.MonthKnown && date.DayKnown && int.TryParse(month, out var m) && int.TryParse(day, out var d) && int.TryParse(year, out var y))
                {
                    var dt = new DateTime(y, m, d);
                    dateLong = dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                }
                else if (date.MonthKnown && int.TryParse(month, out var m2) && int.TryParse(year, out var y2))
                {
                    var dt = new DateTime(y2, m2, 1);
                    dateLong = dt.ToString("MMMM", CultureInfo.InvariantCulture) + $" {day}, {year}";
                    if (!date.DayKnown)
                    {
                        dateLong = dt.ToString("MMMM", CultureInfo.InvariantCulture) + $" {new string(dateReplaceChar[0], 2)}, {year}";
                    }
                }
                else if (int.TryParse(year, out _))
                {
                    dateLong = $"{year}";
                }
                else
                {
                    dateLong = $"{year}-{month}-{day}";
                }

                // {date_iso}: "2024-12-31"
                dateIso = $"{year}-{month}-{day}";

                // {date_usa}: "12-31-2024"
                dateUsa = $"{month}-{day}-{year}";

                // {date_numeric}: "31-12-2024"
                dateNumeric = $"{day}-{month}-{year}";

                // Append variant if present
                if (!string.IsNullOrWhiteSpace(date.DateVariant))
                {
                    dateLong += $" ({date.DateVariant})";
                    dateIso += $" ({date.DateVariant})";
                    dateUsa += $" ({date.DateVariant})";
                    dateNumeric += $" ({date.DateVariant})";
                }

                // Append (matinee) if time is "matinee"
                if (!string.IsNullOrWhiteSpace(date.Time) && date.Time.Equals("matinee", StringComparison.OrdinalIgnoreCase))
                {
                    dateLong += " (matinée)";
                    dateIso += " (matinée)";
                    dateUsa += " (matinée)";
                    dateNumeric += " (matinée)";
                }

                // Append Act suffix, if requested (TV episodes only - movies attach Act to {show} instead)
                if (!string.IsNullOrWhiteSpace(actSuffix))
                {
                    dateLong += $" (Act {actSuffix})";
                    dateIso += $" (Act {actSuffix})";
                    dateUsa += $" (Act {actSuffix})";
                    dateNumeric += $" (Act {actSuffix})";
                }
            }

            return new EncoraDateVariants(dateLong, dateIso, dateUsa, dateNumeric);
        }

        /// <summary>
        /// Builds a stable, chronologically-sortable key for a recording date, for use as a Season/Episode's
        /// <c>ForcedSortName</c> so items order correctly without needing to compare against sibling
        /// Season/Episode items or expose a raw date as a nonsensical-looking episode/season number.
        /// </summary>
        /// <param name="date">The recording date.</param>
        /// <param name="path">The file path, used to detect an "Act N" suffix.</param>
        /// <returns>The computed sort key, or null if no usable date is available.</returns>
        public static string? BuildDateSortKey(EncoraDate? date, string? path)
        {
            if (date == null || string.IsNullOrWhiteSpace(date.FullDate))
            {
                return null;
            }

            var sessionDigit = !string.IsNullOrWhiteSpace(date.Time) && date.Time.Equals("matinee", StringComparison.OrdinalIgnoreCase) ? '1' : '0';
            var variantDigit = (char)('0' + Math.Clamp(ParseVariantDigit(date.DateVariant), 0, 9));

            var actDigit = '0';
            if (!string.IsNullOrWhiteSpace(path))
            {
                var match = Regex.Match(path, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var act))
                {
                    actDigit = (char)('0' + Math.Clamp(act, 0, 9));
                }
            }

            return $"{date.FullDate}-{sessionDigit}{variantDigit}{actDigit}";
        }

        /// <summary>
        /// Computes a stable chronological-sort-order integer for a recording date, encoded as YYYYMMDD
        /// with a trailing session/act digit. Jellyfin orders episodes within a season by <c>IndexNumber</c>
        /// specifically (not by <c>ForcedSortName</c>), so Episodes need an actual numeric index to sort
        /// chronologically - unlike Seasons, where there are normally few enough siblings that a numberless,
        /// name-only fallback is an acceptable trade for not showing a nonsensical-looking season number.
        /// </summary>
        /// <param name="date">The recording date.</param>
        /// <param name="path">The file path, used to detect an "Act N" suffix.</param>
        /// <returns>The computed index number, or null if no usable date is available.</returns>
        public static int? ComputeDateIndexNumber(EncoraDate? date, string? path)
        {
            if (date == null || string.IsNullOrWhiteSpace(date.FullDate))
            {
                return null;
            }

            var parts = date.FullDate.Split('-');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var year))
            {
                return null;
            }

            var month = (date.MonthKnown && parts.Length > 1 && int.TryParse(parts[1], out var m)) ? m : 1;
            var day = (date.DayKnown && parts.Length > 2 && int.TryParse(parts[2], out var d)) ? d : 1;

            var sessionDigit = !string.IsNullOrWhiteSpace(date.Time) && date.Time.Equals("matinee", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var variantDigit = Math.Clamp(ParseVariantDigit(date.DateVariant), 0, 9);

            var actDigit = 0;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var match = Regex.Match(path, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var act))
                {
                    actDigit = Math.Clamp(act, 0, 9);
                }
            }

            var yearMonthDay = (year * 10000) + (month * 100) + day;
            var suffix = (sessionDigit * 100) + (variantDigit * 10) + actDigit;
            return (yearMonthDay * 1000) + suffix;
        }

        /// <summary>
        /// Extracts a single sort-distinguishing digit from Encora's <c>date_variant</c> field (e.g. "2",
        /// "3" for same-day recordings Encora itself disambiguates), so two recordings sharing a date but
        /// differing only by variant don't collide onto the same sort key.
        /// </summary>
        /// <param name="dateVariant">The raw date_variant value.</param>
        /// <returns>The variant as a digit, or 0 if absent/non-numeric.</returns>
        private static int ParseVariantDigit(string? dateVariant)
        {
            return !string.IsNullOrWhiteSpace(dateVariant) && int.TryParse(dateVariant, out var variant) ? variant : 0;
        }
    }
}
