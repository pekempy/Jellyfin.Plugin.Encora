namespace Jellyfin.Plugin.Encora.Configuration
{
    /// <summary>
    /// A non-Encora TV recording (no resolvable Encora ID under its season folder, and no
    /// <c>.encora-tour</c> marker yet) awaiting the admin to manually assign which tour/Season it
    /// belongs to via the plugin's config page. Once <see cref="AssignedTour"/> is filled in and the
    /// config page saved, Encora writes a <c>.encora-tour</c> marker file to <see cref="Path"/> and
    /// removes the entry from the pending list.
    /// </summary>
    public class EncoraPendingTourAssignment
    {
        /// <summary>
        /// Gets or sets the season folder path this assignment applies to.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Series (show) name, for display on the config page.
        /// </summary>
        public string SeriesName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a label identifying the specific recording, for display on the config page.
        /// </summary>
        public string RecordingLabel { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tour name the admin has assigned, or null/empty while still pending.
        /// </summary>
        public string? AssignedTour { get; set; }
    }
}
