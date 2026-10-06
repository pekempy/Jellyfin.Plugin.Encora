namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents a single headshot source for thumbnail compositing.
    /// The local Jellyfin person-image cache is preferred; a StageMedia URL is used
    /// as a fallback when no locally cached image is available yet.
    /// </summary>
    public sealed class HeadshotSource
    {
        /// <summary>Gets the performer's display name.</summary>
        public string PerformerName { get; init; } = string.Empty;

        /// <summary>Gets the absolute path to the locally cached Jellyfin person image, or null.</summary>
        public string? LocalPath { get; init; }

        /// <summary>Gets the StageMedia headshot URL to fall back to when no local image exists, or null.</summary>
        public string? RemoteUrl { get; init; }

        /// <summary>Gets a value indicating whether any image source is available.</summary>
        public bool HasImage => LocalPath != null || RemoteUrl != null;
    }
}
