namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// The four date presentation variants: long-form, ISO, US-order, and day-first numeric.
    /// </summary>
    /// <param name="Long">"December 31, 2024" style.</param>
    /// <param name="Iso">"2024-12-31" style.</param>
    /// <param name="Usa">"12-31-2024" style.</param>
    /// <param name="Numeric">"31-12-2024" style.</param>
    public readonly record struct EncoraDateVariants(string? Long, string? Iso, string? Usa, string? Numeric);
}
