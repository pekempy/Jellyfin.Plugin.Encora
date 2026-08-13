namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Masks sensitive values (API keys) for logging, keeping only enough visible to recognize which key
    /// is which without exposing the value itself.
    /// </summary>
    public static class EncoraSecretMasking
    {
        /// <summary>
        /// Masks <paramref name="secret"/>, keeping only the first and last 3 characters visible.
        /// </summary>
        /// <param name="secret">The value to mask.</param>
        /// <returns>The masked value.</returns>
        public static string Mask(string? secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                return "(none)";
            }

            if (secret.Length <= 6)
            {
                return new string('*', secret.Length);
            }

            return $"{secret[..3]}...{secret[^3..]}";
        }
    }
}
