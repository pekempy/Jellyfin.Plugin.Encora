using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a cast member in a recording.
    /// </summary>
    public class EncoraCastMember
    {
        /// <summary>
        /// Gets or sets the performer information.
        /// </summary>
        [JsonPropertyName("performer")]
        public EncoraPerformer? Performer { get; set; }

        /// <summary>
        /// Gets or sets the character information.
        /// </summary>
        [JsonPropertyName("character")]
        public EncoraCharacter? Character { get; set; }

        /// <summary>
        /// Gets or sets the status of the cast member (e.g., "understudy"), if any.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Maps the cast members to the metadata result.
        /// </summary>
        /// <param name="result">The metadata result to which the cast members will be added.</param>
        /// <param name="cast">The collection of cast members to map.</param>
        public static void MapCastToResult(MetadataResult<Movie> result, IEnumerable<EncoraCastMember> cast)
        {
            foreach (var castMember in cast)
            {
                var performerName = castMember.Performer?.Name;
                var characterName = castMember.Character?.Name;
                var performerId = castMember.Performer?.Id;

                string? performerIdString = performerId?.ToString(CultureInfo.InvariantCulture);
                performerIdString = string.IsNullOrWhiteSpace(performerIdString) ? performerName : performerIdString;

                if (!string.IsNullOrWhiteSpace(performerName))
                {
                    var personInfo = new PersonInfo
                    {
                        Type = PersonKind.Actor,
                        Name = castMember.Performer?.Name,
                        Role = castMember.Character?.Name
                    };

                    result.AddPerson(personInfo);
                }
            }
        }
    }
}
