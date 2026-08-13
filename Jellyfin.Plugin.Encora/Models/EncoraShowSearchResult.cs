using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a single result from Encora's show search API, used to power Jellyfin's Identify search
    /// for Series.
    /// </summary>
    public class EncoraShowSearchResult
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
        /// Gets or sets a poster image URL for the show, if Encora has one.
        /// </summary>
        [JsonPropertyName("poster_url")]
        public string? PosterUrl { get; set; }

        /// <summary>
        /// Gets or sets the show's premiere year.
        /// </summary>
        [JsonPropertyName("year")]
        public int? Year { get; set; }
    }
}
