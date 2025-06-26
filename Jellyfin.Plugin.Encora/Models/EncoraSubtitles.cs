using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a subtitle associated with a recording in the Encora plugin.
    /// </summary>
    public class EncoraSubtitles
    {
        /// <summary>
        /// Gets or sets the recording ID associated with the subtitle.
        /// </summary>
        [JsonPropertyName("recording_id")]
        public int RecordingId { get; set; }

        /// <summary>
        /// Gets or sets the language of the subtitle.
        /// </summary>
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        /// <summary>
        /// Gets or sets the author of the subtitle.
        /// </summary>
        [JsonPropertyName("author")]
        public string? Author { get; set; }

        /// <summary>
        /// Gets or sets the file type of the subtitle.
        /// </summary>
        [JsonPropertyName("file_type")]
        public string? FileType { get; set; }

        /// <summary>
        /// Gets or sets the URL where the subtitle file can be accessed.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
