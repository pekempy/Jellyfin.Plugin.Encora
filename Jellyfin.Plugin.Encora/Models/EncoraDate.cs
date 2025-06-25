using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents the date information for a recording.
    /// </summary>
    public class EncoraDate
    {
        /// <summary>
        /// Gets or sets the full date as a string (e.g., "2016-10-01").
        /// </summary>
        [JsonPropertyName("full_date")]
        public string? FullDate { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the month is known.
        /// </summary>
        [JsonPropertyName("month_known")]
        public bool MonthKnown { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the day is known.
        /// </summary>
        [JsonPropertyName("day_known")]
        public bool DayKnown { get; set; }

        /// <summary>
        /// Gets or sets the date variant (e.g., "preview", "matinee"), if any.
        /// </summary>
        [JsonPropertyName("date_variant")]
        public string? DateVariant { get; set; }

        /// <summary>
        /// Gets or sets the time of the recording (e.g., "evening").
        /// </summary>
        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }
}
