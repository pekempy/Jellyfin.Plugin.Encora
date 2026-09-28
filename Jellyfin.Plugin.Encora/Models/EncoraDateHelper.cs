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
        /// Computes a stable chronological-sort-order integer for a recording date, encoded so it reads
        /// as the date itself: <c>YYYYMMDD</c> (day/month <c>00</c> if unknown) followed by a 2-digit
        /// variant/Act suffix, e.g. <c>2026050100</c> for 2026-05-01, or <c>2026050010</c> for an unknown
        /// day with variant "1". Jellyfin's <c>IndexNumber</c> is a plain <c>int</c> - there's no way to
        /// show literal dashes in the episode-number badge - so this is the closest readable equivalent
        /// that still fits Int32 and sorts correctly. Jellyfin orders episodes within a season by
        /// <c>IndexNumber</c> specifically (not by <c>ForcedSortName</c>), so Episodes need an actual
        /// numeric index to sort chronologically. Seasons get their own chronological IndexNumber too
        /// (see <see cref="EncoraSeasonIndexResolver"/>) rather than this scheme, since Jellyfin's local
        /// folder-name scanner already assigns Seasons a raw digit and leaving IndexNumber unset does not
        /// clear that pre-existing, often nonsensical, folder-derived number.
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
            if (parts.Length == 0 || !int.TryParse(parts[0], out var year) || year is < 0 or > 9999)
            {
                return null;
            }

            var month = (date.MonthKnown && parts.Length > 1 && int.TryParse(parts[1], out var m)) ? Math.Clamp(m, 0, 99) : 0;
            var day = (date.DayKnown && parts.Length > 2 && int.TryParse(parts[2], out var d)) ? Math.Clamp(d, 0, 99) : 0;

            // Encora's own date_variant already disambiguates same-day recordings (matinee vs evening
            // included), so a matinee showing just bumps the variant digit by one rather than needing its
            // own digit slot - that's the only way a 2-digit suffix (the most Int32 has room for once
            // YYYYMMDD is the visible base) can still fit both variant and Act.
            var sessionBump = !string.IsNullOrWhiteSpace(date.Time) && date.Time.Equals("matinee", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var variantDigit = Math.Clamp(ParseVariantDigit(date.DateVariant) + sessionBump, 0, 9);

            var actDigit = 0;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var match = Regex.Match(path, @"Act\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var act))
                {
                    actDigit = Math.Clamp(act, 0, 9);
                }
            }

            // YYYYMMDD*100 (max ~2,026,123,100 for a 2026 date) stays comfortably under Int32.MaxValue
            // (2,147,483,647) for any date up to roughly the 22nd century - the old YYYYMMDD*1000 scheme
            // overflowed for every single date (e.g. 2022-05-01 silently wrapped to -1254335470).
            var yearMonthDay = (year * 10000) + (month * 100) + day;
            var suffix = (variantDigit * 10) + actDigit;
            return (yearMonthDay * 100) + suffix;
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
