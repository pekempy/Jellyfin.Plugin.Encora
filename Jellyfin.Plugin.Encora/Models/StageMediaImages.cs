using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents media images, including posters and performers.
    /// </summary>
    public class StageMediaImages
    {
#pragma warning disable CA2227 // Collection properties should be read only
        /// <summary>
        /// Gets or sets the collection of poster image URLs.
        /// </summary>
        [JsonPropertyName("posters")]
        public Collection<string>? Posters { get; set; }

        /// <summary>
        /// Gets or sets the collection of performers associated with the media.
        /// </summary>
        [JsonPropertyName("performers")]
        public Collection<StageMediaPerformer>? Performers { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only
    }
}
