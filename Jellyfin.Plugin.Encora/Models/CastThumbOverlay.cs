using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Composites cast headshots as an overlapping avatar stack onto a generated thumb.png.
    /// The original thumb is preserved as thumb.original.png before the first overlay is applied,
    /// so re-running always composites onto the clean original.
    /// </summary>
    public static class CastThumbOverlay
    {
        private const int MaxAvatars = 6;
        private const string BackupName = "thumb.original.png";
        private const string ThumbName = "thumb.png";

        /// <summary>
        /// Extracts headshot URLs from a StageMedia response in cast order, up to <see cref="MaxAvatars"/>.
        /// </summary>
        /// <param name="cast">The cast list from the Encora recording.</param>
        /// <param name="headshots">StageMedia performer headshots keyed by performer ID.</param>
        /// <returns>An ordered list of headshot URLs, at most <see cref="MaxAvatars"/> entries.</returns>
        public static IReadOnlyList<string> GetHeadshotUrls(
            IEnumerable<EncoraCastMember>? cast,
            Collection<StageMediaPerformer>? headshots)
        {
            if (cast == null || headshots == null || headshots.Count == 0)
            {
                return Array.Empty<string>();
            }

            var urls = new List<string>();
            foreach (var member in cast)
            {
                if (urls.Count >= MaxAvatars)
                {
                    break;
                }

                var pid = member.Performer?.Id ?? 0;
                if (pid <= 0)
                {
                    continue;
                }

                var match = headshots.FirstOrDefault(p => p.Id == pid);
                if (!string.IsNullOrWhiteSpace(match?.Url))
                {
                    urls.Add(match.Url!);
                }
            }

            return urls;
        }

        /// <summary>
        /// Downloads headshots and composites them as a circular avatar stack onto thumb.png.
        /// No-ops if no headshots are available or if thumb.png does not exist yet.
        /// </summary>
        /// <param name="httpClientFactory">HTTP client factory for downloading headshots.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="episodeDir">Directory containing the episode's thumb.png.</param>
        /// <param name="headshotUrls">Ordered list of headshot image URLs to composite.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous overlay operation.</returns>
        public static async Task OverlayAsync(
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            string episodeDir,
            IReadOnlyList<string> headshotUrls,
            CancellationToken cancellationToken)
        {
            if (headshotUrls.Count == 0)
            {
                return;
            }

            var thumbPath = Path.Combine(episodeDir, ThumbName);
            var backupPath = Path.Combine(episodeDir, BackupName);

            if (!File.Exists(thumbPath) && !File.Exists(backupPath))
            {
                return;
            }

            // Back up the original once so re-runs always composite onto clean source
            if (!File.Exists(backupPath) && File.Exists(thumbPath))
            {
                try
                {
                    File.Copy(thumbPath, backupPath, overwrite: false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] [CastOverlay] Could not back up thumb to {Path}", backupPath);
                    return;
                }
            }

            var sourcePath = File.Exists(backupPath) ? backupPath : thumbPath;

            // Download all headshots concurrently
            var http = httpClientFactory.CreateClient();
            var downloadTasks = headshotUrls.Select(async url =>
            {
                try
                {
                    return await http.GetByteArrayAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            });

            var allBytes = await Task.WhenAll(downloadTasks).ConfigureAwait(false);
            var headshotBitmaps = allBytes
                .Where(b => b != null)
                .Select(b => SKBitmap.Decode(b!))
                .Where(bmp => bmp != null)
                .ToList();

            if (headshotBitmaps.Count == 0)
            {
                return;
            }

            try
            {
                await CompositeAsync(logger, sourcePath, thumbPath, headshotBitmaps!, cancellationToken)
                    .ConfigureAwait(false);
                logger.LogInformation(
                    "[Encora] [CastOverlay] ✅ Overlaid {Count} cast avatars onto {Path}",
                    headshotBitmaps.Count,
                    thumbPath);
            }
            finally
            {
                foreach (var bmp in headshotBitmaps)
                {
                    bmp?.Dispose();
                }
            }
        }

        private static Task CompositeAsync(
            ILogger logger,
            string sourcePath,
            string destPath,
            List<SKBitmap> headshots,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                () =>
                {
                    var sourceBytes = File.ReadAllBytes(sourcePath);
                    using var sourceBitmap = SKBitmap.Decode(sourceBytes);
                    if (sourceBitmap == null)
                    {
                        logger.LogWarning("[Encora] [CastOverlay] Could not decode source image {Path}", sourcePath);
                        return;
                    }

                    // Scale avatar relative to image height: ~8%, clamped 48–96px
                    var avatarSize = Math.Clamp((int)(sourceBitmap.Height * 0.08), 48, 96);
                    var borderWidth = Math.Max(2, avatarSize / 20);
                    var step = (int)(avatarSize * 0.70);
                    var marginX = avatarSize / 2;
                    var marginY = avatarSize / 2;

                    var imageInfo = new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(imageInfo);
                    var canvas = surface.Canvas;
                    canvas.DrawBitmap(sourceBitmap, 0, 0);

                    // Subtle drop-shadow behind the avatar stack
                    var totalWidth = avatarSize + ((headshots.Count - 1) * step) + (borderWidth * 2);
                    using var shadowPaint = new SKPaint
                    {
                        Color = new SKColor(0, 0, 0, 100),
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, avatarSize * 0.3f),
                        IsAntialias = true,
                    };
                    float shadowPad = avatarSize * 0.15f;
                    var shadowRect = SKRect.Create(
                        marginX - borderWidth - shadowPad,
                        sourceBitmap.Height - marginY - avatarSize - borderWidth - shadowPad,
                        totalWidth + (shadowPad * 2f),
                        (avatarSize + (borderWidth * 2)) + (shadowPad * 2f));
                    canvas.DrawRoundRect(new SKRoundRect(shadowRect, avatarSize / 2f), shadowPaint);

                    // Draw avatars right-to-left so leftmost (first cast member) is on top
                    for (int i = headshots.Count - 1; i >= 0; i--)
                    {
                        var headshot = headshots[i];
                        float cx = marginX + (i * step) + (avatarSize / 2f);
                        float cy = sourceBitmap.Height - marginY - (avatarSize / 2f);
                        float r = avatarSize / 2f;

                        using var borderPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                        canvas.DrawCircle(cx, cy, r + borderWidth, borderPaint);

                        canvas.Save();
                        using var clipPath = new SKPath();
                        clipPath.AddCircle(cx, cy, r);
                        canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                        canvas.DrawBitmap(headshot, SKRect.Create(cx - r, cy - r, avatarSize, avatarSize));
                        canvas.Restore();
                    }

                    using var image = surface.Snapshot();
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
                    File.WriteAllBytes(destPath, encoded.ToArray());
                },
                cancellationToken);
        }
    }
}
