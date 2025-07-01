using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
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
        /// <param name="info">movie info.</param>
        /// <returns name="Task">Task.</returns>
        public static async Task GenerateThumbPng(ILogger logger, IMediaEncoder mediaEncoder, string? movieDir, MovieInfo info)
        {
            // Extract thumb.png from the media file
            if (!string.IsNullOrWhiteSpace(movieDir))
            {
                var thumbPath = Path.Combine(movieDir, "thumb.png");
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
                                    Arguments = $"-i \"{info.Path}\" -hide_banner",
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                }
                            };

                            durationProcess.Start();
                            var stderr = await durationProcess.StandardError.ReadToEndAsync().ConfigureAwait(false);
                            await durationProcess.WaitForExitAsync().ConfigureAwait(false);

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

                        // Calculate seek time between 15% and 60%
                        var random = new Random();
                        var seekSeconds = duration.TotalSeconds * (0.15 + (0.45 * random.NextDouble()));
                        var seekTime = TimeSpan.FromSeconds(seekSeconds);

                        var thumbArgs = $"-ss {seekTime:hh\\:mm\\:ss} -i \"{info.Path}\" -frames:v 1 -vf \"scale=1920:1080\" -y \"{thumbPath}\"";
                        logger.LogInformation("[Encora] [Thumb] Extracting thumb.png from media file {Path} at time {Time}", info.Path, seekTime);

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
                        var cancellationToken = default(CancellationToken);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
    }
}
