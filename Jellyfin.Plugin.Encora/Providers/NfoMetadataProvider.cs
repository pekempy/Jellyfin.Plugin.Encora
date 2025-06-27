using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Encora.Providers
{
    internal sealed class NfoMetadataProvider
    {
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();
            if (string.IsNullOrWhiteSpace(info.Path))
            {
                return result;
            }

            var movieDir = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrWhiteSpace(movieDir))
            {
                return result;
            }

            // Look for movie.nfo or <filename>.nfo
            var nfoPath = Path.Combine(movieDir, "movie.nfo");
            if (!File.Exists(nfoPath))
            {
                var fileNameNoExt = Path.GetFileNameWithoutExtension(info.Path);
                nfoPath = Path.Combine(movieDir, fileNameNoExt + ".nfo");
                if (!File.Exists(nfoPath))
                {
                    return result;
                }
            }

            string nfoContent;
            using (var reader = new StreamReader(nfoPath))
            {
                nfoContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var doc = XDocument.Parse(nfoContent);
            var movieElem = doc.Root;

            if (movieElem == null || !string.Equals(movieElem.Name.LocalName, "movie", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var movie = new Movie
            {
                Name = movieElem.Element("title")?.Value,
                Overview = movieElem.Element("plot")?.Value,
                OriginalTitle = movieElem.Element("originaltitle")?.Value,
                SortName = movieElem.Element("sorttitle")?.Value
            };

            if (DateTime.TryParse(movieElem.Element("premiered")?.Value, out var premiere))
            {
                movie.PremiereDate = premiere;
            }
            else if (DateTime.TryParse(movieElem.Element("releasedate")?.Value, out var release))
            {
                movie.PremiereDate = release;
            }

            if (int.TryParse(movieElem.Element("year")?.Value, out var year))
            {
                movie.ProductionYear = year;
            }

            // Studio
            var studio = movieElem.Element("studio")?.Value;
            if (!string.IsNullOrWhiteSpace(studio))
            {
                movie.AddStudio(studio);
            }

            // Genres (can be multiple)
            foreach (var genreElem in movieElem.Elements("genre"))
            {
                var genre = genreElem.Value;
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    movie.AddGenre(genre);
                }
            }

            // Certifications (only keeps last)
            foreach (var certElem in movieElem.Elements("certification"))
            {
                var cert = certElem.Value;
                if (!string.IsNullOrWhiteSpace(cert))
                {
                    movie.OfficialRating = "NFT";
                }
            }

            // Poster/Thumb
            var posterUrl = movieElem.Elements("thumb")
                .FirstOrDefault(e => (string?)e.Attribute("aspect") == "poster")?.Value;
            if (!string.IsNullOrWhiteSpace(posterUrl))
            {
                var image = new ItemImageInfo { Path = posterUrl, Type = ImageType.Primary };
                movie.AddImage(image);
            }

            // Actors
            foreach (var actorElem in movieElem.Elements("actor"))
            {
                var name = actorElem.Element("name")?.Value;
                var role = actorElem.Element("role")?.Value;
                var thumb = actorElem.Element("thumb")?.Value;
                var type = actorElem.Element("type")?.Value;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var person = new PersonInfo
                    {
                        Name = name,
                        Role = role,
                        ImageUrl = thumb,
                        Type = Data.Enums.PersonKind.Actor
                    };
                    result.AddPerson(person);
                }
            }

            result.HasMetadata = true;
            result.Item = movie;
            return result;
        }
    }
}
