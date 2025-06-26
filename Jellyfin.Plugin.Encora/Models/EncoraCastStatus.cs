using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents the cast status in the Encora plugin.
    /// </summary>
    public class EncoraCastStatus
    {
        /// <summary>
        /// Gets or sets the label of the cast status.
        /// </summary>
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        /// <summary>
        /// Gets or sets the abbreviation of the cast status.
        /// </summary>
        [JsonPropertyName("abbreviation")]
        public string? Abbreviation { get; set; }
    }
}
