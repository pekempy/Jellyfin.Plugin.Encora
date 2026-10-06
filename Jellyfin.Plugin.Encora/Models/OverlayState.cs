using System;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Persisted sidecar state written as <c>thumb.overlay-state.json</c> next to an episode's
    /// thumb.png, recording when the overlay was last applied or verified against StageMedia.
    /// </summary>
    public sealed class OverlayState
    {
        /// <summary>Gets or sets the UTC timestamp of the last overlay apply or verification.</summary>
        public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
    }
}
