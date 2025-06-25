using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a performer in the cast.
    /// </summary>
    public class EncoraPerformer
    {
        /// <summary>
        /// Gets or sets the unique identifier for the performer.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the name of the performer.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the slug (URL-friendly name) for the performer.
        /// </summary>
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>
        /// Gets or sets the URL for the performer.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
