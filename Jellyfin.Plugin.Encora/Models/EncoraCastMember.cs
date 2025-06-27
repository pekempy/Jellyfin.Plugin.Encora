using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.LiveTv;

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
        public EncoraCastStatus? Status { get; set; }

        /// <summary>
        /// Maps the cast members to the metadata result.
        /// </summary>
        /// <param name="result">The metadata result to which the cast members will be added.</param>
        /// <param name="cast">The collection of cast members to map.</param>
        /// <param name="headshots">Optional collection of headshots associated with the cast members.</param>
        /// <param name="master">The master name to be added as a director, if applicable.</param>
        /// <param name="shouldAddMasterDirector">Indicates whether to add the master as a director.</param>
        public static void MapCastToResult(MetadataResult<Movie> result, IEnumerable<EncoraCastMember> cast, Collection<StageMediaPerformer> headshots, string? master, bool shouldAddMasterDirector)
        {
            var config = Plugin.Instance?.Configuration;
            var addedPeople = new List<PersonInfo>();

            foreach (var castMember in cast)
            {
                var performerName = castMember.Performer?.Name;
                var characterName = castMember.Character?.Name;
                var performerId = castMember.Performer?.Id;

                string? performerIdString = performerId?.ToString(CultureInfo.InvariantCulture);
                performerIdString = string.IsNullOrWhiteSpace(performerIdString) ? performerName : performerIdString;

                if (!string.IsNullOrWhiteSpace(performerName))
                {
                    string? role = characterName;
                    if (castMember.Status?.Abbreviation is { Length: > 0 })
                    {
                        role = $"{castMember.Status.Abbreviation} {characterName}";
                    }

                    var personInfo = new PersonInfo
                    {
                        Type = PersonKind.Actor,
                        Name = performerName,
                        Role = role
                    };

                    if (performerId > 0 && headshots != null && headshots.Any(h => h.Id == performerId))
                    {
                        personInfo.ImageUrl = headshots.FirstOrDefault(h => h.Id == performerId)?.Url;
                    }

                    addedPeople.Add(personInfo);
                }
            }

            // Add Master as Director if enabled
            if (shouldAddMasterDirector)
            {
                var normalizedMaster = master?.Trim().ToLowerInvariant() ?? string.Empty;
                var skipMasters = new HashSet<string> { "pro-shot", "house-cam", "press-reel", "soundboard" };

                if (!skipMasters.Contains(normalizedMaster))
                {
                    addedPeople.Add(new PersonInfo
                    {
                        Type = PersonKind.Director,
                        Name = master ?? string.Empty,
                        Role = "Director"
                    });
                }
            }

            foreach (var person in addedPeople)
            {
                result.AddPerson(person);
            }
        }
    }
}
