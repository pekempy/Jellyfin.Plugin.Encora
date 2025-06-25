using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents additional metadata for a recording.
    /// </summary>
    public class EncoraMetadata
    {
        /// <summary>
        /// Gets or sets the unique identifier for the show.
        /// </summary>
        [JsonPropertyName("show_id")]
        public int ShowId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is an opening performance.
        /// </summary>
        [JsonPropertyName("is_opening")]
        public bool IsOpening { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is a closing performance.
        /// </summary>
        [JsonPropertyName("is_closing")]
        public bool IsClosing { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is a preview performance.
        /// </summary>
        [JsonPropertyName("is_preview")]
        public bool IsPreview { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is a concert performance.
        /// </summary>
        [JsonPropertyName("is_concert")]
        public bool IsConcert { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this recording is not for sharing (NFS).
        /// </summary>
        [JsonPropertyName("is_nfs")]
        public bool IsNfs { get; set; }

        /// <summary>
        /// Gets or sets the venue name.
        /// </summary>
        [JsonPropertyName("venue")]
        public string? Venue { get; set; }

        /// <summary>
        /// Gets or sets the city where the recording took place.
        /// </summary>
        [JsonPropertyName("city")]
        public string? City { get; set; }

        /// <summary>
        /// Gets or sets the media type (e.g., "video").
        /// </summary>
        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }

        /// <summary>
        /// Gets or sets the recording type (e.g., "bootleg").
        /// </summary>
        [JsonPropertyName("recording_type")]
        public string? RecordingType { get; set; }

        /// <summary>
        /// Gets or sets the amount recorded (e.g., "complete").
        /// </summary>
        [JsonPropertyName("amount_recorded")]
        public string? AmountRecorded { get; set; }

        /// <summary>
        /// Gets or sets the gifting status.
        /// </summary>
        [JsonPropertyName("gifting_status")]
        public string? GiftingStatus { get; set; }

        /// <summary>
        /// Gets or sets the limited status.
        /// </summary>
        [JsonPropertyName("limited_status")]
        public string? LimitedStatus { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this recording is boot camp recommended.
        /// </summary>
        [JsonPropertyName("boot_camp_recommended")]
        public bool BootCampRecommended { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this recording has screenshots.
        /// </summary>
        [JsonPropertyName("has_screenshots")]
        public bool HasScreenshots { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this recording has subtitles.
        /// </summary>
        [JsonPropertyName("has_subtitles")]
        public bool HasSubtitles { get; set; }

        /// <summary>
        /// Gets or sets the number of owners of this recording.
        /// </summary>
        [JsonPropertyName("owners_count")]
        public int OwnersCount { get; set; }

        /// <summary>
        /// Gets or sets the number of users who want this recording.
        /// </summary>
        [JsonPropertyName("wanters_count")]
        public int WantersCount { get; set; }

        /// <summary>
        /// Gets or sets the show description (may contain HTML).
        /// </summary>
        [JsonPropertyName("show_description")]
        public string? ShowDescription { get; set; }
    }
}
