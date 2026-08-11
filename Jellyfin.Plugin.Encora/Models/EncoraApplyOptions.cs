namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// The set of per-library-type toggles controlling how much of a fetched recording gets applied to an item.
    /// Built once per provider from its own scoped (Movie/TV) plugin configuration section.
    /// </summary>
    public sealed class EncoraApplyOptions
    {
        /// <summary>
        /// Gets or sets the character used in place of an unknown day/month in date variables.
        /// </summary>
        public string DateReplaceChar { get; set; } = "x";

        /// <summary>
        /// Gets or sets a value indicating whether the recording master should be added as a Director.
        /// </summary>
        public bool AddMasterDirector { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a manually edited Overview should be preserved instead of overwritten.
        /// </summary>
        public bool PreserveManualDescriptionEdits { get; set; } = true;

        /// <summary>
        /// Gets or sets which Encora field feeds the Overview. One of: description_notes, description, notes, none.
        /// </summary>
        public string OverviewSource { get; set; } = "description_notes";

        /// <summary>
        /// Gets or sets which Encora field feeds the Studio. One of: venue, city, none.
        /// </summary>
        public string StudioSource { get; set; } = "venue";

        /// <summary>
        /// Gets or sets which Encora field feeds the Production Location. One of: city, venue, none.
        /// </summary>
        public string ProductionLocationSource { get; set; } = "city";

        /// <summary>
        /// Gets or sets which Encora field feeds the Tagline. One of: tour, venue, city, master, recording_type, none.
        /// </summary>
        public string TaglineSource { get; set; } = "tour";

        /// <summary>
        /// Gets or sets a value indicating whether recording-derived genre tags should be added.
        /// </summary>
        public bool IncludeGenreTags { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the NFT tag/rating should be added.
        /// </summary>
        public bool IncludeNftTag { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a StageMedia poster should be fetched/written.
        /// </summary>
        public bool FetchPoster { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a thumb.png should be generated for Continue Watching.
        /// </summary>
        public bool GenerateThumbnail { get; set; } = true;

        /// <summary>
        /// Gets or sets the minimum percentage into the video to seek when generating a thumbnail.
        /// </summary>
        public int ThumbnailSeekMinPercent { get; set; } = 15;

        /// <summary>
        /// Gets or sets the maximum percentage into the video to seek when generating a thumbnail.
        /// </summary>
        public int ThumbnailSeekMaxPercent { get; set; } = 60;
    }
}
