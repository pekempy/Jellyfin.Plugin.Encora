using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Scans a Series/Season/Artist/Album folder for a media file bearing an Encora ID, used to bootstrap
    /// show/tour-level metadata since those lookups aren't tied to a single file.
    /// </summary>
    public static class EncoraFolderScanner
    {
        /// <summary>
        /// Video file extensions considered when scanning Movie/TV folders.
        /// </summary>
        public static readonly IReadOnlyList<string> VideoExtensions = new[] { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".vob", ".ifo", ".iso" };

        /// <summary>
        /// Audio file extensions considered when scanning Music folders.
        /// </summary>
        public static readonly IReadOnlyList<string> AudioExtensions = new[] { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac" };

        /// <summary>
        /// Finds the Encora ID of the first (alphabetically, for determinism) media file within
        /// <paramref name="folderPath"/> or its subfolders that carries a resolvable Encora ID.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="folderPath">The folder to scan.</param>
        /// <param name="extensions">The media file extensions to consider. Defaults to video extensions.</param>
        /// <returns>The Encora ID if one was found; otherwise, null.</returns>
        public static string? FindFirstEncoraId(ILogger logger, string folderPath, IReadOnlyList<string>? extensions = null)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return null;
            }

            var allowedExtensions = extensions ?? VideoExtensions;

            var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var id = EncoraIdExtractor.ExtractEncoraId(logger, file);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            logger.LogInformation("[Encora] No Encora ID found in any media file under folder: {FolderPath}", folderPath);
            return null;
        }

        /// <summary>
        /// Checks whether any audio file within <paramref name="folderPath"/> already carries embedded
        /// cover art (e.g. an ID3 APIC frame), so an Album poster download can be skipped rather than
        /// overwriting/competing with artwork the user already has. Shells out to ffprobe directly (like
        /// <see cref="ThumbGenerator"/> does) rather than going through Jellyfin's own media-stream lookup,
        /// since that internal API has proven to drift across Jellyfin versions.
        /// </summary>
        /// <param name="mediaEncoder">Used to locate the ffprobe binary.</param>
        /// <param name="folderPath">The folder to scan.</param>
        /// <returns>True if any audio file in the folder has an embedded image stream.</returns>
        public static async Task<bool> FolderHasEmbeddedArtworkAsync(IMediaEncoder mediaEncoder, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                if (await FileHasEmbeddedArtworkAsync(mediaEncoder, file).ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> FileHasEmbeddedArtworkAsync(IMediaEncoder mediaEncoder, string filePath)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = mediaEncoder.ProbePath,
                        Arguments = $"-v quiet -select_streams v -show_entries stream_disposition=attached_pic -of csv=p=0 \"{filePath}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Any(line => line == "1");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
