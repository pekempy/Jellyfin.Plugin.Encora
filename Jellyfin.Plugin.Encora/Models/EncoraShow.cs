using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a show fetched directly from Encora's show API, independent of any single recording -
    /// used once a Series has been manually identified/matched to an Encora show.
    /// </summary>
    public class EncoraShow
    {
        /// <summary>
        /// Gets or sets the unique identifier for the show.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the show's name.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the show's description (may contain HTML).
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
