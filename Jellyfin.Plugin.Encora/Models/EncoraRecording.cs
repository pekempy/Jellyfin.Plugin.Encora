using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Encora.Models;

namespace Jellyfin.Plugin.Encora.Providers
{
    /// <summary>
    /// Represents the response structure from Encora.it's recording API.
    /// </summary>
    public class EncoraRecording
    {
        /// <summary>
        /// Gets or sets the unique identifier for the recording.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the name of the show.
        /// </summary>
        [JsonPropertyName("show")]
        public string? Show { get; set; }

        /// <summary>
        /// Gets or sets the tour name, if applicable.
        /// </summary>
        [JsonPropertyName("tour")]
        public string? Tour { get; set; }

        /// <summary>
        /// Gets or sets the date information for the recording.
        /// </summary>
        [JsonPropertyName("date")]
        public EncoraDate? Date { get; set; }

        /// <summary>
        /// Gets or sets the name of the master (recorder).
        /// </summary>
        [JsonPropertyName("master")]
        public string? Master { get; set; }

        /// <summary>
        /// Gets or sets NFT-related information for the recording.
        /// </summary>
        [JsonPropertyName("nft")]
        public EncoraNft? Nft { get; set; }

        /// <summary>
        /// Gets or sets the list of cast members for the recording.
        /// </summary>
        [JsonPropertyName("cast")]
        public IReadOnlyList<EncoraCastMember>? Cast { get; set; }

        /// <summary>
        /// Gets or sets the notes for the recording.
        /// </summary>
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Gets or sets the notes from the master (recorder).
        /// </summary>
        [JsonPropertyName("master_notes")]
        public string? MasterNotes { get; set; }

        /// <summary>
        /// Gets or sets the release format (e.g., MP4, MKV).
        /// </summary>
        [JsonPropertyName("release_format")]
        public string? ReleaseFormat { get; set; }

        /// <summary>
        /// Gets or sets additional metadata for the recording.
        /// </summary>
        [JsonPropertyName("metadata")]
        public EncoraMetadata? Metadata { get; set; }
    }
}
