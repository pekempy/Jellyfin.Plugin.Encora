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
        private readonly ReadOnlyCollection<string> _posters;

        /// <summary>
        /// Initializes a new instance of the <see cref="StageMediaImages"/> class.
        /// </summary>
        public StageMediaImages()
        {
            _posters = new ReadOnlyCollection<string>(new List<string>());
            Performers = new ReadOnlyCollection<StageMediaPerformer>(new List<StageMediaPerformer>());
        }

        /// <summary>
        /// Gets the collection of poster image URLs.
        /// </summary>
        [JsonPropertyName("posters")]
        public ReadOnlyCollection<string> Posters => _posters;

        /// <summary>
        /// Gets the collection of performers associated with the media.
        /// </summary>
        [JsonPropertyName("performers")]
        public ReadOnlyCollection<StageMediaPerformer>? Performers { get; private set; }

        /// <summary>
        /// Sets the performers collection.
        /// </summary>
        /// <param name="performers">The collection of performers to set.</param>
        public void SetPerformers(Collection<StageMediaPerformer> performers)
        {
            Performers = new ReadOnlyCollection<StageMediaPerformer>(performers);
        }
    }
}
