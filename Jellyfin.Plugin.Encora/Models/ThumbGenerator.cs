using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Provides thumbnail generation utilities.
    /// </summary>
    public sealed class ThumbGenerator
    {
        /// <summary>
        /// Gets the image response for a given URL.
        /// </summary>
        /// <param name="logger">logger instance.</param>
        /// <param name="mediaEncoder">mediaencoder instance.</param>
        /// <param name="movieDir">dir of the movie file.</param>
        /// <param name="mediaPath">path of the media file to extract the thumb from.</param>
        /// <param name="seekMinPercent">minimum percentage into the video to seek (0-100).</param>
        /// <param name="seekMaxPercent">maximum percentage into the video to seek (0-100).</param>
        /// <param name="isEpisode">whether this thumbnail is for an episode (uses {videoName}-thumb.png).</param>
        /// <returns name="Task">Task.</returns>
        public static async Task GenerateThumbPng(
            ILogger logger,
            IMediaEncoder mediaEncoder,
            string? movieDir,
            string mediaPath,
            int seekMinPercent = 15,
            int seekMaxPercent = 60,
            bool isEpisode = false)
        {
            // Extract thumbnail from the media file
            if (!string.IsNullOrWhiteSpace(movieDir))
            {
                var thumbFileName = isEpisode
                    ? CastThumbOverlay.GetThumbFileName(mediaPath)
                    : "thumb.png";
                var thumbPath = Path.Combine(movieDir, thumbFileName);

                if (isEpisode)
                {
                    // Migration: if legacy thumb.png exists, migrate it to the episode-specific filename
                    var legacyThumbPath = Path.Combine(movieDir, "thumb.png");
                    if (!File.Exists(thumbPath) && File.Exists(legacyThumbPath))
                    {
                        try
                        {
                            File.Move(legacyThumbPath, thumbPath, overwrite: true);
                            logger.LogInformation("[Encora] [Thumb] Migrated legacy thumb.png to {ThumbPath}", thumbPath);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "[Encora] [Thumb] Could not migrate legacy thumb.png to {ThumbPath}", thumbPath);
                        }
                    }

                    // Remove legacy thumb.png so Jellyfin never treats it as Season art
                    if (File.Exists(legacyThumbPath))
                    {
                        try
                        {
                            File.Delete(legacyThumbPath);
                        }
                        catch
                        {
                        }
                    }

                    // Remove any {name}-thumb.jpg so Jellyfin doesn't prefer it over {name}-thumb.png
                    var legacyJpg = Path.Combine(movieDir, Path.GetFileNameWithoutExtension(mediaPath) + "-thumb.jpg");
                    if (File.Exists(legacyJpg))
                    {
                        try
                        {
                            File.Delete(legacyJpg);
                        }
                        catch
                        {
                        }
                    }
                }

                if (!File.Exists(thumbPath))
                {
                    try
                    {
                        var ffmpegPath = mediaEncoder.EncoderPath;

                        // Determine video duration using ffmpeg (stderr parsing)
                        var duration = TimeSpan.FromMinutes(30); // fallback
                        try
                        {
                            var durationProcess = new System.Diagnostics.Process
                            {
                                StartInfo = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = ffmpegPath,
                                    Arguments = $"-i \"{mediaPath}\" -hide_banner",
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                }
                            };

                            durationProcess.Start();
                            var stderr = await durationProcess.StandardError.ReadToEndAsync().ConfigureAwait(false);
                            if (!await TryWaitForExitAsync(durationProcess, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
                            {
                                logger.LogWarning("[Encora] [Thumb] ⚠️ Timed out probing video duration for {Path}, using fallback duration", mediaPath);
                            }

                            var match = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration: (\d+):(\d+):(\d+)\.(\d+)");
                            if (match.Success)
                            {
                                var h = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                                var m = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                                var s = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                                duration = new TimeSpan(h, m, s);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "[Encora] [Thumb] ⚠️ Failed to determine video duration");
                        }

                        // Calculate seek time between the configured min/max percentage
                        var minFraction = Math.Clamp(seekMinPercent, 0, 100) / 100.0;
                        var maxFraction = Math.Clamp(Math.Max(seekMaxPercent, seekMinPercent), 0, 100) / 100.0;
                        var random = new Random();
                        var seekSeconds = duration.TotalSeconds * (minFraction + ((maxFraction - minFraction) * random.NextDouble()));
                        var seekTime = TimeSpan.FromSeconds(seekSeconds);

                        var thumbArgs = $"-ss {seekTime:hh\\:mm\\:ss} -i \"{mediaPath}\" -frames:v 1 -vf \"scale=1920:1080\" -y \"{thumbPath}\"";
                        logger.LogInformation("[Encora] [Thumb] Extracting thumb.png from media file {Path} at time {Time}", mediaPath, seekTime);

                        var process = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = ffmpegPath,
                                Arguments = thumbArgs,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };

                        process.Start();
                        if (!await TryWaitForExitAsync(process, TimeSpan.FromSeconds(45)).ConfigureAwait(false))
                        {
                            logger.LogWarning("[Encora] [Thumb] ⚠️ FFmpeg timed out generating thumb.png for {Path}, skipping thumbnail", mediaPath);
                            return;
                        }

                        if (process.ExitCode == 0 && File.Exists(thumbPath))
                        {
                            logger.LogInformation("[Encora] [Thumb] ✅ Successfully generated thumb.png at {ThumbPath}", thumbPath);
                        }
                        else
                        {
                            logger.LogWarning("[Encora] [Thumb] ⚠️ FFmpeg failed to generate thumb.png. Exit code: {ExitCode}", process.ExitCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[Encora] [Thumb] ❌ Exception while generating thumb.png");
                    }
                }
            }
        }

        /// <summary>
        /// Awaits process exit with a bounded timeout, killing the process tree if it hangs. FFmpeg has
        /// been observed to hang indefinitely probing/seeking certain video files - without a timeout,
        /// that hang blocks the calling metadata provider forever, silently discarding an otherwise fully
        /// resolved Name/Overview/Cast result that was never returned.
        /// </summary>
        /// <param name="process">The started process to wait on.</param>
        /// <param name="timeout">The maximum time to wait before killing the process.</param>
        /// <returns><c>true</c> if the process exited within the timeout; <c>false</c> if it was killed.</returns>
        private static async Task<bool> TryWaitForExitAsync(System.Diagnostics.Process process, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited between the timeout firing and us trying to kill it.
                }

                return false;
            }
        }
    }
}
