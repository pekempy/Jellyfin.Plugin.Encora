namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Builds the shared <see cref="EncoraApplyOptions"/> for Music libraries from the plugin's Audio-scoped
    /// configuration. Centralized here since the Artist/Album/Track providers all need the same options.
    /// </summary>
    public static class EncoraAudioOptions
    {
        /// <summary>
        /// Builds the apply-options for Music libraries.
        /// </summary>
        /// <returns>The apply options.</returns>
        public static EncoraApplyOptions Build()
        {
            var config = Plugin.Instance?.Configuration;
            return new EncoraApplyOptions
            {
                DateReplaceChar = config?.AudioDateReplaceChar ?? "x",
                PreserveManualDescriptionEdits = config?.AudioPreserveManualDescriptionEdits ?? true,
                OverviewSource = config?.AudioOverviewSource ?? "description_notes",
                StudioSource = config?.AudioStudioSource ?? "venue",
                ProductionLocationSource = config?.AudioProductionLocationSource ?? "city",
                TaglineSource = config?.AudioTaglineSource ?? "tour",
                IncludeGenreTags = config?.AudioIncludeGenreTags ?? true,
                IncludeNftTag = config?.AudioIncludeNftTag ?? true,
                FetchPoster = config?.AudioFetchPoster ?? true,
            };
        }
    }
}
