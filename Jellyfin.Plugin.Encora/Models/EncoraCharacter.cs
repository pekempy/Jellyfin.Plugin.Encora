using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a character in the cast.
    /// </summary>
    public class EncoraCharacter
    {
        /// <summary>
        /// Gets or sets the unique identifier for the character.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the name of the character.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the slug (URL-friendly name) for the character.
        /// </summary>
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>
        /// Gets or sets the URL for the character.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the order of the character in the cast list.
        /// </summary>
        [JsonPropertyName("order")]
        public int Order { get; set; }
    }
}
