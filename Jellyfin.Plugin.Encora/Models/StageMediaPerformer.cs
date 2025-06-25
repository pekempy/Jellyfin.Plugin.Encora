using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a performer in stage media.
    /// </summary>
    public class StageMediaPerformer
    {
        /// <summary>
        /// Gets or sets the unique identifier of the performer.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the URL associated with the performer.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
