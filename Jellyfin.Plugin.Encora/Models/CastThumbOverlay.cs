using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Composites cast headshots as an overlapping avatar stack onto a generated thumb.png,
    /// reading headshots from Jellyfin's own local people-image cache rather than re-fetching
    /// from StageMedia. The original thumb is preserved as thumb.original.png before the first
    /// overlay is applied so re-runs always composite onto the clean source.
    /// </summary>
    public static class CastThumbOverlay
    {
        private const int MaxAvatars = 6;
        private const string BackupName = "thumb.original.png";
        private const string ThumbName = "thumb.png";

        /// <summary>
        /// Resolves local on-disk headshot paths for up to <see cref="MaxAvatars"/> cast members
        /// by looking each performer up in Jellyfin's own people library and reading their cached
        /// primary image. Falls back gracefully to an empty list when people aren't cached yet
        /// (first-ever refresh before Jellyfin has downloaded the headshots).
        /// </summary>
        /// <param name="cast">The recording's cast list.</param>
        /// <param name="libraryManager">Jellyfin library manager used to find person items.</param>
        /// <returns>Ordered list of local image file paths, at most <see cref="MaxAvatars"/> entries.</returns>
        public static IReadOnlyList<string> GetLocalHeadshotPaths(
            IEnumerable<EncoraCastMember>? cast,
            ILibraryManager libraryManager)
        {
            if (cast == null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (var member in cast)
            {
                if (paths.Count >= MaxAvatars)
                {
                    break;
                }

                var name = member.Performer?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var person = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Person },
                    Name = name,
                    Limit = 1,
                }).OfType<Person>().FirstOrDefault();

                if (person == null)
                {
                    continue;
                }

                var imageInfo = person.GetImageInfo(ImageType.Primary, 0);
                if (imageInfo?.Path != null && File.Exists(imageInfo.Path))
                {
                    paths.Add(imageInfo.Path);
                }
            }

            return paths;
        }

        /// <summary>
        /// Reads cached headshot images and composites them as a circular avatar stack onto
        /// thumb.png. No-ops if no local images are available or if thumb.png does not exist yet.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="episodeDir">Directory containing the episode's thumb.png.</param>
        /// <param name="localHeadshotPaths">Ordered list of local headshot file paths.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous overlay operation.</returns>
        public static async Task OverlayAsync(
            ILogger logger,
            string episodeDir,
            IReadOnlyList<string> localHeadshotPaths,
            CancellationToken cancellationToken)
        {
            if (localHeadshotPaths.Count == 0)
            {
                return;
            }

            var thumbPath = Path.Combine(episodeDir, ThumbName);
            var backupPath = Path.Combine(episodeDir, BackupName);

            if (!File.Exists(thumbPath) && !File.Exists(backupPath))
            {
                return;
            }

            // Back up the clean original once; subsequent runs composite from that backup
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

            // Decode cached headshot bitmaps (synchronous file reads, no HTTP)
            var headshotBitmaps = new List<SKBitmap>();
            foreach (var path in localHeadshotPaths)
            {
                try
                {
                    var bmp = SKBitmap.Decode(path);
                    if (bmp != null)
                    {
                        headshotBitmaps.Add(bmp);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[Encora] [CastOverlay] Could not decode headshot {Path}, skipping", path);
                }
            }

            if (headshotBitmaps.Count == 0)
            {
                return;
            }

            try
            {
                await CompositeAsync(logger, sourcePath, thumbPath, headshotBitmaps, cancellationToken)
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
                    bmp.Dispose();
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

                    var imageInfo = new SKImageInfo(
                        sourceBitmap.Width,
                        sourceBitmap.Height,
                        SKColorType.Rgba8888,
                        SKAlphaType.Premul);

                    using var surface = SKSurface.Create(imageInfo);
                    var canvas = surface.Canvas;
                    canvas.DrawBitmap(sourceBitmap, 0, 0);

                    // Subtle drop-shadow behind the avatar stack for legibility
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

                    // Draw right-to-left so first cast member renders on top
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
