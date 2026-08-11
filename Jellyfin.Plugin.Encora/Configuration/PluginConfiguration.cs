using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Encora.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EncoraAPIKey = string.Empty;
        StageMediaAPIKey = string.Empty;
        AutoRefreshIntervalHours = 0;

        EnableMovieMatching = true;
        MovieLibraryIds = new Collection<string>();
        MovieTitleFormat = "{show} - {date}";
        MovieDateReplaceChar = "x";
        MovieAddMasterDirector = false;
        MoviePreserveManualDescriptionEdits = true;
        MovieOverviewSource = "description_notes";
        MovieTaglineSource = "tour";
        MovieStudioSource = "venue";
        MovieProductionLocationSource = "city";
        MovieIncludeGenreTags = true;
        MovieIncludeNftTag = true;
        MovieDownloadSubtitles = true;
        MovieFetchPoster = true;
        MovieGenerateThumbnail = true;
        MovieThumbnailSeekMinPercent = 15;
        MovieThumbnailSeekMaxPercent = 60;

        EnableTvMatching = false;
        TvLibraryIds = new Collection<string>();
        TvSeriesTitleFormat = "{show}";
        TvSeasonTitleFormat = "{tour}";
        TvEpisodeTitleFormat = "{date}";
        TvDateReplaceChar = "x";
        TvAddMasterDirector = false;
        TvPreserveManualDescriptionEdits = true;
        TvOverviewSource = "notes";
        TvTaglineSource = "tour";
        TvStudioSource = "venue";
        TvProductionLocationSource = "city";
        TvIncludeGenreTags = true;
        TvIncludeNftTag = true;
        TvDownloadSubtitles = true;
        TvFetchPoster = true;
        TvGenerateThumbnail = true;
        TvThumbnailSeekMinPercent = 15;
        TvThumbnailSeekMaxPercent = 60;

        EnableAudioMatching = false;
        AudioLibraryIds = new Collection<string>();
        AudioArtistTitleFormat = "{show}";
        AudioAlbumTitleFormat = "{tour} - {date}";
        AudioTrackTitleFormat = "Act {act}";
        AudioRespectEmbeddedTags = true;
        AudioDateReplaceChar = "x";
        AudioPreserveManualDescriptionEdits = true;
        AudioOverviewSource = "notes";
        AudioTaglineSource = "tour";
        AudioStudioSource = "venue";
        AudioProductionLocationSource = "city";
        AudioIncludeGenreTags = true;
        AudioIncludeNftTag = true;
        AudioFetchPoster = true;
    }

    /// <summary>
    ///  Gets or sets the Encora API Key.
    /// </summary>
    public string EncoraAPIKey { get; set; }

    /// <summary>
    /// Gets or sets the StageMedia API key.
    /// </summary>
    public string StageMediaAPIKey { get; set; }

    /// <summary>
    ///  Gets or sets the number of hours between automatic re-refreshes of already-matched items (0 = disabled).
    /// </summary>
    public int AutoRefreshIntervalHours { get; set; }

    // ----- Videos - Movie Library -----

    /// <summary>
    ///  Gets or sets a value indicating whether Movie libraries should be matched against Encora.
    /// </summary>
    public bool EnableMovieMatching { get; set; }

    /// <summary>
    ///  Gets or sets the specific Movie library IDs to match against Encora. Empty means every Movie library.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public Collection<string> MovieLibraryIds { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only

    /// <summary>
    /// Gets or sets the Movie Title Format. Variables: {show}, {date}, {date_usa}, {date_iso}, {date_numeric}, {tour}, {master}.
    /// </summary>
    public string MovieTitleFormat { get; set; }

    /// <summary>
    ///  Gets or sets the date replace character used for Movie libraries.
    /// </summary>
    public string MovieDateReplaceChar { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the master should be added as a director, for Movie libraries.
    /// </summary>
    public bool MovieAddMasterDirector { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether manually edited descriptions should be preserved, for Movie libraries.
    /// </summary>
    public bool MoviePreserveManualDescriptionEdits { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Overview, for Movie libraries. One of: description_notes, description, notes, none.
    /// </summary>
    public string MovieOverviewSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Tagline, for Movie libraries. One of: tour, venue, city, master, recording_type, none.
    /// </summary>
    public string MovieTaglineSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Studio, for Movie libraries. One of: venue, city, none.
    /// </summary>
    public string MovieStudioSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Production Location, for Movie libraries. One of: city, venue, none.
    /// </summary>
    public string MovieProductionLocationSource { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether genre tags (recording type, amount recorded, boot camp, subtitled, concert) should be added, for Movie libraries.
    /// </summary>
    public bool MovieIncludeGenreTags { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the NFT tag/rating should be added, for Movie libraries.
    /// </summary>
    public bool MovieIncludeNftTag { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether subtitles should be downloaded, for Movie libraries.
    /// </summary>
    public bool MovieDownloadSubtitles { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a StageMedia poster should be fetched, for Movie libraries.
    /// </summary>
    public bool MovieFetchPoster { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a thumb.png should be generated for Continue Watching, for Movie libraries.
    /// </summary>
    public bool MovieGenerateThumbnail { get; set; }

    /// <summary>
    ///  Gets or sets the minimum percentage into the video to seek when generating a thumbnail, for Movie libraries.
    /// </summary>
    public int MovieThumbnailSeekMinPercent { get; set; }

    /// <summary>
    ///  Gets or sets the maximum percentage into the video to seek when generating a thumbnail, for Movie libraries.
    /// </summary>
    public int MovieThumbnailSeekMaxPercent { get; set; }

    // ----- Videos - TV Library -----

    /// <summary>
    ///  Gets or sets a value indicating whether TV libraries should be matched against Encora.
    /// </summary>
    public bool EnableTvMatching { get; set; }

    /// <summary>
    ///  Gets or sets the specific TV library IDs to match against Encora. Empty means every TV library.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public Collection<string> TvLibraryIds { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only

    /// <summary>
    /// Gets or sets the Series Title Format, used for TV libraries. Variables: {show}, {venue}, {city}.
    /// </summary>
    public string TvSeriesTitleFormat { get; set; }

    /// <summary>
    /// Gets or sets the Season Title Format, used for TV libraries. Variables: {tour}, {venue}, {city}.
    /// </summary>
    public string TvSeasonTitleFormat { get; set; }

    /// <summary>
    /// Gets or sets the Episode Title Format, used for TV libraries. Variables: {date}, {date_usa}, {date_iso}, {date_numeric}, {master}, {venue}, {city}.
    /// </summary>
    public string TvEpisodeTitleFormat { get; set; }

    /// <summary>
    ///  Gets or sets the date replace character used for TV libraries.
    /// </summary>
    public string TvDateReplaceChar { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the master should be added as a director, for TV libraries.
    /// </summary>
    public bool TvAddMasterDirector { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether manually edited descriptions should be preserved, for TV libraries.
    /// </summary>
    public bool TvPreserveManualDescriptionEdits { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Episode Overview. The Series overview always uses the
    ///  full show description regardless of this setting. One of: description_notes, description, notes, none.
    /// </summary>
    public string TvOverviewSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the episode Tagline, for TV libraries. One of: tour, venue, city, master, recording_type, none.
    /// </summary>
    public string TvTaglineSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Studio, for TV libraries. One of: venue, city, none.
    /// </summary>
    public string TvStudioSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Production Location, for TV libraries. One of: city, venue, none.
    /// </summary>
    public string TvProductionLocationSource { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether genre tags (recording type, amount recorded, boot camp, subtitled, concert) should be added, for TV libraries.
    /// </summary>
    public bool TvIncludeGenreTags { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the NFT tag/rating should be added, for TV libraries.
    /// </summary>
    public bool TvIncludeNftTag { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether subtitles should be downloaded, for TV libraries.
    /// </summary>
    public bool TvDownloadSubtitles { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a StageMedia poster should be fetched for the Series, for TV libraries.
    /// </summary>
    public bool TvFetchPoster { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a thumb.png should be generated for Continue Watching, for TV libraries.
    /// </summary>
    public bool TvGenerateThumbnail { get; set; }

    /// <summary>
    ///  Gets or sets the minimum percentage into the video to seek when generating a thumbnail, for TV libraries.
    /// </summary>
    public int TvThumbnailSeekMinPercent { get; set; }

    /// <summary>
    ///  Gets or sets the maximum percentage into the video to seek when generating a thumbnail, for TV libraries.
    /// </summary>
    public int TvThumbnailSeekMaxPercent { get; set; }

    // ----- Audios - Music Library -----

    /// <summary>
    ///  Gets or sets a value indicating whether Music libraries should be matched against Encora.
    /// </summary>
    public bool EnableAudioMatching { get; set; }

    /// <summary>
    ///  Gets or sets the specific Music library IDs to match against Encora. Empty means every Music library.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public Collection<string> AudioLibraryIds { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only

    /// <summary>
    /// Gets or sets the Artist Title Format, used for Music libraries. Variables: {show}, {venue}, {city}.
    /// </summary>
    public string AudioArtistTitleFormat { get; set; }

    /// <summary>
    /// Gets or sets the Album Title Format, used for Music libraries. Variables: {date}, {date_usa}, {date_iso}, {date_numeric}, {tour}, {master}, {venue}, {city}.
    /// </summary>
    public string AudioAlbumTitleFormat { get; set; }

    /// <summary>
    /// Gets or sets the Track Title Format, used for Music libraries when an Act is detected in the filename. Variables: {act}. Falls back to the Album title when no Act is detected.
    /// </summary>
    public string AudioTrackTitleFormat { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a track's existing embedded title/track-number/cover art (e.g. from ID3 tags) should be kept instead of being overwritten by Encora/StageMedia.
    /// </summary>
    public bool AudioRespectEmbeddedTags { get; set; }

    /// <summary>
    ///  Gets or sets the date replace character used for Music libraries.
    /// </summary>
    public string AudioDateReplaceChar { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether manually edited descriptions should be preserved, for Music libraries.
    /// </summary>
    public bool AudioPreserveManualDescriptionEdits { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Album Overview. The Artist overview always uses the
    ///  full show description regardless of this setting. One of: description_notes, description, notes, none.
    /// </summary>
    public string AudioOverviewSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Tagline, for Music libraries. One of: tour, venue, city, master, recording_type, none.
    /// </summary>
    public string AudioTaglineSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Studio, for Music libraries. One of: venue, city, none.
    /// </summary>
    public string AudioStudioSource { get; set; }

    /// <summary>
    ///  Gets or sets which Encora field feeds the Production Location, for Music libraries. One of: city, venue, none.
    /// </summary>
    public string AudioProductionLocationSource { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether genre tags (recording type, amount recorded, boot camp, subtitled, concert) should be added, for Music libraries.
    /// </summary>
    public bool AudioIncludeGenreTags { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the NFT tag/rating should be added, for Music libraries.
    /// </summary>
    public bool AudioIncludeNftTag { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether a StageMedia poster should be fetched for the Artist/Album, for Music libraries.
    /// </summary>
    public bool AudioFetchPoster { get; set; }
}
