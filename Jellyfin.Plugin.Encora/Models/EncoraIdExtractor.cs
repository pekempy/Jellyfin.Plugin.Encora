using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Extracts an Encora recording ID from a file path, shared by Movie and Episode matching.
    /// </summary>
    public static class EncoraIdExtractor
    {
        /// <summary>
        /// Extracts the Encora ID for the recording at <paramref name="path"/>.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="path">The video file path.</param>
        /// <returns>The Encora ID if found; otherwise, null.</returns>
        public static string? ExtractEncoraId(ILogger logger, string path)
        {
            logger.LogInformation("[Encora] Extracting Encora ID from path: {Path}", path);

            // Try to extract from path
            var match = Regex.Match(path, @"{e-(\d+)}", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                logger.LogInformation("[Encora] Found Encora ID in path: {Path} - ID: {Id}", path, match.Groups[1].Value);
                return match.Groups[1].Value;
            }

            // Fallback: look for .encora-<id> file in the directory
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var files = Directory.GetFiles(directory, ".encora-*");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var fileMatch = Regex.Match(fileName, @"\.encora-(\d+)", RegexOptions.IgnoreCase);
                    if (fileMatch.Success)
                    {
                        logger.LogInformation("[Encora] Found Encora ID in file: {FileName} - ID: {Id}", fileName, fileMatch.Groups[1].Value);
                        return fileMatch.Groups[1].Value;
                    }
                }

                // Fallback: check for .encora-id file
                var encoraIdFile = Path.Combine(directory, ".encora-id");
                if (File.Exists(encoraIdFile))
                {
                    var id = File.ReadAllText(encoraIdFile).Trim();
                    logger.LogInformation("[Encora] Found Encora ID in .encora-id file: {Id}", id);
                    return string.IsNullOrWhiteSpace(id) ? null : id;
                }
            }

            logger.LogInformation("[Encora] No Encora ID found in path: {Path}", path);
            return null;
        }
    }
}
